# Critical Review: актуальные замечания по TelegramBot

## Статус проверки

Документ пересмотрен после повторной проверки текущего кода. Большинство ранее описанных
проблем закрыты минимальными изменениями в Worker, Server и Data.

## 🟠 P2 — Осталось актуально

### 6. Нет durable completion notifications

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

## Закрыто в текущем проходе

- `LISTEN/NOTIFY` gap на старте и после reconnect: `LISTEN new_tasks` выполняется до первого drain.
- Worker shutdown: фоновые задачи отменяются в начале shutdown, kill активных процессов выполняется
  параллельно в общем bounded budget.
- `async void` notification handler: заменён на синхронный event handler с `TryWrite`.
- Plugin-reported `failed`: теперь проходит через `HandleFailureAsync()` и `ErrorClassifier`, поэтому
  transient ошибки могут уйти в retry.
- Malformed/unreadable result JSON: больше не падает в fallback по exit code и не может стать ложным `Done`.
- Trust boundary для `FilePath`: Worker проверяет `FileSystem:RootPath`, если он задан, и отклоняет
  reparse point файлы.
- `WorkerOptions` валидируется на старте; некорректные partition pool sizes больше не превращаются
  в молча зависающие семафоры.
- `AUTORES` получил явный `WorkingDirectory = "."` в `TelegramBot.Worker/appsettings.json`.
- `ErrorClassifier` расширен базовыми русскоязычными permanent file/access паттернами.

## Связанные документы

- [AGENTS.md](../AGENTS.md) — архитектура проекта, BimLib, DI, code style
- [ExecutionAlgorithm.md](./ExecutionAlgorithm.md) — спецификация алгоритма выполнения команд
