using TelegramBot.Core.DTOs;
using TelegramBot.Core.Interfaces;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;

namespace TelegramBot.Server.Services.Application;

public sealed class CommandAppService(
    ISessionManager sessionManager,
    ICallbackDispatcher callbackDispatcher,
    ISlashCommandService slashCommandService,
    ILogger<CommandAppService> logger) : ICommandAppService
{
    private readonly ISessionManager _sessionManager = sessionManager;
    private readonly ICallbackDispatcher _callbackDispatcher = callbackDispatcher;
    private readonly ISlashCommandService _slashCommandService = slashCommandService;

    public Task HandleUserCommandAsync(MessageDto message, CancellationToken cancellationToken = default)
    {
        var session = _sessionManager.GetOrCreateSession(message.UserId);
        session.TrackMessage(message.MessageId);
        return _slashCommandService.HandleUserCommandAsync(message, session, cancellationToken);
    }

    public async Task HandleCallbackAsync(CallbackQueryDto callback, CancellationToken cancellationToken = default)
    {
        if (callback.Username == null || callback.MessageText == null || callback.CallbackData == null || callback.CallbackQueryId == null)
        {
            logger.LogWarning("Received incomplete callback from user {UserId}", callback.UserId);
            return;
        }

        var session = _sessionManager.GetOrCreateSession(callback.UserId);
        session.TrackMessage(callback.MessageId);
        var parsed = CallbackDataParser.Parse(callback.CallbackData);

        logger.LogInformation("Callback received: prefix={Prefix}, user={UserId}", parsed.Prefix, callback.UserId);

        var isRegistrationCallback = parsed.Prefix is
            CallbackPrefixes.RequestAccess or
            CallbackPrefixes.ApproveUser or
            CallbackPrefixes.RejectUser;

        if (!isRegistrationCallback && !await _slashCommandService.CheckAndNotifyAccessAsync(callback.UserId, session))
        {
            logger.LogWarning("Callback rejected: prefix={Prefix}, user={UserId}, reason=access_denied", parsed.Prefix, callback.UserId);
            return;
        }

        var context = new CallbackContext
        {
            UserId = callback.UserId,
            ChatId = callback.ChatId,
            MessageId = callback.MessageId,
            Username = callback.Username,
            CallbackQueryId = callback.CallbackQueryId,
            ParsedCallback = parsed,
            Session = session,
            Buttons = callback.Buttons
        };

        _ = await _callbackDispatcher.DispatchAsync(context, cancellationToken);
    }
}
