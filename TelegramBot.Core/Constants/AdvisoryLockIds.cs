namespace TelegramBot.Core.Constants;

/// <summary>
/// Ключи PostgreSQL advisory lock. 1-arg (<c>pg_advisory_lock(key)</c>) и 2-arg
/// (<c>pg_advisory_xact_lock(ns, id)</c>) — разные пространства; одинаковое число
/// в разных арностях не конфликтует.
/// </summary>
public static class AdvisoryLockIds
{
    /// <summary>1-arg: взаимное исключение lease cleanup между Worker.</summary>
    public const int LeaseCleanup = 1_234_567;

    /// <summary>1-arg: single-writer drain NotificationOutbox между репликами Server.</summary>
    public const int OutboxSender = 1_234_569;

    /// <summary>1-arg: глобальный зазор Process.Start для AutoCAD.</summary>
    public const int AutoCadLaunch = 1_234_570;

    /// <summary>1-arg: глобальный зазор Process.Start для Revit. Не совпадает с <see cref="OutboxSender"/>.</summary>
    public const int RevitLaunch = 1_234_571;

    /// <summary>2-arg namespace: <c>(PartitionClaim, hashtext(partition))</c> на время claim-транзакции.</summary>
    public const int PartitionClaim = 1_234_568;

    /// <summary>
    /// 2-arg namespace: <c>(SessionCompletion, SessionId)</c> на terminal write.
    /// Число совпадает с <see cref="AutoCadLaunch"/>; это безопасно из‑за другой арности.
    /// </summary>
    public const int SessionCompletion = 1_234_570;

    /// <summary>2-arg namespace: <c>(UserQueue, hashtext(UserId))</c> на создание Session+Commands и пересчёт дневного лимита.</summary>
    public const int UserQueue = 1_234_572;
}
