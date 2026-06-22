using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramBot.Data.Models;

namespace TelegramBot.Data;

public sealed class NotificationOutboxDataService(
    IConfiguration configuration,
    ILogger<NotificationOutboxDataService> logger)
    : DataAccessBase(configuration.GetConnectionString("Postgres") ?? DefaultConnectionString, logger)
{
    public const string SessionCompletedEvent = "session_completed";

    public async Task<IReadOnlyList<NotificationOutboxItem>> ClaimPendingAsync(
        string eventType,
        int limit,
        TimeSpan leaseDuration)
    {
        await using var conn = await CreateOpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var items = await conn.QueryAsync<NotificationOutboxItem>(
            SqlQueries.NotificationOutbox.ClaimPending,
            new
            {
                EventType = eventType,
                Limit = limit,
                LeaseSeconds = (int)leaseDuration.TotalSeconds
            },
            tx);

        await tx.CommitAsync();
        return items.ToList().AsReadOnly();
    }

    public async Task MarkSentAsync(long outboxId)
    {
        await using var conn = await CreateOpenConnectionAsync();
        var affected = await conn.ExecuteAsync(
            SqlQueries.NotificationOutbox.MarkSent,
            new { OutboxId = outboxId });

        if (affected == 0)
        {
            Logger.LogWarning("Outbox item was not marked sent: outboxId={OutboxId}", outboxId);
        }
    }

    public async Task MarkFailedAsync(long outboxId, int attempts, Exception exception)
    {
        var retryDelaySeconds = Math.Min(300, Math.Max(5, attempts * 10));
        var error = exception.Message.Length <= 2000
            ? exception.Message
            : exception.Message[..2000];

        await using var conn = await CreateOpenConnectionAsync();
        _ = await conn.ExecuteAsync(
            SqlQueries.NotificationOutbox.MarkFailed,
            new
            {
                OutboxId = outboxId,
                RetryDelaySeconds = retryDelaySeconds,
                LastError = error
            });
    }
}
