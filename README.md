# TelegramBot

[![CI](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml)
[![Qodana](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/code_quality.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/code_quality.yml)

Windows-сервис на .NET 10: Telegram-бот принимает задания, PostgreSQL хранит очередь, отдельный Worker запускает BIM/AI-исполнители.

## Документация

| Документ | Назначение |
|---|---|
| [AGENTS.md](AGENTS.md) | Архитектура, DI, правила разработки |
| [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md) | Очередь, статусы, retry, уведомления, схема БД |
| [Docs/RevitCrashes.md](Docs/RevitCrashes.md) | История расследования `ACCESS_VIOLATION` |
| [BimPluginContract.md](https://github.com/Yerkebulan777/RevitBIMFusion/blob/master/Docs/BimPluginContract.md) | Единственный эталонный контракт BIM-исполнителей |

## Возможности

- `/export`: `PDF`, `DWG`, `NWC`, `DATA`, `IFC`.
- `/automation`: `BIMDOC`, `CLASHREP`, `AUTORES`.
- Навигация `RootPath → проект → 01_PROJECT → разделы → 01_RVT`.
- Фильтры, пагинация и soft-delete через `/status`.
- Регистрация пользователей и одобрение доступа администраторами.
- Дедупликация RVT, дневной лимит файлов и защита от повторной постановки.
- PostgreSQL `LISTEN/NOTIFY`, leases, partition scheduling и retry.
- Durable completion notifications через `NotificationOutbox`.
- Корреляция сессии и команд по `CorrelationId`.

## Проекты

```text
TelegramBot.Core   ← TelegramBot.Data
       ↑                    ↑
       ├── TelegramBot.Server
       └── TelegramBot.Worker
                └── BimLib/
```

| Проект | Ответственность |
|---|---|
| `TelegramBot.Core` | модели, DTO, конфигурация, константы, общие helpers |
| `TelegramBot.Data` | Dapper/Npgsql, SQL и инициализация схемы |
| `TelegramBot.Server` | Telegram long-polling, команды, callbacks, уведомления |
| `TelegramBot.Worker` | claim очереди, внешние процессы, ResultFile, retry, cleanup |

Все сервисы DI — singleton. Приложение и BimLib предназначены только для Windows.

## Быстрый старт

Требования: .NET 10 SDK, PostgreSQL 18, Windows; для BIM-команд — установленные Revit/Navisworks и плагины.

```powershell
docker compose up -d
dotnet build TelegramBot.slnx
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj
dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj
```

Server и Worker — независимые процессы с общей БД.

Секреты храните в gitignored `appsettings.Local.json`:

```json
{
  "TelegramBot": {
    "Token": "BOT_TOKEN",
    "AdminUserIds": [123456789]
  },
  "FileSystem": {
    "RootPath": "B:\\"
  }
}
```

Переменные окружения используют стандартный синтаксис .NET, например `TelegramBot__Token`.

## Конфигурация

### Server

| Параметр | По умолчанию / назначение |
|---|---|
| `TelegramBot:Token` | обязателен |
| `TelegramBot:AdminUserIds` | ID администраторов |
| `ConnectionStrings:Postgres` | DSN PostgreSQL |
| `FileSystem:RootPath` | обязательный существующий каталог |
| `FileSystem:RvtDirectoryName` | `01_RVT` |
| `FileSystem:ProjectDirectoryName` | `01_PROJECT` |
| `FileSystem:SectionFolderPattern` | regex для каталогов проектов |
| `FileSystem:LogDirectory` | `%USERPROFILE%\Documents\TelegramBot\Logs` |
| `RateLimit:MaxRequests` / `WindowSeconds` | `30` / `60` в committed config |
| `RateLimit:MaxFilesPerUserPerDay` | `1000`; `0` отключает |

### Worker

| Параметр | По умолчанию / назначение |
|---|---|
| `ConnectionStrings:Postgres` | DSN PostgreSQL |
| `FileSystem:TaskDirectory` | `%USERPROFILE%\Documents\TelegramBot\TaskDirectory` |
| `FileSystem:LogDirectory` | `%USERPROFILE%\Documents\TelegramBot\Logs` |
| `BimIntegration:MinSupportedVersion` / `MaxSupportedVersion` | `2018` / `2026` |
| `DialogDismisser:Enabled` | `false` в committed config |
| `Worker:ProcessTimeoutMinutes` | `180` |
| `Worker:MaxRetries` | `5` |
| `Worker:RetryDelayBaseSeconds` | `60` |
| `Worker:PermanentFailureExitCodes` | пустой набор |
| `Worker:FallbackPollingIntervalSeconds` | `300` |
| `Worker:CleanupIntervalSeconds` | `300` |
| `Worker:ProcessMonitorIntervalSeconds` | `30`; `0` отключает |
| `Worker:CompletedSessionRetentionDays` | `30`; `0` отключает retention cleanup |
| `Worker:MaxConcurrentCommands` | `5` |
| `Worker:Commands` | обязательный mapping команд на executable/arguments/extensions |

Полный пример находится в `TelegramBot.Server/appsettings.json` и `TelegramBot.Worker/appsettings.json`. Значения, отсутствующие в JSON, берутся из option-классов.

## Поток задания

1. Server последовательно обрабатывает обновления одного пользователя и параллельно — разных пользователей.
2. Пользователь выбирает команды, проект и разделы.
3. Server параллельно сканирует `01_RVT`, оставляет RVT больше 50 MiB с допустимым именем и дедуплицирует список.
4. Одна транзакция создаёт `Sessions` и `Commands`, затем отправляет `pg_notify('new_tasks', correlationId)`.
5. Worker атомарно claim-ит доступные partition-команды по priority и запускает не больше `MaxConcurrentCommands`.
6. Исполнитель пишет `ResultFile`; Worker обновляет статус или планирует retry.
7. Последняя команда сессии создаёт запись `NotificationOutbox`; Server отправляет итог и помечает запись `sent`.

Подробности — в [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md).

## BIM-контракт

Worker создаёт `task_{project}_{commandId}.xml` и ожидает `result_{project}_{commandId}.xml` в `TaskDirectory`.

- Revit запускается без контрактных CLI-аргументов.
- TaskFile передаётся только через process-scoped `REVITBIMFUSION_TASK_FILE`.
- `PDF`, `DWG`, `NWC`, `DATA`, `IFC`, `BIMDOC` считаются Revit-командами.
- Для Revit ResultFile обязателен; exit code `0` без ResultFile не означает успех.
- Exit-code fallback разрешён только console/wrapper-командам.
- TaskFile атомарно записывается и валидируется встроенной canonical XSD.

Текущая поддержка эталонного Revit AddIn:

| Команда | Статус |
|---|---|
| `PDF`, `DWG`, `NWC`, `DATA` | поддерживается |
| `IFC`, `BIMDOC` | planned; AddIn возвращает permanent `Unsupported command` |
| `CLASHREP`, `AUTORES` | внешние wrapper/agent-команды |

Canonical XSD берётся при сборке из соседнего `RevitBIMFusion/Docs`; другой путь задаётся через `BIM_CONTRACT_DIRECTORY`.

## Логи

Serilog пишет в Console, Seq (`http://localhost:5341`) и rolling files:

```text
%USERPROFILE%\Documents\TelegramBot\Logs\
├── Server\log-YYYYMMDD.txt
└── Worker\
    ├── log-YYYYMMDD.txt
    └── BimLib\log-YYYYMMDD.txt
```

Файлы ротируются ежедневно и по 50 MiB, хранится до 31 файла. `FileSystem:LogDirectory` меняет базовый каталог.

Логи структурированные: в ключевых событиях сохраняются `CommandId`, `SessionId`, `CorrelationId`, `UserId`, `ProcessId`. Нормальные циклические события не пишутся на `Information`; подробная диагностика доступна на `Debug`.

## Проверка

```powershell
dotnet build TelegramBot.slnx
dotnet format TelegramBot.slnx
```

Тесты намеренно отключены: не добавляйте test projects и не запускайте `dotnet test`.
