using Dapper;
using Npgsql;
using TelegramBot.Core.Interfaces;
using TelegramBot.Server.Helpers;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Infrastructure.Telegram;

/// <summary>
/// Background service: слушает PostgreSQL LISTEN/NOTIFY на канале 'command_completed'.
/// При получении уведомления отправляет пользователю Telegram-сообщение о завершении/ошибке команды.
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
        logger.LogInformation("Command notification listener starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunListenerLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Connection lost. Reconnecting in {Delay}ms...", ReconnectDelayMs);
                await Task.Delay(ReconnectDelayMs, stoppingToken);
            }
        }

        logger.LogInformation("Command notification listener stopped");
    }

    private async Task RunListenerLoopAsync(CancellationToken stoppingToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(stoppingToken);

        _ = await conn.ExecuteAsync("LISTEN command_completed;");
        conn.Notification += OnNotificationReceived;

        logger.LogInformation("Listening for NOTIFY on 'command_completed'...");

        // Держим соединение открытым — WaitAsync блокируется до получения NOTIFY
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await conn.WaitAsync(stoppingToken);
            }
            catch (NpgsqlException ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "PostgreSQL error in notification listener, reconnecting...");
                break;
            }
        }
    }

    private async void OnNotificationReceived(object sender, NpgsqlNotificationEventArgs e)
    {
        if (e.Payload == null) return;

        try
        {
            // Payload: UserId|CommandId|CommandText|Status|ErrorMessage
            var parts = e.Payload.Split('|', 5);
            if (parts.Length < 4) return;

            if (!long.TryParse(parts[0], out var userId)) return;
            var commandText = parts[2];
            var status = parts[3];
            var errorMessage = parts.Length > 4 ? parts[4] : "";

            var isSuccess = status == "Done";

            var message = isSuccess
                ? $"✅ *{MarkdownHelper.EscapeMarkdownV2(commandText)}* завершена"
                : $"❌ *{MarkdownHelper.EscapeMarkdownV2(commandText)}* — ошибка:\n{MarkdownHelper.EscapeMarkdownV2(errorMessage)}";

            _ = await telegramOutput.SendMessageAsync(userId, message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process command_completed notification");
        }
    }
}
