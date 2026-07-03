# Алгоритм выполнения команд

> **Связанные документы:** [AGENTS.md](../AGENTS.md) — архитектура проекта, BimLib, DI |
> [BimPluginContract.md](BimPluginContract.md) — контракт BIM-плагинов | [README.md](../README.md) — общее
> описание

## Архитектура

```
Server → PostgreSQL (Sessions, Commands Status='pending')
    → LISTEN/NOTIFY 'new_tasks' → Worker (мгновенная реакция) + fallback polling (5 мин)
    → DrainPendingCommandsAsync: claim пачки (FOR UPDATE SKIP LOCKED + Lease) → запуск ProcessRunner
    → Выполнение: подготовка (валидация + BimLib) → запуск процесса → ожидание → ResultFile/exit-code
    → ErrorClassifier → Done / Failed / ScheduleRetry (NextRetryAt + RetryCount)
    → OnCommandCompletedAsync: in-memory счётчик → 0 → CountPendingProcessingBySessionAsync (DB confirm)
        → NotifySessionCompletedOnceAsync (atomic Sessions.CompletionNotified=TRUE)
            → INSERT NotificationOutbox(session_completed) + pg_notify('command_completed') wake-up
            → NotificationSenderService → claim outbox → GetSessionCompletionSummaryAsync → Telegram-сводка
                → mark sent
    → CommandNotificationService (Server) слушает также 'session_started' → "⚙️ Задание запущено"
```

Детальная схема компонентов и DI-регистрации — в [AGENTS.md](../AGENTS.md).

### Параллельный pipeline

| Слой | Server | Worker |
|------|--------|--------|
| Входящие обновления | Telegram updates: `Channel<Update>` (200) + `Parallel.ForEachAsync` (`MaxDegree=10`); notification wake-up: `Channel<NotificationItem>` (256) | PostgreSQL `new_tasks` LISTEN + fallback polling |
| Per-user / per-session | `SessionManager.AcquireUserLockAsync` (`SemaphoreSlim`) | один `SemaphoreSlim` в `CommandExecutionService` |
| Background tasks | `TelegramBotHostedService` + `CommandNotificationService` + `NotificationSenderService` | `CommandExecutionService` + `SessionCleanupService` |

---

## Жизненный цикл команды

| Статус | Описание |
|--------|----------|
| `pending` | Команда создана и ожидает выполнения |
| `processing` | Команда захвачена воркером (Lease установлен) |
| `Done` | Успешно завершена |
| `Failed` | Завершена с ошибкой (permanent или после исчерпания retries) |
| `Deleted` | Soft-delete (пользователь отменил) |

**Переходы:** `pending → processing → Done/Failed/Deleted`. Статус `Deleted` финальный — Worker не
перезаписывает его (`WHERE Status != 'Deleted'` в `UpdateCommandStatusAsync`).

### Pipeline команды

1. `SlashCommandService.ConfirmFileSelectionAsync` собирает файлы через `RevitFileDeduplicator` (parallel
   `Parallel.ForAsync` по разделам с лимитом `FileSystem:RvtScanMaxDegreeOfParallelism`), проверяет:
   - **Rate limit** (`CountQueuedFilesByUserSinceAsync` — сумма `FilesAmount` за 24ч, default ≤1000)
   - **Duplicate guard** (`HasDuplicateCommandsAsync` — активные pending/processing с теми же
     `(CommandText, FilePath)`)
2. `SessionDataService.CreateSessionWithCommandsAsync` (одна транзакция):
   - INSERT `Sessions` с `CorrelationId` (GUID без дефисов)
   - INSERT `Commands` батчем (`unnest(@CommandTexts::text[])` × N)
   - `pg_notify('new_tasks', @CorrelationId)` — wake-up сигнал
3. Worker:
   - `CommandExecutionService.RunListenerLoopAsync` слушает `new_tasks` (LISTEN + `conn.WaitAsync` +
     fallback polling)
   - `DrainPendingCommandsAsync` claim'ит до `min(DefaultBatchSize=5, availableSlots)` команд, запускает
     каждую как background `Task` и сразу пытается claim'ить ещё (drain loop устраняет head-of-line
     blocking)
