# Алгоритм выполнения команд

Semantics pipeline Server → PostgreSQL → Worker → Telegram. Точный SQL — в `TelegramBot.Data/Sql/`.

## Общий поток

```text
Telegram update → Server → Sessions + Commands (1 транзакция) → NOTIFY new_tasks
→ Worker claim → process + TaskFile/ResultFile → Done/retry/Failed → NotificationOutbox → Server отправляет итог
```

## 1. Создание задания

`SlashCommandService` проверяет команды и разделы, сканирует `01_RVT`, дедуплицирует, проверяет дневной лимит и дубликаты.

В одной DB-транзакции под user-level `pg_advisory_xact_lock`:
- повторная проверка дубликатов (TOCTOU)
- INSERT в `Sessions` + Cartesian product команд×файлов в `Commands`
- `pg_notify('new_tasks', correlationId)`

При дубликатах под lock — откат.

### Priority

Меньшее число — раньше: `PDF/DWG` (1) → `NWC` (2) → `IFC` (3) → `DATA` (4) → default/остальные команды (5).

### Partition

```text
Partition = "file:" + md5(lower(FilePath))
```

Одна команда на partition за раз. Файлы одного проекта — последовательно, разных — параллельно.

## 2. Claim очереди

`CommandExecutionService` слушает `new_tasks`. При connect: освобождает expired leases → drain очереди → ждёт NOTIFY → fallback polling при таймауте → reconnect с backoff.

Drain: `availableSlots = MaxConcurrentCommands - runningTaskCount`. Claim атомарно выбирает `pending`-команды с наступившим `NextRetryAt`, исключая partition с `processing`-командой, используя `FOR UPDATE SKIP LOCKED` и partition advisory xact lock. Сортировка по `Priority`, `CreatedAt`, `CommandId`. `_drainGate` не допускает параллельные drain-циклы.

## 3. Подготовка и запуск

`CommandPreparer.PrepareAsync`:
- находит `CommandConfig`
- валидирует FilePath (RootPath, reparse point, extension, существование)
- резолвит Revit/Navisworks executable через BimLib
- возвращает копию конфига с resolved path

`ProcessStarter.StartAsync`:
- создаёт TaskFile (`task_{project}_{commandId}.xml`) с XSD-валидацией
- заполняет `ProcessStartInfo` (Revit: пустые args, TaskFile path в `REVITBIMFUSION_TASK_FILE`)
- сериализованный `Process.Start()` через `_launchGate`

## 4. Ожидание и результат

| Условие | Результат |
|---|---|
| `status=done` | `Done` |
| `status=failed` | permanent `Failed` или retry |
| `status=cancelled` | `Failed`, без retry |
| invalid XML | `.bad`, failure |
| Revit без ResultFile | failure |
| wrapper без ResultFile, exit 0 | `Done` fallback |
| wrapper без ResultFile, exit ≠ 0 | failure |
| timeout | process kill, `Failed` |

stdout/stderr: 64 KiB capture, 4 KiB в лог. Прочитанный ResultFile удаляется. При отрицательном exit code — Revit journal evidence.

## 5. Retry

Permanent (без retry): `PermanentFailureExitCodes`, invalid input, validation errors, cancellation, non-transient exceptions.

Transient: `delay = RetryDelayBaseSeconds × 2^RetryCount + jitter`. После `MaxRetries` → `Failed`.

## 6. Завершение сессии

После terminal transition команды — проверка `pending`/`processing` в сессии. Если нет — один SQL: `CompletionNotified = TRUE`, INSERT в `NotificationOutbox`, `pg_notify('command_completed')`.

Server:
- `CommandNotificationService` слушает `session_started` и `command_completed`
- `NotificationSenderService` drain-ит outbox (при старте, по wake-up, каждые 30 с)
- advisory lock на sender для Server replicas

## 7. Cleanup и shutdown

- **Lease recovery**: каждые `CleanupIntervalSeconds` — expired `processing` → `pending`
- **Process health monitoring**: каждые `ProcessMonitorIntervalSeconds` — `ProcessHealthHelper` + `DialogDismisser`
- **Session retention**: `SessionCleanupService` — soft-delete сессий старше `CompletedSessionRetentionDays`
- **Worker shutdown**: остановка циклов → process-tree kill (30s budget) → освобождение ресурсов

## Статусы

`pending` → `processing` → `Done` / `Failed`. `Deleted` — скрыта пользователем/retention. `Cancelled` мигрирован в `Deleted`.

## PostgreSQL channels и locks

| Канал | Назначение |
|---|---|
| `new_tasks` | разбудить Worker |
| `session_started` | уведомление «задание запущено» |
| `command_completed` | разбудить outbox sender |

Session advisory locks для: lease cleanup, outbox sender, partition claim, duplicate check. Точные ID — в `Queries.Commands.cs` и `Queries.NotificationOutbox.cs`.

## Добавление команды

1. `CommandCodes` + `CallbackPrefixes` + `CommandCatalog`
2. priority map в `SlashCommandService`
3. `Worker:Commands` config
4. `IsRevitCommand` если Revit AddIn
5. canonical BIM contract/XSD/plugin если меняется boundary
6. README, AGENTS и этот документ
