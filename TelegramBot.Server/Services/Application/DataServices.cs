using TelegramBot.Data;

namespace TelegramBot.Server.Services.Application;

public sealed class DataServices(
    SessionDataService sessionDataService,
    CommandDataService commandDataService,
    MessageTrackingDataService messageTrackingDataService)
{
    public SessionDataService Sessions { get; } = sessionDataService;
    public CommandDataService Commands { get; } = commandDataService;
    public MessageTrackingDataService MessageTracking { get; } = messageTrackingDataService;
}