4. Для каждой команды:
   - `_commandSlots.WaitAsync(ct)` — общий лимит параллельных команд
   - `ProcessRunner.RunAsync`:
     - `CommandPreparer.PrepareAsync` — валидация FilePath (path traversal, reparse-point, extension, root
       containment) + BimLib резолвинг (`RevitVersionDetector` → `RevitPathResolver`,
       `NavisworksPathResolver`)
     - `CreateTaskFile` — atomic write `task_{CommandId}_{attemptToken}.xml` (`.tmp` → `File.Move`); перед Move — runtime XSD-валидация через `TaskFileValidator` (embedded `Schemas/TaskFile.schema.xsd`); abort при drift модель↔XSD
     - `StartProcessAsync` — захват `_launchGate` (SemaphoreSlim 1/1) → `Process.Start` → регистрация в
       `_activeProcesses` (ДО stagger-задержки, чтобы health-check и shutdown видели процесс) →
       `Task.Delay(LaunchStaggerSeconds)` с `CancellationToken.None` (gate освобождается даже при shutdown)
      → освобождение gate → `MarkProcessStartedAndNotifyOnceAsync`
      (`ProcessId` + идемпотентный `pg_notify('session_started', SessionId|CorrelationId|UserId)`)
     - `WaitAndHandleResultAsync` — `OutputDataReceived` (64KB лимит, `truncated` флаг) +
       `WaitForExitAsync` + `TryReadResultFile`:
       - `Valid` + `status="done"` → `Done`
       - `Valid` + `status="failed"` → `HandleFailureAsync` (классификация + retry/fail)
       - `Valid` + `status="cancelled"` → permanent `Failed` без retry
       - `Invalid` (битый XML / unknown status) → rename в `.bad` → `HandleFailureAsync`
       - `NotFound` (нет result файла) → fallback по exit code (`0` = Done, иначе `HandleFailureAsync`)
     - `CleanupTempFiles` в `finally` (per-attempt)
5. `HandleFailureAsync`:
   - `ErrorClassifier.IsPermanentFailure(message, exitCode, PermanentFailureExitCodes, exception)`
     → `Failed` сразу для permanent-ошибки
   - Иначе если `RetryCount < MaxRetries` (default 5) → `ScheduleRetryAsync` с
     `NextRetryAt = NOW() + RetryDelayBaseSeconds * 2^RetryCount` (default 60→120→240→480→960s)
   - Иначе `Failed` после исчерпания
6. `SessionCompletionTracker.OnCommandCompletedAsync`:
   - `CountPendingProcessingBySessionAsync` (DB confirm) → `NotifySessionCompletedOnceAsync`
     (`CompletionNotified=TRUE`, `NotificationOutbox` insert,
     `pg_notify('command_completed', SessionId|CorrelationId)` если первый раз)
7. Server: `CommandNotificationService.OnNotificationReceived` (sync handler) → wake-up в
   `Channel<NotificationItem>` → `NotificationSenderService` (под session-level advisory lock `1234569`
   для single-writer между репликами) claim'ит pending outbox-записи, отправляет
   `SendMessageAsync` (project + counts + duration + failed files), затем помечает outbox-запись `sent`

---

## Схема базы данных

### BotUsers

| Колонка | Тип | Описание |
|---------|-----|----------|
| `UserId` | `BIGINT PK` | Telegram user ID |
| `Username` | `TEXT` | Telegram username (без @) |
| `Role` | `INTEGER NOT NULL DEFAULT 0` | `UserRole` enum: `User=0`, `Admin=1` |
| `Status` | `INTEGER NOT NULL DEFAULT 0` | `UserAccessStatus` enum: `Pending=0`, `Approved=1`, `Rejected=2`, `Blocked=3` |
| `CreatedAt` | `TIMESTAMPTZ NOT NULL DEFAULT NOW()` | |
| `UpdatedAt` | `TIMESTAMPTZ NOT NULL DEFAULT NOW()` | Используется для optimistic concurrency в `RefreshApprovedAdminUserAsync` |

### Sessions

