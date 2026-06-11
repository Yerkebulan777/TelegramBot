# Алгоритм выполнения команд

> **Связанные документы:** [AGENTS.md](../AGENTS.md) — архитектура проекта, BimLib, DI | [BimPluginContract.md](BimPluginContract.md) — контракт BIM-плагинов | [README.md](../README.md) — общее описание

## Архитектура

```
Server → PostgreSQL (Commands, Status='pending')
    → LISTEN/NOTIFY new_tasks → Worker (мгновенно) + fallback polling (5 мин)
    → FOR UPDATE SKIP LOCKED → выполнение → Done/Failed
    → NOTIFY command_completed (SessionId|CorrelationId) → Server
    → SessionDataService.GetSessionCompletionSummaryAsync() → Telegram-уведомление
```

Детальная схема компонентов и DI-регистрации — в [AGENTS.md](../AGENTS.md).

## Жизненный цикл команды

| Статус | Описание |
|--------|----------|
| `pending` | Команда создана и ожидает выполнения |
| `processing` | Команда захвачена воркером (Lease установлен) |
| `Done` | Успешно завершена |
| `Failed` | Завершена с ошибкой |
| `Deleted` | Soft-delete (пользователь отменил) |

**Переходы:** `pending → processing → Done/Failed`. Статус `Deleted` финальный — Worker не перезаписывает его.

## SQL-операции

### Вставка команды

```sql
INSERT INTO "Commands" 
    ("SessionId", "CommandText", "FilePath", "ExecutionOrder", "Priority")
SELECT 
    @SessionId, unnest(@CommandTexts::text[]), unnest(@FilePaths::text[]), 
    unnest(@Orders::int[]), unnest(@Priorities::int[]);
```

### Захват команд (атомарный, FOR UPDATE SKIP LOCKED)

```sql
WITH selected AS (
    SELECT c.CommandId, c.SessionId, c.CommandText, c.FilePath, 
           c.ExecutionOrder, s.UserId, s.Username, c.Partition, c.Priority
    FROM "Commands" c
    JOIN "Sessions" s ON s."SessionId" = c."SessionId"
    WHERE c."Status" = 'pending'
      AND s."Status" != 'Deleted'
      AND (c."NextRetryAt" IS NULL OR c."NextRetryAt" <= NOW())
    ORDER BY c."Priority" ASC, c."CreatedAt" ASC
    LIMIT @Limit
    FOR UPDATE SKIP LOCKED
)
UPDATE "Commands" c
SET "Status" = 'processing', 
    "Lease" = @LeaseExpiry,
    "StartedAt" = NOW()
FROM selected
WHERE c."CommandId" = selected."CommandId"
RETURNING selected.CommandId, selected.SessionId, selected.CommandText,
          selected.FilePath, selected.ExecutionOrder, selected.UserId, 
          selected.Username, selected.Partition, selected.Priority;
```

### Обновление статуса

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

### Сигнал завершения сессии (Worker → Server)

```sql
NOTIFY command_completed, 'SessionId|CorrelationId';
```

`command_completed` — только транспортный сигнал. Единственный источник данных для текста
уведомления — `SessionDataService.GetSessionCompletionSummaryAsync()`, который читает из БД:
пользователя, проект, total/done/failed, длительность и список failed-файлов.

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

### Отмена команды пользователем

```sql
UPDATE Commands
SET Status = 'Deleted'
WHERE CommandId = @CommandId
  AND (SessionId IN (SELECT SessionId FROM Sessions WHERE UserId = @UserId)
       OR @IsAdmin = true);
```

## Приоритеты команд и партиции

| Команда | Priority | Лимит процессов |
|---------|----------|-----------------|
| PDF | 1 (Critical) | до 3 |
| DWG | 2 (High) | до 5 |
| NWC, IFC, BIMDOC, CLASHREP | 3 (Medium) | до 3 |
| AUTORES | 4 (Low) | до 1 |
| Не указана | 50 (Default) | до 1 |

