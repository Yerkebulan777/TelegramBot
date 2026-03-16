namespace TelegramBotServer.Services;

public static class CallbackPrefixes
{
    public const string Nav1 = "NAV1:";
    public const string Nav2 = "NAV2:";
    public const string File = "FILE:";
    public const string SelectionMode = "SELMODE:";
    public const string ApplyFiles = "APPLYFILES:";
    public const string CancelSelection = "CANCELSEL:";
    public const string CancelFileSelection = "CANCELFILESEL:";
    public const string Pdf = "PDF:";
    public const string Dwg = "DWG:";
    public const string Nwc = "NWC:";
    public const string Ifc = "IFC:";
    public const string ApplyCommands = "APPLYCOMMANDS:";
    public const string CancelCommandSelection = "CANCELCOMMANDSSEL:";
    public const string SessionDetails = "Sessiondetails:";
    public const string DeleteSession = "Deletesession:";
    public const string DeleteCommand = "Deletecommand:";
    public const string BackToStatus = "Backtostatus:";
    public const string BimDoc = "BIMDOC:";
    public const string ClashRep = "CLASHREP:";
    public const string AutoRes = "AUTORES:";
}

public readonly record struct ParsedCallback(string Prefix, string Argument)
{
    public bool Is(string prefix)
    {
        return string.Equals(Prefix, prefix, StringComparison.Ordinal);
    }

    public bool IsAny(string prefix1, string prefix2)
    {
        return Is(prefix1) || Is(prefix2);
    }
}

public static class CallbackDataParser
{
    public static ParsedCallback Parse(string callbackData)
    {
        if (string.IsNullOrEmpty(callbackData))
        {
            return new ParsedCallback(string.Empty, string.Empty);
        }

        int delimiterIndex = callbackData.IndexOf(':');
        if (delimiterIndex < 0)
        {
            return new ParsedCallback(callbackData, string.Empty);
        }

        var prefix = callbackData[..(delimiterIndex + 1)];
        var argument = delimiterIndex + 1 < callbackData.Length
            ? callbackData[(delimiterIndex + 1)..]
            : string.Empty;

        return new ParsedCallback(prefix, argument);
    }
}