| Колонка | Тип | Описание |
|---------|-----|----------|
| `SessionId` | `SERIAL PK` | |
| `UserId` | `BIGINT NOT NULL` | FK на `BotUsers.UserId` (логически) |
| `Username` | `TEXT` | Денормализован для `/status` отображения |
| `CorrelationId` | `TEXT NOT NULL` | GUID без дефисов; уникален на сессию; защищён `ALTER COLUMN ... SET NOT NULL` миграцией с `legacy-{SessionId}` для существующих строк |
| `PriorityId` | `INTEGER NOT NULL DEFAULT 0` | Резерв для приоритета сессии (сейчас не используется активно) |
| `Status` | `TEXT NOT NULL DEFAULT 'pending'` | `'pending'` / `'Done'` / `'Failed'` / `'Deleted'` |
| `ProjectName` | `TEXT` | Имя проекта из `GetCurrentProjectName(session)` |
| `FilesAmount` | `INTEGER` | Количество файлов в сессии (для `CountQueuedFilesByUserSinceAsync`) |
| `CompletionNotified` | `BOOLEAN NOT NULL DEFAULT FALSE` | Идемпотентность enqueue completion event в `NotificationOutbox` при multi-worker |
| `StartNotified` | `BOOLEAN NOT NULL DEFAULT FALSE` | Идемпотентность уведомления "Задание запущено" |
| `CreatedAt` | `TIMESTAMPTZ NOT NULL DEFAULT NOW()` | |
| `UpdatedAt` | `TIMESTAMPTZ NOT NULL DEFAULT NOW()` | |

**Миграция `EnsureSessionsColumns`:** добавление колонок `IF NOT EXISTS` для существующих БД, заполнение
`CorrelationId = 'legacy-' || SessionId` для NULL, `SET NOT NULL`.

### Commands

| Колонка | Тип | Описание |
|---------|-----|----------|
| `CommandId` | `SERIAL PK` | |
| `SessionId` | `INTEGER NOT NULL` | FK на `Sessions` |
| `CommandText` | `TEXT NOT NULL` | `PDF` / `DWG` / `IFC` / `BIMDOC` / `NWC` / `CLASHREP` / `AUTORES` |
| `FilePath` | `TEXT` | Полный путь к исходному файлу |
| `ExecutionOrder` | `INTEGER NOT NULL` | Порядок в батче (1..N) |
| `Status` | `TEXT NOT NULL DEFAULT 'pending'` | |
| `CreatedAt` | `TIMESTAMPTZ NOT NULL DEFAULT NOW()` | |
| `StartedAt` | `TIMESTAMPTZ` | Устанавливается в `ClaimAndReturn` |
| `CompletedAt` | `TIMESTAMPTZ` | Устанавливается в `UpdateStatus` при `Done`/`Failed` |
| `GUID` | `TEXT` | Резерв для дополнительной идентификации |
| `Lease` | `INTEGER` | Unix seconds — момент истечения lease; NULL когда не processing |
| `Partition` | `TEXT` | Имя партиции (для группировки) |
| `Priority` | `INTEGER NOT NULL DEFAULT 5` | 1=Critical, 2=High, 3=Medium, 4=Low, 5=Default |
| `ProcessId` | `INTEGER` | PID запущенного процесса |
| `ErrorMessage` | `TEXT` | Последнее сообщение об ошибке (для Failed) |
| `RetryCount` | `INTEGER NOT NULL DEFAULT 0` | Счётчик попыток |
| `NextRetryAt` | `TIMESTAMPTZ` | Время следующей попытки; используется в `ClaimAndReturn` (пропускает если `> NOW()`) |
| `Progress` | `INTEGER NOT NULL DEFAULT 0` | 0..100, обновляется через `UpdateCommandStatusAsync` (параметр `progress`) |
| `Result` | `TEXT` | Произвольный JSON-результат (обновляется через `UpdateCommandStatusAsync` (параметр `result`)) |
| `UpdatedAt` | `TIMESTAMPTZ NOT NULL DEFAULT NOW()` | |

### TrackedMessages

| Колонка | Тип | Описание |
|---------|-----|----------|
| `MessageId` | `SERIAL PK` | |
| `SessionId` | `INTEGER` (nullable) | FK на `Sessions`; nullable для сообщений вне сессии |
| `ChatId` | `BIGINT NOT NULL` | Telegram chat ID |
| `MessageIdPg` | `INTEGER NOT NULL` | Telegram message ID |
| `CreatedAt` | `TIMESTAMPTZ NOT NULL DEFAULT NOW()` | |

**Миграция `MakeTrackedMessagesSessionNullable`:** для старых схем, где `SessionId` был NOT NULL.

### NotificationOutbox

