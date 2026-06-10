using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramBot.Core.Interfaces;

namespace TelegramBot.Data;

/// <summary>
/// Service for message tracking persistence.
/// </summary>
public sealed class MessageTrackingDataService(
    IConfiguration configuration,
    ILogger<MessageTrackingDataService> logger)
    : DataAccessBase(configuration.GetConnectionString("Postgres") ?? DefaultConnectionString, logger), IMessageTrackingDataService
{
    /// <inheritdoc/>
    public async Task TrackMessageAsync(long chatId, int messageId, int? sessionId = null)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            _ = await conn.ExecuteAsync(SqlQueries.TrackedMessages.Insert,
                new { SessionId = sessionId, ChatId = chatId, MessageIdPg = messageId });
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Failed to track message {MessageId} for chat {ChatId}", messageId, chatId);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteTrackedMessagesAsync(int sessionId, IEnumerable<int> messageIds)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            _ = await conn.ExecuteAsync(SqlQueries.TrackedMessages.DeleteByIds,
                new { SessionId = sessionId, MessageIds = messageIds.ToArray() });
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Failed to delete tracked messages for session {SessionId}", sessionId);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteTrackedMessagesBySessionAsync(int sessionId)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            _ = await conn.ExecuteAsync(SqlQueries.TrackedMessages.DeleteBySession,
                new { SessionId = sessionId });
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Failed to delete all tracked messages for session {SessionId}", sessionId);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteTrackedMessagesByChatAsync(long chatId, IEnumerable<int> messageIds)
    {
        try
        {
            var ids = messageIds.ToArray();
            if (ids.Length == 0)
            {
                return;
            }

            await using var conn = await CreateOpenConnectionAsync();
            _ = await conn.ExecuteAsync(SqlQueries.TrackedMessages.DeleteByChatAndMessages,
                new { ChatId = chatId, MessageIds = ids });
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Failed to delete tracked messages for chat {ChatId}", chatId);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<int>> GetTrackedMessagesBySessionAsync(int sessionId)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            var result = await conn.QueryAsync<int>(SqlQueries.TrackedMessages.GetBySession,
                new { SessionId = sessionId });
            return result.ToList().AsReadOnly();
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Failed to get tracked messages for session {SessionId}", sessionId);
            return [];
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<int>> GetTrackedMessagesByChatAsync(long chatId)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            var result = await conn.QueryAsync<int>(SqlQueries.TrackedMessages.GetByChat,
                new { ChatId = chatId });
            return result.ToList().AsReadOnly();
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Failed to get tracked messages for chat {ChatId}", chatId);
            return [];
        }
    }
}
