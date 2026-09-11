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
        menu.Items.Add(_serverItem);
        menu.Items.Add(_databaseItem);
        menu.Items.Add(_telegramItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(refreshItem);
        menu.Items.Add(logsItem);

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
