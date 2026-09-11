using TelegramBot.Core.Constants;

namespace TelegramBot.Core.Models;

/// <summary>
/// Central execution traits of command codes shared by queueing and Worker process handling.
/// </summary>
public static class CommandTraits
{
    public static bool RequiresRevit(string commandCode) => commandCode.ToUpperInvariant() is
        CommandCodes.Pdf or CommandCodes.Dwg or CommandCodes.Nwc or CommandCodes.Data or CommandCodes.Ifc or CommandCodes.Resave;

    public static ProcessLaunchGateKind GetLaunchGate(string commandCode) =>
        RequiresRevit(commandCode) ? ProcessLaunchGateKind.Revit
        : commandCode.ToUpperInvariant() is CommandCodes.MergeDwg ? ProcessLaunchGateKind.AutoCad
        : ProcessLaunchGateKind.None;

    public static int GetPriority(string commandCode) => commandCode.ToUpperInvariant() switch
    {
        CommandCodes.Pdf or CommandCodes.Dwg => CommandPriorities.Critical,
        CommandCodes.Nwc => CommandPriorities.High,
        CommandCodes.Ifc or CommandCodes.Resave => CommandPriorities.Medium,
        CommandCodes.Data => CommandPriorities.Low,
        _ => CommandPriorities.Default,
    };
}
