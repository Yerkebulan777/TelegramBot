namespace TelegramBot.Worker.BimLib.Config;

/// <summary>
/// Конфигурация автоматического закрытия диалоговых окон Revit/Navisworks.
/// </summary>
public sealed class DialogDismisserOptions
{
    /// <summary>Имя секции конфигурации в appsettings.json.</summary>
    public const string SectionName = "DialogDismisser";

    /// <summary>
    /// Включает автоматическое закрытие диалогов. false — DialogDismisser ничего не делает
    /// (для диагностики, чтобы проверить, не он ли сам убивает Revit-процессы).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Максимальное количество неудачных попыток закрыть диалог перед завершением процесса.
    /// Значение 0 отключает принудительное завершение.
    /// </summary>
    public int MaxDismissAttempts { get; set; } = 10;

    /// <summary>
    /// Известные заголовки диалоговых окон (поиск по Contains).
    /// Окна, заголовок которых содержит хотя бы один из этих паттернов, считаются диалогами.
    /// </summary>
    public string[] KnownDialogPatterns { get; set; } = [
        "Error",
        "Warning",
        "Information",
        "Ошибка",
        "Предупреждение",
        "Внимание"
    ];

    /// <summary>
    /// Текст кнопок, которые будут автоматически нажаты для закрытия диалога.
    /// </summary>
    public string[] CloseButtonTexts { get; set; } = [
        "OK", "ОК", "Принять", "Accept", "Закрыть", "Close",
        "Игнорировать", "Ignore", "Отмена", "Cancel", "Нет", "No",
        "Да", "Yes", "Продолжить", "Continue",
        "Не сохранять", "Don't Save", "Сохранить", "Save"
    ];

    /// <summary>
    /// Заголовки диалогов, которые НЕ нужно автоматически закрывать.
    /// </summary>
    public string[] ExclusionDialogTitles { get; set; } = [
        "Информация", "Information", "Справка", "Help"
    ];
}
