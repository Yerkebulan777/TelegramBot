using TelegramBotServer.DTOs;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers.Commands;

public class StatusCommandHandler : IUserCommandHandler
{
    private readonly IDataService _dataService;
    private readonly ITelegramOutputService _outputService;
    private readonly IKeyboardBuilder _keyboardBuilder;
    private readonly Config.FileSystemOptions _options;

    public string Command => "/status";

    public StatusCommandHandler(
        IDataService dataService,
        ITelegramOutputService outputService,
        IKeyboardBuilder keyboardBuilder,
        Microsoft.Extensions.Options.IOptions<Config.FileSystemOptions> options)
    {
        _dataService = dataService;
        _outputService = outputService;
        _keyboardBuilder = keyboardBuilder;
        _options = options.Value;
    }

    public async Task HandleAsync(MessageDto message, UserSession session, CancellationToken cancellationToken = default)
    {
        session.Reset(_options.RootPath);
        session.StatusLevel = true;

        var sessionsStatus = await _dataService.GetSessionsListAsync(message.UserId);
        var keyboard = await _keyboardBuilder.GetSessionsListKeyboardAsync(sessionsStatus);

        await _outputService.SendMessageWithKeyboardAsync(message.UserId, "Сессии:", keyboard);
    }
}
