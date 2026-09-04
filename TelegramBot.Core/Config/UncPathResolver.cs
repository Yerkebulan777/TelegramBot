using System.Runtime.InteropServices;

namespace TelegramBot.Core.Config;

/// <summary>
/// Определяет UNC-путь подключённого сетевого диска: <c>Z:\Проекты</c> → <c>\\сервер\шара\Проекты</c>.
/// Пользователь видит в проводнике только букву диска — UNC-путь Windows скрывает.
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

    [DllImport("mpr.dll", EntryPoint = "WNetGetUniversalNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetGetUniversalName(string localPath, uint infoLevel, IntPtr buffer, ref int bufferSize);
}
