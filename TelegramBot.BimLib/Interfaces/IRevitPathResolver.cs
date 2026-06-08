namespace TelegramBot.BimLib.Interfaces;

/// <summary>Определяет путь к Revit.exe по версии и проверяет доступные установки.</summary>
public interface IRevitPathResolver
{
    /// <summary>Возвращает список установленных версий Revit (отсортированный по убыванию).</summary>
    IReadOnlyList<int> GetInstalledVersions();

    /// <summary>Возвращает путь к Revit.exe для указанной версии, или null если не установлен.</summary>
    string? ResolveExecutablePath(int versionYear);
}
