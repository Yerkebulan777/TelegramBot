using System.Text;
using TelegramBotServer.DTOs;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers.Commands;

public class StartCommandHandler : IUserCommandHandler
{
    private readonly ITelegramOutputService _outputService;
    private readonly Config.FileSystemOptions _options;

    public string Command => "/start";

    public StartCommandHandler(ITelegramOutputService outputService, Microsoft.Extensions.Options.IOptions<Config.FileSystemOptions> options)
    {
        _outputService = outputService;
        _options = options.Value;
    }

    public async Task HandleAsync(MessageDto message, UserSession session, CancellationToken cancellationToken = default)
    {
        session.Reset(_options.RootPath);

        var startText = new StringBuilder()
            .AppendLine($"Привет, {message.Username}! 👋")
            .AppendLine("Я бот для работы с BIM-документами, автоматизации задач и экспорта файлов.")
            .AppendLine()
            .AppendLine("Вот что я умею (нажмите на команду):")
            .AppendLine("🔹 /export - экспорт в форматы PDF, DWG, NWC, IFC")
            .AppendLine("🔹 /automation - задачи автоматизации (BIM Doctor, Clash Report и др.)")
            .AppendLine("🔹 /status - проверка состояния выполнения ваших задач")
            .AppendLine("🔹 /help - показать подробную справку")
            .ToString();

        await _outputService.SendMessageAsync(message.UserId, startText);
    }
}
