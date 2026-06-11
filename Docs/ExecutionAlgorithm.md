# Алгоритм выполнения команд

> **Связанные документы:** [AGENTS.md](../AGENTS.md) — архитектура проекта, BimLib, DI | [ROADMAP.md](../ROADMAP.md) — дорожная карта

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
