namespace TelegramBot.Core.Constants;

/// <summary>
/// Константы префиксов для callback-кнопок.
/// </summary>
public static class CallbackPrefixes
{
    /// <summary>Переход в родительскую папку.</summary>
    public const string GoToParent = "GOTOPARENT:";

    /// <summary>Навигация по файлам.</summary>
    public const string File = "FILE:";

    /// <summary>Выбор файла PDF.</summary>
    public const string Pdf = "PDF:";

    /// <summary>Выбор файла DWG.</summary>
    public const string Dwg = "DWG:";

    /// <summary>Выбор файла NWC.</summary>
    public const string Nwc = "NWC:";

    /// <summary>Выбор файла IFC.</summary>
    public const string Ifc = "IFC:";

    /// <summary>Выбор файла BIMDOC.</summary>
    public const string BimDoc = "BIMDOC:";

    /// <summary>Выбор файла CLASHREP.</summary>
    public const string ClashRep = "CLASHREP:";

    /// <summary>Выбор файла AUTORES.</summary>
    public const string AutoRes = "AUTORES:";

    /// <summary>Применение выбранных команд.</summary>
    public const string ApplyCommands = "APPLYCOMMANDS:";

    /// <summary>Отмена выбора команд.</summary>
    public const string CancelCommandSelection = "CANCELCOMMANDSSEL:";

    /// <summary>Детали сессии.</summary>
    public const string SessionDetails = "SESSIONDETAILS:";

    /// <summary>Удаление сессии.</summary>
    public const string DeleteSession = "DELETESESSION:";

    /// <summary>Удаление команды.</summary>
    public const string DeleteCommand = "DELETECOMMAND:";

    /// <summary>Подтверждение удаления сессии.</summary>
    public const string ConfirmDeleteSession = "CONFIRMDELETESESSION:";

    /// <summary>Подтверждение удаления команды.</summary>
    public const string ConfirmDeleteCommand = "CONFIRMDELETECOMMAND:";

    /// <summary>Удаление сессии по типу.</summary>
    public const string DeleteSessionByType = "DELETESESSIONBYTYPE:";

    /// <summary>Выбрать все папки разделов.</summary>
    public const string SelectAllSectionFolders = "SELECTALLSECTIONS:";

    /// <summary>Подтверждение удаления сессии по типу.</summary>
    public const string ConfirmDeleteSessionByType = "CONFIRMDELETESESSIONBYTYPE:";

    /// <summary>Запрос доступа.</summary>
    public const string RequestAccess = "REQACCESS:";

    /// <summary>Одобрить пользователя.</summary>
    public const string ApproveUser = "APPROVEUSER:";

    /// <summary>Отклонить пользователя.</summary>
    public const string RejectUser = "REJECTUSER:";
}
