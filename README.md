# TelegramBot

[![CI](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml)

Windows .NET 10: Telegram-бот ставит задания в PostgreSQL, Worker запускает BIM-исполнители.

| Компонент | Развёртывание |
|---|---|
| **Server** | Task Scheduler (`onlogon`, интерактивная сессия) |
| **Worker** | Task Scheduler (`onlogon`, интерактивная сессия); нужна залогиненная учётка (Revit — видимый desktop) |

Оба стартуют после входа (на выделенном ПК — автологон). Почему не Windows Service — [ADR-012](Docs/ADR.md). Server после старта показывает иконку в трее: серая — ещё проверяет, зелёная — БД и Telegram доступны, жёлтая — одно из двух недоступно, красная — оба недоступны. Подсказка при наведении и меню по правой кнопке.

Детали pipeline: [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md). Правила для агентов: [AGENTS.md](AGENTS.md).

## Быстрый старт

Нужны .NET 10 SDK, Docker Desktop, Windows.

```powershell
git clone https://github.com/Yerkebulan777/TelegramBot.git
cd TelegramBot
copy .env.example .env
docker compose up -d
dotnet build TelegramBot.slnx
```

Пароль в `.env` должен совпадать с `ConnectionStrings:Postgres`. `docker-compose.yml` слушает `127.0.0.1:5432` и требует `POSTGRES_PASSWORD`. Секреты — в `appsettings.Local.json` (gitignore) у Server и Worker; схема БД поднимается Server'ом в фоне.

**Server** — токен и Postgres:

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres;Timeout=30;Minimum Pool Size=2;Connection Idle Lifetime=300"
  },
  "TelegramBot": { "Token": "BOT_TOKEN" }
}
```

**Worker** — та же строка Postgres. `FileSystem:TaskDirectory` необязателен (default — `%USERPROFILE%\Documents\TelegramBot\TaskDirectory`).

```powershell
dotnet run --project TelegramBot.Server/TelegramBot.Server.csproj
dotnet run --project TelegramBot.Worker/TelegramBot.Worker.csproj
```

## Деплой (подписанный Setup)

На ПК сборки: .NET 10 SDK, Inno Setup 7, `signtool.exe`. Одна команда от учётки сборки:

```powershell
dotnet build TelegramBot.slnx -c Release
# или: powershell -ExecutionPolicy Bypass -File .\scripts\build-signed-installer.ps1
```

Release Build Server сам вызывает `scripts\build-signed-installer.ps1` (отключить: `-p:BuildSignedInstaller=false`; на CI пропускается). Результат: `Installer\Output\TelegramBotSetup.exe`. CER: `%USERPROFILE%\Documents\TelegramBot-CodeSigning\TelegramBot-Internal-Code-Signing-<THUMBPRINT>.cer`.

На целевом ПК: Docker Desktop, Setup от администратора. Мастер ставит Server/Worker, пишет `appsettings.Local.json`. При выборе Server, если `localhost:5432` ещё не отвечает рабочей строкой — копирует `docker-compose.yml` в `%ProgramData%\TelegramBot\PostgreSQL`, создаёт `.env` со случайным паролем (не перезаписывает) и поднимает PostgreSQL 18. Только Worker контейнер не поднимает. Нативный PostgreSQL Setup не ставит.

После старта Server первый пользователь, успешно задавший корень через `/help`, становится администратором. Буква сетевого диска (`Z:\`) или UNC резолвится на машине Server; в Telegram UNC не показывается. Локальные диски не принимаются.

CI: неподписанный Setup — `dotnet build Installer\Installer.build.proj -t:Installer`.

### Смена рабочей папки

Пуск → TelegramBot → Изменить рабочую папку → в течение 30 мин подтвердить в `/help` администратором. Переустановка не нужна; уже созданные задания сохраняют свой снимок пути.

### Доверие к CER

GPO: Trusted Root + Trusted Publishers. Или на тестовом ПК от администратора:

```powershell
$cer = "$env:USERPROFILE\Documents\TelegramBot-CodeSigning\TelegramBot-Internal-Code-Signing-THUMBPRINT.cer"
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```

Продление (~90 дней до истечения): `.\scripts\setup-internal-code-signing.ps1 -Renew` → раздать новый CER → снова собрать Setup. PFX не коммитить.

### Troubleshooting: Server не стартует после перезагрузки

Setup регистрирует `TelegramBotServer` как задачу планировщика при входе. После reboot она стартует, только если учётка из мастера уже в сессии.

```powershell
schtasks /query /tn TelegramBotServer /v /fo list
Get-Process TelegramBot.Server -ErrorAction SilentlyContinue
```

| Симптом | Причина | Что делать |
|---|---|---|
| Задачи нет | Setup не ставили / ставили только Worker | Повторить Setup, компонент Server |
| Setup: ошибка XML `(1,40)`, «не удалось переключить кодировку» | Старый Setup записывал XML задачи в UTF-8 | Пересобрать Setup из актуальных исходников и повторить установку: XML записывается в UTF-16LE с BOM |
| Задача есть, процесса нет, никто не залогинен | Logon-trigger ждёт интерактивный вход | Включить автологон под учёткой задачи |
| Старая служба `TelegramBotServer` в SCM, события **7038** | leftover Windows Service с прошлых Setup; вход службы не умеет cached credentials и падает, если повреждён secure channel (`Test-ComputerSecureChannel` = False) | Удалить службу (`sc.exe delete TelegramBotServer`) и поставить актуальный Setup — задача планировщика этот вход не использует |
| Процесс стартовал и сразу вышел | нет `appsettings.Local.json` / токена / Postgres | Логи `%USERPROFILE%\Documents\TelegramBot\Logs\Server`; Docker Desktop и контейнер `postgres_telegram` |
| Иконки в трее нет | процесс не запущен или сессия не интерактивная | `Get-Process TelegramBot.Server`; задача должна быть `InteractiveToken`. Жёлтая/красная иконка — БД или Telegram недоступны; меню → «Открыть логи» |

## Поведение (кратко)

- Очередь: Worker polling 1 с (`Worker:FallbackPollingIntervalSeconds`; 0 = 1). Одна операция на файл (`Partition`); разные файлы — до `MaxConcurrentCommands`. Между глобальными запусками Revit — ≥ 15 с, между запусками AutoCAD (`MERGEDWG`) — ≥ 15 с (`ProcessLaunchGate`).
- Дубликаты активных пар команда+файл пропускаются; `/status` rerun создаёт новое задание.
- Уведомления старта/итога — durable outbox (poll 3 с). Итог защищён от интерактивной очистки. Defaults очистки — `MessageCleanupOptions`.
- Обновление: остановить задачи Server/Worker → Server (миграция схемы) → Worker.

## BIM-контракт

Эталон TaskFile/ResultFile: [BimPluginContract.md](https://github.com/Yerkebulan777/RevitBIMFusion/blob/master/Docs/BimPluginContract.md) (v2026-09-09). Локально — `../RevitBIMFusion/Docs`. XSD — `Docs/BimContract/` (ресинк вручную). Копию контракта в этот репозиторий не класть.
