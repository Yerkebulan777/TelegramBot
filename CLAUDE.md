# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build all projects
dotnet build TelegramBot.sln

# Run
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj

# Publish
dotnet publish TelegramBot.Server/TelegramBot.Server.csproj -c Release
```

There are no automated tests in this project.

## Project Structure

3 projects in `TelegramBot.sln`:

| Project | Purpose | Dependencies |
|---|---|---|
| `TelegramBot.Core` | Models, DTOs, interfaces, config | None (no Telegram SDK) |
| `TelegramBot.Data` | SQLite persistence (Dapper) | Core |
| `TelegramBot.Server` | Telegram bot, handlers, hosting | Core + Data |

The old `TelegramBotServer/` directory contains the legacy single-project code and is no longer used.

## Configuration

Bot token and root path are **not** hardcoded. Set them in `TelegramBot.Server/appsettings.Local.json` (gitignored):

```json
{
  "TelegramBot": { "Token": "<your-bot-token>" },
  "FileSystem": { "RootPath": "B:\\" },
  "ConnectionStrings": { "Sqlite": "Data Source=botdata.db" }
}
```

`TelegramBot:Token` can also be set via the environment variable `TelegramBot__Token`. The app throws on startup if the token is missing.

## Architecture

### Request Flow

```
Telegram API -> TelegramBotHostedService (polling)
             -> TelegramUpdateMapper (Update -> MessageDto | CallbackQueryDto)
             -> CommandAppService.HandleUserCommandAsync / HandleCallbackAsync
             -> ICallbackDispatcher -> CallbackDispatcher.DispatchAsync (Chain of Responsibility)
```

All services are registered as **Singletons** via `DependencyInjectionExtensions.cs`.

**`TelegramBotHostedService`** — Entry point. Registers bot commands, starts polling, routes incoming updates. Uses `ISessionManager.AcquireUserLockAsync()` for per-user concurrency control.

**`CommandAppService`** — Handles text commands (`/export`, `/automation`, `/status`, `/help`). Delegates all inline keyboard callbacks to `ICallbackDispatcher`.

**`CallbackDispatcher`** — Chain of Responsibility dispatcher implementing `ICallbackDispatcher`. Routes callback queries to the first `ICallbackHandler` that `CanHandle()` the prefix. Handlers sorted by `Priority` (lower = first):

| Handler | Priority | Prefixes |
|---|---|---|
| `FileNavigationHandler` | 10 | OPENFOLDER:, GOTOPARENT: |
| `FileSelectionHandler` | 20 | FILE:, APPLYFILES:, CANCELFILESEL: |
| `ExportCommandHandler` | 100 | PDF:, DWG:, NWC:, IFC: |
| `AutomationCommandHandler` | 100 | BIMDOC:, CLASHREP:, AUTORES: |
| `SessionManagementHandler` | 100 | SESSIONDETAILS:, DELETESESSION:, DELETECOMMAND:, BACKTOSTATUS: |
| `CommandSelectionHandler` | 100 | SELMODE:, APPLYCOMMANDS:, CANCELCOMMANDSSEL: |

Use `CallbackDataParser.Parse(callbackData)` to get a `ParsedCallback` struct, then match with `parsed.Is(CallbackPrefixes.OpenFolder)`.

### Key Services

| Class | Location | Responsibility |
|---|---|---|
| `FileSystemBrowser` | Server/Infrastructure | Builds inline keyboards for filesystem navigation |
| `KeyboardBuilder` | Server/Infrastructure | Context-aware keyboards with selection state |
| `SessionManager` | Server/Application | In-memory sessions (`ConcurrentDictionary`, 5-min timeout) |
| `TelegramOutputService` | Server/Infrastructure | Send/edit Telegram messages with retry |
| `SqliteDataService` | Data | All DB persistence via Dapper |

### Database

Tables: `Sessions`, `Commands`. Soft-delete only — rows are never physically removed.
DB file: `botdata.db`. Initialized at startup via `host.InitializeDatabaseAsync()`.
All queries use Dapper with parameterized SQL.

### Logging

Serilog configured via `appsettings.json`. Supports Console and Seq (`http://localhost:5341`) sinks.
