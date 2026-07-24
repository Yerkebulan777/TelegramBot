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

Server — Windows Service (`Host.UseWindowsService()`; под SCM — служба, при `dotnet run` — консоль).

**Worker — НЕ служба, а задача Task Scheduler** (триггер "при входе в систему"). Причина: Worker запускает Revit (GUI), а службы работают в изолированной Session 0 без рабочего стола — окна и диалоги Revit, запущенного службой, никто не видит, процесс виснет молча. Задача планировщика с флагом `/it` запускается в интерактивной сессии — Revit отображается нормально.

Плата: машина должна оставаться залогиненной под учёткой Worker'а (на выделенных машинах — авто-логон), иначе Worker не запустится до следующего входа.

### Сборка инсталлятора

Скрипт — [Installer/TelegramBot.iss](Installer/TelegramBot.iss) (Inno Setup, не компилируется в git). Требование: [Inno Setup](https://jrsoftware.org/isdl.php) — компилятор `ISCC.exe` (не входит в .NET SDK).

```powershell
dotnet build Installer\Installer.build.proj -t:Installer
# → Installer\Output\TelegramBotSetup.exe (не коммитится, *.exe в .gitignore)
```

Публикует Server/Worker/GrantLogonRight и вызывает ISCC — по умолчанию `C:\Program Files (x86)\Inno Setup 6\ISCC.exe`. Другая версия/путь (например, Inno Setup 7):

```powershell
dotnet build Installer\Installer.build.proj -t:Installer /p:IsccExe="C:\Program Files\Inno Setup 7\ISCC.exe"
```

Через GUI — открыть `TelegramBot.iss` в Inno Setup Compiler (`Compil32.exe`), F9.

**Подпись (опционально):** свежесобранный неподписанный `TelegramBotSetup.exe` Windows Defender может удалить как `Trojan:Win32/Bearfoos.B!ml` (ML-эвристика на непроверенный installer с privileged-действиями). Подписать — передать thumbprint сертификата из `Cert:\CurrentUser\My`:

```powershell
dotnet build Installer\Installer.build.proj -t:Installer /p:SignThumbprint=<thumbprint> /p:SignToolExe="<путь к signtool.exe>"
```

Подписывает `Server.exe`/`Worker.exe`/`GrantLogonRight.exe` и финальный `TelegramBotSetup.exe`. `signtool.exe` не входит в .NET SDK — часть Windows SDK или `Microsoft SDKs\ClickOnce\SignTool`. Self-signed сертификат достаточно создать один раз: `New-SelfSignedCertificate -Type CodeSigning -Subject "CN=..." -CertStoreLocation Cert:\CurrentUser\My`.

### Мастер установки

- выбор компонентов — Server / Worker / оба;
- учётная запись (не `LocalSystem`/`NetworkService` — нужен доступ к сетевой шаре), поле автоподставляет текущего пользователя; пароль — только для Server (`sc.exe`), Worker как интерактивная задача пароль не хранит, но требует, чтобы учётка была залогинена;
- путь к файловой шаре — буква диска (`B:`) резолвится в UNC (`\\server\share`) в сессии инсталлятора (служба/задача в фоне маппинг дисков не видят);
- токен бота — только для Server.

Регистрирует Server через `sc.exe create` с авто-рестартом (`sc.exe failure ... restart/...`), Worker — через `schtasks /create /sc onlogon /it`. Патчит `appsettings.Local.json` каждого компонента, выдаёт NTFS-права на папку установки и сетевую шару. Право **"Вход в качестве службы"** выдаётся автоматически через `Installer\GrantLogonRight` (`LsaAddAccountRights`, до `sc.exe create` — сам `sc.exe` это право не назначает). Удаление — стандартный деинсталлятор Inno.

### Troubleshooting

**Server не стартует (event ID 7000/7041):** не хватает права "Вход в качестве службы". Проверить/выдать вручную:

```powershell
GrantLogonRight.exe DOMAIN\username    # Installer\publish\GrantLogonRight
# или secpol.msc → Локальные политики → Назначение прав пользователя → "Вход в качестве службы"
gpupdate /force
Start-Service TelegramBotServer
```

Если право пропадает повторно после `gpupdate` — доменная GPO откатывает его при каждом обновлении; чинить на стороне домена, локальная переустановка не поможет.

**Worker не запускается / Revit "висит":** `schtasks /query /tn TelegramBotWorker /v /fo list` — если задача не запущена, учётка не залогинена. Если Revit запущен, но завис — `Get-Process Revit | Select Id,SI`: `SI=0` означает, что процесс выполнился в Session 0, а не интерактивно (проверить `/it` в определении задачи).

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

Эталон: [BimPluginContract.md](https://github.com/Yerkebulan777/RevitBIMFusion/blob/master/Docs/BimPluginContract.md) (v2026-07-24). XSD — vendored в `Docs/BimContract/` (ресинк вручную из `RevitBIMFusion/Docs`).

Worker создаёт `task_{project}_{commandId}.xml`, ждёт `result_{project}_{commandId}.xml` в `TaskDirectory`.
- Revit: без контрактных CLI-аргументов; TaskFile path — `REVITBIMFUSION_TASK_FILE`; допускается `/language RUS`
- `.rvt` только в TaskFile XML, не в process args
- Revit `commandText`: `PDF`, `DWG`, `NWC`, `IFC`, `DATA`
- ResultFile обязателен для Revit; exit-code fallback — только wrapper (`CLASHREP`, `AUTORES`)
- `status=failed` / `cancelled` от плагина → permanent Failed без retry; нет/битый ResultFile → retry policy

## Логи

Serilog: Console + Seq (`http://localhost:5341`) + rolling files (`%USERPROFILE%\\...\\Logs\\Server\\`, `\\Worker\\`, `\\BimLib\\`). Ежедневно + 50 MiB, до 31 файла.

## Проверка

```powershell
dotnet build TelegramBot.slnx && dotnet format TelegramBot.slnx
```

Тесты отключены.
