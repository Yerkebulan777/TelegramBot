namespace TelegramBotServer.Config;

/// <summary>
/// Centralized configuration for file system paths and patterns.
/// All path-related settings are defined here to avoid duplication.
/// </summary>
public sealed class FileSystemOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json
    /// </summary>
    public const string SectionName = "FileSystem";

    /// <summary>
    /// Root directory for the file browser.
    /// Example: "B:\\"
    /// </summary>
    public required string RootPath { get; set; }

    /// <summary>
    /// Relative path to RVT files within a section.
    /// Default: "01_RVT"
    /// </summary>
    public string RvtDirectoryName { get; set; } = "01_RVT";

    /// <summary>
    /// Relative path to project directory within a project root.
    /// Default: "01_PROJECT"
    /// </summary>
    public string ProjectDirectoryName { get; set; } = "01_PROJECT";

    /// <summary>
    /// File extension for Revit files (with dot).
    /// Default: ".rvt"
    /// </summary>
    public string RevitFileExtension { get; set; } = ".rvt";

    /// <summary>
    /// Regex pattern for matching section folders.
    /// Default: @"^(\d{2}|\d{3}|I{1,3})_"
    /// </summary>
    public string SectionFolderPattern { get; set; } = @"^(\d{2}|\d{3}|I{1,3})_";

    /// <summary>
    /// Regex pattern for matching Roman numeral III sections.
    /// Default: @"^III_"
    /// </summary>
    public string RomanThreePattern { get; set; } = @"^III_";

    // --- Computed properties for convenience ---

    /// <summary>
    /// Gets the full path to RVT directory for a given section.
    /// </summary>
    public string GetRvtPath(string sectionPath) => Path.Combine(sectionPath, RvtDirectoryName);

    /// <summary>
    /// Gets the full path to project directory for a given base path.
    /// </summary>
    public string GetProjectPath(string basePath) => Path.Combine(basePath, ProjectDirectoryName);

    /// <summary>
    /// Checks if a file is a Revit file based on extension.
    /// </summary>
    public bool IsRevitFile(string filePath)
        => filePath.EndsWith(RevitFileExtension, StringComparison.OrdinalIgnoreCase);
}
