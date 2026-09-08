using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramBot.Data.Models;
namespace TelegramBot.Data;

/// <summary>
/// Service for message tracking persistence.
/// </summary>
public sealed class MessageTrackingDataService(
    IConfiguration configuration,
    ILogger<MessageTrackingDataService> logger)
    : DataAccessBase(ResolveConnectionString(configuration), logger)
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
    public async Task<IReadOnlyList<int>> GetTrackedMessagesByChatAsync(long chatId)
    {
        return await TryQueryTrackedAsync<int>(
            SqlQueries.TrackedMessages.GetByChat,
            new { ChatId = chatId },
            "Failed to get tracked messages for chat {ChatId}",
            chatId);
    }

    /// <summary>
    /// Возвращает устаревшие сообщения, которые ещё можно удалить в Telegram.
    /// </summary>
    public async Task<IReadOnlyList<TrackedMessageReference>> GetTrackedMessagesForCleanupAsync(
        DateTime olderThan,
        DateTime newerThan,
        int limit)
    {
        return await TryQueryTrackedAsync<TrackedMessageReference>(
            SqlQueries.TrackedMessages.GetForCleanup,
            new { OlderThan = olderThan, NewerThan = newerThan, Limit = limit },
            "Failed to get tracked messages for background cleanup");
    }

    /// <summary>
    /// Удаляет tracking-записи, вышедшие за лимит Telegram на удаление сообщений.
    /// Сами сообщения уже невозможно удалить через Bot API.
    /// </summary>
    public async Task<int> DeleteTrackedMessagesOlderThanAsync(DateTime olderThan)
    {
        return await TryExecuteTrackedCountAsync(
            SqlQueries.TrackedMessages.DeleteOlderThan,
            new { OlderThan = olderThan },
            "Failed to delete expired tracked messages");
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

    private async Task<int> TryExecuteTrackedCountAsync(string sql, object? parameters, string failureTemplate, params object?[] args)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            return await conn.ExecuteAsync(sql, parameters);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, failureTemplate, args);
            return 0;
        }
    }
}
