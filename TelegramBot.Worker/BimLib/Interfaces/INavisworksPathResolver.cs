namespace TelegramBot.Worker.BimLib.Interfaces;

/// <summary>Определяет путь к Navisworks.exe/FileConvert.exe по версии.</summary>
public interface INavisworksPathResolver
{
    /// <summary>Возвращает список установленных версий Navisworks (отсортированный по убыванию).</summary>
    IReadOnlyList<int> GetInstalledVersions();

    /// <summary>Возвращает путь к Navisworks.exe для указанной версии, или null если не установлен.</summary>
    string? ResolveNavisworksPath(int versionYear);

    /// <summary>Возвращает путь к FileConvert.exe для указанной версии, или null если не установлен.</summary>
    string? ResolveFileConvertPath(int versionYear);
}
