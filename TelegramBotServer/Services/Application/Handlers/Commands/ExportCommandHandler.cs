using Microsoft.Extensions.Options;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotServer.DTOs;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers.Commands;

public class ExportCommandHandler(
    ITelegramOutputService outputService,
    IKeyboardBuilder keyboardBuilder,
    IOptions<Config.FileSystemOptions> options) : IUserCommandHandler
{
    private readonly ITelegramOutputService _outputService = outputService;
    private readonly IKeyboardBuilder _keyboardBuilder = keyboardBuilder;
    private readonly Config.FileSystemOptions _options = options.Value;

    public string Command => "/export";

    public async Task HandleAsync(MessageDto message, UserSession session, CancellationToken cancellationToken = default)
    {
        session.Reset(_options.RootPath);

        InlineKeyboardMarkup commandKeyboard = await _keyboardBuilder.GetCommandsKeyboardAsync(session);
        await _outputService.SendMessageWithKeyboardAsync(message.UserId, "Выберите команду:", commandKeyboard);

        ReplyKeyboardMarkup replyKeyboard = await _keyboardBuilder.GetExportActionsReplyKeyboardAsync();
        await _outputService.SendMessageWithReplyKeyboardAsync(message.UserId, "Подтвердите выбор:", replyKeyboard);
    }
}
