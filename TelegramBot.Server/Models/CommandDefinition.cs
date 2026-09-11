using TelegramBot.Core.Constants;

namespace TelegramBot.Server.Models;

public enum CommandGroup
{
    Export,
    Automation
}

public readonly record struct CommandDefinition(
    string Code,
    string Name,
    string Prefix,
    CommandGroup Group);

public static class CommandCatalog
{
    public static readonly IReadOnlyList<CommandDefinition> All =
    [
        new(CommandCodes.Pdf, "Export to PDF", CallbackPrefixes.Pdf, CommandGroup.Export),
        new(CommandCodes.Dwg, "Export to DWG", CallbackPrefixes.Dwg, CommandGroup.Export),
        new(CommandCodes.Nwc, "Export to NWC", CallbackPrefixes.Nwc, CommandGroup.Export),
        new(CommandCodes.Ifc, "Export to IFC", CallbackPrefixes.Ifc, CommandGroup.Export),
        new(CommandCodes.Data, "Export DATA", CallbackPrefixes.Data, CommandGroup.Export),
        new(CommandCodes.ClashRep, "Clash Report", CallbackPrefixes.ClashRep, CommandGroup.Automation), // FileConvert пока; planned — Navisworks AddIn
        new(CommandCodes.Resave, "Resave RVT", CallbackPrefixes.Resave, CommandGroup.Automation),
        new(CommandCodes.MergeDwg, "Merge DWG", CallbackPrefixes.MergeDwg, CommandGroup.Automation),
    ];

    private static readonly IReadOnlyDictionary<string, CommandDefinition> _byPrefix =
        All.ToDictionary(command => command.Prefix);

    public static IEnumerable<CommandDefinition> GetByGroup(CommandGroup group)
    {
        return All.Where(command => command.Group == group);
    }

    public static bool TryGetByPrefix(string prefix, out CommandDefinition definition)
    {
        return _byPrefix.TryGetValue(prefix, out definition);
    }
}
