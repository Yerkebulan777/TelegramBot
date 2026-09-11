using Npgsql;
using Telegram.Bot;
using TelegramBot.Data;

namespace TelegramBot.Server.Services.Infrastructure.Status;

/// <summary>
/// Периодически проверяет PostgreSQL и Telegram Bot API для индикатора в трее.
/// </summary>
public sealed class ServerHealthCheckService(
    IConfiguration configuration,
    ITelegramBotClient botClient,
    ServerHealthMonitor monitor,
    ILogger<ServerHealthCheckService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(8);
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                try
                {
                    await CheckOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Health check cycle failed; retrying on next poll");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        await _checkGate.WaitAsync(cancellationToken);
        try
        {
            await Task.WhenAll(
                CheckDatabaseAsync(cancellationToken),
                CheckTelegramAsync(cancellationToken));
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _checkGate.Dispose();
        base.Dispose();
    }

    private async Task CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        var before = monitor.Snapshot.Database;
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(DataAccessBase.ResolveConnectionString(configuration));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(CheckTimeout);
            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync(timeoutCts.Token);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            _ = await cmd.ExecuteScalarAsync(timeoutCts.Token);

            var endpoint = $"{builder.Host}:{builder.Port}/{builder.Database}";
            monitor.ReportDatabase(true, endpoint);
            LogIfRecovered(before, "Health database recovered: {Endpoint}", endpoint);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            monitor.ReportDatabase(false, Truncate(ex.Message));
            LogIfDown(before, ex, "Health database down");
        }
    }

    private async Task CheckTelegramAsync(CancellationToken cancellationToken)
    {
        var before = monitor.Snapshot.Telegram;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(CheckTimeout);
            var me = await botClient.GetMe(timeoutCts.Token);
            var name = string.IsNullOrWhiteSpace(me.Username) ? me.FirstName : "@" + me.Username;
            monitor.ReportTelegram(true, name);
            LogIfRecovered(before, "Health Telegram recovered: {Bot}", name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            monitor.ReportTelegram(false, Truncate(ex.Message));
            LogIfDown(before, ex, "Health Telegram down");
        }
    }

    private void LogIfRecovered(ProbeStatus before, string template, object arg)
    {
        if (before.State == ProbeState.Down)
        {
            logger.LogInformation(template, arg);
        }
    }

    private void LogIfDown(ProbeStatus before, Exception error, string template)
    {
        if (before.State != ProbeState.Down)
        {
            logger.LogWarning(error, template);
        }
        else if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(error, "Health probe still down");
        }
    }

    private static string Truncate(string message)
    {
        const int maxLength = 80;
        var trimmed = message.ReplaceLineEndings(" ").Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
