namespace TelegramBot.Worker.Services;

/// <summary>
/// Управляет пулами процессов по партициям приоритетов.
/// Каждая партиция имеет свой SemaphoreSlim, ограничивающий количество
/// одновременно выполняемых команд данного уровня приоритета.
/// Команда попадает в первый threshold >= Priority. Чем меньше Priority, тем выше приоритет.
/// </summary>
public sealed class PartitionPoolManager(ILogger<PartitionPoolManager> logger) : IDisposable
{
    private readonly SortedDictionary<int, SemaphoreSlim> _partitionPools = [];
    private readonly SortedDictionary<int, int> _partitionCapacities = [];
    private int[] _partitionThresholds = [];

    /// <summary>Количество пулов.</summary>
    public int PoolCount => _partitionPools.Count;

    /// <summary>Общая ёмкость всех пулов. Используется Worker'ом для ограничения in-flight задач.</summary>
    public int TotalCapacity { get; private set; }

    /// <summary>Инициализирует пулы из конфигурации партиций.</summary>
    public void Initialize(SortedDictionary<int, int> partitions)
    {
        // Валидация конфигурации
        if (partitions == null || partitions.Count == 0)
        {
            logger.LogWarning("No partition configuration provided, using default pool of 5");
            partitions = new SortedDictionary<int, int> { { 0, 5 } };
        }

        foreach (var (threshold, poolSize) in partitions)
        {
            if (poolSize <= 0)
            {
                logger.LogWarning("Invalid pool size {PoolSize} for threshold {Threshold}, skipping", poolSize, threshold);
            }
        }

        // Dispose old semaphores before clearing (безопасно, т.к. Initialize вызывается до старта потоков)
        foreach (var pool in _partitionPools.Values)
        {
            pool.Dispose();
        }

        _partitionPools.Clear();
        _partitionCapacities.Clear();
        TotalCapacity = 0;

        foreach (var (threshold, poolSize) in partitions.Where(p => p.Value > 0))
        {
            _partitionPools[threshold] = new SemaphoreSlim(poolSize, poolSize);
            _partitionCapacities[threshold] = poolSize;
            TotalCapacity += poolSize;
        }

        // Гарантируем хотя бы один пул
        if (_partitionPools.Count == 0)
        {
            _partitionPools[0] = new SemaphoreSlim(5, 5);
            _partitionCapacities[0] = 5;
            TotalCapacity = 5;
        }

        _partitionThresholds = _partitionPools.Keys.ToArray();

        logger.LogInformation("Partition pools initialized: {Info}", GetPoolInfo());
    }

    /// <summary>Возвращает threshold партиции для указанного приоритета (первый threshold >= priority).</summary>
    public int GetThreshold(int priority)
    {
        foreach (var threshold in _partitionThresholds)
        {
            if (priority <= threshold)
            {
                return threshold;
            }
        }

        return _partitionThresholds[^1];
    }

    /// <summary>Ожидает освобождения слота в партиции для указанного приоритета.</summary>
    public async Task WaitForSlotAsync(int priority, CancellationToken ct)
    {
        var threshold = GetThreshold(priority);
        await _partitionPools[threshold].WaitAsync(ct);
    }

    /// <summary>Освобождает слот в партиции для указанного приоритета.</summary>
    public void ReleaseSlot(int priority)
    {
        var threshold = GetThreshold(priority);
        var pool = _partitionPools[threshold];
        var capacity = _partitionCapacities[threshold];

        if (pool.CurrentCount >= capacity)
        {
            logger.LogError(
                "Partition pool over-release prevented: priority={Priority}, threshold={Threshold}, current={Current}, capacity={Capacity}",
                priority, threshold, pool.CurrentCount, capacity);
            return;
        }

        _ = pool.Release();
    }

    /// <summary>Возвращает строковое представление информации о пулах (для логирования при старте).</summary>
    public string GetPoolInfo()
    {
        return string.Join(", ", _partitionPools.Select(p => $"{p.Key}={p.Value.CurrentCount}"));
    }

    /// <summary>Освобождает все пулы.</summary>
    public void Dispose()
    {
        foreach (var pool in _partitionPools.Values)
        {
            pool.Dispose();
        }

        _partitionPools.Clear();
        _partitionCapacities.Clear();
        TotalCapacity = 0;
    }
}
