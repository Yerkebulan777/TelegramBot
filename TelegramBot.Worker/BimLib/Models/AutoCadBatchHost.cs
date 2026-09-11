namespace TelegramBot.BimLib.Models;

/// <summary>Совместимая пара acad.exe + установленный AutoBIMFusion.bundle.</summary>
public sealed record AutoCadBatchHost(string ExecutablePath, int Year, string PluginPath);
