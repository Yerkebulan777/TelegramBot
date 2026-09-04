using TelegramBot.Data;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Services.Application;

/// <summary>Владеет текущим корневым UNC-путём Server и сериализует его загрузку и смену.</summary>
public sealed class RootPathProvider(
    RootPathDataService rootPathDataService,
    ILogger<RootPathProvider> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _rootPath = string.Empty;
    private bool _isLoaded;
    private long? _administratorUserId;
    private bool _isAdministratorLoaded;

    public async Task<string> GetRootPathAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_isLoaded)
            {
                return _rootPath;
            }

            try
            {
                _rootPath = await rootPathDataService.GetRootPathAsync(cancellationToken) ?? string.Empty;
                _isLoaded = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load runtime root path");
            }

            return _rootPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> CanConfigureRootPathAsync(long userId, CancellationToken cancellationToken = default)
    {
        var administratorUserId = await GetRootPathAdministratorUserIdAsync(cancellationToken);
        return !administratorUserId.HasValue || administratorUserId.Value == userId;
    }

    public async Task<bool> HasRootPathAdministratorAsync(CancellationToken cancellationToken = default)
    {
        return (await GetRootPathAdministratorUserIdAsync(cancellationToken)).HasValue;
    }

    public async Task<RootPathUpdateResult> SetRootPathAsync(string rootPath, long updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = await rootPathDataService.SetRootPathAsync(rootPath, updatedByUserId, cancellationToken);
            if (result != RootPathUpdateResult.Updated)
            {
                return result;
            }

            _rootPath = rootPath;
            _isLoaded = true;
            _administratorUserId = updatedByUserId;
            _isAdministratorLoaded = true;
            logger.LogInformation("Runtime root path changed: user={UserId}", updatedByUserId);
            return RootPathUpdateResult.Updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PendingRootPathChange?> GetPendingRootPathChangeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await rootPathDataService.GetPendingRootPathChangeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load pending runtime root path change");
            return null;
        }
    }

    public async Task<PendingRootPathChangeDecisionResult> DecidePendingRootPathChangeAsync(
        Guid changeId,
        long userId,
        string? verifiedRootPath,
        string? expectedUncPath,
        bool apply,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = await rootPathDataService.DecidePendingRootPathChangeAsync(
                changeId, userId, verifiedRootPath, expectedUncPath, apply, cancellationToken);
            if (result == PendingRootPathChangeDecisionResult.Applied)
            {
                _rootPath = verifiedRootPath!;
                _isLoaded = true;
                _administratorUserId = userId;
                _isAdministratorLoaded = true;
                logger.LogInformation("Pending runtime root path applied: user={UserId}, request={RequestId}", userId, changeId);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<long?> GetRootPathAdministratorUserIdAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_isAdministratorLoaded)
            {
                return _administratorUserId;
            }

            try
            {
                _administratorUserId = await rootPathDataService.GetRootPathAdministratorUserIdAsync(cancellationToken);
                _isAdministratorLoaded = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load runtime root path administrator");
            }

            return _administratorUserId;
        }
        finally
        {
            _gate.Release();
        }
    }
}
