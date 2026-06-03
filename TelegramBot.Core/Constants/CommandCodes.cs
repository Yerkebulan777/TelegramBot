namespace TelegramBot.Core.Constants;

/// <summary>Константы кодов команд, хранящихся в сессии пользователя.</summary>
public static class CommandCodes
{
    public const string Pdf = "PDF";
    public const string Dwg = "DWG";
    public const string Nwc = "NWC";
    public const string Ifc = "IFC";
    public const string BimDoc = "BIMDOC";
    public const string ClashRep = "CLASHREP";
    public const string AutoRes = "AUTORES";

    public static readonly IReadOnlySet<string> AutomationCodes =
        new HashSet<string>([BimDoc, ClashRep, AutoRes]);

    public static readonly IReadOnlySet<string> ExportCodes =
        new HashSet<string>([Pdf, Dwg, Nwc, Ifc]);
}
