namespace TelegramBot.Core.Models;

/// <summary>
/// Состояние и переходы потока выбора задания (selection flow): выбор команд → проект →
/// разделы → файлы → подтверждение. Чистый модуль: не знает про Telegram, БД и файловую
/// систему; токены путей разрешает вызывающий. Логика уровней навигации реализована
/// здесь один раз. Переходы вызываются под per-user блокировкой (ADR-007).
/// </summary>
public sealed class SelectionFlow
{
    /// <summary>Уровень навигации в дереве RootPath → 01_PROJECT → разделы.</summary>
    public enum Level
    {
        /// <summary>Список проектов (текущий путь — корень).</summary>
        Project,

        /// <summary>Список разделов внутри 01_PROJECT.</summary>
        Sections,

        /// <summary>Список файлов внутри раздела.</summary>
        Files
    }

    /// <summary>Итог перехода <see cref="Confirm"/>.</summary>
    public enum ConfirmOutcomeKind
    {
        /// <summary>На уровне проектов ничего не выбрано — отправка заблокирована.</summary>
        BlockedNoProject,

        /// <summary>Файлы не выбраны — отправка заблокирована.</summary>
        BlockedNoFiles,

        /// <summary>Переход глубже: выбранный проект открыт как 01_PROJECT.</summary>
        Advanced,

        /// <summary>Сформирована заявка на отправку в очередь.</summary>
        ReadyToSubmit
    }

    /// <summary>Снимок заявки: коды команд × пути файлов. Модуль в БД не пишет.</summary>
    public sealed record JobSubmission(IReadOnlyList<string> Commands, IReadOnlyList<string> Files);

    /// <summary>Результат перехода подтверждения.</summary>
    public sealed record ConfirmOutcome(ConfirmOutcomeKind Kind, JobSubmission? Submission = null);

    private readonly HashSet<string> _selectedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pendingCommands = [];
    private readonly string _projectDirectoryName;
    private readonly string _rvtDirectoryName;
    private string _rootPath;

    public SelectionFlow(string? rootPath = null, string projectDirectoryName = "01_PROJECT", string rvtDirectoryName = "01_RVT")
    {
        _rootPath = rootPath ?? Directory.GetCurrentDirectory();
        _projectDirectoryName = projectDirectoryName;
        _rvtDirectoryName = rvtDirectoryName;
        CurrentPath = _rootPath;
    }

    /// <summary>Текущая папка навигации.</summary>
    public string CurrentPath { get; private set; }

    /// <summary>Идёт ли выбор файлов (после применения команд).</summary>
    public bool IsFileSelectionActive { get; private set; }

    /// <summary>Коды выбранных команд в порядке выбора.</summary>
    public IReadOnlyList<string> PendingCommands => _pendingCommands;

    /// <summary>Выбранные файлы.</summary>
    public IReadOnlySet<string> SelectedFiles => _selectedFiles;

    /// <summary>Текущий уровень навигации.</summary>
    public Level CurrentLevel => GetLevel(CurrentPath, _rootPath, _projectDirectoryName);

