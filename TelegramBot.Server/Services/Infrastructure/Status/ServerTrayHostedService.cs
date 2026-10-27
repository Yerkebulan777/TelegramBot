using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TelegramBot.Core.Helpers;
using WinFormsApp = System.Windows.Forms.Application;

namespace TelegramBot.Server.Services.Infrastructure.Status;

/// <summary>
/// Иконка в системном трее на STA-потоке. Показывает, что Server запущен,
/// и доступны ли PostgreSQL и Telegram.
/// </summary>
public sealed class ServerTrayHostedService(
    ServerHealthMonitor monitor,
    ServerHealthCheckService healthCheck,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<ServerTrayHostedService> logger) : IHostedService
{
    private ServerTrayApplicationContext? _context;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Environment.UserInteractive)
        {
            logger.LogInformation("Tray skipped: session is not interactive");
            return Task.CompletedTask;
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logDirectory = Path.Combine(SerilogSetup.GetConfiguredLogBasePath(configuration), "Server");

        var uiThread = new Thread(() =>
        {
            try
            {
                WinFormsApp.SetHighDpiMode(HighDpiMode.SystemAware);
                WinFormsApp.EnableVisualStyles();
                WinFormsApp.SetCompatibleTextRenderingDefault(false);
                _context = new ServerTrayApplicationContext(
                    monitor, healthCheck, logDirectory, lifetime.ApplicationStopping, logger);
                ready.TrySetResult();
                WinFormsApp.Run(_context);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tray UI failed");
                ready.TrySetResult();
            }
        })
        {
            Name = "ServerTray",
            IsBackground = true
        };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        return ready.Task;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var context = _context;
        context?.Post(context.ExitThread);
        return Task.CompletedTask;
    }
}

internal sealed class ServerTrayApplicationContext : ApplicationContext
{
    private const string ServerTaskName = "TelegramBotServer";
    private const string WorkerTaskName = "TelegramBotWorker";
    private const string WorkerProcessName = "TelegramBot.Worker";

    private readonly ServerHealthMonitor _monitor;
    private readonly ServerHealthCheckService _healthCheck;
    private readonly string _logDirectory;
    private readonly CancellationToken _stoppingToken;
    private readonly ILogger _logger;
    private readonly Control _syncControl = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _serverItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _databaseItem = new();
    private readonly ToolStripMenuItem _telegramItem = new();
    private readonly Icon _startingIcon = TrayStatusIcons.Create(Color.FromArgb(158, 158, 158));
    private readonly Icon _healthyIcon = TrayStatusIcons.Create(Color.FromArgb(46, 160, 67));
    private readonly Icon _degradedIcon = TrayStatusIcons.Create(Color.FromArgb(227, 160, 8));
    private readonly Icon _downIcon = TrayStatusIcons.Create(Color.FromArgb(218, 54, 51));
    private TrayHealthKind _lastKind = TrayHealthKind.Starting;

