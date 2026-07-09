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
- Дедупликация RVT, дневной лимит, защита от дублей
- PostgreSQL `LISTEN/NOTIFY`, partition scheduling, retry, durable notifications

## Быстрый старт

Требования: .NET 10 SDK, PostgreSQL 18, Windows.

```powershell
docker compose up -d
dotnet build TelegramBot.slnx
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj
dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj
```

Секреты в gitignored `appsettings.Local.json`:

```json
{
  "TelegramBot": { "Token": "BOT_TOKEN", "AdminUserIds": [123456789] },
  "FileSystem": { "RootPath": "B:\\" }
}
```

## Настройка PostgreSQL в Docker на новом компьютере

### 1. Установить зависимости

Нужны:
- Docker Desktop с включенным Linux containers mode
- .NET 10 SDK
- Git

Проверка:

```powershell
docker --version
docker compose version
dotnet --version
git --version
```

### 2. Получить репозиторий

```powershell
git clone https://github.com/Yerkebulan777/TelegramBot.git
cd TelegramBot
```

### 3. Запустить PostgreSQL

`docker-compose.yml` уже содержит готовый PostgreSQL:
- container: `telegram-bot-db`
- database: `telegram_bot`
- user/password: `postgres` / `postgres`
- port: `5432`
- volume: `pgdata`

```powershell
docker compose up -d
```

Проверить, что контейнер поднялся:

```powershell
docker compose ps
docker exec telegram-bot-db pg_isready -U postgres -d telegram_bot
```

Проверить подключение и список таблиц:

```powershell
docker exec -it telegram-bot-db psql -U postgres -d telegram_bot
\dt
\q
```

На чистой базе таблиц может еще не быть — они создаются Server-приложением при первом запуске.

### 4. Настроить локальные appsettings

Создать `appsettings.Local.json` в `TelegramBot.Server/`:

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres;Timeout=30;Minimum Pool Size=2;Connection Idle Lifetime=300"
  },
  "TelegramBot": {
    "Token": "BOT_TOKEN",
    "AdminUserIds": [123456789]
  },
  "FileSystem": {
    "RootPath": "B:\\"
  }
}
```

Создать `appsettings.Local.json` в `TelegramBot.Worker/`:

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres;Timeout=30;Minimum Pool Size=2;Connection Idle Lifetime=300"
  },
  "FileSystem": {
    "TaskDirectory": "C:\\TelegramBot\\TaskDirectory"
  }
}
```

Если меняются `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` или порт в `docker-compose.yml`, такую же правку нужно сделать в обеих строках `ConnectionStrings:Postgres`.

### 5. Создать схему БД

Ручные SQL-скрипты для чистой установки не нужны. Server при старте выполняет idempotent-инициализацию схемы: создает `BotUsers`, `Sessions`, `Commands`, `TrackedMessages`, `NotificationOutbox`, индексы и добавляет админов из `TelegramBot:AdminUserIds`.

```powershell
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj
```

После успешного старта можно проверить таблицы:

```powershell
docker exec -it telegram-bot-db psql -U postgres -d telegram_bot
\dt
SELECT UserId, Role, Status FROM BotUsers;
\q
```

### 6. Запустить Worker

В отдельном PowerShell:

```powershell
dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj
```

Server и Worker должны использовать одну и ту же строку подключения.

### Обслуживание Docker-БД

Остановить контейнер без удаления данных:

```powershell
docker compose stop
```

Запустить снова:

```powershell
docker compose up -d
```

Посмотреть логи PostgreSQL:

```powershell
docker logs telegram-bot-db
```

Сделать backup:

```powershell
docker exec telegram-bot-db pg_dump -U postgres -d telegram_bot -Fc -f /tmp/telegram_bot.dump
docker cp telegram-bot-db:/tmp/telegram_bot.dump .\telegram_bot.dump
```

Восстановить backup в пустую БД:

```powershell
docker cp .\telegram_bot.dump telegram-bot-db:/tmp/telegram_bot.dump
docker exec telegram-bot-db pg_restore -U postgres -d telegram_bot --clean --if-exists /tmp/telegram_bot.dump
```

Полностью удалить локальную БД и volume:

```powershell
docker compose down -v
docker compose up -d
```

## Конфигурация

### Server

| Параметр | Назначение |
|---|---|
| `TelegramBot:Token` | обязателен |
| `TelegramBot:AdminUserIds` | ID администраторов |
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

Canonical XSD — `RevitBIMFusion/Docs`. Детали — [AGENTS.md](AGENTS.md).

## Логи

Serilog: Console + Seq (`http://localhost:5341`) + rolling files (`%USERPROFILE%\\...\\Logs\\Server\\`, `\\Worker\\`, `\\BimLib\\`). Ежедневно + 50 MiB, до 31 файла. Структурированные с `CommandId`, `SessionId`, `CorrelationId`.

## Проверка

```powershell
dotnet build TelegramBot.slnx && dotnet format TelegramBot.slnx
```

Тесты отключены.
