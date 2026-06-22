using System.Diagnostics;

namespace TelegramBot.Worker.Helpers;

/// <summary>
/// Унифицированное завершение процесса с ограниченным ожиданием выхода.
/// Используется в <see cref="Services.CommandExecutionService"/> (shutdown) и
/// <see cref="Services.ProcessRunner"/> (timeout). Защищает Worker от зависания
/// на процессе, который не реагирует на Kill (см. процессы Revit/Navisworks).
/// </summary>
internal static class ProcessKillHelper
{
    /// <summary>
    /// Убивает процесс (entire tree) и ожидает выхода в пределах <paramref name="timeout"/>.
    /// Не бросает исключения — возвращает <c>true</c> если процесс завершился в пределах таймаута,
    /// <c>false</c> при таймауте или ошибке ожидания.
    /// </summary>
    public static async Task<bool> KillAsync(Process process, TimeSpan timeout, ILogger logger, int commandId, CancellationToken externalToken = default)
    {
        if (process.HasExited)
        {
            return true;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            // Процесс мог выйти между HasExited и Kill — это не ошибка
            logger.LogDebug(ex, "Kill failed (likely already exited): commandId={Id}, pid={Pid}", commandId, SafeGetPid(process));
            return true;
        }

        using var killCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        killCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(killCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Process did not exit within {Timeout} after Kill: commandId={Id}, pid={Pid}",
                timeout, commandId, SafeGetPid(process));
            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "WaitForExitAsync after Kill failed: commandId={Id}, pid={Pid}",
                commandId, SafeGetPid(process));
            return false;
        }
    }

    private static int SafeGetPid(Process p)
    {
        try { return p.Id; } catch { return -1; }
    }
}
