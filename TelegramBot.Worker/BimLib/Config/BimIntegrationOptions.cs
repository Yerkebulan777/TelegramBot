namespace TelegramBot.BimLib.Config;

/// <summary>Диапазон поддерживаемых версий Revit и Navisworks.</summary>
public sealed class BimIntegrationOptions
{
    public const string SectionName = "BimIntegration";

    /// <summary>Минимальная поддерживаемая версия Revit (по умолчанию 2018).</summary>
    public int MinSupportedVersion { get; set; } = 2018;

    /// <summary>Максимальная поддерживаемая версия Revit (по умолчанию 2026).</summary>
    public int MaxSupportedVersion { get; set; } = 2026;
}
