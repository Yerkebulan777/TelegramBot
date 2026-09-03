# TelegramBot

[![CI](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml)

Windows-сервис на .NET 10: Telegram-бот принимает задания, PostgreSQL хранит очередь, Worker запускает BIM-исполнители.

- **Server** — Windows Service
- **Worker** — Task Scheduler (`onlogon` + `/it`); учётка должна быть залогинена (Revit нужен интерактивный desktop)

Подробности: [AGENTS.md](AGENTS.md), [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md), [BIM-контракт](#bim-контракт).

## Быстрый старт

Нужны .NET 10 SDK, Docker Desktop, Windows.

```powershell
git clone https://github.com/Yerkebulan777/TelegramBot.git
cd TelegramBot
docker compose up -d
dotnet build TelegramBot.slnx
```

Секреты и локальные пути — в `appsettings.Local.json` у Server и Worker (файл в `.gitignore`). Одна строка Postgres у обоих; схема БД поднимается Server'ом в фоне (`DatabaseInitializerService`) и не блокирует старт.

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
  }
}
```

`FileSystem:TaskDirectory` у Worker необязателен: если не задан — `%USERPROFILE%\Documents\TelegramBot\TaskDirectory`. Остальное — defaults в option-классах / `appsettings.json`.

```powershell
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj
dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj
```

## Деплой с автоматической подписью

На ПК сборки — **одна команда**. Первый запуск сам создаёт сертификат в `Cert:\CurrentUser\My` и больше его не перевыпускает.

Нужны: [.NET 10 SDK](https://dotnet.microsoft.com/download), [Inno Setup 7](https://jrsoftware.org/isdl.php), `signtool.exe` (Windows SDK или ClickOnce Signing Tools). Запускать **от той учётки**, под которой собираете.

```powershell
cd C:\path\to\TelegramBot
powershell -ExecutionPolicy Bypass -File .\scripts\build-signed-installer.ps1
```

Скрипт: сертификат → `dotnet publish` (Server, Worker, GrantLogonRight) → подпись всех `.exe` (DigiCert / Sectigo / GlobalSign; если timestamp недоступен — подпись без него) → Inno Setup (Setup + Uninstall) → проверка подписей.

Результат: `Installer\Output\TelegramBotSetup.exe`  
Публичный CER: `%USERPROFILE%\Documents\TelegramBot-CodeSigning\TelegramBot-Internal-Code-Signing-<THUMBPRINT>.cer`

Скопируйте Setup на внутренний UNC и на целевом ПК запустите **от администратора**. Мастер: Server/Worker, учётка, `B:` → UNC, токен бота.

CI собирает **неподписанный** Setup: `dotnet build Installer\Installer.build.proj -t:Installer`. Для релиза на рабочие ПК нужна команда выше.

### Один раз: доверить CER на рабочих ПК

Это нельзя сделать из скрипта сборки (нужны GPO или права администратора на целевых машинах).

**GPO:** `Computer Configuration → Policies → Windows Settings → Security Settings → Public Key Policies`

- **Trusted Root Certification Authorities**
- **Trusted Publishers**

**Или один тестовый ПК** от администратора (точный путь печатает сборка):

```powershell
$cer = "$env:USERPROFILE\Documents\TelegramBot-CodeSigning\TelegramBot-Internal-Code-Signing-THUMBPRINT.cer"
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```

- **CER** — публичный, раздают на рабочие ПК.
- **PFX** — только в сейф, по желанию: `.\scripts\setup-internal-code-signing.ps1 -BackupPfx`. Не коммитить, не раздавать.

### Продление сертификата

За ~90 дней до истечения сборка предупредит. Тогда:

1. `.\scripts\setup-internal-code-signing.ps1 -Renew`
2. Раздать **новый** CER через GPO (старый не удалять, пока стоят сборки, подписанные им)
3. Снова `.\scripts\build-signed-installer.ps1`

Без `-Renew` / `-BackupPfx` setup-скрипт ничего не делает: обычная сборка сама создаёт сертификат при первом запуске.

## BIM-контракт

Эталон TaskFile / ResultFile: [BimPluginContract.md](https://github.com/Yerkebulan777/RevitBIMFusion/blob/master/Docs/BimPluginContract.md) (v2026-08-10). Локально — `../RevitBIMFusion/Docs`. Vendored XSD — `Docs/BimContract/` (ресинк из эталона вручную). Локальную копию контракта в этот репозиторий не класть.