| Колонка | Тип | Описание |
|---------|-----|----------|
| `OutboxId` | `BIGSERIAL PK` | |
| `EventType` | `TEXT NOT NULL` | Сейчас используется `session_completed` |
| `SessionId` | `INTEGER NOT NULL` | FK на `Sessions` |
| `CorrelationId` | `TEXT NOT NULL` | Корреляция логов и payload wake-up |
| `Status` | `TEXT NOT NULL DEFAULT 'pending'` | `pending` / `processing` / `sent` |
| `Attempts` | `INTEGER NOT NULL DEFAULT 0` | Увеличивается при claim |
| `NextAttemptAt` | `TIMESTAMPTZ NOT NULL DEFAULT NOW()` | Backoff после ошибок отправки |
| `LockedUntil` | `TIMESTAMPTZ` | Lease для crash recovery Server |
| `LastError` | `TEXT` | Последняя ошибка отправки |
| `CreatedAt` | `TIMESTAMPTZ NOT NULL DEFAULT NOW()` | |
| `UpdatedAt` | `TIMESTAMPTZ NOT NULL DEFAULT NOW()` | |
| `SentAt` | `TIMESTAMPTZ` | Время успешной отправки Telegram-сводки |

### Indexes

| Index | Columns | Notes |
|-------|---------|-------|
| `idx_commands_status` | `Status` | |
| `idx_commands_session` | `SessionId` | |
| `idx_commands_status_lease` | `Status, Lease` | partial `WHERE Status = 'processing'` |
| `idx_sessions_user_created` | `UserId, CreatedAt DESC` | |
| `idx_sessions_correlation_id` | `CorrelationId` | |
| `idx_commands_pending_priority` | `Status, Priority ASC, CreatedAt ASC, CommandId ASC` | partial `WHERE Status = 'pending'` |
| `idx_commands_claim_partition` | `Status, Partition, Priority ASC, CreatedAt ASC, CommandId ASC` | partial `WHERE Status = 'pending'`. Критичен для производительности `ClaimAndReturn` (DISTINCT ON + ORDER BY) |
| `idx_commands_partition_status` | `Partition, Status` | |
| `idx_commands_processing_partition` | `Partition` | partial `WHERE Status = 'processing'`; ускоряет исключение уже выполняемых partition |
| `idx_commands_unique` | `SessionId, CommandText, FilePath` | UNIQUE |
| `idx_tracked_messages_session` | `SessionId` | |
| `idx_tracked_messages_chat` | `ChatId` | |
| `idx_commands_updated_at` | `UpdatedAt DESC` | |
| `idx_notification_outbox_session_completed` | `EventType, SessionId` | UNIQUE partial `WHERE EventType = 'session_completed'` |
| `idx_notification_outbox_pending` | `Status, NextAttemptAt, CreatedAt, OutboxId` | partial `WHERE Status IN ('pending', 'processing')` |

---

## SQL-операции

### Вставка сессии с командами (одна транзакция)

```sql
-- 1) INSERT Sessions
INSERT INTO Sessions (UserId, Username, CorrelationId, ProjectName, FilesAmount)
VALUES (@UserId, @Username, @CorrelationId, @ProjectName, @FilesAmount)
RETURNING SessionId;

-- 2) INSERT Commands батчем (Partition вычисляется на лету из FilePath)
INSERT INTO Commands (SessionId, CommandText, FilePath, ExecutionOrder, Priority, Partition)
SELECT @SessionId,
       data.CommandText,
       data.FilePath,
       data.ExecutionOrder,
       data.Priority,
       'file:' || md5(lower(COALESCE(NULLIF(data.FilePath, ''), data.CommandText)))
FROM unnest(
    @CommandTexts::text[],
    @FilePaths::text[],
    @Orders::int[],
    @Priorities::int[]
) AS data(CommandText, FilePath, ExecutionOrder, Priority);

-- 3) wake-up сигнал для Worker
SELECT pg_notify('new_tasks', @CorrelationId);
```

Реализация: `SessionDataService.CreateSessionWithCommandsAsync` — единая `BeginTransactionAsync` + commit.

### Захват команд (атомарный DB scheduler)

Worker не решает, какая команда следующая. Он только сообщает БД, сколько свободных слотов есть, а БД
атомарно выбирает команды через `SqlQueries.Commands.ClaimAndReturn`.

`Partition` назначается при вставке команды как стабильный ключ исходного файла:

```sql
'file:' || md5(lower(COALESCE(NULLIF(FilePath, ''), CommandText)))
```

Это сериализует команды одного файла: пока по этой партиции есть `processing`, следующая команда того же
файла не будет отдана worker-у. Разные файлы могут выполняться параллельно до общего лимита worker-а.
Для старых строк с `Partition IS NULL` тот же ключ backfill-ится при инициализации схемы.

Claim-запрос делает всё в одной транзакции:

