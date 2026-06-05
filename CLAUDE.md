# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build all projects
dotnet build TelegramBot.slnx

# Run
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj

# Run worker (separate terminal)
dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj

# Publish
dotnet publish TelegramBot.Server/TelegramBot.Server.csproj -c Release

# Format code (uses .editorconfig rules)
dotnet format TelegramBot.slnx
```

There are no automated tests in this project.

> **Roadmap:** See [ROADMAP.md](ROADMAP.md) for the full project roadmap (v1.0–v2.0+).

## Project Structure

4 projects in `TelegramBot.slnx`:

| Project | Purpose | Dependencies |
|---|---|---|
| `TelegramBot.Core` | Models, DTOs, interfaces, config, constants (`net10.0`) | None (no Telegram SDK) |
| `TelegramBot.Data` | PostgreSQL persistence (Dapper + Npgsql, `net10.0`) | Core |
| `TelegramBot.Server` | Telegram bot, handlers, hosting, helpers (`net10.0`) | Core + Data |
| `TelegramBot.Worker` | Async task execution (Revit/Navisworks/AI), LISTEN/NOTIFY (`net10.0`) | Core + Data |

## Configuration

Bot token and root path are **not** hardcoded. Set them in `TelegramBot.Server/appsettings.Local.json` (gitignored):

```json
{
  "TelegramBot": { "Token": "<your-bot-token>" },
  "FileSystem": { "RootPath": "B:\\" }
}
```

`TelegramBot:Token` can also be set via the environment variable `TelegramBot__Token`. The app throws on startup if the token is missing.

Worker uses the same PostgreSQL database. Connection string in `TelegramBot.Worker/appsettings.json`:
```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres"
  }
}
```

## Architecture

### Request Flow

```
Telegram API -> TelegramBotHostedService (polling, BackgroundService)
             -> TelegramUpdateMapper (Update -> MessageDto | CallbackQueryDto)
             -> CommandAppService
                  ├── HandleUserCommandAsync (text commands)
                  │    └── SlashCommandService (/start, /help, /export, /automation, /status)
                  └── HandleCallbackAsync (inline keyboards)
                       └── ICallbackDispatcher -> CallbackDispatcher (Chain of Responsibility)
                            └── ICallbackHandler (first matching prefix)
