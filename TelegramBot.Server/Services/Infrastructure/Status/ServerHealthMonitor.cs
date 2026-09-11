namespace TelegramBot.Server.Services.Infrastructure.Status;

public enum TrayHealthKind
{
    Starting,
    Healthy,
    Degraded,
    Down
}

public enum ProbeState
{
    Pending,
    Up,
    Down
}

public readonly record struct ProbeStatus(ProbeState State, string Detail)
{
    public static ProbeStatus Pending { get; } = new(ProbeState.Pending, "проверка…");

    public static ProbeStatus Up(string detail) => new(ProbeState.Up, detail);

    public static ProbeStatus Down(string detail) => new(ProbeState.Down, detail);

    public bool IsUp => State == ProbeState.Up;

    public bool IsPending => State == ProbeState.Pending;
}

public sealed record ServerHealthSnapshot(ProbeStatus Database, ProbeStatus Telegram)
{
    public static ServerHealthSnapshot Starting { get; } = new(ProbeStatus.Pending, ProbeStatus.Pending);

    public TrayHealthKind Kind =>
        Database.IsPending || Telegram.IsPending
            ? TrayHealthKind.Starting
            : Database.IsUp && Telegram.IsUp
                ? TrayHealthKind.Healthy
                : Database.IsUp || Telegram.IsUp
                    ? TrayHealthKind.Degraded
                    : TrayHealthKind.Down;
}

/// <summary>
/// Снимок доступности PostgreSQL и Telegram для иконки в трее.
/// </summary>
public sealed class ServerHealthMonitor
{
    private readonly object _gate = new();
    private ServerHealthSnapshot _snapshot = ServerHealthSnapshot.Starting;

    public event Action<ServerHealthSnapshot>? Changed;

    public ServerHealthSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public void ReportDatabase(bool available, string detail) =>
        Report(database: true, available, detail);

    public void ReportTelegram(bool available, string detail) =>
        Report(database: false, available, detail);

    private void Report(bool database, bool available, string detail)
    {
        ServerHealthSnapshot snapshot;
        lock (_gate)
        {
            var status = available ? ProbeStatus.Up(detail) : ProbeStatus.Down(detail);
            snapshot = database
                ? _snapshot with { Database = status }
                : _snapshot with { Telegram = status };
            if (snapshot == _snapshot)
            {
                return;
            }

            _snapshot = snapshot;
        }

        Changed?.Invoke(snapshot);
    }
}
