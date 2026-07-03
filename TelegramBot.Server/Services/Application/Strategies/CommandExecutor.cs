using Telegram.Bot.Exceptions;
using TelegramBot.Core.DTOs;
using TelegramBot.Core.Models;
using TelegramBot.Server.Interfaces;
using TelegramBot.Server.Services.Application.Handlers;

namespace TelegramBot.Server.Services.Application.Strategies;

/// <summary>
/// Выполняет бизнес-логику команды в зависимости от стратегии.
/// </summary>
public sealed class CommandExecutor(
    ITelegramOutputService outputService,
    SlashCommandService slashCommandService,
    MessageTrackingService messageTrackingService,
    ILogger<CommandExecutor> logger)
{
    /// <summary>
    /// Выполняет команду согласно стратегии.
    /// </summary>
            CommandStrategy.AccessDenied => CommandExecutionResult.AccessDenied(),
            CommandStrategy.Start => await HandleStartAsync(context),
            CommandStrategy.CommandSelectionAction => await HandleCommandSelectionActionAsync(context, cancellationToken),
            CommandStrategy.SlashCommand => await HandleSlashCommandAsync(context, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown command strategy '{strategy}'.")
        };
    }

    private async Task<CommandExecutionResult> HandleStartAsync(UserCommandContext context)
    {
        context.Session.ResetNavigation(context.Session.CurrentPath);

        if (context.IsActive)
        {
            await slashCommandService.SendHelpMessageAsync(context.UserId, context.Session);
        }
        else
        {
            await slashCommandService.SendRegistrationMessageAsync(context.UserId, context.Session);
        }

        return CommandExecutionResult.Handled();
    }

    private async Task<CommandExecutionResult> HandleCommandSelectionActionAsync(
        UserCommandContext context,
        CancellationToken cancellationToken)
    {
        var handled = await slashCommandService.HandleCommandSelectionActionsAsync(
            context.UserId,
            context.Username,
            context.RawText,
            context.Session,
            cancellationToken);

        if (handled)
        {
            return CommandExecutionResult.Handled();
        }

        await slashCommandService.HandleSlashCommandAsync(context.Command, context.Message, context.Session, context.Username);
        return CommandExecutionResult.Handled();
    }

    private async Task<CommandExecutionResult> HandleSlashCommandAsync(
        UserCommandContext context,
        CancellationToken cancellationToken)
    {
        await slashCommandService.HandleSlashCommandAsync(context.Command, context.Message, context.Session, context.Username);
        return CommandExecutionResult.Handled();
    }

    private async Task SendSafeResponseAsync(long chatId, string? message, UserSession session)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            _ = await messageTrackingService.TrackAsync(outputService.SendMessageAsync(chatId, message), session.SessionId > 0 ? session.SessionId : null);
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(ex, "Failed to send command response to {ChatId}", chatId);
        }
    }

}
