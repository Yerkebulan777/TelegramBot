namespace TelegramBotServer.Models;

/// <summary>
/// Represents available command codes for export and automation operations.
/// </summary>
public enum CommandCode
{
    /// <summary>Export to PDF format.</summary>
    Pdf,
    /// <summary>Export to DWG format.</summary>
    Dwg,
    /// <summary>Export to NWC format.</summary>
    Nwc,
    /// <summary>Export to IFC format.</summary>
    Ifc,
    /// <summary>BIM Doctor automation.</summary>
    BimDoc,
    /// <summary>Clash Report automation.</summary>
    ClashRep,
    /// <summary>Auto Resolver automation.</summary>
    AutoRes
}

/// <summary>
/// Provides extension methods for <see cref="CommandCode"/>.
/// </summary>
public static class CommandCodeExtensions
{
    private static readonly Dictionary<CommandCode, (string Code, string DisplayName)> CommandInfo = new()
    {
        [CommandCode.Pdf] = ("PDF", "Export to PDF"),
        [CommandCode.Dwg] = ("DWG", "Export to DWG"),
        [CommandCode.Nwc] = ("NWC", "Export to NWC"),
        [CommandCode.Ifc] = ("IFC", "Export to IFC"),
        [CommandCode.BimDoc] = ("BIMDOC", "BIM Doctor"),
        [CommandCode.ClashRep] = ("CLASHREP", "Clash Report"),
        [CommandCode.AutoRes] = ("AUTORES", "Auto Resolver")
    };

    /// <summary>
    /// Gets the string code used in callbacks and database.
    /// </summary>
    public static string GetCode(this CommandCode command) => CommandInfo[command].Code;

    /// <summary>
    /// Gets the display name shown to users.
    /// </summary>
    public static string GetDisplayName(this CommandCode command) => CommandInfo[command].DisplayName;

    /// <summary>
    /// Determines if this is an export command (PDF, DWG, NWC, IFC).
    /// </summary>
    public static bool IsExportCommand(this CommandCode command) =>
        command is CommandCode.Pdf or CommandCode.Dwg or CommandCode.Nwc or CommandCode.Ifc;

    /// <summary>
    /// Determines if this is an automation command (BIMDOC, CLASHREP, AUTORES).
    /// </summary>
    public static bool IsAutomationCommand(this CommandCode command) =>
        command is CommandCode.BimDoc or CommandCode.ClashRep or CommandCode.AutoRes;

    /// <summary>
    /// Tries to parse a string code to <see cref="CommandCode"/>.
    /// </summary>
    public static bool TryParse(string? code, out CommandCode command)
    {
        command = default;
        if (string.IsNullOrEmpty(code)) return false;

        foreach (var kvp in CommandInfo)
        {
            if (kvp.Value.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
            {
                command = kvp.Key;
                return true;
            }
        }
        return false;
    }
}
