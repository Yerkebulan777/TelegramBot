using Microsoft.Extensions.Options;
using System.Runtime.Versioning;
using TelegramBot.Worker.BimLib.Config;

namespace TelegramBot.Worker.BimLib.Services;

/// <summary>
/// Очистка папок экспорта от старых дублей: удаление + архивация.
/// Политика разделена на не старые (≤ N дней) и старые (> N дней) файлы.
/// Защита от частого запуска — маркерный файл в корневой папке экспорта.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ExportFolderCleanupService(
    IOptions<ExportFolderCleanupOptions> options,
    ILogger<ExportFolderCleanupService> logger)
{
    private readonly ExportFolderCleanupOptions _options = options.Value;
    private const string MarkerFileName = ".cleanup_marker";

    /// <summary>
    /// Выполняет очистку всех подпапок экспорта по настроенной политике.
    /// Безопасен для повторных вызовов — проверяет маркерный файл (один раз в день).
    /// </summary>
    /// <param name="baseExportDir">Корневая директория экспорта (родитель 03_PDF/, 02_DWG/ и т.д.).</param>
    public void CleanupExportDirs(string baseExportDir)
    {
        if (string.IsNullOrWhiteSpace(baseExportDir) || !Directory.Exists(baseExportDir))
        {
            return;
        }

        // 1. Защита от нагрузки: маркерный файл — один cleanup в день
        if (IsCleanupDoneToday(baseExportDir))
        {
            logger.LogDebug("Export folder cleanup already done today, skipping");
            return;
        }

        DateTime cutoffDate = DateTime.UtcNow - TimeSpan.FromDays(_options.OldFileThresholdDays);

        // 2. Обход каждой подпапки экспорта с фильтром по формату
        foreach (var entry in _options.ExportFolderMap)
        {
            string subfolder = entry.Key;
            string extension = entry.Value;
            string dir = Path.Combine(baseExportDir, subfolder);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            CleanupSingleFolder(dir, extension, baseExportDir, cutoffDate);
        }

        // 3. Запись маркера — сегодня чистили
        MarkCleanupDoneToday(baseExportDir);
        logger.LogInformation("Export folder cleanup completed for {BaseDir}", Path.GetFileName(baseExportDir));
    }

    private bool IsCleanupDoneToday(string baseExportDir)
    {
        string markerPath = Path.Combine(baseExportDir, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            string lastDate = File.ReadAllText(markerPath).Trim();
            return string.Equals(lastDate, DateTime.UtcNow.ToString("yyyy-MM-dd"), StringComparison.Ordinal);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to read cleanup marker: {Path}", markerPath);
            return false;
        }
    }

    private void MarkCleanupDoneToday(string baseExportDir)
    {
        string markerPath = Path.Combine(baseExportDir, MarkerFileName);
        try
        {
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to write cleanup marker: {Path}", markerPath);
        }
    }

    private void CleanupSingleFolder(string folderPath, string expectedExtension, string baseExportDir, DateTime cutoffDate)
    {
        FileInfo[] allFiles;
        try
        {
            allFiles = new DirectoryInfo(folderPath).GetFiles("*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Cannot access export subfolder: {Path}", folderPath);
            return;
        }

        if (allFiles.Length == 0)
        {
            return;
        }

        // Разделяем на профильные (соответствуют формату папки) и посторонние
        var formatFiles = new List<FileInfo>();
        foreach (FileInfo file in allFiles)
        {
            if (file.Extension.Equals(expectedExtension, StringComparison.OrdinalIgnoreCase))
            {
                formatFiles.Add(file);
            }
            else
            {
                SafeDelete(file);
            }
        }

        if (formatFiles.Count == 0)
        {
            return;
        }

        // Профильные: группировка по имени и полная политика
        var groups = formatFiles.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var sorted = group.OrderByDescending(f => f.LastWriteTimeUtc).ToList();

            // Определяем, есть ли среди файлов не старые (≤ N дней)
            FileInfo newest = sorted[0];
            bool newestIsNonOld = newest.LastWriteTimeUtc >= cutoffDate;

            // Не старые: политика для файлов ≤ N дней
            if (newestIsNonOld)
            {
                MoveToArchive(newest, baseExportDir);

                // Остальные дубли среди не старых → удалить
                foreach (FileInfo file in sorted.Skip(1))
                {
                    if (file.LastWriteTimeUtc >= cutoffDate)
                    {
                        SafeDelete(file);
                    }
                }
            }

            // Старые: политика для файлов > N дней
            var oldFiles = sorted.Where(f => f.LastWriteTimeUtc < cutoffDate).ToList();
            if (oldFiles.Count >= 2)
            {
                // oldFiles уже отсортирован по убыванию — унаследовано от sorted.
                // Keep last N — оставляем
                var candidates = oldFiles.Skip(_options.KeepLastCount).ToList();

                foreach (FileInfo file in candidates)
                {
                    if (file.Length < _options.ArchiveSizeThresholdBytes)
                    {
                        SafeDelete(file);
                    }
                    else
                    {
                        MoveToArchive(file, baseExportDir);
                    }
                }
            }
            // oldFiles.Count == 1: уникальный старый файл → не трогаем
        }
    }

    /// <summary>Перемещает файл в #_АРХИВ/{yyyy-MM-dd}/.</summary>
    private void MoveToArchive(FileInfo file, string baseExportDir)
    {
        try
        {
            string archiveDir = Path.Combine(baseExportDir, _options.ArchiveFolderName,
                file.LastWriteTimeUtc.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(archiveDir);

            string destPath = Path.Combine(archiveDir, file.Name);

            if (File.Exists(destPath))
            {
                File.Delete(destPath);
            }
            File.Move(file.FullName, destPath);

            logger.LogInformation("Archived: {File} → {Dest}", file.Name, Path.GetFileName(destPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to archive file: {Path}", file.FullName);
        }
    }

    /// <summary>Удаляет файл. Сбой логируется как warning, исключение не пробрасывается.</summary>
    private void SafeDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            logger.LogInformation("Deleted duplicate: {File} ({Size} bytes, modified {Modified})",
                file.Name, file.Length, file.LastWriteTimeUtc.ToString("yyyy-MM-dd"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to delete file: {Path}", file.FullName);
        }
    }
}
