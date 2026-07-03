# Алгоритм выполнения команд

Актуальная operational-схема Server → PostgreSQL → Worker → Telegram. Точный SQL находится только в `TelegramBot.Data/Sql/`; этот документ фиксирует semantics без копирования запросов.

## Общий поток

```text
Telegram update
→ Server validates access and selection
→ Sessions + Commands transaction
→ NOTIFY new_tasks
→ Worker claims commands
→ external process + TaskFile/ResultFile
→ Done / retry / Failed
→ NotificationOutbox
→ Server sends completion summary
```

## 1. Создание задания

`SlashCommandService`:

1. проверяет выбранные команды и разделы;
2. параллельно сканирует `01_RVT`;
3. оставляет `.rvt` больше 50 MiB с допустимым именем;
4. дедуплицирует файлы;
5. проверяет дневной лимит и активные пары `(CommandText, FilePath)`;
6. создаёт `CorrelationId` формата GUID `N`;
7. вызывает `SessionDataService.CreateSessionWithCommandsAsync`.

В одной DB-транзакции:

- берётся user-level `pg_advisory_xact_lock`;
- повторно проверяются дубликаты, закрывая double-submit TOCTOU;
- вставляется `Sessions`;
- Cartesian product выбранных commands × files вставляется в `Commands`;
- каждой команде назначаются `ExecutionOrder`, `Priority`, `Partition`;
- отправляется `pg_notify('new_tasks', correlationId)`;
- транзакция commit-ится.

Если под lock найдены дубликаты, транзакция откатывается и метод возвращает `null`.

### Priority

Меньшее число выполняется раньше:

| Значение | Команды |
|---:|---|
| 1 Critical | `PDF` |
| 2 High | `DWG` |
| 3 Medium | `NWC`, `DATA`, `IFC`, `BIMDOC`, `CLASHREP` |
| 4 Low | `AUTORES` |
| 5 Default | неизвестный fallback |

Priority влияет на порядок claim, но не прерывает уже выполняющиеся команды.

### Partition

```text
Partition = "file:" + md5(lower(FilePath))
```

Для пустого `FilePath` используется `CommandText`. Одновременно может выполняться только одна команда partition: один файл обрабатывается последовательно, разные файлы — параллельно.

## 2. Claim очереди

`CommandExecutionService` слушает `new_tasks`. После connect он:

1. освобождает expired leases;
2. drain-ит уже накопившуюся очередь;
3. ждёт PostgreSQL notification;
4. при timeout выполняет fallback polling;
5. переподключается с backoff при потере listener connection.

Drain вычисляет:

```text
availableSlots = MaxConcurrentCommands - runningTaskCount
claimLimit = min(5, availableSlots)
lease = now + ProcessTimeoutMinutes + 5 minutes
```

`ClaimPendingCommandsAsync` атомарно:

- выбирает `pending` с наступившим `NextRetryAt`;
- исключает partition с `processing` command;
- оставляет одну лучшую command на partition;
- берёт partition advisory xact lock;
- сортирует по `Priority`, `CreatedAt`, `CommandId`;
- использует `FOR UPDATE SKIP LOCKED`;
- переводит rows в `processing`, записывает `Lease` и `StartedAt`;
- возвращает данные command + session/user/correlation.

`_drainGate` не допускает параллельные drain-циклы внутри одного Worker. Running tasks учитываются сразу, поэтому claim не превышает `MaxConcurrentCommands`.

## 3. Подготовка и запуск

`ProcessRunner.RunAsync` создаёт общий timeout token на `ProcessTimeoutMinutes`.

### CommandPreparer

`PrepareAsync`:

- находит `CommandConfig`;
- проверяет absolute path, `RootPath`, reparse point, существование и extension;
- создаёт копию shared config перед изменением executable path;
- для Revit читает версию из OLE `BasicFileInfo` и ищет `Revit.exe`;
- для `CLASHREP` ищет последнюю установленную Navisworks executable;
- оставляет configured executable fallback, если BIM lookup не дал путь.

Неизвестная команда, invalid file и отсутствующий executable сразу переводят command в `Failed`.

### TaskFile

Перед `Process.Start` Worker:

1. создаёт `task_{project}_{commandId}.xml.tmp`;
2. сериализует `TaskFile`;
3. валидирует tmp по embedded canonical `TaskFile.schema.xsd`;
4. атомарно переименовывает tmp в `.xml`.

Для Revit:

- arguments пусты;
- абсолютный TaskFile path помещается в process-scoped `REVITBIMFUSION_TASK_FILE`;
- реальная команда находится в `<commandText>`.

Для wrapper/console commands `ArgumentsTemplate` может использовать:

```text
{CommandText} {FilePath} {CommandId} {TaskFilePath} {ResultFilePath}
```

`ProcessStarter._launchGate` сериализует сам вызов `Process.Start()`. Дополнительной паузы между процессами нет.

После старта `ProcessId` сохраняется в `Commands`. Первая команда сессии атомарно выставляет `Sessions.StartNotified = TRUE` и отправляет `session_started`.

## 4. Ожидание и результат

`OutputCollector` подписывается на stdout/stderr до ожидания процесса:

- каждый stream ограничен 64 KiB;
- в лог попадает не более 4 KiB;
- stdout форматируется только при включённом `Debug`;
- stderr пишется как `Warning`.

После выхода `ResultAnalyzer` читает ожидаемый ResultFile.

