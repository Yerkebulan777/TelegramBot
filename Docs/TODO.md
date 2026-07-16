# TODO / Бэклог

## 🟢 НИЗКО

### 1. Stagger для Process.Start() — устарело

CEF collision не подтверждён как причина crash (см. [RevitCrashes.md](RevitCrashes.md) — основная причина: неподдерживаемый `/command` handoff, исправлено 2026-07-03).

Concurrency уже ограничена через два семафора (без stagger-задержки):

- `_drainGate` (`SemaphoreSlim(1,1)`, [CommandOrchestrator.cs](../TelegramBot.Worker/Services/CommandOrchestrator.cs)) — не допускает параллельные drain-циклы; claim выбирает pending-команды с учётом `availableSlots = MaxConcurrentCommands - runningTaskCount`.
- `_launchGate` (`SemaphoreSlim(1,1)`, [ProcessStarter.cs:18](../TelegramBot.Worker/Services/ProcessStarter.cs:18)) — сериализует сами вызовы `Process.Start()`.

При подозрении на CEF collision — снизить `Worker:MaxConcurrentCommands` в конфиге (default `5`), а не менять код.
