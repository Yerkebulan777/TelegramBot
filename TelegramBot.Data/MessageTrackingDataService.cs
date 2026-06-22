using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
namespace TelegramBot.Data;

/// <summary>
/// Service for message tracking persistence.
/// </summary>
public sealed class MessageTrackingDataService(
    IConfiguration configuration,
    ILogger<MessageTrackingDataService> logger)
    : DataAccessBase(configuration.GetConnectionString("Postgres") ?? DefaultConnectionString, logger)
{
    /// <inheritdoc/>
    public async Task TrackMessageAsync(long chatId, int messageId, int? sessionId = null)
    {
        await TryExecuteTrackedAsync(
            SqlQueries.TrackedMessages.Insert,
            new { SessionId = sessionId, ChatId = chatId, MessageIdPg = messageId },
            "Failed to track message {MessageId} for chat {ChatId}",
            messageId, chatId);
    }

    /// <inheritdoc/>
    public async Task DeleteTrackedMessagesAsync(int sessionId, IEnumerable<int> messageIds)
    {
        await TryExecuteTrackedAsync(
            SqlQueries.TrackedMessages.DeleteByIds,
            new { SessionId = sessionId, MessageIds = messageIds.ToArray() },
            "Failed to delete tracked messages for session {SessionId}",
            sessionId);
    }

    /// <inheritdoc/>
    public async Task DeleteTrackedMessagesBySessionAsync(int sessionId)
    {
        await TryExecuteTrackedAsync(
            SqlQueries.TrackedMessages.DeleteBySession,
            new { SessionId = sessionId },
            "Failed to delete all tracked messages for session {SessionId}",
            sessionId);
    }

    /// <inheritdoc/>
    public async Task DeleteTrackedMessagesByChatAsync(long chatId, IEnumerable<int> messageIds)
    {
        var ids = messageIds.ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await TryExecuteTrackedAsync(
            SqlQueries.TrackedMessages.DeleteByChatAndMessages,
            new { ChatId = chatId, MessageIds = ids },
            "Failed to delete tracked messages for chat {ChatId}",
            chatId);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<int>> GetTrackedMessagesBySessionAsync(int sessionId)
    {
        return await TryQueryTrackedAsync<int>(
            SqlQueries.TrackedMessages.GetBySession,
            new { SessionId = sessionId },
            "Failed to get tracked messages for session {SessionId}",
            sessionId);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<int>> GetTrackedMessagesByChatAsync(long chatId)
    {
        return await TryQueryTrackedAsync<int>(
            SqlQueries.TrackedMessages.GetByChat,
            new { ChatId = chatId },
            "Failed to get tracked messages for chat {ChatId}",
            chatId);
    }

    /// <summary>
    /// Выполняет SQL без возврата результата в fire-and-forget режиме: ошибки логируются на LogWarning,
    /// но не пробрасываются. Используется для best-effort tracked-message операций.
    /// </summary>
    private async Task TryExecuteTrackedAsync(string sql, object? parameters, string failureTemplate, params object?[] args)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            _ = await conn.ExecuteAsync(sql, parameters);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, failureTemplate, args);
        }
    }

    /// <summary>
    /// Выполняет SQL с возвратом списка в fire-and-forget режиме. На ошибке возвращает пустой массив.
    /// </summary>
    private async Task<IReadOnlyList<T>> TryQueryTrackedAsync<T>(string sql, object? parameters, string failureTemplate, params object?[] args)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            var result = await conn.QueryAsync<T>(sql, parameters);
            return result.ToList().AsReadOnly();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, failureTemplate, args);
            return [];
        }
    }
}