| Условие | Результат |
|---|---|
| `status=done` | command → `Done` |
| `status=failed` | permanent `Failed` или retry |
| `status=cancelled` | command → `Failed`, без retry |
| invalid XML/status | result → `.bad`, затем failure classification |
| Revit без ResultFile | failure независимо от exit code |
| wrapper без ResultFile, exit `0` | `Done` fallback |
| wrapper без ResultFile, exit `!= 0` | failure |
| timeout | process tree kill, command → `Failed` |

Прочитанный valid ResultFile удаляется. Task/result files после попытки очищаются best-effort.

При отрицательном exit code Worker пытается добавить evidence из Revit journal. Exit code логируется decimal + hex + известное NTSTATUS name.

## 5. Retry

`ErrorClassifier` считает permanent:

- configured `PermanentFailureExitCodes`;
- invalid/unsupported/not-implemented input;
- path/file/extension validation errors;
- cancellation;
- известные non-transient exception types.

Transient failure при `RetryCount < MaxRetries` возвращает command в `pending`:

```text
delay = RetryDelayBaseSeconds × 2^RetryCount + random jitter
NextRetryAt = now + delay
RetryCount += 1
```

После исчерпания попыток command становится `Failed`.

## 6. Завершение сессии и уведомления

После terminal transition `ProcessRunner` проверяет количество `pending`/`processing` commands сессии.

Если их нет, один SQL statement:

- выставляет `CompletionNotified = TRUE`, только если флаг ещё false;
- вставляет unique `NotificationOutbox(EventType='session_completed', SessionId, CorrelationId)`;
- отправляет `pg_notify('command_completed', sessionId|correlationId)`.

`command_completed` — только wake-up. Durable source — `NotificationOutbox`.

Server:

- `CommandNotificationService` слушает `session_started` и `command_completed`;
- bounded channel capacity 256 переносит direct start notifications и completion wake-ups;
- `NotificationSenderService` drain-ит outbox при старте, wake-up и каждые 30 секунд;
- session advisory lock `1_234_569` оставляет один sender среди Server replicas;
- claim lease outbox — 5 минут, batch — 20;
- success → `sent`; failure → `pending` с bounded retry delay и `LastError`.

## 7. Cleanup и shutdown

### Lease recovery

Каждые `CleanupIntervalSeconds` Worker под advisory lock `1_234_567` возвращает expired `processing` commands в `pending`.

### Session retention

`SessionCleanupService` soft-delete-ит sessions старше `CompletedSessionRetentionDays`, если у них нет `pending`/`processing` commands. Связанные commands также получают `Deleted`. Значение `<= 0` отключает retention cleanup.

### Worker shutdown

1. отменяются cleanup/monitor loops;
2. snapshot active processes завершается параллельно через process-tree kill;
3. общий shutdown budget — 30 секунд, kill одного процесса — до 10 секунд;
4. background loops и running command tasks ожидаются в оставшемся budget;
5. semaphores/CTS освобождаются.

## Схема БД

`DatabaseInitializerService` создаёт/дополняет схему при старте Server.

| Таблица | Ключевые поля |
|---|---|
| `BotUsers` | `UserId`, `Username`, `Role`, `Status`, timestamps |
| `Sessions` | `SessionId`, `UserId`, `CorrelationId`, `Status`, `ProjectName`, `CompletionNotified`, `StartNotified`, `FilesAmount`, timestamps |
| `Commands` | `CommandId`, `SessionId`, `CommandText`, `FilePath`, `ExecutionOrder`, `Status`, `Lease`, `Partition`, `Priority`, `ProcessId`, retry/result fields, timestamps |
| `TrackedMessages` | Telegram message tracking by optional `SessionId` and `ChatId` |
| `NotificationOutbox` | event, session/correlation, status, attempts, retry/lease/error timestamps |

Основные constraints/indexes:

- unique `(SessionId, CommandText, FilePath)`;
- unique completion outbox `(EventType, SessionId)` для `session_completed`;
- partial indexes для pending priority, processing leases/partitions и outbox;
- FK `Commands.SessionId` и `NotificationOutbox.SessionId` → `Sessions`.

Все пользовательские удаления — soft-delete. Физический `DELETE` не используется.

## Статусы

| Статус | Значение |
|---|---|
| `pending` | ожидает claim/retry |
| `processing` | захвачена Worker, lease активен |
| `Done` | успешно завершена |
| `Failed` | terminal failure |
| `Deleted` | скрыта пользователем/retention |

`Cancelled` мигрируется в `Deleted` при инициализации схемы и не является текущим статусом команды.

## PostgreSQL channels и locks

| Механизм | Назначение |
|---|---|
| `new_tasks` | разбудить Worker после commit задания |
| `session_started` | direct уведомление «задание запущено» |
| `command_completed` | разбудить completion outbox sender |
| advisory `1_234_567` | singleton lease cleanup |
| advisory xact `(1_234_568, hash(partition))` | partition claim |
| advisory `1_234_569` | singleton outbox sender |
| advisory xact `(1_234_570, hash(userId))` | duplicate check + insert |

## Добавление команды

Минимальный checklist:

1. `CommandCodes`;
2. `CallbackPrefixes`;
3. `CommandCatalog`;
4. priority map в `SlashCommandService`;
5. `Worker:Commands` config;
6. `CommandPreparer.IsRevitCommand`, если это Revit AddIn command;
7. canonical BIM contract/XSD/plugin, если меняется boundary;
8. `README.md`, `AGENTS.md` и этот документ.
