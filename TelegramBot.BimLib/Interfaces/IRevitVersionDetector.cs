using TelegramBot.BimLib.Models;

namespace TelegramBot.BimLib.Interfaces;

/// <summary>Определяет версию Revit по заголовку .rvt/.rfa-файла.</summary>
public interface IRevitVersionDetector
{
    /// <summary>
    /// Анализирует OLE-заголовок .rvt/.rfa-файла и определяет версию Revit,
    /// в которой был создан файл.
    /// </summary>
    /// <param name="filePath">Путь к .rvt/.rfa-файлу.</param>
    /// <param name="ct">CancellationToken.</param>
    /// <returns>Информация о версии или null, если определить не удалось.</returns>
    Task<RevitDetectedVersion?> DetectVersionAsync(string filePath, CancellationToken ct = default);
}
