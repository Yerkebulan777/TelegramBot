namespace TelegramBot.Worker.Helpers;

/// <summary>
/// Общее чтение ResultFile / MERGEDWG status: sharing с писателем и bounded retry.
/// </summary>
internal static class PluginOutputFileRead
{
    public const int RetryCount = 50;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    public static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public static async Task<bool> ShouldRetryAsync(int attempt, CancellationToken ct)
    {
        if (attempt >= RetryCount)
        {
            return false;
        }

        await Task.Delay(RetryDelay, ct);
        return true;
    }
}
