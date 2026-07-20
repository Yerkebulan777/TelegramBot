# TelegramBot

[![CI](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml)
[![Qodana](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/code_quality.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/code_quality.yml)

Windows-сервис на .NET 10: Telegram-бот принимает задания, PostgreSQL хранит очередь, Worker запускает BIM/AI-исполнители.

## Документация

| Документ | Назначение |
|---|---|
| [AGENTS.md](AGENTS.md) | Архитектура, DI, правила разработки |
| [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md) | Pipeline, статусы, retry, БД |
| [Docs/RevitCrashes.md](Docs/RevitCrashes.md) | История `ACCESS_VIOLATION` |
| [Docs/ADR.md](Docs/ADR.md) | Architecture Decision Log |
| [BimPluginContract.md](https://github.com/Yerkebulan777/RevitBIMFusion/blob/master/Docs/BimPluginContract.md) | Контракт BIM-исполнителей |

## Возможности

- `/export`: `PDF`, `DWG`, `NWC`, `DATA`, `IFC`
- `/automation`: `CLASHREP`, `AUTORES`
- Навигация `RootPath → проект → разделы → 01_RVT`, фильтры `/status`, soft-delete
- PostgreSQL `LISTEN/NOTIFY`, partition scheduling, retry, durable notifications

## Быстрый старт

Требования: .NET 10 SDK, Docker Desktop, Windows.

```powershell
git clone https://github.com/Yerkebulan777/TelegramBot.git
cd TelegramBot
docker compose up -d          # PostgreSQL: telegram_bot@localhost:5432
dotnet build TelegramBot.slnx
```

### Локальная конфигурация

Создать `appsettings.Local.json` в **каждом** проекте (`Server/` и `Worker/`). Значения ниже — дефолт docker-compose.

**TelegramBot.Server/**:

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres;Timeout=30;Minimum Pool Size=2;Connection Idle Lifetime=300"
  },
  "TelegramBot": { "Token": "BOT_TOKEN", "AdminUserId": 0 },
  "FileSystem": { "RootPath": "B:\\" }
}
```

**TelegramBot.Worker/**:

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres;Timeout=30;Minimum Pool Size=2;Connection Idle Lifetime=300"
  },
  "FileSystem": { "TaskDirectory": "C:\\TelegramBot\\TaskDirectory" }
}
```

Схема БД создаётся автоматически при первом запуске Server.

### Запуск

```powershell
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj
dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj
```

Server и Worker должны использовать одну и ту же строку подключения.

## Деплой (production)

Server и Worker разворачиваются как Windows Services (`Host.UseWindowsService()` — под SCM переключается в режим службы, при `dotnet run` работает как консоль).

Исходник инсталлятора — [Installer/TelegramBot.iss](Installer/TelegramBot.iss) (solution item в `TelegramBot.slnx`, Inno Setup 6, в git не компилируется).

**Требование:** [Inno Setup 6](https://jrsoftware.org/isdl.php) — компилятор `ISCC.exe` (не входит в .NET SDK, ставится отдельно).

Пересборка `TelegramBotSetup.exe`:

```powershell
dotnet publish TelegramBot.Server\TelegramBot.Server.csproj -c Release -o Installer\publish\Server
dotnet publish TelegramBot.Worker\TelegramBot.Worker.csproj -c Release -o Installer\publish\Worker
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" Installer\TelegramBot.iss
# admin-установка Inno Setup → "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
# → Installer\Output\TelegramBotSetup.exe (не коммитится, *.exe в .gitignore)
```

Через GUI — открыть `TelegramBot.iss` в Inno Setup Compiler (`Compil32.exe`) и нажать F9.

Мастер установки:
- выбор компонентов — Server / Worker / оба;
- учётная запись службы (не `LocalSystem`/`NetworkService` — им нужен явный доступ к сетевой шаре); поле автоподставляет текущего пользователя (`{userdomain}\{username}`), пароль вводится вручную всегда — Windows не отдаёт его программно ни одному процессу;
- путь к файловой шаре — буква смонтированного диска (`B:`) автоматически резолвится в UNC (`\\server\share`) в сессии инсталлятора, поскольку сама служба маппинг дисков не видит;
- токен бота и `AdminUserId` — только для Server.

Регистрирует службы через `sc.exe create` с авто-рестартом при падении (`sc.exe failure ... actions= restart/...`), патчит `appsettings.Local.json` каждого выбранного компонента, выдаёт NTFS-права на папку установки и на сетевую шару. Удаление — через стандартный деинсталлятор Inno (останавливает и удаляет обе службы).

## Конфигурация

### Server

| Параметр | Назначение |
|---|---|
| `TelegramBot:Token` | обязателен |
| `TelegramBot:AdminUserId` | ID администратора |
| `ConnectionStrings:Postgres` | DSN |
| `FileSystem:RootPath` | обязательный каталог |
| `FileSystem:RvtDirectoryName` | `01_RVT` |
| `FileSystem:ProjectDirectoryName` | `01_PROJECT` |
| `FileSystem:SectionFolderPattern` | regex проектов |
| `FileSystem:LogDirectory` | `%USERPROFILE%\\...\\Logs` |
| `RateLimit:MaxRequests` / `WindowSeconds` | `10` / `60` |
| `RateLimit:MaxFilesPerUserPerDay` | `1000`; `0` отключает |

### Worker

| Параметр | Назначение |
|---|---|
| `ConnectionStrings:Postgres` | DSN |
| `FileSystem:TaskDirectory` | `%USERPROFILE%\\...\\TaskDirectory` |
| `BimIntegration:Min/MaxSupportedVersion` | `2018` / `2026` |
| `DialogDismisser:MaxDismissAttempts` | `10`; `0` отключает kill после неудачных попыток |
| `Worker:ProcessTimeoutMinutes` | `180` |
| `Worker:MaxRetries` | `5` |
| `Worker:RetryDelayBaseSeconds` | `60` |
| `Worker:FallbackPolling/CleanupIntervalSeconds` | `300` |
| `Worker:ProcessMonitorIntervalSeconds` | `30`; минимум/шаг 30 с |
| `Worker:UnresponsiveThresholdSeconds` | `60`; минимум/шаг 30 с |
| `Worker:CompletedSessionRetentionDays` | `30`; `0` отключает |
| `Worker:MaxConcurrentCommands` | `5` |
| `Worker:Commands` | маппинг команд |

Defaults — из option-классов. Полный пример — `appsettings.json` в каждом проекте.

## BIM-контракт

Worker создаёт `task_{project}_{commandId}.xml` и ждёт `result_{project}_{commandId}.xml` в `TaskDirectory`.
- Revit: без command/file CLI-аргументов, TaskFile через `REVITBIMFUSION_TASK_FILE`
- Revit запускается с `/language RUS`
- Revit-команды: `PDF`, `DWG`, `NWC`, `DATA`, `IFC`
- ResultFile обязателен для Revit; exit-code fallback — только wrapper-командам

XSD-схемы — vendored копия в `Docs/BimContract/` (источник истины — `RevitBIMFusion/Docs`, ресинк вручную). Детали — [AGENTS.md](AGENTS.md).

## Логи

Serilog: Console + Seq (`http://localhost:5341`) + rolling files (`%USERPROFILE%\\...\\Logs\\Server\\`, `\\Worker\\`, `\\BimLib\\`). Ежедневно + 50 MiB, до 31 файла.

## Проверка

```powershell
dotnet build TelegramBot.slnx && dotnet format TelegramBot.slnx
```

Тесты отключены.