    public ServerTrayApplicationContext(
        ServerHealthMonitor monitor,
        ServerHealthCheckService healthCheck,
        string logDirectory,
        CancellationToken stoppingToken,
        ILogger logger)
    {
        _monitor = monitor;
        _healthCheck = healthCheck;
        _logDirectory = logDirectory;
        _stoppingToken = stoppingToken;
        _logger = logger;
        _ = _syncControl.Handle;

        var menu = new ContextMenuStrip();
        _databaseItem.Click += (_, _) => ShowStatusBalloon();
        _telegramItem.Click += (_, _) => ShowStatusBalloon();
        var refreshItem = new ToolStripMenuItem("Проверить сейчас");
        refreshItem.Click += (_, _) => OnRefreshClicked();
        var logsItem = new ToolStripMenuItem("Открыть логи");
        logsItem.Click += (_, _) => OpenLogs();
        var restartServerItem = new ToolStripMenuItem("Перезапустить Server");
        restartServerItem.Click += (_, _) => RestartServer();
        var restartWorkerItem = new ToolStripMenuItem("Перезапустить Worker");
        restartWorkerItem.Click += (_, _) => RestartWorker();
        menu.Items.Add(_serverItem);
        menu.Items.Add(_databaseItem);
        menu.Items.Add(_telegramItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(refreshItem);
        menu.Items.Add(logsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(restartServerItem);
        menu.Items.Add(restartWorkerItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _startingIcon,
            Text = "TelegramBot: запуск…",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowStatusBalloon();
        _monitor.Changed += OnHealthChanged;
        ApplySnapshot(_monitor.Snapshot);
    }

    public void Post(Action action)
    {
        if (_syncControl.IsHandleCreated && _syncControl.InvokeRequired)
        {
            _ = _syncControl.BeginInvoke(action);
            return;
        }

        action();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitor.Changed -= OnHealthChanged;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _syncControl.Dispose();
            _startingIcon.Dispose();
            _healthyIcon.Dispose();
            _degradedIcon.Dispose();
            _downIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnHealthChanged(ServerHealthSnapshot snapshot) => Post(() => ApplySnapshot(snapshot));

    private void ApplySnapshot(ServerHealthSnapshot snapshot)
    {
        _serverItem.Text = "Сервер: работает";
        _databaseItem.Text = FormatLine("БД", snapshot.Database, "доступна", "недоступна");
        _telegramItem.Text = FormatLine("Telegram", snapshot.Telegram, "доступен", "недоступен");
        _notifyIcon.Icon = snapshot.Kind switch
        {
            TrayHealthKind.Healthy => _healthyIcon,
            TrayHealthKind.Degraded => _degradedIcon,
            TrayHealthKind.Down => _downIcon,
            _ => _startingIcon
        };
        _notifyIcon.Text = ToTooltip(snapshot);

        if (snapshot.Kind != _lastKind)
        {
            if (snapshot.Kind == TrayHealthKind.Healthy && _lastKind == TrayHealthKind.Starting)
            {
                _notifyIcon.ShowBalloonTip(
                    4000,
                    "TelegramBot Server",
                    "Сервер запущен. База данных и Telegram доступны.",
                    ToolTipIcon.Info);
            }
            else if (_lastKind is TrayHealthKind.Healthy or TrayHealthKind.Starting
                     && snapshot.Kind is TrayHealthKind.Degraded or TrayHealthKind.Down)
            {
                _notifyIcon.ShowBalloonTip(
                    5000,
                    "TelegramBot Server",
                    BalloonProblemText(snapshot),
                    ToolTipIcon.Warning);
            }
        }

        _lastKind = snapshot.Kind;
    }

    private void ShowStatusBalloon()
    {
        var snapshot = _monitor.Snapshot;
        _notifyIcon.ShowBalloonTip(
            4000,
            "TelegramBot Server",
            $"{_databaseItem.Text}\n{_telegramItem.Text}",
            snapshot.Kind == TrayHealthKind.Healthy ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private void OnRefreshClicked()
    {
        _ = RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            await _healthCheck.CheckOnceAsync(_stoppingToken);
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tray refresh failed");
        }
    }

    private void OpenLogs()
    {
        try
        {
            _ = Directory.CreateDirectory(_logDirectory);
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = _logDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open logs folder fail");
        }
    }

    private void RestartServer()
    {
        try
        {
            // /end завершит этот процесс, поэтому /run выполняет отдельный cmd:
            // он не входит в задачу и переживает остановку её процесса.
            // && — если /end отказал, старый экземпляр жив и /run всё равно не запустит новый.
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c schtasks /end /tn \"{ServerTaskName}\" && ping -n 3 127.0.0.1 >nul && schtasks /run /tn \"{ServerTaskName}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            // Иконку прячем только после успешного старта cmd: иначе при ошибке трей исчезнет навсегда.
            _notifyIcon.Visible = false;
            _logger.LogInformation("Tray restart requested: task={Task}", ServerTaskName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tray restart fail: task={Task}", ServerTaskName);
        }
    }

    private void RestartWorker()
    {
        // /end завершает процесс жёстко: Worker не успевает погасить Revit/AutoCAD и освободить lease.
        // Новый Worker сам возвращает такие команды в очередь (ReleaseDeadProcessLeasesAsync —
        // каждый cleanup-тик, первый сразу при старте): Lease им не мешает — отпускается по мёртвому ProcessId.
        var confirm = MessageBox.Show(
            "Перезапустить Worker?\n\nАктивные задания будут прерваны и вернутся в очередь автоматически.",
            "TelegramBot",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        var oldPid = WorkerProcessId();
        // Результат /end нарочно не проверяется: ошибка «задача не запущена» перезапуску не мешает.
        RunScheduledTaskCommand($"/end /tn \"{WorkerTaskName}\"");
        RunScheduledTaskCommand($"/run /tn \"{WorkerTaskName}\"");

        // Код возврата не доказывает перезапуск: /run для уже работающей задачи тоже возвращает 0.
        var newPid = WaitForNewWorkerPid(oldPid);
        if (newPid is not null)
        {
            _logger.LogInformation(
                "Tray restart: task={Task}, oldPid={OldPid}, pid={ProcessId}", WorkerTaskName, oldPid, newPid);
            _notifyIcon.ShowBalloonTip(
                3000, "TelegramBot", $"Worker перезапущен (PID {newPid}).", ToolTipIcon.Info);
            return;
        }

        _logger.LogWarning("Tray restart unconfirmed: task={Task}, oldPid={OldPid}", WorkerTaskName, oldPid);
        _notifyIcon.ShowBalloonTip(
            5000,
            "TelegramBot",
            "Worker не перезапустился: новый процесс не появился. Подробности — в логах.",
            ToolTipIcon.Warning);
    }

    private static int? WorkerProcessId()
    {
        using var worker = Process.GetProcessesByName(WorkerProcessName).FirstOrDefault();
        return worker?.Id;
    }

    private static int? WaitForNewWorkerPid(int? oldPid)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            var pid = WorkerProcessId();
            if (pid is not null && pid != oldPid)
            {
                return pid;
            }

            Thread.Sleep(300);
        }

        return null;
    }

    private void RunScheduledTaskCommand(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            // Ждём завершения: /run не должен стартовать раньше, чем /end отпустит задачу.
            process?.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "schtasks fail: args={Args}", arguments);
        }
    }

    private static string FormatLine(string label, ProbeStatus status, string upWord, string downWord)
    {
        if (status.IsPending)
        {
            return $"{label}: проверка…";
        }

        var state = status.IsUp ? upWord : downWord;
        return string.IsNullOrWhiteSpace(status.Detail)
            ? $"{label}: {state}"
            : $"{label}: {state} ({status.Detail})";
    }

    private static string ToTooltip(ServerHealthSnapshot snapshot)
    {
        return snapshot.Kind switch
        {
            TrayHealthKind.Starting => "TelegramBot: запуск…",
            TrayHealthKind.Healthy => "TelegramBot: БД и Telegram ок",
            TrayHealthKind.Degraded when !snapshot.Database.IsUp => "TelegramBot: нет БД",
            TrayHealthKind.Degraded => "TelegramBot: нет Telegram",
            _ => "TelegramBot: нет БД и Telegram"
        };
    }

    private static string BalloonProblemText(ServerHealthSnapshot snapshot)
    {
        if (!snapshot.Database.IsUp && !snapshot.Telegram.IsUp)
        {
            return "Нет связи с базой данных и Telegram.";
        }

        return snapshot.Database.IsUp
            ? "Telegram недоступен."
            : "База данных недоступна.";
    }
}

internal static class TrayStatusIcons
{
    public static Icon Create(Color fill)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            var rect = new Rectangle(3, 3, size - 7, size - 7);
            using var brush = new SolidBrush(fill);
            using var pen = new Pen(Color.FromArgb(220, 20, 20, 20), 2f);
            graphics.FillEllipse(brush, rect);
            graphics.DrawEllipse(pen, rect);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