1. Берёт только `pending` команды с готовым `NextRetryAt` (IS NULL или ≤ NOW()) и заполненной `Partition`.
2. Исключает partition, где уже есть `processing`.
3. Через `DISTINCT ON (Partition)` оставляет максимум одну команду каждой partition в batch.
4. Сортирует кандидатов по `Priority ASC, CreatedAt ASC, CommandId ASC`.
5. Закрывает гонки между worker-ами через `pg_try_advisory_xact_lock(1234568, hashtext(Partition))`
   (per-partition advisory lock, namespace отличный от lease cleanup 1234567) и `FOR UPDATE SKIP LOCKED`.
6. Обновляет выбранные строки в `processing`, проставляет `Lease` + `StartedAt = NOW()` и возвращает команды worker-у.

**Lease:** `LeaseExpiry = NOW() + ProcessTimeoutMinutes + 5min` (дополнительные 5 мин — буфер для crash
recovery). `ProcessTimeoutMinutes` = 180 (3ч) по умолчанию.

### Обновление статуса (финальное)

```sql
UPDATE Commands
SET Status = @Status,
    CompletedAt = CASE
        WHEN @Status IN ('Done', 'Failed') THEN NOW()
        ELSE CompletedAt
    END,
    ProcessId = @ProcessId,
    ErrorMessage = @ErrorMessage,
    Progress = COALESCE(@Progress, Progress),
    Result = COALESCE(@Result, Result)
WHERE CommandId = @CommandId
  AND Status != 'Deleted';
```

`Progress` и `Result` обновляются только если параметр не NULL (оптимистичное обновление).

### Уведомление о старте сессии (Worker → Server)

```sql
WITH command_updated AS (
    UPDATE Commands
    SET ProcessId = @ProcessId
    WHERE CommandId = @CommandId
      AND Status = 'processing'
    RETURNING SessionId
),
session_marked AS (
    UPDATE Sessions s
    SET StartNotified = TRUE,
        UpdatedAt = NOW()
    FROM command_updated cu
    WHERE s.SessionId = cu.SessionId
      AND s.StartNotified = FALSE
      AND s.Status != 'Deleted'
    RETURNING s.SessionId
),
notified AS (
    SELECT pg_notify('session_started', @Payload)
    FROM session_marked
)
SELECT COUNT(*)::int FROM notified;
-- Payload: "SessionId|CorrelationId|UserId"
```

Вызывается в `ProcessRunner.StartProcessAsync` после регистрации процесса в `_activeProcesses`.

### Идемпотентный сигнал завершения сессии (Worker → Server)

```sql
WITH marked AS (
    UPDATE Sessions
    SET CompletionNotified = TRUE,
        UpdatedAt = NOW()
    WHERE SessionId = @SessionId
      AND CompletionNotified = FALSE
      AND Status != 'Deleted'
    RETURNING SessionId
),
outbox AS (
    INSERT INTO NotificationOutbox (EventType, SessionId, CorrelationId)
    SELECT 'session_completed', SessionId, @CorrelationId
    FROM marked
    ON CONFLICT DO NOTHING
    RETURNING OutboxId
),
notified AS (
    SELECT pg_notify('command_completed', @Payload)
    FROM marked
)
SELECT (SELECT COUNT(*)::int FROM outbox)
FROM (SELECT COUNT(*) FROM notified) force_notify;
```

`command_completed` — только wake-up сигнал. Durable-событие хранится в `NotificationOutbox`.
`Sessions.CompletionNotified` защищает от дублей при нескольких Worker: только первый успешный
`UPDATE ... WHERE CompletionNotified = FALSE` вставляет outbox-запись и отправляет wake-up. Единственный
источник данных для текста уведомления — `SessionDataService.GetSessionCompletionSummaryAsync()`, который
читает из БД пользователя, проект, total/done/failed, длительность и список failed-файлов.

`NotificationSenderService` читает outbox при старте, по wake-up и периодически каждые 30 секунд. Drain
обёрнут в **session-level advisory lock** (`pg_try_advisory_lock(1234569)` через
`NotificationOutboxDataService.TryAcquireSenderLockAsync` → `SenderLockHolder`) для single-writer mutual
exclusion между репликами Server; реплика, не получившая lock, пропускает цикл. Claim использует
`FOR UPDATE SKIP LOCKED` + `LockedUntil`; после успешного Telegram send запись помечается `sent`, после
ошибки возвращается в `pending` с backoff.

