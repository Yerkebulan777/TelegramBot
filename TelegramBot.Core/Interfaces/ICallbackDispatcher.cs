namespace TelegramBot.Core.Interfaces;

/// <summary>
/// Dispatches callback queries to appropriate handlers (Chain of Responsibility).
/// </summary>
public interface ICallbackDispatcher
{
    /// <summary>Перенаправляет callback на первый подходящий обработчик.</summary>
    Task<bool> DispatchAsync(CallbackContext context, CancellationToken cancellationToken = default);
}
