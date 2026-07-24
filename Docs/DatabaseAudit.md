# Аудит потенциальных ошибок PostgreSQL

Дата аудита: 2026-07-25

Проверенный commit: `85edca5da81f8922992b393592bc18ddfa2db169`

Статус документа: первичный статический аудит, выводы требуют воспроизведения

## Назначение

Документ фиксирует потенциальные ошибки в DB-слое TelegramBot, чтобы их можно
было воспроизвести, исправить и повторно проверить.

Это не нормативное описание pipeline. Источником истины для штатного поведения
остаётся [ExecutionAlgorithm.md](ExecutionAlgorithm.md), а для фактической схемы
и запросов — `TelegramBot.Data/Sql/`.

## Область проверки

Проверены:

- создание и обновление схемы PostgreSQL;
- транзакции Dapper/Npgsql;
- claim команд, partition scheduling и lease recovery;
- переходы `pending` → `processing` → `Done` / `Failed`;
- soft-delete и ручной requeue;
- retry и ограничение количества попыток;
- `NotificationOutbox` и `LISTEN/NOTIFY`;
- дневной лимит файлов;
- формирование статуса и итоговой статистики сессии.

Не проверены:

- состояние и статистика реальной production-БД;
- планы `EXPLAIN (ANALYZE, BUFFERS)` на production-объёмах;
- поведение при реальном отключении PostgreSQL и нескольких Worker/Server;
- нагрузочные сценарии;
- сценарии с реальным Telegram API.

## Сводка

| ID | Риск | Потенциальная ошибка | Статус |
|---|---|---|---|
| DB-001 | Critical | Retry может воскресить удалённую команду | Требует воспроизведения |
| DB-002 | High | `Failed` после lease cleanup не создаёт completion outbox | Требует воспроизведения |
| DB-003 | High | Повторное завершение после requeue не попадает в outbox | Требует воспроизведения |
| DB-004 | High | Нет fencing token между попытками Worker | Требует конкурентного теста |
| DB-005 | High | Миграция legacy `Cancelled` выполняется после `CHECK` | Требует проверки upgrade |
| DB-006 | Medium | Дневной лимит имеет TOCTOU и обходится soft-delete | Требует конкурентного теста |
| DB-007 | Medium | `MaxRetries` допускает лишнюю попытку | Требует уточнения семантики |
| DB-008 | Medium | Сессия не получает terminal status | Подтверждено статически |
| DB-009 | Medium | Количество command-file операций называется файлами | Подтверждено статически |
| DB-010 | Medium | Polling outbox прекращается после одной ошибки | Подтверждено статически |

## DB-001 — retry может воскресить удалённую команду

### Наблюдение

`ScheduleRetry` обновляет строку только по `CommandId`:

```sql
UPDATE Commands
SET Status = 'pending',
    ...
WHERE CommandId = @CommandId
RETURNING RetryCount;
```

Проверки `Status = 'processing'` и статуса родительской сессии нет.

### Сценарий

1. Worker забирает команду и переводит её в `processing`.
2. Пользователь удаляет команду или всю сессию.
3. Запущенный процесс завершается transient-ошибкой.
4. Worker вызывает `ScheduleRetryAsync`.
5. Удалённая команда снова становится `pending`.
6. Если сессия уже `Deleted`, claim её игнорирует.
7. Partial unique index продолжает считать команду активной и может навсегда
   блокировать повторную постановку `(CommandText, FilePath)`.

### Связанные места

- `TelegramBot.Data/Sql/Queries.Commands.cs`: `SoftDelete`,
  `SoftDeleteBySession`, `ScheduleRetry`;
- `TelegramBot.Data/Sql/Queries.Schema.cs`:
  `idx_commands_active_unique`;
- `TelegramBot.Worker/Services/ProcessRunner.cs`:
  `HandleFailureAsync`.

### Предлагаемая проверка

В одной транзакции/сессии БД создать `processing` команду, затем:

1. выполнить soft-delete;
2. выполнить текущий `ScheduleRetry`;
3. проверить, что строка стала `pending`;
4. попытаться вставить ту же `(CommandText, FilePath)`;
5. убедиться, что active unique index блокирует вставку.

### Возможное исправление

Обновлять retry только при совпадении ожидаемого статуса и актуальной попытки:

```sql
WHERE CommandId = @CommandId
  AND Status = 'processing'
  AND LeaseToken = @LeaseToken
```

Если обновлено `0` строк, Worker должен считать попытку устаревшей.

## DB-002 — lease cleanup не создаёт уведомление о завершении

### Наблюдение

`ReleaseExpiredLeases` может перевести последнюю команду в `Failed`, но запрос:

- не возвращает `SessionId`;
- не создаёт запись `NotificationOutbox`;
- не вызывает `NotifySessionCompletedOnceAsync`.

### Последствие

Сессия будет выглядеть завершённой в `/status`, но пользователь не получит
итоговое Telegram-уведомление, а `CompletionNotified` останется `FALSE`.

### Связанные места

- `TelegramBot.Data/Sql/Queries.Commands.cs`:
  `ReleaseExpiredLeases`;
