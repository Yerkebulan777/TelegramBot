namespace TelegramBot.Core.Models;

/// <summary>Константы префиксов для callback-кнопок.</summary>
public static class CallbackPrefixes
{
    public const string OpenFolder = "OPENFOLDER:";
    public const string GoToParent = "GOTOPARENT:";
    public const string File = "FILE:";
    public const string ApplyFiles = "APPLYFILES:";
    public const string CancelFileSelection = "CANCELFILESEL:";
    public const string Pdf = "PDF:";
    public const string Dwg = "DWG:";
    public const string Nwc = "NWC:";
    public const string Ifc = "IFC:";
    public const string BimDoc = "BIMDOC:";
    public const string ClashRep = "CLASHREP:";
    public const string AutoRes = "AUTORES:";
    public const string ApplyCommands = "APPLYCOMMANDS:";
    public const string CancelCommandSelection = "CANCELCOMMANDSSEL:";
    public const string SessionDetails = "SESSIONDETAILS:";
    public const string DeleteSession = "DELETESESSION:";
    public const string DeleteCommand = "DELETECOMMAND:";
    public const string BackToStatus = "BACKTOSTATUS:";
    public const string RequestAccess = "REQACCESS:";
    public const string ApproveUser = "APPROVEUSER:";
    public const string RejectUser = "REJECTUSER:";
}

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
