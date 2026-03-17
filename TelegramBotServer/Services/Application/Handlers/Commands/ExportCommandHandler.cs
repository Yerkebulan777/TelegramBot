using TelegramBotServer.DTOs;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers.Commands;

public class ExportCommandHandler : IUserCommandHandler
{
    private readonly ITelegramOutputService _outputService;
    private readonly IKeyboardBuilder _keyboardBuilder;
    private readonly Config.FileSystemOptions _options;

    public string Command => "/export";

    public ExportCommandHandler(
        ITelegramOutputService outputService,
        IKeyboardBuilder keyboardBuilder,
        Microsoft.Extensions.Options.IOptions<Config.FileSystemOptions> options)
    {
        _outputService = outputService;
        _keyboardBuilder = keyboardBuilder;
        _options = options.Value;
    }

    public async Task HandleAsync(MessageDto message, UserSession session, CancellationToken cancellationToken = default)
    {
        session.Reset(_options.RootPath);

        var commandKeyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(session);
        await _outputService.SendMessageWithKeyboardAsync(message.UserId, "Выберите команду:", commandKeyboard);

        var replyKeyboard = await _keyboardBuilder.GetExportActionsReplyKeyboardAsync();
        await _outputService.SendMessageWithReplyKeyboardAsync(message.UserId, "Подтвердите выбор:", replyKeyboard);
    }
}
