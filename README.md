# TelegramBot

[![CI](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml)

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
  "TelegramBot": { "Token": "BOT_TOKEN" },
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

Server разворачивается как Windows Service (`Host.UseWindowsService()` — под SCM переключается в режим службы, при `dotnet run` работает как консоль).

**Worker — НЕ служба, а задача Task Scheduler с триггером "при входе в систему".** Worker запускает Revit, а Revit — GUI-приложение; службы Windows работают в изолированной Session 0 без доступа к реальному рабочему столу (опция "Allow service to interact with desktop" убрана ещё в Vista), поэтому окна и диалоги Revit, запущенного службой, никто не видит и не может закрыть — процесс виснет молча. Задача планировщика с флагом `/it` запускается в интерактивной сессии реального пользователя, и Revit отображается как обычно.

Плата за это: машина должна оставаться залогиненной под учёткой Worker'а (для выделенных машин — настроить авто-логон), иначе Worker не запустится до следующего входа в систему. Также у `schtasks` в базовом режиме нет аналога `sc.exe`-политики автоперезапуска при падении — при необходимости можно добавить через XML-задачу с `<RestartOnFailure>`.

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
- учётная запись (не `LocalSystem`/`NetworkService` — им нужен явный доступ к сетевой шаре); поле автоподставляет текущего пользователя (`{userdomain}\{username}`); пароль нужен только для Server (регистрируется через `sc.exe`) — Worker как интерактивная задача планировщика запускается без хранения пароля, но требует, чтобы эта учётка была залогинена в системе;
- путь к файловой шаре — буква смонтированного диска (`B:`) автоматически резолвится в UNC (`\\server\share`) в сессии инсталлятора, поскольку ни служба, ни задача планировщика в фоновом режиме маппинг дисков не видят;
- токен бота — только для Server.

Регистрирует Server через `sc.exe create` с авто-рестартом при падении (`sc.exe failure ... actions= restart/...`), Worker — через `schtasks /create` с триггером `/sc onlogon /it` (см. выше про Session 0). Патчит `appsettings.Local.json` каждого выбранного компонента, выдаёт NTFS-права на папку установки и на сетевую шару. Удаление — через стандартный деинсталлятор Inno (останавливает/удаляет службу Server и задачу планировщика Worker).

**Server не стартует (ошибка входа, event ID 7000/7041 в System log):** учётной записи не хватает права **"Вход в качестве службы"** (`Log on as a service`) — `sc.exe create` обычно назначает его автоматически, но доменная GPO может это право откатывать. Выдать вручную:

1. `Win+R` → `secpol.msc`
2. Локальные политики → Назначение прав пользователя → **"Вход в качестве службы"**
3. Добавить учётную запись службы (например, `DOMAIN\username`)
4. `gpupdate /force`, затем `Start-Service TelegramBotServer`

Если право пропадает повторно — политику перезаписывает доменная GPO, нужно менять её у администратора домена, а не локально.

**Worker не запускается / Revit "висит" без результата:** Worker — задача планировщика, не служба (см. выше), поэтому запускается только при интерактивном входе учётки в систему. Проверить: `schtasks /query /tn TelegramBotWorker /v /fo list`. Если задача есть, но не запущена — учётка не залогинена; если процессы Revit запущены, но зависли без результата — проверьте `Get-Process Revit | Select Id,SI` (столбец `SI` = 0 означает, что задача всё-таки выполнилась в Session 0, а не интерактивно — перепроверьте `/it` в определении задачи).

## Конфигурация

### Server

| Параметр | Назначение |
|---|---|
| `TelegramBot:Token` | обязателен |
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
