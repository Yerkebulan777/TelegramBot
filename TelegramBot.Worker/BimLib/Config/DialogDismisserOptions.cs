namespace TelegramBot.Worker.BimLib.Config;

/// <summary>
/// Конфигурация автоматического закрытия диалоговых окон Revit/Navisworks.
/// </summary>
public sealed class DialogDismisserOptions
{
    /// <summary>Имя секции конфигурации в appsettings.json.</summary>
    public const string SectionName = "DialogDismisser";

    /// <summary>
    /// Максимальное количество неудачных попыток закрыть диалог перед завершением процесса.
    /// Значение 0 отключает принудительное завершение.
    /// </summary>
    public int MaxDismissAttempts { get; set; } = 10;

    /// <summary>
    /// Текст кнопок, которые будут автоматически нажаты для закрытия диалога, в порядке приоритета.
    /// </summary>
    public string[] CloseButtonTexts { get; set; } = [
        "OK", "ОК", "Close", "Закрыть",
        "Do not save the project", "Don't Save", "Не сохранять проект", "Не сохранять",
        "No", "Нет",
        "Always Load", "Всегда загружать",
        "Ignore and open the project", "Ignore", "Игнорировать и открыть проект", "Игнорировать",
        "Relinquish all elements and worksets", "Relinquish elements and worksets",
        "Освободить все элементы и рабочие наборы", "Освободить элементы и рабочие наборы",
        "Accept", "Принять",
        "Continue", "Продолжить",
        "Cancel", "Отмена"
    ];

    /// <summary>
    /// Заголовки диалогов, которые НЕ нужно автоматически закрывать.
    /// </summary>
    public string[] ExclusionDialogTitles { get; set; } = [
        "Model Upgrade", "Обновление модели",
        "Load Link", "Загрузка связи"
    ];
}
