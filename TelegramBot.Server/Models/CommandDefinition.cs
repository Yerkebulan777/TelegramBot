using TelegramBot.Core.Constants;
using TelegramBot.Core.Models;

namespace TelegramBot.Server.Models;

public enum CommandGroup
{
    Export,
    Automation
}

public readonly record struct CommandDefinition(
    string Code,
    string DisplayName,
    string CallbackPrefix,
    CommandGroup Group);

public static class CommandCatalog
{
    public static readonly IReadOnlyList<CommandDefinition> All =
    [
        new(CommandCodes.Pdf, "Export to PDF", CallbackPrefixes.Pdf, CommandGroup.Export),
        new(CommandCodes.Dwg, "Export to DWG", CallbackPrefixes.Dwg, CommandGroup.Export),
        new(CommandCodes.Nwc, "Export to NWC", CallbackPrefixes.Nwc, CommandGroup.Export),
        new(CommandCodes.Ifc, "Export to IFC", CallbackPrefixes.Ifc, CommandGroup.Export),
        new(CommandCodes.BimDoc, "BIM Doctor", CallbackPrefixes.BimDoc, CommandGroup.Automation),
        new(CommandCodes.ClashRep, "Clash Report", CallbackPrefixes.ClashRep, CommandGroup.Automation),
        new(CommandCodes.AutoRes, "Auto Resolver", CallbackPrefixes.AutoRes, CommandGroup.Automation)
    ];

    private static readonly IReadOnlyDictionary<string, CommandDefinition> _byPrefix =
        All.ToDictionary(command => command.CallbackPrefix);

    public static IEnumerable<CommandDefinition> GetByGroup(CommandGroup group)
    {
        return All.Where(command => command.Group == group);
    }

    public static bool TryGetByCallbackPrefix(string prefix, out CommandDefinition definition)
    {
        return _byPrefix.TryGetValue(prefix, out definition);
    }
}
