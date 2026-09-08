# TelegramBot

[![CI](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml)

Windows-сервис на .NET 10: Telegram-бот принимает задания, PostgreSQL хранит очередь, Worker запускает BIM-исполнители.

- **Server** — Windows Service
- **Worker** — Task Scheduler (`onlogon` + `/it`); учётка должна быть залогинена (Revit нужен интерактивный desktop)

Подробности: [AGENTS.md](AGENTS.md), [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md), [BIM-контракт](#bim-контракт).

В итоговом уведомлении ошибки и предупреждения показываются отдельно для каждой команды: относительный путь файла, код команды и причина на следующей строке. Путь отсчитывается от рабочего корня, сохранённого в задании; для старых заданий без корня показывается имя файла. Этот же путь заменяет абсолютный путь входного файла в тексте причины. Типовые технические ошибки отображаются по-русски; если причина отсутствует, бот явно сообщает об этом. В уведомление попадает первая строка причины (до 200 символов), максимум 15 пунктов в каждом разделе.

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
  "TelegramBot": { "Token": "BOT_TOKEN" }
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
dotnet build TelegramBot.slnx -c Release          # инсталлятор соберётся автоматически
# или напрямую:
powershell -ExecutionPolicy Bypass -File .\scripts\build-signed-installer.ps1
```

Release-сборка (`Directory.Build.targets`, якорь — `TelegramBot.Server`) после Build сама запускает `scripts\build-signed-installer.ps1`. Отключить: `-p:BuildSignedInstaller=false`. Автоматически пропускается на CI (`GITHUB_ACTIONS`/`TF_BUILD`/`ContinuousIntegrationBuild`) и внутри самого скрипта (`TELEGRAMBOT_INSTALLER_BUILD=1` — защита от рекурсии через `dotnet publish`).

Скрипт: сертификат → `dotnet publish` (Server, Worker, GrantLogonRight) → подпись всех `.exe` (DigiCert / Sectigo / GlobalSign; если timestamp недоступен — подпись без него) → Inno Setup (Setup + Uninstall) → проверка подписей.

Результат: `Installer\Output\TelegramBotSetup.exe`  
Публичный CER: `%USERPROFILE%\Documents\TelegramBot-CodeSigning\TelegramBot-Internal-Code-Signing-<THUMBPRINT>.cer`

Скопируйте Setup на внутренний UNC и на целевом ПК запустите **от администратора**. Мастер: Server/Worker, учётка, токен бота. После запуска Server первый пользователь, который успешно задаст корневой путь через `/help`, становится администратором; далее менять путь может только он. Администратор отправляет букву сетевого диска, например `Z:\` (либо вложенную папку `Z:\Проекты` или готовый `\\сервер\шара`). Для буквы Server сначала использует своё подключение, затем ищет единственное совпадение в загруженных профилях Windows на машине Server; UNC сохраняется только в БД и не показывается в Telegram. Профиль с подключённым диском должен быть загружен, а учётные записи Server и Worker — иметь доступ к шаре. Локальные диски не принимаются.

CI собирает **неподписанный** Setup: `dotnet build Installer\Installer.build.proj -t:Installer`.

### Изменить рабочую папку после установки

1. На машине Server войдите в Windows под пользователем, у которого подключён нужный диск.
2. Откройте **Пуск → TelegramBot → Изменить рабочую папку**.
3. Выберите диск или папку и подготовьте изменение.
4. В течение 30 минут откройте `/help` в Telegram и подтвердите изменение администратором бота.

Переустановка и перезапуск не нужны. Server и Worker должны иметь доступ к выбранной сетевой папке. Созданные ранее задания используют прежний путь.

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

## Очистка сообщений Telegram

Предупреждения о превышении лимита запросов, анонимном профиле и отклонённые входящие сообщения регистрируются для очистки. Предупреждение о лимите отправляется пользователю не чаще одного раза за `RateLimit:WindowSeconds`, включая нажатия кнопок. Они удаляются вместе с историей при следующей принятой slash-команде; без взаимодействия действует фоновая очистка `MessageCleanup` (по умолчанию сообщения старше 24 часов, проверка каждые 15 минут).

Удаление выполняется пакетами до 100 сообщений, с повторными попытками при временных ошибках и ожиданием `RetryAfter` при ответе 429. Неудачные удаления сохраняются для последующей попытки. Удаление сессии не сбрасывает tracking её сообщений. Уже отправленные ранее предупреждения, которые не попали в tracking, нужно удалить вручную.

## BIM-контракт

Эталон TaskFile / ResultFile: [BimPluginContract.md](https://github.com/Yerkebulan777/RevitBIMFusion/blob/master/Docs/BimPluginContract.md) (v2026-09-03). Локально — `../RevitBIMFusion/Docs`. Vendored XSD — `Docs/BimContract/` (ресинк из эталона вручную). Локальную копию контракта в этот репозиторий не класть.
