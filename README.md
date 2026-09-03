# TelegramBot

[![CI](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml)

Windows-сервис на .NET 10: Telegram-бот принимает задания, PostgreSQL хранит очередь, Worker запускает BIM-исполнители.

- **Server** — Windows Service  
- **Worker** — Task Scheduler (`onlogon` + `/it`); учётка должна быть залогинена (Revit нужен интерактивный desktop)

Подробности: [AGENTS.md](AGENTS.md), [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md), [BIM-контракт](https://github.com/Yerkebulan777/RevitBIMFusion/blob/master/Docs/BimPluginContract.md).

## Деплой с автоматической подписью

Один PowerShell-скрипт публикует **все** проекты, подписывает **каждый** `.exe` и собирает подписанный `TelegramBotSetup.exe`.

### Что нужно на ПК сборки

1. [.NET 10 SDK](https://dotnet.microsoft.com/download)
2. [Inno Setup 7](https://jrsoftware.org/isdl.php) → обычно `C:\Program Files\Inno Setup 7\ISCC.exe`
3. Windows SDK или ClickOnce Signing Tools (`signtool.exe`)
4. PowerShell **от имени пользователя**, под которым будете собирать (сертификат кладётся в `Cert:\CurrentUser\My`)

### Шаг 1 — один раз: создать сертификат

В корне репозитория:

```powershell
cd C:\path\to\TelegramBot
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\setup-internal-code-signing.ps1
```

Скрипт спросит пароль для офлайн-бэкапа PFX и выведет путь к CER, например:

`%USERPROFILE%\Documents\TelegramBot-CodeSigning\TelegramBot-Internal-Code-Signing-<THUMBPRINT>.cer`

- **PFX** — только на ПК сборки / в сейф. Не коммитить, не раздавать.
- **CER** — публичный, его раздают на рабочие ПК.

### Шаг 2 — один раз: доверить CER на рабочих ПК

**Предпочтительно — GPO** (автоматически на все машины):

`Computer Configuration → Policies → Windows Settings → Security Settings → Public Key Policies`

- импорт CER в **Trusted Root Certification Authorities**
- импорт CER в **Trusted Publishers**

**Или один тестовый ПК** — PowerShell **от администратора** (подставьте свой путь к CER):

```powershell
$cer = "$env:USERPROFILE\Documents\TelegramBot-CodeSigning\TelegramBot-Internal-Code-Signing-THUMBPRINT.cer"
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```

### Шаг 3 — каждый релиз: одна команда

```powershell
cd C:\path\to\TelegramBot
.\scripts\build-signed-installer.ps1
```

Что делает скрипт сам:

1. Находит сертификат в `Cert:\CurrentUser\My`
2. `dotnet publish` → Server, Worker, GrantLogonRight
3. Подписывает **все** `.exe` в `Installer\publish\`
4. Собирает Inno Setup и подписывает Setup + Uninstall
5. Проверяет подписи

Результат:

```text
Installer\Output\TelegramBotSetup.exe
```

Скопируйте файл на внутренний UNC и на целевом ПК запустите **от администратора**. Мастер спросит: Server/Worker, учётку, `B:` → UNC, токен бота.

### Продление сертификата

До истечения срока:

```powershell
.\scripts\setup-internal-code-signing.ps1 -Renew
```

Сначала разверните **новый** CER через GPO (или Import-Certificate), затем снова `.\scripts\build-signed-installer.ps1`. Старый CER не удаляйте, пока ещё стоят сборки, подписанные им.