    /// <summary>Определяет уровень навигации для произвольного пути.</summary>
    public static Level GetLevel(string path, string rootPath, string projectDirectoryName)
    {
        var normalized = Normalize(path);
        if (normalized == Normalize(rootPath))
        {
            return Level.Project;
        }

        if (string.Equals(Path.GetFileName(normalized), projectDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            return Level.Sections;
        }

        // Check if current path is inside projectDirectoryName (including deeper subfolders)
        var current = normalized;
        while (current != null && current != Normalize(rootPath))
        {
            var folderName = Path.GetFileName(current);
            if (string.Equals(folderName, projectDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return Level.Files;
            }

            current = Path.GetDirectoryName(current);
        }

        return Level.Project;
    }

    // ────────────────────────── Команды ──────────────────────────

    /// <summary>Переключает выбор команды. True — команда теперь выбрана.</summary>
    public bool ToggleCommand(string code)
    {
        if (_pendingCommands.Remove(code))
        {
            return false;
        }

        _pendingCommands.Add(code);
        return true;
    }

    /// <summary>Снимает выбор всех команд.</summary>
    public void ClearCommands()
    {
        _pendingCommands.Clear();
    }

    /// <summary>Применяет выбранные команды и начинает выбор файлов с корня. False — команд нет.</summary>
    public bool ApplyCommands()
    {
        if (_pendingCommands.Count == 0)
        {
            return false;
        }

        CurrentPath = _rootPath;
        IsFileSelectionActive = true;
        return true;
    }

    // ────────────────────────── Навигация ──────────────────────────

    /// <summary>
    /// Открывает папку по уже разрешённому пути. С уровня проектов — одиночный выбор:
    /// предыдущий выбор сбрасывается, путь углубляется в 01_PROJECT.
    /// </summary>
    public void OpenFolder(string newPath)
    {
        if (CurrentLevel == Level.Project)
        {
            _selectedFiles.Clear();
            newPath = Path.Combine(newPath, _projectDirectoryName);
        }

        CurrentPath = newPath;
    }

    /// <summary>Шаг назад: с уровня файлов — в папку раздела (или вверх из подпапки 01_RVT), иначе — на корень.</summary>
    public void GoBack()
    {
        if (CurrentLevel == Level.Files)
        {
            var parent = Path.GetDirectoryName(CurrentPath) ?? _rootPath;
            var parentFileName = Path.GetFileName(parent);

            // If parent is a RVT folder (like 01_RVT), go up one more level to get the section folder
            if (string.Equals(parentFileName, _rvtDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                parent = Path.GetDirectoryName(parent) ?? _rootPath;
            }

            CurrentPath = parent;
        }
        else
        {
            CurrentPath = _rootPath;
        }
    }

    /// <summary>Переход по явному пути (GOTOPARENT): выбор файлов сбрасывается.</summary>
    public void NavigateTo(string path)
    {
        _selectedFiles.Clear();
        CurrentPath = path;
    }

    /// <summary>Возврат на корень при недопустимом пути навигации (выбор сохраняется).</summary>
    public void ResetPathToRoot()
    {
        CurrentPath = _rootPath;
    }

    // ────────────────────────── Файлы ──────────────────────────

    /// <summary>
    /// Переключает выбор файла. На уровне проектов — одиночный выбор (сброс + выбор),
    /// на уровнях разделов/файлов — обычный тоггл. True — файл выбран.
    /// </summary>
    public bool ToggleFile(string filePath)
    {
        if (CurrentLevel == Level.Project)
        {
            _selectedFiles.Clear();
            _selectedFiles.Add(filePath);
            return true;
        }

        if (_selectedFiles.Contains(filePath))
        {
            _ = _selectedFiles.Remove(filePath);
            return false;
        }

        _selectedFiles.Add(filePath);
        return true;
    }

    /// <summary>Добавляет файлы к выбору («Выбрать все»).</summary>
    public void AddFiles(IEnumerable<string> filePaths)
    {
        foreach (var filePath in filePaths)
        {
            _ = _selectedFiles.Add(filePath);
        }
    }

    // ────────────────────────── Подтверждение ──────────────────────────

    /// <summary>
    /// Кнопка подтверждения. На уровне проектов — продвижение в 01_PROJECT выбранного
    /// проекта; на уровнях разделов/файлов — снятие активности выбора файлов и заявка
    /// на отправку (снимок команд × файлов). Запись в БД выполняет вызывающий.
    /// </summary>
    public ConfirmOutcome Confirm()
    {
        if (CurrentLevel == Level.Project)
        {
            var selectedProject = _selectedFiles.FirstOrDefault();
            if (selectedProject == null)
            {
                return new ConfirmOutcome(ConfirmOutcomeKind.BlockedNoProject);
            }

            CurrentPath = Path.Combine(selectedProject, _projectDirectoryName);
            _selectedFiles.Clear();
            return new ConfirmOutcome(ConfirmOutcomeKind.Advanced);
        }

        IsFileSelectionActive = false;

        if (_selectedFiles.Count == 0)
        {
            return new ConfirmOutcome(ConfirmOutcomeKind.BlockedNoFiles);
        }

        return new ConfirmOutcome(
            ConfirmOutcomeKind.ReadyToSubmit,
            new JobSubmission(_pendingCommands.ToArray(), _selectedFiles.ToArray()));
    }

    /// <summary>Завершает выбор файлов (например, сообщение выбора не найдено).</summary>
    public void StopFileSelection()
    {
        IsFileSelectionActive = false;
    }

    // ────────────────────────── Сброс ──────────────────────────

    /// <summary>Полный сброс потока выбора: команды, файлы, путь, активность.</summary>
    public void Reset(string? rootPath = null)
    {
        if (rootPath != null)
        {
            _rootPath = Normalize(rootPath);
        }

        _pendingCommands.Clear();
        _selectedFiles.Clear();
        CurrentPath = _rootPath;
        IsFileSelectionActive = false;
    }

    private static string Normalize(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
