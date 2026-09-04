using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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
}

public enum RootPathUpdateResult
{
    Updated,
    NotAdministrator,
    Failed
}
