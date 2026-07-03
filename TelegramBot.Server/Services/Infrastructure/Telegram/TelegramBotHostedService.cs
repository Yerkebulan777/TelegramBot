using System.Threading.Channels;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBot.Core.DTOs;
using TelegramBot.Server.Config;
using TelegramBot.Server.Services.Application;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

public class TelegramBotHostedService(
    ITelegramBotClient botClient,
    CommandAppService commandAppService,
    ILogger<TelegramBotHostedService> logger,
    TelegramUpdateMapper inputService,
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Независимый CTS: см. комментарий к полю _processingCts
        _processingCts = new CancellationTokenSource();
        var processingToken = _processingCts.Token;

        var processingTask = ProcessUpdatesAsync(processingToken);

        logger.LogInformation("Telegram polling starting with parallel processing (maxConcurrency={MaxConcurrency}, channelCapacity={Capacity})",
            MaxConcurrentUpdates, ChannelCapacity);

        await BotCommandsSetup.ConfigureAsync(botClient, logger);

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

        logger.LogInformation("Telegram polling stopped");
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
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Отдельное обновление было отменено (например, таймаут) — логируем и продолжаем
                        logger.LogWarning("Update {UpdateId} processing was cancelled", update.Id);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Unhandled exception processing update {UpdateId}", update.Id);
                    }
                });
        }
        catch (OperationCanceledException)
        {
            // Shutdown — нормальное завершение
            logger.LogDebug("Parallel update processing stopped (shutdown)");
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
        var dto = inputService.Map(update);
        logger.LogDebug("Update processing: id={UpdateId}, type={UpdateType}, dto={DtoType}",
            update.Id, update.Type, dto?.GetType().Name ?? "null");

        switch (dto)
        {
            case MessageDto message:
                using (await sessionManager.AcquireUserLockAsync(message.UserId))
                {
                    _ = sessionManager.GetOrCreateSession(message.UserId);

                    if (message.Text == null)
                    {
                        logger.LogWarning("Received message with null Text from {Username} ({UserId})", message.Username, message.UserId);
                        return;
                    }

                    await commandAppService.HandleUserCommandAsync(message, ct);
                }
                break;
            case CallbackQueryDto callback:
                if (callback.CallbackQueryId == null)
                {
                    logger.LogWarning("Received callback with null CallbackQueryId from {Username} ({UserId})", callback.Username, callback.UserId);
                    return;
                }
                using (await sessionManager.AcquireUserLockAsync(callback.UserId))
                {
                    _ = sessionManager.GetOrCreateSession(callback.UserId);
                    await commandAppService.HandleCallbackAsync(callback, ct);
                    await botClient.AnswerCallbackQuery(callback.CallbackQueryId, cancellationToken: ct);
                }
                break;
        }
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
            logger.LogDebug(ex, "Channel writer rejected update {UpdateId}: channel closed", update.Id);
        }
        catch (OperationCanceledException)
        {
            // Отмена токена (shutdown) — ожидаемо, обновление уйдёт в следующий polling-цикл
            logger.LogDebug("Channel writer rejected update {UpdateId}: operation cancelled", update.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enqueue update {UpdateId}", update.Id);
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken token)
    {
        logger.LogError(exception, "Polling error");
        return Task.CompletedTask;
    }
}
