using Dapper;
using Npgsql;
using TelegramBot.Data;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

/// <summary>
/// Background service: слушает PostgreSQL LISTEN/NOTIFY на канале 'command_completed'.
/// При получении уведомления отправляет пользователю сводку по завершённой сессии.
/// </summary>
public sealed class CommandNotificationService(
    IConfiguration configuration,
    ITelegramOutputService telegramOutput,
    ILogger<CommandNotificationService> logger) : BackgroundService
{
    private const int ReconnectDelayMs = 5_000;

    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Command notifications starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunListenerLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Command notifications lost: retryMs={Delay}", ReconnectDelayMs);
                await Task.Delay(ReconnectDelayMs, stoppingToken);
            }
        }

        logger.LogInformation("Command notifications stopped");
    }

    private async Task RunListenerLoopAsync(CancellationToken stoppingToken)
    {
        await using var conn = await NpgsqlHelper.CreateOpenConnectionAsync(_connectionString, stoppingToken);

        await conn.ExecuteAsync("LISTEN command_completed;");
        conn.Notification += OnNotificationReceived;

        logger.LogInformation("Command notifications listening: channel=command_completed");

        // Держим соединение открытым — WaitAsync блокируется до получения NOTIFY
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await conn.WaitAsync(stoppingToken);
            }
            catch (NpgsqlException ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Command notifications error: source=postgres");
                break;
            }
        }
    }

    private void OnNotificationReceived(object sender, NpgsqlNotificationEventArgs e)
    {
        // Fire-and-forget: событие Npgsql требует void, асинхронная работа — в HandleNotificationAsync
        _ = HandleNotificationAsync(e);
    }

    private async Task HandleNotificationAsync(NpgsqlNotificationEventArgs e)
    {
        try
        {
            if (e.Payload == null)
            {
                return;
            }

            // Payload: UserId|CommandId|CommandText|Status|FilePath|ErrorMessage|Done|Total
            var parts = e.Payload.Split('|', 8);
            if (parts.Length < 4)
            {
                logger.LogWarning("Completion notify ignored: reason=invalid_payload");
                return;
            }

            if (!long.TryParse(parts[0], out var userId))
            {
                logger.LogWarning("Completion notify ignored: reason=invalid_user");
                return;
            }

            // Payload: UserId|CommandId|CommandText|Status|FilePath|ErrorMessage|Done|Total
            // Приходит только когда вся сессия завершена (remaining == 0 на стороне Worker)
            if (!int.TryParse(parts[6], out var done) || !int.TryParse(parts[7], out var total) || total == 0)
            {
                return;
            }

            var failed = total - done;

            var summary = failed == 0
                ? $"✅ Сессия завершена — все {done} файлов обработано"
                : done == 0
                    ? $"❌ Сессия завершена — все {failed} файлов с ошибками"
                    : $"⚠️ Сессия завершена: {done} ✅, {failed} ❌ из {total}";

            await telegramOutput.SendMessageAsync(userId, summary);
            logger.LogInformation("Session completed: user={UserId}, done={Done}, failed={Failed}, total={Total}",
                userId, done, failed, total);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process command_completed notification");
        }
    }
}