### Очистка истёкших Lease (crash recovery)

```sql
UPDATE "Commands"
SET "Status" = 'pending',
    "Lease" = NULL,
    "StartedAt" = NULL,
    "ErrorMessage" = 'Lease expired: worker crash or timeout'
WHERE "Status" = 'processing'
  AND "Lease" IS NOT NULL
  AND "Lease" < @CurrentTimeSec;
```

**Advisory lock:** `pg_try_advisory_lock(1234567)` (namespace `telegram_bot_lease_cleanup`) — предотвращает
race между несколькими воркерами. Освобождается в `finally`. Для claim-запроса используется отдельный
namespace: `pg_try_advisory_xact_lock(1234568, hashtext(Partition))` с транзакционной (xact) блокировкой,
которая автоматически освобождается при коммите транзакции. Для single-writer mutual exclusion при drain'е
`NotificationOutbox` (Server-side, multi-instance) — `pg_try_advisory_lock(1234569)` (namespace
`telegram_bot_outbox_sender`), session-level, удерживается через `SenderLockHolder` на весь drain-цикл.

### Schedule retry

```sql
UPDATE Commands
SET Status = 'pending',
    Lease = NULL,
    StartedAt = NULL,
    RetryCount = COALESCE(RetryCount, 0) + 1,
    NextRetryAt = @NextRetryAt,
    ErrorMessage = @ErrorMessage
WHERE CommandId = @CommandId
RETURNING RetryCount;
```

`NextRetryAt = NOW() + RetryDelayBaseSeconds * 2^RetryCount` (экспоненциальная задержка). При следующем
`ClaimAndReturn` команда будет пропущена, пока `NOW() < NextRetryAt`.

### Отмена команды пользователем

```sql
UPDATE Commands SET Status = 'Deleted'
WHERE CommandId = @CommandId
  AND (SessionId IN (SELECT SessionId FROM Sessions WHERE UserId = @UserId)
       OR @IsAdmin = true);
```

`DeleteCommandAsync` (UserId/IsAdmin), `DeleteCommandsByTypeAsync`
(`WHERE Status NOT IN ('Deleted', 'processing')` — нельзя отменить выполняющуюся).

### Проверка дубликатов в активной очереди

```sql
SELECT COUNT(*) FROM (
    SELECT unnest(@CommandTexts::text[]) AS cmd, unnest(@FilePaths::text[]) AS fpath
) input
WHERE EXISTS (
    SELECT 1 FROM Commands c
    JOIN Sessions s ON s.SessionId = c.SessionId
    WHERE c.Status IN ('pending', 'processing')
      AND s.Status != 'Deleted'
      AND c.CommandText = input.cmd
      AND c.FilePath = input.fpath
);
```

Используется в `HasDuplicateCommandsAsync` перед `CreateSessionWithCommandsAsync` (если есть дубликаты —
сессия не создаётся).

### Получение сводки завершения

```sql
SELECT
    s.UserId, s.Username, s.SessionId, s.CorrelationId, s.ProjectName,
    COUNT(CASE WHEN c.Status != 'Deleted' THEN 1 END)::int AS TotalFiles,
    COUNT(CASE WHEN c.Status = 'Done'    THEN 1 END)::int AS DoneFiles,
    COUNT(CASE WHEN c.Status = 'Failed'  THEN 1 END)::int AS FailedFiles,
    EXTRACT(EPOCH FROM (MAX(c.CompletedAt) - MIN(c.StartedAt)))::int AS DurationSeconds
FROM Sessions s
LEFT JOIN Commands c ON c.SessionId = s.SessionId AND c.Status != 'Deleted'
WHERE s.SessionId = @SessionId
GROUP BY s.SessionId;
```

+ отдельный запрос `SqlQueries.Commands.GetFailedFilePathsBySession` для `FailedFilePaths`:

```sql
SELECT FilePath
FROM Commands
WHERE SessionId = @SessionId
  AND Status = 'Failed'
ORDER BY ExecutionOrder, CommandId;
```

### Soft-delete неактивных сессий

```sql
WITH deleted_sessions AS (
    UPDATE Sessions s
    SET Status = 'Deleted', UpdatedAt = NOW()
    WHERE s.Status != 'Deleted'
      AND s.CreatedAt < @CutoffUtc
      AND NOT EXISTS (
          SELECT 1 FROM Commands c
          WHERE c.SessionId = s.SessionId
            AND c.Status IN ('pending', 'processing')
      )
    RETURNING s.SessionId
),
deleted_commands AS (
    UPDATE Commands c
    SET Status = 'Deleted'
    FROM deleted_sessions ds
    WHERE c.SessionId = ds.SessionId
      AND c.Status != 'Deleted'
    RETURNING c.CommandId
)
SELECT COUNT(*)::int FROM deleted_sessions;
```

