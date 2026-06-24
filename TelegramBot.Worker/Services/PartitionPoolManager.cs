namespace TelegramBot.Worker.Services;

/// <summary>
/// Управляет пулом параллельно выполняемых команд через один SemaphoreSlim.
/// Каждая команда захватывает слот при старте и освобождает при завершении.
/// Приоритеты команд не влияют на пропускную способность — FIFO через SemaphoreSlim.
/// </summary>
public sealed class PartitionPoolManager(ILogger<PartitionPoolManager> logger) : IDisposable
{
    private SemaphoreSlim? _pool;

    /// <summary>Количество пулов (всегда 1 после инициализации).</summary>
    public int PoolCount => _pool != null ? 1 : 0;

    /// <summary>Общая ёмкость пула. Используется Worker'ом для ограничения in-flight задач.</summary>
    public int TotalCapacity { get; private set; }

    /// <summary>Инициализирует пул: суммирует ёмкость из конфигурации партиций.</summary>
    public void Initialize(SortedDictionary<int, int> partitions)
    {
        var poolSize = partitions?.Sum(p => p.Value) ?? 5;
        if (poolSize <= 0)
        {
            poolSize = 5;
        }

        _pool?.Dispose();
        _pool = new SemaphoreSlim(poolSize, poolSize);
        TotalCapacity = poolSize;

        logger.LogInformation("Partition pool initialized: capacity={Capacity}", poolSize);
    }

    /// <summary>Ожидает освобождения слота. Приоритет игнорируется — FIFO.</summary>
    public Task WaitForSlotAsync(int priority, CancellationToken ct)
    {
        return _pool!.WaitAsync(ct);
    }

    /// <summary>Освобождает слот. Приоритет игнорируется.</summary>
    public void ReleaseSlot(int priority)
    {
        var pool = _pool;
        if (pool == null)
        {
            logger.LogError("Partition pool over-release prevented: pool not initialized");
            return;
        }

        try
        {
            _ = pool.Release();
        }
        catch (SemaphoreFullException)
        {
            logger.LogError("Partition pool over-release prevented: current={Current}, capacity={Capacity}",
                pool.CurrentCount, TotalCapacity);
        }
    }

    /// <summary>Возвращает строковое представление информации о пуле (для логирования при старте).</summary>
    public string GetPoolInfo()
    {
        return _pool != null ? $"{_pool.CurrentCount}/{TotalCapacity}" : "uninitialized";
    }

    /// <summary>Освобождает пул.</summary>
    public void Dispose()
    {
        _pool?.Dispose();
        TotalCapacity = 0;
    }
}
