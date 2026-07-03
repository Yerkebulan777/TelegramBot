namespace TelegramBot.Core.Helpers;

/// <summary>
/// Extension methods для коллекций.
/// </summary>
public static class CollectionExtensions
{
    /// <summary>
    /// Безопасное удаление элемента из ConcurrentDictionary с проверкой CurrentCount.
    /// </summary>
    public static bool TryRemoveIfIdle<TKey, TValue>(
        this ConcurrentDictionary<TKey, TValue> dictionary,
        TKey key,
        Func<TValue, bool> isIdlePredicate)
        where TKey : notnull
    {
        if (!dictionary.TryGetValue(key, out var value))
        {
            return false;
        }

        if (!isIdlePredicate(value))
        {
            return false;
        }

        return dictionary.TryRemove(key, out _);
    }
}
