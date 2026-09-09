namespace TelegramBot.Worker.Services;

/// <summary>
/// Проверяет и удаляет временные каталоги RevitBIMFusion вне слотов выполнения команд.
/// </summary>
public sealed class RevitTemporaryDirectoryCleaner(
    ILogger<RevitTemporaryDirectoryCleaner> logger) : IHostedService
{
    private const int DeleteRetryCount = 5;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromSeconds(30);

    private readonly HashSet<Task> _activeCleanups = [];
    private readonly object _gate = new();
    private bool _acceptingRequests;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _acceptingRequests = true;
        }

        return Task.CompletedTask;
    }

    /// <summary>Запускает отслеживаемую status-neutral очистку в фоне.</summary>
    public void Schedule(string? temporaryDirectoryPath, string? sourceFilePath, int commandId)
    {
        if (string.IsNullOrWhiteSpace(temporaryDirectoryPath))
        {
            return;
        }

        Task? cleanupTask;
        lock (_gate)
        {
            if (!_acceptingRequests)
            {
                cleanupTask = null;
            }
            else
            {
                var request = new CleanupRequest(temporaryDirectoryPath, sourceFilePath, commandId);
                cleanupTask = Task.Run(() => ProcessRequestAsync(request));
                _ = _activeCleanups.Add(cleanupTask);
            }
        }

        if (cleanupTask is null)
        {
            logger.LogWarning("Temporary directory cleanup was not scheduled during shutdown: id={CommandId}", commandId);
            return;
        }

        _ = cleanupTask.ContinueWith(
            (completedTask, state) =>
            {
                var (cleanups, gate) = ((HashSet<Task>, object))state!;
                lock (gate)
                {
                    _ = cleanups.Remove(completedTask);
                }
            },
            (_activeCleanups, _gate),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] pendingCleanups;
        lock (_gate)
        {
            _acceptingRequests = false;
            pendingCleanups = [.. _activeCleanups];
        }

        if (pendingCleanups.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pendingCleanups).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Temporary directory cleanup shutdown timeout: pending={PendingCount}",
                pendingCleanups.Count(task => !task.IsCompleted));
        }
    }

    private async Task ProcessRequestAsync(CleanupRequest request)
    {
        try
        {
            var directoryPath = Validate(request);
            if (directoryPath is not null)
            {
                await DeleteWithRetryAsync(directoryPath, request.CommandId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Temporary directory cleanup failed unexpectedly: id={CommandId}", request.CommandId);
        }
    }

    private string? Validate(CleanupRequest request)
    {
        try
        {
            if (!Path.IsPathFullyQualified(request.TemporaryDirectoryPath))
            {
                logger.LogWarning("Temporary directory cleanup rejected non-absolute path: id={CommandId}", request.CommandId);
                return null;
            }

            var directoryPath = Normalize(request.TemporaryDirectoryPath);
            var tempRoot = Normalize(Path.GetTempPath());
            var directoryName = Path.GetFileName(directoryPath);
            var parentPath = Directory.GetParent(directoryPath)?.FullName;

            if (!string.Equals(Normalize(parentPath), tempRoot, StringComparison.OrdinalIgnoreCase)
                || !directoryName.StartsWith("RBF-", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(directoryName.AsSpan(4), "N", out _))
            {
                logger.LogWarning("Temporary directory cleanup rejected non-contract path: id={CommandId}", request.CommandId);
                return null;
            }

            var sourceFileName = Path.GetFileName(request.SourceFilePath);
            if (string.IsNullOrWhiteSpace(sourceFileName)
                || !Path.GetExtension(sourceFileName).Equals(".rvt", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Temporary directory cleanup rejected source file name: id={CommandId}", request.CommandId);
                return null;
            }

            var directoryAttributes = File.GetAttributes(directoryPath);
            var rvtAttributes = File.GetAttributes(Path.Combine(directoryPath, sourceFileName));
            if (!IsOrdinaryDirectory(directoryAttributes)
                || rvtAttributes.HasFlag(FileAttributes.Directory)
                || rvtAttributes.HasFlag(FileAttributes.ReparsePoint))
            {
                logger.LogWarning("Temporary directory cleanup rejected invalid directory or RVT marker: id={CommandId}", request.CommandId);
                return null;
            }

            return directoryPath;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            logger.LogWarning(ex, "Temporary directory cleanup validation failed: id={CommandId}", request.CommandId);
            return null;
        }
    }

    private async Task DeleteWithRetryAsync(string directoryPath, int commandId)
    {
        for (var attempt = 0; attempt <= DeleteRetryCount; attempt++)
        {
            try
            {
                if (!IsOrdinaryDirectory(File.GetAttributes(directoryPath)))
                {
                    logger.LogWarning("Temporary directory cleanup stopped after path changed: id={CommandId}", commandId);
                    return;
                }

                Directory.Delete(directoryPath, recursive: true);
                logger.LogDebug("Temporary directory deleted: id={CommandId}, attempt={Attempt}", commandId, attempt + 1);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= DeleteRetryCount)
                {
                    logger.LogWarning(ex,
                        "Temporary directory cleanup exhausted retries: id={CommandId}, retries={RetryCount}",
                        commandId, DeleteRetryCount);
                    return;
                }

                logger.LogDebug(ex,
                    "Temporary directory cleanup retry: id={CommandId}, retry={Retry}/{RetryCount}",
                    commandId, attempt + 1, DeleteRetryCount);
                await Task.Delay(DeleteRetryDelay);
            }
        }
    }

    private static string Normalize(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsOrdinaryDirectory(FileAttributes attributes)
    {
        return attributes.HasFlag(FileAttributes.Directory)
            && !attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private readonly record struct CleanupRequest(
        string TemporaryDirectoryPath,
        string? SourceFilePath,
        int CommandId);
}
