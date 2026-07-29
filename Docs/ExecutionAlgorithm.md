# Алгоритм выполнения команд

Semantics pipeline Server → PostgreSQL → Worker → Telegram. Точный SQL — в `TelegramBot.Data/Sql/`.

## Общий поток

```text
Telegram update → Server → Sessions + Commands (1 транзакция) → NOTIFY new_tasks
→ Worker claim → process + TaskFile/ResultFile → Done/retry/Failed → NotificationOutbox → Server отправляет итог
```

## 0. Старт Server и инициализация схемы

`DatabaseInitializerService` — hosted service (`BackgroundService`), зарегистрирован первым среди hosted-сервисов в `AddTelegramBotServer`. Создаёт схему (таблицы, индексы, constraints, legacy soft-delete) в одной транзакции с rollback при ошибке.

Ключевое: **инициализация не блокирует старт хоста**. Раньше вызывалась синхронно в `Program.Main` до `host.RunAsync()` — пока PostgreSQL (в Docker) не поднимался при загрузке машины, инициализация висела > 60 c и SCM убивал старт службы по таймауту (event 7009/7000). Теперь схема создаётся в `ExecuteAsync` с retry (2 → 5 → 15 c, бесконечно до успеха), а хост рапортует SCM «started» немедленно.

Hosted-сервисы стартуют параллельно (fire-and-forget `ExecuteAsync`), поэтому каждый сам толерантен к временно недоступной БД: `CommandNotificationService` — reconnect-циклом, `NotificationSenderService` — изолированным стартовым drain + polling (outbox retry'ется), `TelegramBotHostedService` — пер-апдейтным catch. Стартовое окно без схемы не теряет данные: update'ы буферизуются каналом, outbox и LISTEN переподключаются.

## 1. Создание задания

`SlashCommandService` проверяет команды и разделы, сканирует `01_RVT`, дедуплицирует, проверяет дневной лимит и дубликаты.

В одной DB-транзакции под user-level `pg_advisory_xact_lock`:
- повторная проверка дубликатов (TOCTOU)
- INSERT в `Sessions` + Cartesian product команд×файлов в `Commands`
- `pg_notify('new_tasks', correlationId)`

При дубликатах под lock — откат.

DB-level backstop: `idx_commands_active_unique` — уникальный partial-индекс на `(CommandText, FilePath) WHERE Status IN ('pending', 'processing')`, глобально по всем сессиям/юзерам. Advisory lock сериализует только одного юзера — гонку между **разными** юзерами на идентичный (команда, файл) ловит только этот индекс. При его срабатывании `INSERT` кидает `PostgresException` (`23505`), `SessionDataService` ловит, откатывает транзакцию, возвращает `null` — тот же путь, что и dup-под-lock.

### Priority

Меньшее число — раньше: `PDF/DWG` (1) → `NWC` (2) → `IFC` (3) → `DATA` (4) → default/остальные команды (5).

### Partition

```text
Partition = "file:" + md5(lower(FilePath))
```

Одна команда на partition за раз. Файлы одного проекта — последовательно, разных — параллельно.

## 2. Claim очереди

`CommandExecutionService` слушает `new_tasks` и владеет только соединением/reconnect-backoff. Сам claim/launch делегирует `CommandOrchestrator`.

`CommandOrchestrator.TriggerDrainAsync` триггерится двумя независимыми путями из `CommandExecutionService`: NOTIFY (мгновенно) и отдельный периодический цикл `StartPeriodicBackgroundTaskAsync` (safety-net, интервал `FallbackPollingIntervalSeconds`, тот же generic-хелпер, что и для lease cleanup/health-monitor) — оба ведут к одному и тому же drain. `_drainGate` (внутри оркестратора) не допускает параллельные drain-циклы.

Drain: `availableSlots = MaxConcurrentCommands - runningTaskCount`. Claim атомарно выбирает `pending`-команды с наступившим `NextRetryAt`, исключая partition с `processing`-командой, используя `FOR UPDATE SKIP LOCKED` и partition advisory xact lock. Сортировка по `Priority`, `CreatedAt`, `CommandId`. Запуск — fire-and-forget `Task` (без ожидания), чтобы долгая команда не блокировала claim остальных.

## 3. Подготовка и запуск

`CommandPreparer.PrepareAsync`:
- находит `CommandConfig`
- валидирует FilePath (RootPath, reparse point, extension, существование)
- резолвит Revit/Navisworks executable через BimLib
- возвращает копию конфига с resolved path

`ProcessStarter.StartAsync`:
- создаёт TaskFile (`task_{project}_{commandId}.xml`) с XSD-валидацией
- заполняет `ProcessStartInfo` (Revit: без контрактных CLI-аргументов, `/language RUS`, TaskFile path в `REVITBIMFUSION_TASK_FILE`)
- сериализованный `Process.Start()` через собственный `_launchGate` (`ProcessStarter`, отдельно от `_drainGate` оркестратора)

## 4. Ожидание и результат

| Условие | Результат |
|---|---|
| `status=done` | `Done` |
| `status=failed` (plugin) | permanent `Failed`, без retry |
| `status=cancelled` | `Failed`, без retry |
| invalid XML | `.bad`, failure → retry policy |
| Revit без ResultFile | failure → retry policy |
| wrapper без ResultFile, exit 0 | `Done` fallback |
| wrapper без ResultFile, exit ≠ 0 | failure → retry/permanent по классификатору |
| timeout | process kill, `Failed` (без retry) |

stdout/stderr: 64 KiB capture, 4 KiB в лог. Прочитанный ResultFile удаляется. При отрицательном exit code — Revit journal evidence.

## 5. Retry

Permanent (без retry): plugin `status=failed`/`cancelled`, `PermanentFailureExitCodes`, invalid input, validation errors, non-transient exceptions, timeout.

Transient: `delay = RetryDelayBaseSeconds × 2^RetryCount + jitter`. После `MaxRetries` → `Failed`.

## 6. Завершение сессии

После terminal transition команды — проверка `pending`/`processing` в сессии. Если нет — один SQL: `CompletionNotified = TRUE`, INSERT в `NotificationOutbox`, `pg_notify('command_completed')`.

Server:
- `CommandNotificationService` слушает `session_started` и `command_completed`
- `NotificationSenderService` drain-ит outbox (при старте, по wake-up, каждые 30 с)
- advisory lock на sender для Server replicas

## 7. Cleanup и shutdown

- **Lease recovery**: каждые `CleanupIntervalSeconds` — expired `processing` → `pending`
- **Process health monitoring**: каждые `ProcessMonitorIntervalSeconds` — проверка `Process` внутри `CommandExecutionService` + `DialogDismisser`
- **Session retention**: `SessionCleanupService` — soft-delete сессий старше `CompletedSessionRetentionDays`
- **Worker shutdown**: остановка циклов → process-tree kill (30s budget) → освобождение ресурсов

## 8. Повторный запуск из /status

Кнопка с именем файла в `/status` (для `processing`/`Done`/`Failed`, не `pending`) шлёт `RERUNCMD:{commandId}:{filter}` → `SessionManagementHandler.HandleRerunCommandAsync`.

`CommandDataService.RequeueCommandAsync` (SQL `Requeue`, `Queries.Commands.cs`):
- если `Status == 'processing'` — no-op, возвращает `RequeueOutcome.Processing`, юзер видит toast «⏳ Уже выполняется» (блок дубликата выполнения того же CommandId)
- иначе — сброс той же строки в `pending` (`Lease`/`ProcessId`/`ErrorMessage`/`NextRetryAt`/`CompletedAt` в NULL, `RetryCount` не трогается — это ручной rerun, не авто-retry), `pg_notify('new_tasks', commandId)` для мгновенного подхвата, `RequeueOutcome.Requeued`, toast «🔁 Перезапущено»
- если строка не найдена/чужая/удалена — `RequeueOutcome.NotFound`, тихо игнорируется

Если файл физически ещё выполняется старым процессом в момент rerun — этот процесс осиротевает; его финальный `UpdateStatus` может перезаписать заново queued/processing строку тем же `CommandId`. Разруливается на уровне БД по `CommandId`, отдельный kill старого процесса не делается.

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
