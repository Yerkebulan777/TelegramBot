namespace TelegramBotServer.Models;

/// <summary>
/// Determines how files are selected: individually, by section, or by project.
/// </summary>
public enum SelectionMode
{
    Files = 1,
    Sections = 2,
    Projects = 3
}
