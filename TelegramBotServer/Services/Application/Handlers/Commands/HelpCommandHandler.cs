using System.Text;
using TelegramBotServer.DTOs;
using TelegramBotServer.Interfaces;
using TelegramBotServer.Models;

namespace TelegramBotServer.Services.Application.Handlers.Commands;

public class HelpCommandHandler : IUserCommandHandler
{
    private readonly ITelegramOutputService _outputService;
    private readonly Config.FileSystemOptions _options;

    public string Command => "/help";

    public HelpCommandHandler(ITelegramOutputService outputService, Microsoft.Extensions.Options.IOptions<Config.FileSystemOptions> options)
    {
        _outputService = outputService;
        _options = options.Value;
    }

    public async Task HandleAsync(MessageDto message, UserSession session, CancellationToken cancellationToken = default)
    {
        session.Reset(_options.RootPath);

        var helpText = new StringBuilder()
            .AppendLine("/export - используется для экспорта в форматы PDF, DWG, NWC, IFC.")
            .AppendLine("/automation - используется для автоматизации задач. BIM Doctor, Clash Report, Auto Resolver")
            .AppendLine("/status - используется для проверки состояния выполнения команды отправленной пользователем.")
            .AppendLine("При отправке данной команды пользователю будет предоставлен список сессий с временем отправки на обработку.")
            .AppendLine("Пользователь может нажать на сессию для мониторинга процесса выполнения команды.")
            .AppendLine("Кроме того в предоставленном меню пользователь может полностью удалить сессию.")
            .ToString();

        await _outputService.SendMessageAsync(message.UserId, helpText);
    }
}
