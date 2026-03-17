using Microsoft.Extensions.Options;
using System.Text;
using TelegramBotServer.DTOs;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers.Commands;

public class StartCommandHandler(ITelegramOutputService outputService, IOptions<Config.FileSystemOptions> options) : IUserCommandHandler
{
    private readonly ITelegramOutputService _outputService = outputService;
    private readonly Config.FileSystemOptions _options = options.Value;

    public string Command => "/start";

    public async Task HandleAsync(MessageDto message, UserSession session, CancellationToken cancellationToken = default)
    {
        session.Reset(_options.RootPath);

        var startText = new StringBuilder()
            .AppendLine($"Привет, {message.Username}! 👋")
            .AppendLine("Я бот для работы с автоматизации задач и экспорта файлов.\n")
            .AppendLine("Вот что я умею (нажмите на команду):")
            .AppendLine("🔹 /export - экспорт в форматы PDF, DWG, NWC")
            .AppendLine("🔹 /automation - автоматизация BIM задач")
            .AppendLine("🔹 /status - проверка выполнения задач")
            .AppendLine("🔹 /help - показать подробную справку")
            .ToString();

        await _outputService.SendMessageAsync(message.UserId, startText);
    }
}