- `TelegramBot.Data/CommandDataService.cs`:
  `ReleaseExpiredLeasesAsync`;
- `TelegramBot.Worker/Services/ProcessRunner.cs`:
  `NotifySessionCompletionAsync`.

### Предлагаемая проверка

1. Создать сессию с одной `processing` командой.
2. Установить истёкший lease и `RetryCount = MaxRetries - 1`.
3. Запустить cleanup.
4. Проверить `Commands.Status = 'Failed'`.
5. Проверить отсутствие `NotificationOutbox` и
   `Sessions.CompletionNotified = FALSE`.

## DB-003 — requeue конфликтует с уникальностью completion outbox

### Наблюдение

Requeue сбрасывает `Sessions.CompletionNotified`, но существующая outbox-запись
не сбрасывается. Индекс разрешает только одну запись:

```sql
UNIQUE (EventType, SessionId)
WHERE EventType = 'session_completed'
```

Следующее завершение выполняет `INSERT ... ON CONFLICT DO NOTHING`.

### Последствия

- после уже отправленного completion повторное завершение не создаёт новую
  pending-запись;
- `NotifySessionCompletedOnceAsync` возвращает `false`;
- если старая запись ещё pending, она может отправить сообщение о завершении
  уже во время повторного запуска.

### Связанные места

- `TelegramBot.Data/Sql/Queries.Commands.cs`: `Requeue`;
- `TelegramBot.Data/Sql/Queries.Sessions.cs`: `NotifyCompletionOnce`;
- `TelegramBot.Data/Sql/Queries.Schema.cs`:
  `idx_notification_outbox_session_completed`.

### Предлагаемая проверка

1. Завершить сессию и пометить outbox как `sent`.
2. Выполнить requeue одной команды.
3. Снова завершить команду.
4. Проверить, что новая pending-запись не появилась.

### Возможное исправление

Выбрать одну семантику:

- уникальность по `(EventType, SessionId, CorrelationId/RunNumber)`; или
- при requeue атомарно переводить существующую outbox-запись обратно в
  `pending`, очищая `SentAt`, `LockedUntil` и ошибки.

## DB-004 — отсутствует fencing token Worker-попытки

### Наблюдение

Partition advisory lock удерживается только до commit claim-транзакции.
Terminal update и retry идентифицируют выполнение только по `CommandId`.

### Сценарий

1. Worker A забирает команду.
2. Lease истекает, команда возвращается в `pending`.
3. Worker B забирает её повторно.
4. Worker A оживает и записывает `Done` или retry.
5. Результат актуальной попытки Worker B может быть перезаписан.

### Возможное исправление

При каждом claim генерировать `LeaseToken`/`AttemptId` и включать его во все:

- `MarkProcessStarted`;
- `UpdateStatus`;
- `ScheduleRetry`;
- cleanup-переходы.

Устаревший Worker не должен обновлять строку после смены token.

## DB-005 — неправильный порядок миграции `Cancelled`

### Наблюдение

`DatabaseInitializerService` сначала добавляет `chk_commands_status`, который не
разрешает `Cancelled`, и только после этого выполняет:

```sql
UPDATE Commands
SET Status = 'Deleted'
WHERE Status = 'Cancelled';
```

PostgreSQL проверяет существующие строки при `ADD CONSTRAINT`, поэтому upgrade
может завершиться ошибкой и откатить всю транзакцию.

### Предлагаемая проверка

1. Создать старую схему без constraint.
2. Добавить команду со статусом `Cancelled`.
3. Запустить `InitializeDatabaseAsync`.
4. Проверить, что текущий порядок падает на `ADD CONSTRAINT`.

### Возможное исправление

Перенести `SoftDeleteLegacyCancelled` до добавления constraint. Для крупных
таблиц рассмотреть `CHECK ... NOT VALID` и отдельный `VALIDATE CONSTRAINT`.

## DB-006 — дневной лимит не атомарен

### Наблюдения

- `CountQueuedFilesByUserSince` исключает `Status = 'Deleted'`;
- проверка лимита выполняется до транзакции создания сессии;
- в `CreateSessionWithCommandsAsync` нет заявленного в
  `ExecutionAlgorithm.md` user-level `pg_advisory_xact_lock`.

### Последствия

- удаление сессии возвращает пользователю израсходованную квоту;
- два Server-инстанса могут одновременно прочитать одно значение и оба
  пропустить лимит.

### Предлагаемая проверка

1. Установить небольшой `MaxFilesPerUserPerDay`.
2. Параллельно из двух соединений создать разные задания одного пользователя.
3. Проверить превышение лимита.
4. Отдельно удалить созданную сессию и проверить уменьшение `queuedToday`.

### Возможное исправление

Проверять квоту и создавать сессию в одной DB-транзакции под user-level
advisory lock. Удалённые сессии не должны уменьшать уже использованную квоту.

## DB-007 — несогласованная семантика `MaxRetries`

### Наблюдение

`WorkerOptions.MaxRetries` описан как максимальное количество попыток, однако
обычный failure-path выполняет retry при:

