using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TelegramBot.Core.Config;

/// <summary>
/// Определяет UNC-путь подключённого сетевого диска: <c>Z:\Проекты</c> → <c>\\сервер\шара\Проекты</c>.
/// Сначала использует подключение текущей сессии, затем сохранённые подключения загруженных профилей Windows.
/// </summary>
public static class UncPathResolver
{
    private const int NoError = 0;
    private const int ErrorMoreData = 234;
    private const uint UniversalNameInfoLevel = 1;

    /// <summary>Приводит путь к прямому UNC-виду. Путь, уже являющийся UNC, возвращается как есть.</summary>
    public static bool TryResolve(string path, out string uncPath)
    {
        uncPath = string.Empty;

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            uncPath = path;
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (TryResolveCurrentSession(path, out uncPath))
        {
            return true;
        }

        return TryResolveLoadedUserProfiles(path, out uncPath);
    }

    private static bool TryResolveCurrentSession(string path, out string uncPath)
    {
        uncPath = string.Empty;
        var size = 1024;
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            var result = WNetGetUniversalName(path, UniversalNameInfoLevel, buffer, ref size);

            if (result == ErrorMoreData)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero; // защита finally от double-free, если следующий Alloc бросит
                buffer = Marshal.AllocHGlobal(size);
                result = WNetGetUniversalName(path, UniversalNameInfoLevel, buffer, ref size);
            }

            if (result != NoError)
            {
                return false;
            }

            var universalName = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer));

            if (string.IsNullOrWhiteSpace(universalName) || !universalName.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            uncPath = universalName;
            return true;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryResolveLoadedUserProfiles(string path, out string uncPath)
    {
        uncPath = string.Empty;

        var driveRoot = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(driveRoot) || driveRoot.Length < 2 || driveRoot[1] != ':')
        {
            return false;
        }

        var driveLetter = char.ToUpperInvariant(driveRoot[0]);
        var relativePath = path[driveRoot.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var sid in Registry.Users.GetSubKeyNames())
            {
                using var driveKey = Registry.Users.OpenSubKey($@"{sid}\Network\{driveLetter}");
                var remotePath = driveKey?.GetValue("RemotePath") as string;
                if (string.IsNullOrWhiteSpace(remotePath) || !remotePath.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    continue;
                }

                var candidate = remotePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrEmpty(relativePath))
                {
                    candidate = Path.Combine(candidate, relativePath);
                }

                candidates.Add(candidate);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }

        if (candidates.Count != 1)
        {
            return false;
        }

        uncPath = candidates.Single();
        return true;
    }

    [DllImport("mpr.dll", EntryPoint = "WNetGetUniversalNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetGetUniversalName(string localPath, uint infoLevel, IntPtr buffer, ref int bufferSize);
}
