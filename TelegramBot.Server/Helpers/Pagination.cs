namespace TelegramBot.Server.Helpers;

/// <summary>
/// Постраничная разбивка списков: вычисление числа страниц и обрезка коллекции.
/// Выделено из <c>KeyboardBuilder</c>, где один и тот же расчёт total/clamped page
/// дублировался в <c>GetSessionsListKeyboard</c> и <c>GetSessionCommandsKeyboard</c>,
/// а также использовался из <c>SessionsListRenderer</c>.
/// </summary>
public static class Pagination
{
    /// <summary>
    /// Вычисляет (clampedPage, totalPages) для pageSize-разбиения <paramref name="totalCount"/>.
    /// Page клампится в валидный диапазон [0, totalPages-1]; при пустом списке возвращает (0, 0).
    /// </summary>
    public static (int ClampedPage, int TotalPages) Calculate(int totalCount, int page, int pageSize)
    {
        var totalPages = totalCount > 0
            ? (totalCount + pageSize - 1) / pageSize
            : 0;
        var clampedPage = totalPages > 0
            ? Math.Clamp(page, 0, totalPages - 1)
            : 0;
        return (clampedPage, totalPages);
    }

    /// <summary>
    /// Возвращает элементы страницы <paramref name="clampedPage"/> (0-based) размера <paramref name="pageSize"/>.
    /// </summary>
    public static IEnumerable<T> Page<T>(IEnumerable<T> source, int clampedPage, int pageSize)
    {
        return source.Skip(clampedPage * pageSize).Take(pageSize);
    }
}
