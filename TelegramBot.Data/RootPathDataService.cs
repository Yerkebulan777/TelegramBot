using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TelegramBot.Core.Models;

namespace TelegramBot.Data;

/// <summary>Хранит единый активный корневой UNC-путь.</summary>
public sealed class RootPathDataService(
    IConfiguration configuration,
    ILogger<RootPathDataService> logger)
    : DataAccessBase(ResolveConnectionString(configuration), logger)
{
    public async Task<string?> GetRootPathAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(SqlQueries.RuntimeSettings.GetRootPath, cancellationToken: cancellationToken));
    }

    public async Task<long?> GetRootPathAdministratorUserIdAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await CreateOpenConnectionAsync();
        var value = await conn.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(SqlQueries.RuntimeSettings.GetRootPathAdministratorUserId, cancellationToken: cancellationToken));

        return long.TryParse(value, out var userId) && userId > 0 ? userId : null;
    }

    public async Task<RootPathUpdateResult> SetRootPathAsync(string rootPath, long updatedByUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync(cancellationToken);

            _ = await conn.ExecuteAsync(new CommandDefinition(
                SqlQueries.RuntimeSettings.LockRootPathAdministration,
                transaction: tx,
                cancellationToken: cancellationToken));

            var administratorValue = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                SqlQueries.RuntimeSettings.GetRootPathAdministratorUserId,
                transaction: tx,
                cancellationToken: cancellationToken));

            if (administratorValue is null)
            {
                _ = await conn.ExecuteAsync(new CommandDefinition(
                    SqlQueries.RuntimeSettings.InsertRootPathAdministratorUserId,
                    new { UserId = updatedByUserId },
                    tx,
                    cancellationToken: cancellationToken));
            }
            else if (!long.TryParse(administratorValue, out var administratorUserId) || administratorUserId != updatedByUserId)
            {
                return RootPathUpdateResult.NotAdministrator;
            }

            _ = await conn.ExecuteAsync(new CommandDefinition(
                SqlQueries.RuntimeSettings.UpsertRootPath,
                new { RootPath = rootPath, UpdatedByUserId = updatedByUserId },
                tx,
                cancellationToken: cancellationToken));
            await tx.CommitAsync(cancellationToken);
            return RootPathUpdateResult.Updated;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to set runtime root path: user={UserId}", updatedByUserId);
            return RootPathUpdateResult.Failed;
        }
    }

    public async Task<bool> CreatePendingRootPathChangeAsync(PendingRootPathChange change, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            _ = await conn.ExecuteAsync(new CommandDefinition(
                SqlQueries.RuntimeSettings.UpsertPendingRootPathChange,
                new { Change = JsonSerializer.Serialize(change with { Status = PendingRootPathChange.PendingStatus }) },
                cancellationToken: cancellationToken));
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to create pending root path change: id={ChangeId}", change.Id);
            return false;
        }
    }

    public async Task<PendingRootPathChange?> GetPendingRootPathChangeAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await CreateOpenConnectionAsync();
        var serializedChange = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            SqlQueries.RuntimeSettings.GetPendingRootPathChange,
            cancellationToken: cancellationToken));
        return DeserializePendingChange(serializedChange);
    }

    public async Task<PendingRootPathChangeDecisionResult> DecidePendingRootPathChangeAsync(
        Guid changeId, long userId, string? verifiedRootPath, string? expectedUncPath, bool apply, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync(cancellationToken);
            _ = await conn.ExecuteAsync(new CommandDefinition(
                SqlQueries.RuntimeSettings.LockRootPathAdministration,
                transaction: tx,
                cancellationToken: cancellationToken));

            var administratorValue = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                SqlQueries.RuntimeSettings.GetRootPathAdministratorUserId,
                transaction: tx,
                cancellationToken: cancellationToken));
            if (administratorValue is not null
                && (!long.TryParse(administratorValue, out var administratorUserId) || administratorUserId != userId))
            {
                return PendingRootPathChangeDecisionResult.NotAdministrator;
            }

            var serializedChange = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                SqlQueries.RuntimeSettings.GetPendingRootPathChangeForUpdate,
                transaction: tx,
                cancellationToken: cancellationToken));
            var change = DeserializePendingChange(serializedChange);
            if (change is null || change.Id != changeId || !change.IsPending || IsExpired(change))
            {
                return PendingRootPathChangeDecisionResult.NotAvailable;
            }

            if (apply && (string.IsNullOrWhiteSpace(verifiedRootPath)
                || !string.Equals(change.UncPath, expectedUncPath, StringComparison.OrdinalIgnoreCase)))
            {
                return PendingRootPathChangeDecisionResult.InvalidPath;
            }

            if (apply && administratorValue is null)
            {
                _ = await conn.ExecuteAsync(new CommandDefinition(
                    SqlQueries.RuntimeSettings.InsertRootPathAdministratorUserId,
                    new { UserId = userId }, tx, cancellationToken: cancellationToken));
            }

            if (apply)
            {
                _ = await conn.ExecuteAsync(new CommandDefinition(
                    SqlQueries.RuntimeSettings.UpsertRootPath,
                    new { RootPath = verifiedRootPath, UpdatedByUserId = userId }, tx, cancellationToken: cancellationToken));
            }

            var completedChange = change with { Status = apply ? PendingRootPathChange.AppliedStatus : PendingRootPathChange.CancelledStatus };
            _ = await conn.ExecuteAsync(new CommandDefinition(
                SqlQueries.RuntimeSettings.UpdatePendingRootPathChange,
                new { Change = JsonSerializer.Serialize(completedChange), UpdatedByUserId = userId }, tx, cancellationToken: cancellationToken));
            await tx.CommitAsync(cancellationToken);
            return apply ? PendingRootPathChangeDecisionResult.Applied : PendingRootPathChangeDecisionResult.Cancelled;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to decide pending root path change: id={ChangeId}, user={UserId}", changeId, userId);
            return PendingRootPathChangeDecisionResult.Failed;
        }
    }

    private static PendingRootPathChange? DeserializePendingChange(string? serializedChange)
    {
        if (string.IsNullOrWhiteSpace(serializedChange))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PendingRootPathChange>(serializedChange);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsExpired(PendingRootPathChange change)
    {
        return change.CreatedAtUtc.AddMinutes(30) < DateTimeOffset.UtcNow;
    }
}

public enum RootPathUpdateResult
{
    Updated,
    NotAdministrator,
    Failed
}

public enum PendingRootPathChangeDecisionResult
{
    Applied,
    Cancelled,
    NotAdministrator,
    NotAvailable,
    InvalidPath,
    Failed
}