Используется в `SessionCleanupService` с `cutoffUtc = NOW() - CompletedSessionRetentionDays`.

---

## LISTEN/NOTIFY каналы PostgreSQL

| Канал | Payload | Отправитель | Получатель | Назначение |
|-------|---------|-------------|------------|------------|
| `new_tasks` | `CorrelationId` | `SessionDataService.CreateSessionWithCommandsAsync` | `CommandExecutionService.RunListenerLoopAsync` | Wake-up: новые команды в очереди |
| `session_started` | `SessionId\|CorrelationId\|UserId` | `CommandDataService.MarkProcessStartedAndNotifyOnceAsync` (внутри `ProcessRunner.StartProcessAsync`) | `CommandNotificationService.OnSessionStarted` | Server шлёт "⚙️ Задание запущено" пользователю |
| `command_completed` | `SessionId\|CorrelationId` | `SessionDataService.NotifySessionCompletedOnceAsync` (атомарно с `CompletionNotified=TRUE` + outbox insert) | `CommandNotificationService.OnNotificationReceived` | Wake-up: Server drain'ит `NotificationOutbox` и шлёт итоговую сводку |

---

## Приоритеты команд и лимит параллельности

Меньше значение `Priority` = выше приоритет. Приоритет используется в SQL при claim'е:
`ORDER BY Priority ASC, CreatedAt ASC, CommandId ASC`.

`WorkerOptions.MaxConcurrentCommands` задаёт лимит параллельных команд (default `5`).

> **TODO (идея на будущее):** динамический приоритет в БД — если задача того же типа/сессии была
> выполнена недавно, снижать `Priority` следующей на 1 (антиголодание/балансировка очереди для
> "тяжёлых" повторяющихся команд). Сейчас `Priority` статичен (`_commandPriorityMap`).

---

## Диагностические запросы

**Очередь pending-команд (с приоритетом и возрастом):**

```sql
SELECT "CommandId", "CommandText", "Priority", "CreatedAt",
       EXTRACT(EPOCH FROM (NOW() - "CreatedAt")) as "AgeSec"
FROM "Commands"
WHERE "Status" = 'pending'
ORDER BY "Priority" ASC, "CreatedAt" ASC;
```

**Активные выполнения (с LeaseStatus):**

```sql
SELECT "CommandId", "CommandText", "ProcessId", "StartedAt",
       EXTRACT(EPOCH FROM (NOW() - "StartedAt")) as "DurationSec",
       "Lease",
       CASE WHEN "Lease" < EXTRACT(EPOCH FROM NOW()) THEN 'EXPIRED' ELSE 'OK' END as "LeaseStatus"
FROM "Commands"
WHERE "Status" = 'processing'
ORDER BY "StartedAt" ASC;
```

**Зависшие команды (истёк Lease):**

```sql
SELECT "CommandId", "CommandText", "ProcessId", "StartedAt", "Lease"
FROM "Commands"
WHERE "Status" = 'processing'
  AND "Lease" IS NOT NULL
  AND "Lease" < EXTRACT(EPOCH FROM NOW())
ORDER BY "Lease" ASC;
```

**Команды, ожидающие retry:**

```sql
SELECT "CommandId", "CommandText", "RetryCount", "NextRetryAt",
       EXTRACT(EPOCH FROM ("NextRetryAt" - NOW())) as "WaitSec"
FROM "Commands"
WHERE "Status" = 'pending'
  AND "NextRetryAt" IS NOT NULL
ORDER BY "NextRetryAt" ASC;
```

**Активность сессий (24ч):**

```sql
SELECT s.SessionId, s.Username, s.ProjectName, s.CreatedAt,
       COUNT(c.CommandId) AS Total,
       COUNT(CASE WHEN c.Status = 'Done'   THEN 1 END) AS Done,
       COUNT(CASE WHEN c.Status = 'Failed' THEN 1 END) AS Failed,
       COUNT(CASE WHEN c.Status IN ('pending','processing') THEN 1 END) AS Active
FROM Sessions s
LEFT JOIN Commands c ON c.SessionId = s.SessionId AND c.Status != 'Deleted'
WHERE s.CreatedAt >= NOW() - INTERVAL '24 hours'
  AND s.Status != 'Deleted'
GROUP BY s.SessionId
ORDER BY s.CreatedAt DESC;
```