**Назначение партиции:** первый threshold `>= Priority`. Thresholds `[0, 1, 2, 3]` → лимиты `[5, 3, 2, 1]`.

## Диагностические запросы

**Очередь pending-команд:**
```sql
SELECT "CommandId", "CommandText", "Priority", "CreatedAt",
       EXTRACT(EPOCH FROM (NOW() - "CreatedAt")) as "AgeSec"
FROM "Commands"
WHERE "Status" = 'pending'
ORDER BY "Priority" ASC, "CreatedAt" ASC;
```

**Активные выполнения:**
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

## Расширение системы

### Контракт внешнего исполнителя

Для Revit/Navisworks/AI-команд Worker использует один механизм:

1. `ProcessRunner.RunAsync()` генерирует `AttemptToken`.
2. `CommandPreparer.CreateTaskFile()` создаёт `task_{CommandId}_{AttemptToken}.json`.
3. `CommandPreparer.CreateProcessStartInfo()` подставляет `{TaskFilePath}` и `{ResultFilePath}` в `ArgumentsTemplate`.
4. После выхода процесса `ProcessRunner.TryReadResultFile()` читает `result_{CommandId}_{AttemptToken}.json`.
5. Если result-файл отсутствует или невалиден, Worker использует fallback по exit code.

Revit требует установленный AddIn: `Revit.exe` сам не выполняет `/command`. Для Navisworks/FileConvert полноценный `TaskFile + ResultFile` контракт тоже требует обёртку или плагин; чистый `FileConvert.exe` может работать только через fallback по exit code.

Подробности: [BimPluginContract.md](BimPluginContract.md).

### Добавление новой команды

1. **Добавить конфигурацию** в `appsettings.json` Worker:
   ```json
   "Commands": {
     "XLSEXPORT": {
       "ExecutablePath": "excel_exporter.exe",
       "ArgumentsTemplate": "--input \"{FilePath}\"",
       "AllowedExtensions": [".xlsx", ".xls"]
     }
   }
   ```

2. **Настроить приоритет** — добавить запись в `CommandPriorityMap` в `SlashCommandService.cs`. Если не добавить — Priority=50 (Lowest).

3. **Настроить лимиты партиций** (опционально):
   ```json
   "Partitions": { "0": 5, "1": 3, "2": 2, "3": 1 }
   ```

## Оптимизации и улучшения (v1.8)

### Критические исправления (v1.8)

#### RateLimiter
- **Проблема**: Race condition между `CleanupExpired` и `lock`, избыточная сложность с `Interlocked.CompareExchange` + `lock`, удаление из словаря внутри lock другого объекта
- **Решение**: Упрощена до единого `lock` на уровне `RequestWindow`. Очистка и проверка выполняются в одной критической секции. Удалён флаг `CleanupInProgress`
- **Файл**: `TelegramBot.Core/Services/RateLimiter.cs`

#### SessionManager  
- **Проблема**: Утечка памяти `_sessionLocks` — семафоры никогда не удалялись при `RemoveSession`, race condition между проверкой таймаута и `GetOrAdd`, блокировка в `CleanUpExpiredSessionsAsync` могла долго удерживать семафор
- **Решение**: Безопасное удаление семафоров при `RemoveSession` с проверкой `CurrentCount == 1`. Атомарная проверка и обновление сессии. Улучшено логирование с указанием userId и причины удаления
- **Файл**: `TelegramBot.Server/Services/Application/SessionManager.cs`

#### ProcessRunner
- **Проблема**: `BlockingCollection<string>` мог потреблять неограниченную память при большом выводе процесса, отсутствие лимита на размер вывода, `TruncateOutput` обрезал до 4KB но после сбора всего вывода (память уже потрачена)
- **Решение**: Потоковая обработка stdout/stderr через события `OutputDataReceived` с ограничением 64KB на поток. `StringBuilder` инициализируется с capacity 1024 и растёт только до лимита
- **Файл**: `TelegramBot.Worker/Services/ProcessRunner.cs`

