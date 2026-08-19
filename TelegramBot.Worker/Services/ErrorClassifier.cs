namespace TelegramBot.Worker.Services;

/// <summary>
/// Классифицирует ошибки выполнения команд на постоянные (не подлежащие retry)
/// и временные (процесс упал — можно повторить).
///
/// <list type="bullet">
///   <item><term>InvalidFileError (permanent)</term>
///     <description>Файл не найден, нет доступа, неверный формат — повторять бессмысленно,
///     файл не станет валидным. → сразу Failed.</description></item>
///   <item><term>ProcessCrashError (transient)</term>
///     <description>Процесс упал с неспецифичным кодом, временный сбой — стоит повторить.
///     → retry (существующая логика).</description></item>
/// </list>
/// </summary>
public static class ErrorClassifier
{
    // Паттерны сообщений об ошибках, указывающие на постоянную проблему с файлом
    private static readonly string[] PermanentFailurePatterns =
    [
        "not found",
        "no such file",
        "cannot open file",
        "access is denied",
        "access denied",
        "invalid file",
        "file does not exist",
        "permission denied",
        "path not found",
        "invalid file path",
        "unsupported command:",
        "notimplemented:",
        "no such directory",
        "cannot access",
        "файл не найден",
        "не удается найти указанный файл",
        "не удаётся найти указанный файл",
        "путь не найден",
        "отказано в доступе",
        "доступ запрещен",
        "доступ запрещён",
        "нет доступа",
        "недопустимый файл",
        "неверный формат файла",
        "невозможно открыть файл",
    ];

    /// <summary>
    /// Определяет, является ли ошибка постоянной (InvalidFileError).
    /// Постоянные ошибки не имеют смысла повторять — файл не станет валидным.
    /// Временные ошибки (ProcessCrashError) могут быть повторены.
    /// </summary>
    /// <param name="errorMessage">Текст ошибки.</param>
    /// <param name="exception">Исключение (если есть) для дополнительной классификации.</param>
    public static bool IsPermanentFailure(string errorMessage, Exception? exception = null)
    {
        // Проверка по типу исключения: файл не найден, нет доступа и т.п.
        if (exception is FileNotFoundException or DirectoryNotFoundException
            or UnauthorizedAccessException or PathTooLongException)
        {
            return true;
        }

        // Проверка по тексту ошибки: ищем характерные паттерны
        var message = errorMessage?.ToLowerInvariant() ?? string.Empty;
        return PermanentFailurePatterns.Any(message.Contains);
    }
}
