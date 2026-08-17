using TelegramBot.Core.Models;
using TelegramBot.Server.Services.Infrastructure.Telegram;

namespace TelegramBot.Server.Services.Application;

/// <summary>
/// Управляет жизненным циклом постоянной «file actions» reply-клавиатуры:
/// удаляет предыдущее tracked-сообщение действий, отправляет новое, трекает его
/// и запоминает новый MessageId в сессии. Вынесено из дословно дублированных копий
/// в SlashCommandService / FileNavigationHandler / CommandSelectionHandler.
/// </summary>
public sealed class FileActionsKeyboardService(
    KeyboardBuilder keyboardBuilder,
    TelegramOutputService outputService,
    MessageTrackingService messageTrackingService)
{
    private const string ActionsPrompt = "Действия:";

    /// <summary>
    /// Заменяет текущее actions-сообщение новым: удаляет старое (если было),
    /// отправляет reply-клавиатуру «Действия:», трекает его и сохраняет MessageId.
    /// </summary>
    public async Task RefreshAsync(long userId, UserSession session)
    {
        if (session.LastActionsMessageId.HasValue)
        {
            await outputService.DeleteMessageAsync(userId, session.LastActionsMessageId.Value);
            session.LastActionsMessageId = null;
        }

        var replyKeyboard = keyboardBuilder.GetFileActionsReplyKeyboard();
        var message = await messageTrackingService.TrackAsync(
            outputService.SendMessageWithReplyKeyboardAsync(userId, ActionsPrompt, replyKeyboard),
            session);
        session.LastActionsMessageId = message?.Id;
    }

    /// <summary>
    /// Скрывает reply-клавиатуру вне списка файлов, сохраняя сообщение для последующей очистки.
    /// </summary>
    public async Task HideAsync(long userId, UserSession session)
    {
        if (session.LastActionsMessageId.HasValue)
        {
            await outputService.DeleteMessageAsync(userId, session.LastActionsMessageId.Value);
            session.LastActionsMessageId = null;
        }

        var message = await messageTrackingService.TrackAsync(
            outputService.RemoveReplyKeyboardAsync(userId, "Выберите папку:"),
            session);
        session.LastActionsMessageId = message?.Id;
    }

    /// <summary>
    /// Отправляет сообщение об ошибке с постоянной «file actions» reply-клавиатурой и трекает его.
    /// Вынесено из дословно дублированных копий в FileNavigationHandler / FileSelectionHandler.
    /// </summary>
    public async Task SendErrorAsync(long userId, string message, UserSession session)
    {
        var replyKeyboard = keyboardBuilder.GetFileActionsReplyKeyboard();
        _ = await messageTrackingService.TrackAsync(
            outputService.SendMessageWithReplyKeyboardAsync(userId, message, replyKeyboard),
            session);
    }
}
