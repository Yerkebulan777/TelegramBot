using TelegramBot.Core.Models;

namespace TelegramBot.Core.Interfaces;

/// <summary>
/// Определяет обработчик для callback-запросов Telegram.
/// </summary>
public interface ICallbackHandler
{
    /// <summary>Обрабатывает callback асинхронно.</summary>
    Task HandleAsync(CallbackContext context, CancellationToken cancellationToken = default);

    /// <summary>Возвращает список callback-префиксов, которые поддерживает этот обработчик.</summary>
    IEnumerable<string> GetSupportedPrefixes();
}
