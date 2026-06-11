using TelegramBot.Core.Models;

namespace TelegramBot.Core.Interfaces;

/// <summary>
/// Определяет обработчик для callback-запросов Telegram.
/// </summary>
public interface ICallbackHandler
{
    /// <summary>Проверяет, может ли обработчик обработать callback.</summary>
    bool CanHandle(string prefix);

    /// <summary>Приоритет обработчика (меньше = раньше).</summary>
    int Priority => 100;

    /// <summary>Обрабатывает callback асинхронно.</summary>
    Task<bool> HandleAsync(CallbackContext context, CancellationToken cancellationToken = default);

    /// <summary>Возвращает список callback-префиксов, которые поддерживает этот обработчик.</summary>
    IEnumerable<string> GetSupportedPrefixes() => [];
}