### Средние улучшения (v1.8)

#### CommandExecutionService
- **Проблема**: Отсутствие ограничения на размер `_runningTasks` — HashSet мог расти бесконечно при высокой нагрузке. `ContinueWith` без `TaskScheduler` выполнялся на thread pool
- **Решение**: Добавлена периодическая очистка завершённых задач при превышении 1000 элементов. Освобождение слота пула вынесено в `finally` блок
- **Файл**: `TelegramBot.Worker/Services/CommandExecutionService.cs`

#### PartitionPoolManager
- **Проблема**: Некорректная логика приоритетов (комментарий говорил "чем меньше Priority, тем выше приоритет", но код инвертировал логику), отсутствие валидации при `ReleaseSlot` приводило к исключениям
- **Решение**: Исправлен комментарий и логика `GetThreshold`. Добавлена проверка на переполнение семафора в `ReleaseSlot` с предупреждением в лог
- **Файл**: `TelegramBot.Worker/Services/PartitionPoolManager.cs`

#### CallbackDispatcher
- **Проблема**: Линейный поиск обработчиков O(n) при каждом callback, отсутствие кэширования маппинга prefix → handler
- **Решение**: Создан словарь `_handlerMap` при инициализации для поиска O(1). Группировка по префиксам с выбором хендлера наименьшего приоритета
- **Файл**: `TelegramBot.Server/Services/Application/CallbackDispatcher.cs`

#### SessionCompletionTracker
- **Проблема**: Лишний SQL-запрос `CountPendingProcessingBySessionAsync` при каждой завершённой сессии, race condition между decremented счётчиком и проверкой БД
- **Решение в текущем коде**: in-memory счётчик `_sessionRemaining` сокращает число проверок, а при обнулении batch-счётчика выполняется `CountPendingProcessingBySessionAsync`, чтобы не отправить уведомление раньше завершения всех pending/processing команд
- **Файл**: `TelegramBot.Worker/Services/SessionCompletionTracker.cs`

#### CommandPreparer
- **Проблема**: Temp-файлы predictable/stale между retry-попытками
- **Решение в текущем коде**: имена task/result включают уникальный `AttemptToken`; temp-файлы текущей попытки удаляются в `ProcessRunner.RunAsync()` через `CommandPreparer.CleanupTempFiles`
- **Файл**: `TelegramBot.Worker/Services/CommandPreparer.cs`

### Улучшения логирования и мониторинга (v1.8)

#### Логирование
- Добавлено логирование времени выполнения callback-хендлеров с `elapsedMs` и именем хендлера
- Добавлены флаги `truncated` и `limit` в логи stdout/stderr процессов
- Добавлено подробное логирование очистки сессий: количество удалённых, возраст, userId
- Добавлено логирование disposal семафоров сессий с указанием причины (expired/manual removal)
- Улучшены сообщения об ошибках с контекстом: userId, correlationId, sessionId, elapsed time, attempt number
- Добавлено логирование переполнения семафоров в `PartitionPoolManager.ReleaseSlot`
- Добавлено логирование очистки `_runningTasks` в `CommandExecutionService` с количеством удалённых задач
- Добавлено логирование stdout/stderr внешних процессов с защитой от переполнения логов

#### Мониторинг
- Health check endpoint `/health` включает базовые checks `database` и `process`.
- Worker добавляет checks:
  - `bimInstallRoot` — наличие `BimIntegration:RevitInstallRoot`
  - `activeProcesses` — количество активных внешних процессов в `ProcessRunner`

#### Диагностика
- Добавлен SQL-запрос для диагностики зависших команд с истёкшим Lease
- Отдельных `/debug/sessions`, `/debug/processes` и Prometheus exporter в текущем коде нет