```csharp
cmd.RetryCount < MaxRetries
```

При `MaxRetries = 5` пятая попытка имеет `RetryCount = 4`, поэтому создаётся
шестая попытка. Lease cleanup использует другую границу:
`RetryCount + 1 >= MaxRetries`.

### Предлагаемая проверка

Запустить всегда падающую transient-команду с `MaxRetries = 2` и посчитать
фактические запуски для обычного failure и lease-expiry. Ожидаемое число должно
быть явно зафиксировано как `MaxAttempts` либо `MaxRetryCount`.

## DB-008 — terminal status сессии недостижим

### Наблюдение

`chk_sessions_status` допускает только:

```text
pending, Deleted
```

При этом `SessionsListRenderer` ожидает `Done` и `Failed`, а `GetStatus`
возвращает непосредственно `Sessions.Status`.

### Последствие

Завершённая сессия в детальном представлении может продолжать отображаться со
значком выполнения.

### Возможное исправление

Либо вычислять status из агрегата команд, либо атомарно обновлять status сессии
при terminal transition и расширить constraint.

## DB-009 — command-file операции считаются файлами

### Наблюдение

При создании сессии формируется Cartesian product:

```text
commands × files
```

`GetCompletionSummary` затем считает строки `Commands`, но aliases и модель
называют их `TotalFiles`, `DoneFiles`, `FailedFiles`.

### Пример

Для 5 файлов и команд `PDF` + `DWG` создаётся 10 строк. Уведомление может
сообщить о 10 обработанных файлах вместо 5 файлов или 10 операций.

### Возможное исправление

Определить продуктовую метрику:

- количество уникальных файлов — `COUNT(DISTINCT FilePath)`/`FilesAmount`;
- количество операций — `COUNT(CommandId)`.

В интерфейсе использовать правильное название.

## DB-010 — outbox polling прекращается после одной ошибки

### Наблюдение

`RunOutboxPollingAsync` содержит `try/catch` снаружи всего `while`. Любая
не-cancellation ошибка из `DrainCompletionOutboxAsync` завершает polling task до
перезапуска Server.

### Последствие

Pending notification останется в БД до:

- нового `LISTEN/NOTIFY` wake-up;
- либо перезапуска Server.

### Возможное исправление

Перенести обработку transient-ошибки внутрь цикла и продолжать следующий tick с
ограниченным backoff.

## Дополнительные риски

### Lease основан на часах приложения

`Lease` хранится как Unix seconds в `INTEGER`, а создание и cleanup используют
часы .NET-процессов. При нескольких машинах clock skew может дать преждевременный
или задержанный expiry. `INTEGER` также ограничивает срок примерно 2038 годом.

Предпочтительно хранить `TIMESTAMPTZ` и вычислять expiry через PostgreSQL
`NOW()`.

### Миграции через `CREATE INDEX IF NOT EXISTS`

`IF NOT EXISTS` проверяет имя объекта, но не соответствие его определения
текущему коду. Старый индекс с тем же именем может остаться с устаревшим
predicate/набором колонок.

Кроме того, `idx_commands_pending_priority` удаляется и создаётся заново при
каждом старте, что на большой таблице может блокировать запись и замедлять
запуск.

### Start notification не имеет durable outbox

Уведомление `session_started` отправляется только через `pg_notify`. При
отключённом listener, рестарте Server или переполненном channel оно теряется, а
`StartNotified = TRUE` предотвращает повтор.

## Чек-лист повторной проверки

- [ ] Зафиксировать ожидаемую state machine команд и допустимые переходы.
- [ ] Добавить attempt/lease token и проверить отказ stale Worker updates.
- [ ] Проверить delete → transient retry без воскрешения строки.
- [ ] Проверить terminal lease cleanup с созданием completion outbox.
- [ ] Проверить два последовательных completion после requeue.
- [ ] Проверить requeue при ещё pending старом outbox.
- [ ] Проверить upgrade БД с legacy `Cancelled`.
- [ ] Проверить дневную квоту при двух Server-инстансах.
- [ ] Проверить, что soft-delete не возвращает дневную квоту.
- [ ] Зафиксировать и проверить точное число попыток при `MaxRetries`.
- [ ] Проверить terminal status сессии в `/status`.
- [ ] Разделить метрики файлов и command-file операций.
- [ ] Имитировать одну transient DB-ошибку в outbox polling.
- [ ] Выполнить `EXPLAIN (ANALYZE, BUFFERS)` для claim, session list и outbox.
- [ ] Повторить аудит после исправлений и отметить закрытые ID.

## Критерии закрытия аудита

Каждый ID можно отметить закрытым только после:

1. воспроизведения исходного поведения либо документированного опровержения;
2. исправления или принятого архитектурного решения;
3. повторной проверки на PostgreSQL той же major-версии, что используется в
   production;
4. обновления `ExecutionAlgorithm.md`, если изменилась state machine,
   транзакция, retry, outbox или схема;
5. полной сборки `dotnet build TelegramBot.slnx`.
