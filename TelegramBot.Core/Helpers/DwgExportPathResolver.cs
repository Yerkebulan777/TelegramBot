namespace TelegramBot.Core.Helpers;

/// <summary>
/// Вычисляет папку экспорта DWG из пути RVT по тем же правилам, что DrawingExportModule
/// (<c>DirectoryLocator</c> + <c>ExportRequest</c> для ExportType.Dwg):
/// <c>{Base}/02_DWG/{relative?}/{RevitFileName}/</c>.
/// </summary>
public static class DwgExportPathResolver
{
    private const string DwgExportFolderName = "02_DWG";
    private const string RvtContainerSuffix = "_RVT";

    private static readonly string[] SectionSearchKeys =
    [
        "_AR", "_AS", "_APT",
        "_KJ", "_KR", "_KG",
        "_EOM", "_EM", "_PS", "_SS",
        "_OV", "_VK", "_OViK", "_OVIK",
        "_BIM"
    ];

    /// <summary>Канонический путь папки DWG-экспорта. Существование папки не проверяется.</summary>
    public static string ResolveExportDirectory(string revitFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revitFilePath);

        var revitFileName = Path.GetFileNameWithoutExtension(revitFilePath);
        if (string.IsNullOrWhiteSpace(revitFileName))
        {
            throw new ArgumentException("Revit file path has no file name.", nameof(revitFilePath));
        }

        var baseDirectory = FindBaseDirectory(revitFilePath, out var subfolder);

        return subfolder is null
            ? Path.Combine(baseDirectory, DwgExportFolderName, revitFileName)
            : Path.Combine(baseDirectory, DwgExportFolderName, subfolder, revitFileName);
    }

    /// <summary>Есть ли в папке хотя бы один <c>*.dwg</c> (рекурсивно).</summary>
    public static bool ContainsDwgFiles(string directoryPath) =>
        Directory.EnumerateFiles(directoryPath, "*.dwg", SearchOption.AllDirectories).Any();

    /// <summary>
    /// Подъём вверх до папки раздела; <c>subfolder</c> — первая подпапка внутри контейнера <c>*_RVT</c>.
    /// </summary>
    private static string FindBaseDirectory(string revitFilePath, out string? subfolder)
    {
        subfolder = null;

        if (Path.GetDirectoryName(revitFilePath) is not string directoryPath)
        {
            throw new ArgumentException("Revit file path has no directory.", nameof(revitFilePath));
        }

        var fileDirectory = new DirectoryInfo(directoryPath);
        DirectoryInfo? folderBelowSection = null;
        DirectoryInfo? folderBelowRvt = null;

        for (DirectoryInfo? directory = fileDirectory; directory is not null; directory = directory.Parent)
        {
            if (IsSectionDirectory(directory.Name))
            {
                if (folderBelowSection?.Name.EndsWith(RvtContainerSuffix, StringComparison.OrdinalIgnoreCase) == true)
                {
                    subfolder = folderBelowRvt?.Name;
                }

                return directory.FullName;
            }

            folderBelowRvt = folderBelowSection;
            folderBelowSection = directory;
        }

        return fileDirectory.FullName;
    }

    private static bool IsSectionDirectory(string directoryName) =>
        SectionSearchKeys.Any(key => directoryName.EndsWith(key, StringComparison.OrdinalIgnoreCase));
}
