# Critical Review: замечания по TelegramBot

### Как мониторить выполнение команд в базе данных и работу Worker'ов

### Нет durable completion notifications

**Где:** `TelegramBot.Server/Services/Infrastructure/Telegram/CommandNotificationService.cs`,
`TelegramBot.Data/SessionDataService.cs`

**Текущий статус:**
- `command_completed` теперь отправляется идемпотентно через `Sessions.CompletionNotified`.
- Multi-worker duplicate notification закрыт: первая транзакция помечает сессию, остальные
  не отправляют повторный `NOTIFY`.
- Но доставка всё ещё не durable: если Server не слушает PostgreSQL в момент `pg_notify`,
  событие не будет переиграно.

**Что нужно для полного исправления:**
- Добавить durable outbox-таблицу для completion events.
- `NOTIFY` оставить только wake-up сигналом.
- `NotificationSenderService` должен читать неподтверждённые outbox-записи и помечать их
  отправленными после успешного Telegram send.

Это уже не минимальный патч, а отдельная подсистема доставки уведомлений.

## Связанные документы

- [AGENTS.md](../AGENTS.md) — архитектура проекта, BimLib, DI, code style
- [ExecutionAlgorithm.md](./ExecutionAlgorithm.md) — спецификация алгоритма выполнения команд
