# TelegramBot

[![CI](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml)

Windows-сервис на .NET 10: Telegram-бот принимает задания, PostgreSQL хранит очередь, Worker запускает BIM-исполнители.

| Документ | Назначение |
|---|---|
| [AGENTS.md](AGENTS.md) | Архитектура, DI, правила |
| [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md) | Pipeline, статусы, retry, БД |
| [BimPluginContract.md](https://github.com/Yerkebulan777/RevitBIMFusion/blob/master/Docs/BimPluginContract.md) | Контракт TaskFile/ResultFile (v2026-07-24); XSD — `Docs/BimContract/` |

## Быстрый старт

Требования: .NET 10 SDK, Docker Desktop, Windows.

```powershell
git clone https://github.com/Yerkebulan777/TelegramBot.git
cd TelegramBot
docker compose up -d          # PostgreSQL @ localhost:5432
dotnet build TelegramBot.slnx
```

`appsettings.Local.json` в `Server/` и `Worker/` (ниже — дефолт docker-compose). Схема БД — при первом запуске Server. Одна строка Postgres у обоих.

**TelegramBot.Server/**

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres;Timeout=30;Minimum Pool Size=2;Connection Idle Lifetime=300"
  },
  "TelegramBot": { "Token": "BOT_TOKEN" },
  "FileSystem": { "RootPath": "B:\\" }
}
```

**TelegramBot.Worker/**

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres;Timeout=30;Minimum Pool Size=2;Connection Idle Lifetime=300"
  },
  "FileSystem": { "TaskDirectory": "C:\\TelegramBot\\TaskDirectory" }
}
```

```powershell
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj
dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj
```

## Деплой

- **Server** — Windows Service; **Worker** — Task Scheduler (`onlogon` + `/it`), не служба (Revit нужен интерактивный desktop). Учётка Worker должна быть залогинена.
- Инсталлятор: [Installer/TelegramBot.iss](Installer/TelegramBot.iss). Нужен [Inno Setup](https://jrsoftware.org/isdl.php).

| Среда | `ISCC.exe` |
|---|---|
| Локально (IS 7) | `C:\Program Files\Inno Setup 7\ISCC.exe` |
| CI (IS 6) | `C:\Program Files (x86)\Inno Setup 6\ISCC.exe` |

```powershell
Test-Path "C:\Program Files\Inno Setup 7\ISCC.exe"
dotnet build Installer\Installer.build.proj -t:Installer
# → Installer\Output\TelegramBotSetup.exe
```

Поиск ISCC: IS 7 → IS 6; override `/p:IsccExe=...`. Подпись (иначе Defender может снести setup): `/p:SignThumbprint=... /p:SignToolExe=...`.

Мастер: Server/Worker, учётка с доступом к шаре, `B:` → UNC, токен (Server), `GrantLogonRight` до `sc.exe create`.

### Troubleshooting

**Server (7000/7041):** нет «Вход в качестве службы» → `GrantLogonRight.exe DOMAIN\user` или secpol; если откатывает после `gpupdate` — доменная GPO.

**Worker / Revit:** учётка залогинена? `Get-Process Revit | Select Id,SI` — `SI=0` значит нет `/it`.

## Конфигурация

Обязательные: `TelegramBot:Token`, `ConnectionStrings:Postgres`, `FileSystem:RootPath` (Server), `FileSystem:TaskDirectory` (Worker). Остальное — defaults в option-классах / `appsettings.json`.

Логи: Serilog → Console + Seq (`http://localhost:5341`) + `%USERPROFILE%\...\Logs\`.

```powershell
dotnet build TelegramBot.slnx && dotnet format TelegramBot.slnx
```
