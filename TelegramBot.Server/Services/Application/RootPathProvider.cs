using TelegramBot.Data;

namespace TelegramBot.Server.Services.Application;

/// <summary>Владеет текущим корневым UNC-путём Server и сериализует его загрузку и смену.</summary>
public sealed class RootPathProvider(
    RootPathDataService rootPathDataService,
    ILogger<RootPathProvider> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _rootPath = string.Empty;
    private bool _isLoaded;

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

    public async Task<bool> SetRootPathAsync(string rootPath, long updatedByUserId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!await rootPathDataService.SetRootPathAsync(rootPath, updatedByUserId, cancellationToken))
            {
                return false;
            }

            _rootPath = rootPath;
            _isLoaded = true;
            logger.LogInformation("Runtime root path changed: user={UserId}, root={RootPath}", updatedByUserId, rootPath);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
