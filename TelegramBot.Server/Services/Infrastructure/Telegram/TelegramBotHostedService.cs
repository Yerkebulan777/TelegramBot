using System.Threading.Channels;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBot.Data;
using TelegramBot.Server.Services.Application;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public class TelegramBotHostedService(
    ITelegramBotClient botClient,
    CommandAppService commandAppService,
    SchemaReadyGate schemaReadyGate,
    IHostApplicationLifetime lifetime,
    ILogger<TelegramBotHostedService> logger,
    SessionManager sessionManager) : BackgroundService
{
    /// <summary>
    /// Максимальное количество обновлений Telegram, обрабатываемых параллельно.
    /// Значение 10 обеспечивает высокую пропускную способность при пиковой нагрузке,
    /// не перегружая при этом Telegram API (rate limits) и PostgreSQL.
    /// </summary>
    private const int MaxConcurrentUpdates = 10;

    /// <summary>
    /// Размер буфера очереди обновлений. При пиковой нагрузке SDK может присылать
    /// до 100 обновлений в одном batch. Буфер 200 гарантирует, что ни одно обновление
    /// не будет потеряно, даже при временной задержке обработки.
    /// </summary>
    private const int ChannelCapacity = 200;

    /// <summary>
    /// Канал для асинхронной очереди входящих обновлений.
    /// HandleUpdateAsync пишет, ProcessUpdatesAsync читает и обрабатывает параллельно.
    /// </summary>
    private readonly Channel<Update> _updateChannel = Channel.CreateBounded<Update>(
        new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>
    /// Независимый CTS для фоновой обработки (НЕ linked к stoppingToken).
    /// При остановке host: сначала завершаем writer канала, дожидаемся опустошения буфера,
    /// и только потом отменяем reader через этот CTS.
    /// Если бы CTS был linked к stoppingToken, ReadAllAsync(ct) прекратился бы немедленно
    /// при shutdown и буферизованные обновления (до 200) были бы потеряны.
    /// </summary>
    private CancellationTokenSource? _processingCts;
    private static readonly TimeSpan[] CommandSetupRetryBackoff =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schemaReadyGate.WaitAsync(stoppingToken);

        BotCommand[] commands =
        [
            new() { Command = "export", Description = "Export to different formats" },
            new() { Command = "automation", Description = "Automation features" },
            new() { Command = "status", Description = "Check your command queue" },
            new() { Command = "help", Description = "Show help menu" }
        ];

        logger.LogInformation("Configuring bot commands: count={Count}", commands.Length);
        if (!await TryConfigureBotCommandsAsync(commands, stoppingToken))
        {
            return;
        }

        // Независимый CTS: см. комментарий к полю _processingCts
        _processingCts = new CancellationTokenSource();
        var processingToken = _processingCts.Token;
        var processingTask = ProcessUpdatesAsync(processingToken);

        logger.LogInformation("Polling start: concurrency={MaxConcurrency}, capacity={Capacity}",
            MaxConcurrentUpdates, ChannelCapacity);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery]
        };

        var updateHandler = new DefaultUpdateHandler(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync
        );

        // StartReceiving использует stoppingToken — при shutdown SDK перестанет присылать новые обновления
        botClient.StartReceiving(updateHandler, receiverOptions, stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // Expected when the host is stopping
        }

        // Шаг 1: завершаем writer — новые обновления больше не попадут в канал
        _=_updateChannel.Writer.TryComplete();

        // Шаг 2: ждём, пока ProcessUpdatesAsync дочитает оставшиеся в буфере обновления
        // processingTask НЕ отменён (processingToken не cancelled), поэтому ReadAllAsync
        // будет читать до Completion (пока writer не завершён).
        try
        {
            _=await Task.WhenAny(processingTask, Task.Delay(TimeSpan.FromSeconds(10)));
        }
        catch (OperationCanceledException)
        {
            // ignored
        }

        // Шаг 3: отменяем processing CTS — останавливаем reader (только если ещё работает)
        await _processingCts.CancelAsync();
        _processingCts.Dispose();

        logger.LogInformation("Polling stopped");
    }

    /// <summary>
    /// Регистрирует меню бота. Пока Telegram недоступен — retry, чтобы хост
    /// (и иконка в трее) остались живы, а не упали вместе с polling.
    /// </summary>
    private async Task<bool> TryConfigureBotCommandsAsync(BotCommand[] commands, CancellationToken stoppingToken)
    {
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await botClient.SetMyCommands(commands, cancellationToken: stoppingToken);
                logger.LogInformation("Bot commands configured");
                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                attempt++;
                var delayIndex = Math.Min(attempt, CommandSetupRetryBackoff.Length) - 1;
                var delay = CommandSetupRetryBackoff[delayIndex];
                if (attempt == 1)
                {
                    logger.LogWarning(ex,
                        "Configure bot commands attempt {Attempt} failed, retrying in {Delay}s",
                        attempt, delay.TotalSeconds);
                }
                else if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(ex,
                        "Configure bot commands attempt {Attempt} failed, retrying in {Delay}s",
                        attempt, delay.TotalSeconds);
                }
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Фоновая задача: читает обновления из канала и обрабатывает их параллельно
    /// через <see cref="Parallel.ForEachAsync"/> с ограничением <see cref="MaxConcurrentUpdates"/>.
    /// </summary>
    private async Task ProcessUpdatesAsync(CancellationToken ct)
    {
        try
        {
            await Parallel.ForEachAsync(
                _updateChannel.Reader.ReadAllAsync(ct),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxConcurrentUpdates,
                    CancellationToken = ct,
                },
                async (update, token) =>
                {
                    try
                    {
                        await ProcessUpdateAsync(update, token);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // Shutdown while waiting for the per-user lock.
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Отдельное обновление было отменено (например, таймаут) — логируем и продолжаем
                        logger.LogWarning("Update {UpdateId} cancelled", update.Id);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Update {UpdateId} error", update.Id);
                    }
                });
        }
        catch (OperationCanceledException)
        {
            // Shutdown — нормальное завершение
            logger.LogDebug("Parallel processing stopped (shutdown)");
        }
    }

    /// <summary>
    /// Обрабатывает одно обновление Telegram: маппинг, валидация, вызов бизнес-логики.
    /// Вызывается из <see cref="ProcessUpdatesAsync"/> параллельно для разных обновлений.
    /// Per-user блокировка через <see cref="SessionManager.AcquireUserLockAsync"/> гарантирует,
    /// что обновления одного пользователя обрабатываются последовательно.
    /// </summary>
    private async Task ProcessUpdateAsync(Update update, CancellationToken ct)
    {
        if (update.Message is { Text: not null, From: not null } message)
        {
            logger.LogDebug("Update: id={UpdateId}, type={UpdateType}, input=Message", update.Id, update.Type);

            using (await sessionManager.AcquireUserLockAsync(message.From.Id, ct))
            {
                await commandAppService.HandleUserCommandAsync(message, ct);
            }

            return;
        }

        if (update.CallbackQuery is { } callback)
        {
            _ = callback.Message
                ?? throw new InvalidOperationException("CallbackQuery.Message is null.");

            logger.LogDebug("Update: id={UpdateId}, type={UpdateType}, input=CallbackQuery", update.Id, update.Type);

            using (await sessionManager.AcquireUserLockAsync(callback.From.Id, ct))
            {
                await commandAppService.HandleCallbackAsync(callback, ct);
            }

            return;
        }

        logger.LogDebug("Update ignored: id={UpdateId}, type={UpdateType}", update.Id, update.Type);
    }

    /// <summary>
    /// Быстро ставит обновление в очередь для параллельной обработки.
    /// SDK вызывает этот метод для каждого обновления по очереди.
    /// Благодаря немедленному возврату SDK может получать следующие обновления,
    /// не дожидаясь завершения обработки текущего.
    /// </summary>
    public async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken token)
    {
        try
        {
            await _updateChannel.Writer.WriteAsync(update, token);
        }
        catch (ChannelClosedException ex)
        {
            logger.LogWarning(ex,
                "Dropped update {UpdateId}: channel closed (shutdown); Telegram offset already advanced",
                update.Id);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Dropped update {UpdateId}: enqueue cancelled (shutdown); Telegram offset already advanced",
                update.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Enqueue update {UpdateId} fail", update.Id);
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken token)
    {
        if (exception is ApiRequestException { ErrorCode: 409 })
        {
            logger.LogCritical(exception,
                "Polling conflict: another instance is using this bot token; stopping host");
            lifetime.StopApplication();
            return Task.CompletedTask;
        }

        logger.LogError(exception, "Polling error");
        return Task.CompletedTask;
    }
}