---

## Расширение системы

### Контракт внешнего исполнителя

Для Revit/Navisworks/AI-команд Worker использует один механизм:

1. `ProcessRunner.RunAsync()` генерирует `AttemptToken` (GUID без дефисов).
2. `CommandPreparer.CreateTaskFile()` создаёт `task_{CommandId}_{AttemptToken}.xml` (atomic write `.tmp` →
   `File.Move`; перед Move — runtime XSD-валидация через `TaskFileValidator` из embedded
   `Schemas/TaskFile.schema.xsd`).
3. `CommandPreparer.CreateProcessStartInfo()` подставляет `{TaskFilePath}` и `{ResultFilePath}` в
   `ArgumentsTemplate`. Для Revit AddIn шаблон должен быть `/command "WORKER" "{TaskFilePath}"`; реальная
   команда остаётся в `TaskFile.commandText`.
4. После выхода процесса `ProcessRunner.TryReadResultFile()` читает
   `result_{CommandId}_{AttemptToken}.xml`.
5. Если result-файл отсутствует, Worker использует fallback по exit code. Если result-файл существует, но не
   читается или содержит битый XML, попытка считается ошибочной и проходит через `ErrorClassifier`
   (permanent → `Failed`, transient → `ScheduleRetry`). `status` — обязательное enum-поле
   (`done`/`failed`/`cancelled`), `cancelled` трактуется как permanent failure без retry.

Revit требует установленный AddIn: `Revit.exe` сам не выполняет `/command`. RevitBIMFusion AddIn ожидает
fixed dispatcher `WORKER` в `args[2]` и task-файл в `args[3]`. Для Navisworks/FileConvert
полноценный `TaskFile + ResultFile` контракт тоже требует обёртку или плагин; чистый `FileConvert.exe` может
работать только через fallback по exit code.

Подробности: [BimPluginContract.md](BimPluginContract.md).

### Добавление новой команды

1. **Добавить конфигурацию** в `appsettings.json` Worker (или в дефолты `WorkerOptions.cs`):

   ```json
   "Commands": {
     "XLSEXPORT": {
       "ExecutablePath": "excel_exporter.exe",
       "ArgumentsTemplate": "--input \"{FilePath}\"",
       "AllowedExtensions": [".xlsx", ".xls"],
       "WorkingDirectory": "."
     }
   }
   ```

   Если команда использует Revit AddIn — `ArgumentsTemplate` должен содержать
   `WorkerOptions.RevitDispatcherCommand` (`"WORKER"`) как `args[2]`:
   `/command \"WORKER\" \"{TaskFilePath}\"`

2. **Настроить приоритет** — добавить запись в `_commandPriorityMap` в `SlashCommandService.cs`. Если не
   добавить — `Priority=5` (`Default`).

3. **Настроить stagger-gate** (опционально): если команда запускает Revit или другой процесс с
   Chromium CEF, stagger-gate уже сериализует все запуски — дополнительных настроек не нужно.

4. **Настроить общий лимит параллельности** (опционально):

   ```json
   "MaxConcurrentCommands": 5
   ```

5. **Добавить `CommandDefinition`** в `TelegramBot.Server/Models/CommandDefinition.cs` +
   `CommandCatalog.GetByGroup(...)` для отображения в меню.

6. **Добавить `CommandCodes` константу** в `TelegramBot.Core/Constants/CommandCodes.cs` (если нужна в
   Server-коде).

7. **Добавить `CallbackPrefixes` константу** в `TelegramBot.Core/Constants/CallbackPrefixes.cs` (для
   inline-кнопки команды). Формат: `"<CODE>:"` (с двоеточием).

8. **Зарегистрировать handler** (если новый callback-префикс): `CommandToggleHandler` уже поддерживает
   `PDF:`, `DWG:`, и т.д. — для новой команды добавить префикс в `SupportedPrefixes`.

### Добавление нового PG-канала

1. `SELECT pg_notify('new_channel', @Payload)` в отправителе (Worker/Server).
2. `await using var conn = await CreateOpenConnectionAsync(); conn.Notification += OnNotification;` в
   получателе.
3. Обработать payload в `OnNotificationReceived` (sync handler) — без `async void`.
