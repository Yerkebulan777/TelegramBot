using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using TelegramBot.Core.Constants;
using TelegramBot.Data.Models;

namespace TelegramBot.Data;

/// <summary>Durable message tracking and deletion scheduling.</summary>
public sealed class MessageTrackingDataService(
    IConfiguration configuration,
    ILogger<MessageTrackingDataService> logger)
    : DataAccessBase(ResolveConnectionString(configuration), logger)
{
    public async Task TrackMessageAsync(
        long chatId, int messageId, int? sessionId, string kind, DateTime sentAt,
        DateTime deleteAfter, CancellationToken cancellationToken = default)
    {
        _ = await ExecuteAsync(SqlQueries.TrackedMessages.Insert,
            new
            {
                SessionId = sessionId,
                ChatId = chatId,
                MessageIdPg = messageId,
                Kind = kind,
                SentAt = sentAt,
                DeleteAfter = deleteAfter
            }, cancellationToken);
    }

    /// <summary>Atomically acknowledges successful deletions and schedules the remaining batch.</summary>
    public async Task SaveDeletionProgressAsync(long chatId, IEnumerable<int> deletedIds,
        IEnumerable<int> deferredIds, DateTime nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        var deleted = deletedIds.Distinct().ToArray();
        var deferred = deferredIds.Except(deleted).ToArray();
        _ = await ExecuteAsync(SqlQueries.TrackedMessages.SaveDeletionProgress,
            new { ChatId = chatId, DeletedIds = deleted, DeferredIds = deferred, NextAttemptAt = nextAttemptAt },
            cancellationToken);
    }

    public Task<IReadOnlyList<int>> GetTrackedMessagesByChatAsync(long chatId,
        CancellationToken cancellationToken = default) =>
        QueryAsync<int>(SqlQueries.TrackedMessages.GetByChat,
            new { ChatId = chatId, CompletionKind = TrackedMessageKinds.Completion }, cancellationToken);

    /// <summary>Records intent before Telegram I/O and returns due, unprotected messages.</summary>
    public async Task<IReadOnlyList<int>> ScheduleDeletionByChatAsync(long chatId, IEnumerable<int> messageIds,
        CancellationToken cancellationToken = default)
    {
        var ids = messageIds.Distinct().ToArray();
        return ids.Length == 0 ? [] : await QueryAsync<int>(SqlQueries.TrackedMessages.ScheduleDeletionByChat,
            new
            {
                ChatId = chatId,
                MessageIds = ids,
                InterfaceKind = TrackedMessageKinds.Interface,
                CompletionKind = TrackedMessageKinds.Completion
            }, cancellationToken);
    }

    public Task<IReadOnlyList<TrackedMessageReference>> GetTrackedMessagesForCleanupAsync(
        DateTime olderThan, DateTime newerThan, int limit, CancellationToken cancellationToken = default) =>
        QueryAsync<TrackedMessageReference>(SqlQueries.TrackedMessages.GetForCleanup,
            new
            {
                OlderThan = olderThan,
                NewerThan = newerThan,
                Limit = limit,
                Retention = DateTime.UtcNow - olderThan,
                JobStatusKind = TrackedMessageKinds.JobStatus
            }, cancellationToken);

    /// <summary>Forgets records past Telegram's deletion age limit.</summary>
    public Task<int> DeleteTrackedMessagesOlderThanAsync(DateTime olderThan, CancellationToken cancellationToken = default) =>
        ExecuteAsync(SqlQueries.TrackedMessages.DeleteOlderThan, new { OlderThan = olderThan }, cancellationToken);

    private Task<int> ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken = default) =>
        WithRetryAsync(conn => conn.ExecuteAsync(new CommandDefinition(sql, parameters,
            cancellationToken: cancellationToken)), cancellationToken);

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object parameters,
        CancellationToken cancellationToken = default) =>
        (await WithRetryAsync(conn => conn.QueryAsync<T>(new CommandDefinition(sql, parameters,
            cancellationToken: cancellationToken)), cancellationToken)).ToList();

    private async Task<T> WithRetryAsync<T>(Func<NpgsqlConnection, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var conn = await CreateOpenConnectionAsync(cancellationToken);
                return await action(conn);
            }
            catch (NpgsqlException ex) when (ex.IsTransient && attempt < 2)
            {
                Logger.LogWarning("Message tracking database operation will retry: attempt={Attempt}", attempt + 1);
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
            }
        }
    }
}
