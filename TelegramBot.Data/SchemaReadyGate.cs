namespace TelegramBot.Data;

/// <summary>
/// Сигнал готовности схемы PostgreSQL. Hosted-сервисы ждут его, чтобы не ходить в БД во время DDL.
/// </summary>
public sealed class SchemaReadyGate
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    public void MarkReady() => _ready.TrySetResult();
}
