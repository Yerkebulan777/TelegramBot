# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet build TelegramBotServer/TelegramBotServer.csproj

# Run
dotnet run --project TelegramBotServer/TelegramBotServer.csproj

# Publish
dotnet publish TelegramBotServer/TelegramBotServer.csproj -c Release
```

There are no automated tests in this project.

## Configuration

Bot token and root path are **not** hardcoded. Set them in `appsettings.Local.json` (gitignored):

```json
{
  "TelegramBot": {
    "Token": "<your-bot-token>",
    "RootPath": "B:\\"
  },
  "ConnectionStrings": {
    "Sqlite": "Data Source=botdata.db"
  }
}
```

`TelegramBot:Token` can also be set via the environment variable `TelegramBot__Token`. The app throws on startup if the token is missing.

## Architecture

Single .NET 8 project (`TelegramBotServer`) running as a background service. The bot uses long-polling (not webhooks). All services are registered as **Singletons** via `DependencyInjectionExtensions.cs`.

### Request Flow

```
Telegram API → TelegramBotHostedService (polling)
             → TelegramUpdateMapper (Update → MessageDto | CallbackQueryDto)
             → Authorization check (AuthService + SessionManager)
             → CommandAppService.HandleUserCommandAsync / HandleCallbackAsync
```

**`TelegramBotHostedService`** — Entry point. Registers bot commands on startup via `Config.ConfigureAsync()`, starts polling, routes incoming updates to `CommandAppService`. Handles `/auth` and password-entry flow before forwarding to command handling. Uses `SessionManager.AcquireUserLockAsync()` for per-user concurrency control.

**`CommandAppService`** — Central command dispatcher (~970 lines). Handles text commands (`/export`, `/automation`, `/status`, `/help`) and all inline keyboard callbacks. Manages multi-step workflows by reading/writing `UserSession` state. Root path read from `TelegramBot:RootPath` config key (defaults to `B:\\`).

**`FileSystemBrowser`** — Builds inline keyboards for browsing the filesystem. Filters directories matching regex `^(\d{2}|\d{3}|I{1,3})_` and files to `.rvt` only. Paginates at 20 items per page. Delegates section-level browsing to `SectionNavigationService`.

**`KeyboardBuilder`** — Wraps `NavigationService` to produce context-aware keyboards (marks selected items with ✅). Three selection modes controlled by `UserSession.SelectionType` (`SelectionMode` enum):
- `SelectionMode.Files` (1) = flat file browser
- `SelectionMode.Sections` (2) = section navigator — `UserSession.Level` (bool) tracks depth (false = sections list, true = inside section toward `01_RVT` → files)
- `SelectionMode.Projects` (3) = project navigator — `UserSession.Level` tracks project → section → RVT depth

**`SessionManager`** — In-memory `ConcurrentDictionary<long, UserSession>` with 5-minute idle timeout. Sessions hold all transient user state (current path, selected files, pending commands, path token map).

**`TelegramOutputService`** — Sends and edits Telegram messages. Uses `ParseMode.MarkdownV2` with `EscapeMarkdownV2()` for plain messages; `ParseMode.Markdown` for keyboard messages. Handles Telegram API exceptions (deleted messages, stale edits) gracefully.

**`SqliteDataService`** — All persistence. Two libraries used inconsistently: `Microsoft.Data.Sqlite` for raw `SqliteCommand` and `System.Data.SQLite` for some methods (e.g., `GetSessionsStatusAsync`, `CheckCommandsStatusAsync`). Dapper is used only in `CreateSessionWithCommandsAsync`.

**`AuthService`** — Checks `Whitelist` table for user; validates against `Credentials` table (plain-text password). Default password is `qwerty123` (inserted on first run).

### Callback Data Protocol

Inline keyboard buttons use short tokens to stay within Telegram's 64-byte callback data limit. Full paths are stored in `UserSession.PathMap[token]` and resolved in handlers. Prefixes are defined as constants in `CallbackPrefixes` (see `CallbackDataParser.cs`).

Callback prefixes:
- `OPENFOLDER:`, `GOTOPARENT:` — navigate into/up directory
- `FILE:` — select/deselect a file
- `SELMODE:` — toggle selection mode (file/section/project)
- `APPLYFILES:`, `CANCELSEL:`, `CANCELFILESEL:` — file selection flow
- `PDF:`, `DWG:`, `NWC:`, `IFC:` — export format selection
- `BIMDOC:`, `CLASHREP:`, `AUTORES:` — automation command selection
- `APPLYCOMMANDS:`, `CANCELCOMMANDSSEL:` — command selection flow
- `Sessiondetails:`, `Deletesession:`, `Deletecommand:`, `Backtostatus:` — status/queue management

Use `CallbackDataParser.Parse(callbackData)` to get a `ParsedCallback` struct, then match with `parsed.Is(CallbackPrefixes.OpenFolder)`.

### Database Schema

Tables: `Sessions`, `Commands`, `Whitelist`, `Credentials`. "Deleted" is a soft-delete status — rows are never physically removed. Database file: `botdata.db` in the project directory. DB is initialized at startup via `host.InitializeDatabaseAsync()` (`HostExtensions.cs`).

### Logging

Serilog is configured via `appsettings.json`. Supports Console, Seq (`http://localhost:5341`), and Elasticsearch (`http://localhost:9200`) sinks. Override sink configuration in `appsettings.Local.json` for local dev.

### Known Issues (see RACE_CONDITION_ANALYSIS.md)

`UserSession` mutable collections (`SelectedFiles`, `PendingCommand`, `PathMap`, etc.) are accessed from concurrent Telegram update handlers without synchronization. `DeleteSessionAsync` uses raw SQL `BEGIN TRANSACTION` instead of the connection's `BeginTransaction()` method.
