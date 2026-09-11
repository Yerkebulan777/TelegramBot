namespace TelegramBot.Core.Constants;

/// <summary>
/// Константы префиксов для callback-кнопок.
/// </summary>
public static class CallbackPrefixes
{
    /// <summary>Навигация по файлам.</summary>
    public const string File = "FILE:";

    /// <summary>Выбор файла PDF.</summary>
    public const string Pdf = "PDF:";

    /// <summary>Выбор файла DWG.</summary>
    public const string Dwg = "DWG:";

    /// <summary>Выбор файла NWC.</summary>
    public const string Nwc = "NWC:";

    /// <summary>Экспорт данных модели.</summary>
    public const string Data = "DATA:";

    /// <summary>Выбор файла IFC.</summary>
    public const string Ifc = "IFC:";

    /// <summary>Выбор файла CLASHREP (сейчас FileConvert.exe; planned — Navisworks AddIn).</summary>
    public const string ClashRep = "CLASHREP:";

    /// <summary>Пересохранение RVT с аудитом и отсоединением от центральной модели.</summary>
    public const string Resave = "RESAVE:";

    /// <summary>Сборка общего DWG через AutoCAD (MERGEDWG_BATCH) по папке экспорта выбранного RVT.</summary>
    public const string MergeDwg = "MERGEDWG:";

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

    /// <summary>Навигация внутрь раздела (список файлов) или назад, без сброса выбора.</summary>
    public const string OpenFolder = "OPENFOLDER:";

    /// <summary>Подтверждение выбранных RVT-файлов.</summary>
    public const string ConfirmFileSelection = "CONFIRMFILESEL:";

    /// <summary>Отмена выбора RVT-файлов.</summary>
    public const string CancelFileSelection = "CANCELFILESEL:";

    /// <summary>Подтверждение удаления сессии по типу.</summary>
    public const string ConfirmDeleteSessionByType = "CONFIRMDELETESESSIONBYTYPE:";

    /// <summary>Фильтр статуса в /status.</summary>
    public const string StatusFilter = "STATUSFILTER:";

    /// <summary>Постраничная навигация в /status.</summary>
    public const string StatusPage = "STATUSPAGE:";

    /// <summary>Постраничная навигация по командам сессии. Аргумент: "sessionId:filter:page".</summary>
    public const string CommandsPage = "CMDPAGE:";

    /// <summary>Повторный запуск команды. Аргумент: "commandId:filter".</summary>
    public const string RerunCommand = "RERUNCMD:";

    /// <summary>Изменение общего корневого UNC-пути.</summary>
    public const string RootPath = "ROOTPATH:";

    /// <summary>Подтверждение заявки, подготовленной локальной утилитой.</summary>
    public const string ApplyPendingRootPath = "APPLYROOTPATH:";

    /// <summary>Отмена заявки, подготовленной локальной утилитой.</summary>
    public const string CancelPendingRootPath = "CANCELROOTPATH:";
}
