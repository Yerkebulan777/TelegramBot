# Critical Review: статус замечаний по TelegramBot

## Durable completion notifications — закрыто

**Реализация:** `TelegramBot.Server/Services/Infrastructure/Telegram/CommandNotificationService.cs`,
`TelegramBot.Server/Services/Infrastructure/Telegram/NotificationSenderService.cs`,
`TelegramBot.Data/SessionDataService.cs`,
`TelegramBot.Data/NotificationOutboxDataService.cs`

**Статус:**
- Completion events пишутся в `NotificationOutbox` в той же SQL-команде, где
  `Sessions.CompletionNotified` переводится в `TRUE`.
- `command_completed` теперь только wake-up сигнал. Если Server не слушает PostgreSQL в момент
  `pg_notify`, событие не теряется: pending-запись остаётся в outbox.
- `NotificationSenderService` читает pending outbox-записи при старте, по wake-up сигналу и
  периодическим polling каждые 30 секунд.
- Отправленная Telegram-сводка помечается `Status='sent'`. При ошибке отправки запись
  возвращается в `pending` с backoff через `NextAttemptAt`.
- Claim выполняется через `FOR UPDATE SKIP LOCKED` + `LockedUntil`, поэтому после падения Server
  processing-запись будет повторно взята после истечения lease.

**Остаточный риск:**
- Если Telegram сообщение успешно отправлено, но запись не успела пометиться `sent` из-за сбоя БД
  или падения процесса, возможен повтор итоговой сводки после retry. Это обычная граница
  at-least-once доставки без внешнего идемпотентного ключа в Telegram API.

## Связанные документы

- [AGENTS.md](../AGENTS.md) — архитектура проекта, BimLib, DI, code style
- [ExecutionAlgorithm.md](./ExecutionAlgorithm.md) — спецификация алгоритма выполнения команд


