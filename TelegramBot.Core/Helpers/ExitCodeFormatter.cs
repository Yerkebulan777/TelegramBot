namespace TelegramBot.Core.Helpers;

/// <summary>
/// Форматирует exit code процесса в читаемый вид.
/// </summary>
public static class ExitCodeFormatter
{
    /// <summary>
    /// Превращает голый exit code в читаемый вид: decimal + hex + имя известного NTSTATUS-краша.
    /// </summary>
    public static string Format(int? exitCode)
    {
        if (exitCode is not { } code)
        {
            return "n/a";
        }

        var name = code switch
        {
            unchecked((int)0xC0000005) => "ACCESS_VIOLATION",
            unchecked((int)0xC00000FD) => "STACK_OVERFLOW",
            unchecked((int)0xC0000135) => "DLL_NOT_FOUND",
            unchecked((int)0xC000013A) => "CONTROL_C_EXIT",
            unchecked((int)0xC0000409) => "STACK_BUFFER_OVERRUN",
            _ => null,
        };

        return name is null
            ? $"{code} (0x{code:X8})"
            : $"{code} (0x{code:X8}, {name})";
    }
}
