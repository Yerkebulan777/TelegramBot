# TelegramBot

[![CI](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml/badge.svg)](https://github.com/Yerkebulan777/TelegramBot/actions/workflows/ci.yml)

Windows-сервис на .NET 10: Telegram-бот принимает задания, PostgreSQL хранит очередь, Worker запускает BIM-исполнители.

- **Server** — Windows Service  
- **Worker** — Task Scheduler (`onlogon` + `/it`); учётка должна быть залогинена (Revit нужен интерактивный desktop)

Подробности: [AGENTS.md](AGENTS.md), [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md), [BIM-контракт](https://github.com/Yerkebulan777/RevitBIMFusion/blob/master/Docs/BimPluginContract.md).

## Деплой с автоматической подпиской

На компьютере сборки: Inno Setup 7 и Windows SDK (`signtool.exe`).

**Один раз** — создать внутренний сертификат:

```powershell
.\scripts\setup-internal-code-signing.ps1
```

PFX хранить офлайн. Публичный CER развернуть через GPO:

`Computer Configuration → Policies → Windows Settings → Security Settings → Public Key Policies`  
→ `Trusted Root Certification Authorities` и `Trusted Publishers`.

**Каждый релиз:**

```powershell
.\scripts\build-signed-installer.ps1
# → Installer\Output\TelegramBotSetup.exe
```

Setup распространять с внутреннего UNC. Мастер ставит Server/Worker: учётка с доступом к шаре, `B:` → UNC, токен бота.

До истечения сертификата: `.\scripts\setup-internal-code-signing.ps1 -Renew`, сначала CER через GPO, затем новые сборки. Старый CER не удалять, пока используются подписанные им версии.
