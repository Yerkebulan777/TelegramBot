namespace TelegramBot.Core.Helpers;

/// <summary>
/// <see cref="File.Exists"/> по UNC может зависнуть на SMB. Ограничиваем ожидание, не удерживая слот обработки.
/// </summary>
public static class FileExistence
{
    public static async Task<List<string>> ExistingPathsAsync(
        IEnumerable<string> paths, TimeSpan perPathTimeout, CancellationToken cancellationToken)
    {
        var existing = new List<string>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ExistsWithinAsync(path, perPathTimeout, cancellationToken))
            {
                existing.Add(path);
            }
        }

        return existing;
    }

    public static async Task<bool> ExistsWithinAsync(
        string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await Task.Run(() => File.Exists(path), timeoutCts.Token).WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
