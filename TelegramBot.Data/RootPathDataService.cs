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

    public async Task<bool> SetRootPathAsync(string rootPath, long updatedByUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await CreateOpenConnectionAsync();
            _ = await conn.ExecuteAsync(new CommandDefinition(
                SqlQueries.RuntimeSettings.UpsertRootPath,
                new { RootPath = rootPath, UpdatedByUserId = updatedByUserId },
                cancellationToken: cancellationToken));
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to set runtime root path: user={UserId}", updatedByUserId);
            return false;
        }
    }
}