```

All services are registered as **Singletons** via `DependencyInjectionExtensions.cs`.

**`TelegramBotHostedService`** — Entry point. Registers bot commands, starts polling, routes incoming updates. Uses `ISessionManager.AcquireUserLockAsync()` for per-user concurrency control. Cleans up stale messages on startup.

**`CommandAppService`** — Manages access control, creates sessions, dispatches text commands to `SlashCommandService` and inline keyboard callbacks to `ICallbackDispatcher`.

**`SlashCommandService`** — Handles text commands (`/start`, `/help`, `/export`, `/automation`, `/status`), reply keyboard actions (Apply, Confirm, Back, Cancel), and file selection flow.

**`CallbackDispatcher`** — Chain of Responsibility dispatcher implementing `ICallbackDispatcher`. Routes callback queries to the first `ICallbackHandler` that `CanHandle()` the prefix. Handlers sorted by `Priority` (lower = first):

| Handler | Priority | Prefixes |
|---|---|---|---|
| `AccessRequestHandler` | 0 | `REQACCESS:`, `APPROVEUSER:`, `REJECTUSER:` |
| `FileNavigationHandler` | 10 | `GOTOPARENT:` |
| `FileSelectionHandler` | 20 | `FILE:`, `APPLYFILES:`, `CANCELFILESEL:` |
| `CommandToggleHandler` | 100 | `PDF:`, `DWG:`, `NWC:`, `IFC:`, `BIMDOC:`, `CLASHREP:`, `AUTORES:` |
| `SessionManagementHandler` | 100 | `SESSIONDETAILS:`, `DELETESESSION:`, `DELETECOMMAND:`, `BACKTOSTATUS:` |
| `CommandSelectionHandler` | 100 | `APPLYCOMMANDS:`, `CANCELCOMMANDSSEL:` |

All handlers extend `CallbackHandlerBase` and use `CommandCatalog.TryGetByPrefix()` for prefix matching.
Use `CallbackDataParser.Parse(callbackData)` (from `ParsedCallback.cs`) to get a `ParsedCallback` struct, then match with `parsed.Is(CallbackPrefixes.GoToParent)`.

### Key Services

| Class | Location | Responsibility |
|---|---|---|
| `SlashCommandService` | Server/Services/Application | /start, /help, /export, /automation, /status + reply actions |
| `SessionManager` | Server/Services/Application | In-memory sessions (`ConcurrentDictionary`, 5-min timeout, auto-cleanup) |
| `FileSystemBrowser` | Server/Services/Infrastructure/FileSystem | Builds inline keyboards for filesystem navigation |
| `KeyboardBuilder` | Server/Services/Infrastructure/Telegram | Context-aware keyboards with selection state |
| `TelegramOutputService` | Server/Services/Infrastructure/Telegram | Send/edit Telegram messages with retry (429) |
| `TelegramUpdateMapper` | Server/Services/Infrastructure/Telegram | Maps Update to MessageDto/CallbackQueryDto |
| `TelegramBotHostedService` | Server/Services/Infrastructure/Telegram | Polling loop, cleanup, bot commands setup |
| `PostgresDataService` | Data | All DB persistence via Dapper + Npgsql |
| `CommandExecutionService` | Worker/Services | LISTEN/NOTIFY queue, command execution (Revit/Navisworks/AI) |
| `MarkdownHelper` | Server/Helpers | Unified Markdown escaping (MarkdownV2 + Markdown) |

### Database (PostgreSQL)

Tables: `BotUsers`, `Sessions`, `Commands`, `TrackedMessages`. Soft-delete only — rows are never physically removed (`Status = 'Deleted'`).
Database: **PostgreSQL** via Npgsql. Initialized at startup via `host.InitializeDatabaseAsync()` + `host.SeedAdminUsersAsync()`.
All queries use Dapper with parameterized SQL. SQL constants are in `TelegramBot.Data/Sql/` (5 partial files).

### Task Queue (PostgreSQL LISTEN/NOTIFY)

Server уведомляет Worker-ов о новых командах через `NOTIFY new_command` после INSERT в Commands. Worker использует `NpgsqlConnection.WaitAsync()` для мгновенного пробуждения. Fallback poll — 5 минут.

### Logging

Serilog configured via `appsettings.json`. Supports Console and Seq (`http://localhost:5341`) sinks.

### Documentation

| Document | Description |
|---|---|
| [ROADMAP.md](ROADMAP.md) | Project roadmap (v1.0–v2.0+) |
| [Docs/execution-algorithm.md](Docs/execution-algorithm.md) | Command execution algorithm specification |
| [AGENTS.md](AGENTS.md) | Guidance for AI agents |
| [README.md](README.md) | Project overview (Russian) |

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **TelegramBot** (1098 symbols, 2900 relationships, 92 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> If any GitNexus tool warns the index is stale, run `npx gitnexus analyze` in terminal first.

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `gitnexus_impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `gitnexus_detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `gitnexus_query({query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `gitnexus_context({name: "symbolName"})`.

## Never Do

- NEVER edit a function, class, or method without first running `gitnexus_impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `gitnexus_rename` which understands the call graph.
- NEVER commit changes without running `gitnexus_detect_changes()` to check affected scope.

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/TelegramBot/context` | Codebase overview, check index freshness |
| `gitnexus://repo/TelegramBot/clusters` | All functional areas |
| `gitnexus://repo/TelegramBot/processes` | All execution flows |
| `gitnexus://repo/TelegramBot/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->
