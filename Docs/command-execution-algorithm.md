# Алгоритм выполнения команд (Command Execution Algorithm)

## Обзор

Система выполняет внешние команды (например, для CAD/CAE-приложений или AI-обработки) через асинхронную очередь на базе PostgreSQL с механизмом LISTEN/NOTIFY.

**Ключевые концепции:**
- **Пул процессов** — ограничение на количество одновременно выполняемых процессов защищает систему от перегрузки
- **Lease-механизм** — аренда команды воркером с TTL для защиты от сбоев
- **Таймауты** — принудительное завершение процессов при превышении лимита времени
- **Приоритеты** — команды с более высоким приоритетом выполняются первыми
- **Партиции (TODO)** — логические очереди для изоляции типов задач (резерв в БД готов)

---

## Архитектура

### Общая схема взаимодействия

```
┌─────────────────┐         ┌─────────────┐         ┌─────────────────┐
│     Server      │         │ PostgreSQL  │         │     Worker      │
│  (создание)     │         │   (очередь) │         │  (выполнение)   │
└────────┬────────┘         └──────┬──────┘         └────────┬────────┘
         │                        │                          │
         │ 1. Создать команду     │                          │
         │    (статус: pending)   │                          │
         ├───────────────────────>│                          │
         │                        │                          │
         │ 2. Отправить NOTIFY    │                          │
         ├───────────────────────>│                          │
         │                        │                          │
         │                        │ 3. Ожидать NOTIFY        │
         │                        │ (блокировка)             │
         │                        ├─────────────────────────>│
         │                        │                          │
         │                        │ 4. Захват команд         │
         │                        │    SELECT ... FOR UPDATE │
         │                        │    SKIP LOCKED           │
         │<───────────────────────┤                          │
         │                        │                          │
         │                        │ 5. Выполнить команду     │
         │                        │    (пул процессов)       │
         │                        │                          │
         │                        │ 6. Обновить статус       │
         │                        │    (Done / Failed)       │
         │<───────────────────────┤                          │
         │                        │                          │
```

### Концепция пула процессов

```
┌─────────────────────────────────────────────────────────────────┐
│                    Служба выполнения команд                     │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │              Пул процессов (N слотов)                   │   │
│  │  ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐     │   │
│  │  │ Слот 1│ │ Слот 2│ │ Слот 3│ │ Слот 4│ │ Слот 5│     │   │
│  │  │Процесс│ │Процесс│ │Процесс│ │Процесс│ │Процесс│     │   │
│  │  └───────┘ └───────┘ └───────┘ └───────┘ └───────┘     │   │
│  └─────────────────────────────────────────────────────────┘   │
│                           │                                     │
│                           ▼                                     │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │              Очередь команд (приоритеты)                │   │
│  │  [команда 1] → [команда 2] → [команда 3] → ...         │   │
│  │     (Priority DESC, CreatedAt ASC)                      │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

**Преимущества пула процессов:**
- Защита от перегрузки CPU/RAM — не более N процессов одновременно
- Контроль нагрузки на внешние системы (CAD/CAE)
- Graceful shutdown — завершение активных процессов при остановке воркера

---

## Жизненный цикл команды

| Статус | Описание |
|--------|----------|
| `pending` | Команда создана и ожидает выполнения в очереди |
| `processing` | Команда захвачена воркером и выполняется (Lease установлен) |
| `Done` | Команда успешно завершена |
| `Failed` | Команда завершена с ошибкой |
| `Deleted` | Команда удалена (логическое удаление, soft-delete) |

**Примечание:** Статус `processing` устанавливается атомарно при захвате команды с использованием `SELECT ... FOR UPDATE SKIP LOCKED`.

---

## Алгоритм работы Worker (службы выполнения)

### 1. Инициализация

- Установление подключения к базе данных
- Подписка на уведомление через `LISTEN new_command`
- Инициализация пула процессов (SemaphoreSlim с лимитом N=5)
- Очистка истёкших Lease (crash recovery упавших воркеров)

### 2. Основной цикл обработки

```
┌─────────────────────────────────────────────────────────────────┐
│  Цикл выполнения (фоновая служба)                               │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  Ожидание уведомления (блокировка)                              │
│  - LISTEN new_command                                           │
│  - WaitAsync с таймаутом 5 мин (fallback)                       │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼ (уведомление или таймаут)
┌─────────────────────────────────────────────────────────────────┐
│  Очистка истёкших Lease (каждый цикл)                          │
│  - ReleaseExpiredLeasesAsync()                                  │
│  - ReleaseTimeoutCommandsAsync()                                │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  Захват pending-команд из БД                                    │
│  - SELECT ... FOR UPDATE SKIP LOCKED                            │
│  - ORDER BY Priority DESC, CreatedAt ASC                        │
│  - Статус → 'processing', Lease = timestamp                     │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  Параллельная обработка с ограничением пула                    │
│  - SemaphoreSlim.WaitAsync() для каждого слота                │
│  - Максимум 5 одновременных процессов                           │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  Выполнение одной команды:                                      │
│  1. Создать ProcessStartInfo (Revit/Navisworks/AI)             │
│  2. process.Start()                                             │
│  3. Сохранить в _activeProcesses (трекинг)                     │
│  4. Обновить статус: 'processing', ProcessId = PID             │
│  5. WaitForExit с таймаутом (1 час)                             │
│  6. Если таймаут → process.Kill(true) (дерево процессов)       │
│  7. Если exit_code == 0: статус = 'Done'                       │
│  8. Иначе: статус = 'Failed' + errorMessage                    │
│  9. _activeProcesses.Remove()                                   │
│  10. SemaphoreSlim.Release()                                    │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  Вернуться к ожиданию следующего NOTIFY                         │
└─────────────────────────────────────────────────────────────────┘
```

### 3. Управление пулом процессов

**Назначение:** Ограничение количества одновременно выполняемых процессов.

**Принцип работы:**
- `SemaphoreSlim` с начальным счётчиком `MaxConcurrentProcesses` (5)
- Перед запуском процесса: `await _processPool.WaitAsync(ct)`
- После завершения (в `finally`): `_processPool.Release()`
- Пул общий для всех типов команд

**Алгоритм захвата слота:**
1. `WaitAsync` блокирует поток, пока слот не освободится
2. При отмене (CancellationToken) выбрасывает `OperationCanceledException`

**Алгоритм освобождения слота:**
1. В блоке `finally` (гарантированно)
2. Даже если процесс упал с исключением

### 4. Трекинг активных процессов

**Назначение:** Возможность принудительного завершения при graceful shutdown или таймауте.

**Реализация:**
```csharp
private readonly ConcurrentDictionary<int, ProcessContext> _activeProcesses;

// Перед запуском
_activeProcesses[cmd.CommandId] = new ProcessContext(process, ...);

// После завершения
_activeProcesses.TryRemove(cmd.CommandId, out _);
```

**ProcessContext содержит:**
- `Process` — для доступа к PID и Kill()
- `CommandId` — для логирования
- `Stopwatch` — для измерения длительности

### 5. Обработка ошибок подключения

При потере соединения с базой данных:
1. Зафиксировать ошибку в логе
2. Выждать паузу 5 сек
3. Восстановить подключение
4. Повторно подписаться на уведомления (`LISTEN`)
5. Продолжить обработку очередей

---

## Защита от зависаний и сбоев

Система реализует многоуровневую защиту от зависаний процессов и сбоев воркеров.

### 1. Lease-механизм (аренда команды)

**Проблема:** Воркер может упасть (crash, перезапуск, сеть) после захвата команды, но до завершения.

**Решение:** Атомарный захват с Lease (TTL):

```sql
-- При захвате команды
UPDATE Commands 
SET Status = 'processing', 
    Lease = @LeaseExpiry,  -- Unix timestamp (секунды)
    StartedAt = NOW()
WHERE CommandId = ...
RETURNING ...;
```

**Очистка истёкших Lease:**
```sql
-- Каждые 60 сек + при старте воркера
UPDATE Commands
SET Status = 'pending', 
    Lease = NULL,
    StartedAt = NULL,
    ErrorMessage = 'Lease expired: worker crash or timeout'
WHERE Status = 'processing'
  AND Lease IS NOT NULL
  AND Lease < @CurrentTimeSec;
```

**Параметры:**
- `LeaseTimeoutMin = 5` — Lease истекает через 5 минут
- `CleanupIntervalSec = 60` — проверка каждые 60 секунд

### 2. Таймаут выполнения процесса

**Проблема:** Внешний процесс (Revit/Navisworks) может зависнуть бесконечно.

**Решение:** Принудительное завершение по таймауту:

```csharp
// Ожидание с таймаутом
var timeout = TimeSpan.FromSeconds(ProcessTimeoutSec); // 3600 сек = 1 час
var completed = await Task.Run(() => 
    process.WaitForExit((int)timeout.TotalMilliseconds), ct);

if (!completed)
{
    // Таймаут: убиваем процесс и всё дерево потомков
    process.Kill(true); // true = kill entire process tree
    await dataService.UpdateCommandStatusAsync(cmd.CommandId, CommandStatuses.Failed,
        errorMessage: $"Timeout: process exceeded {ProcessTimeoutSec}s limit");
}
```

**Дополнительная защита (SQL):**
```sql
-- Фоновая задача каждые 60 сек
UPDATE Commands
SET Status = 'pending',
    StartedAt = NULL,
    ProcessId = NULL,
    ErrorMessage = 'Timeout: process exceeded maximum execution time'
WHERE Status = 'processing'
  AND StartedAt < NOW() - INTERVAL '@TimeoutSeconds seconds';
```

### 3. Трекинг активных процессов

**Проблема:** При graceful shutdown нужно завершить активные процессы корректно.

**Решение:** ConcurrentDictionary для трекинга:

```csharp
private readonly ConcurrentDictionary<int, ProcessContext> _activeProcesses;

// При запуске процесса
_activeProcesses[cmd.CommandId] = new ProcessContext(process, cmd.CommandId, sw);

// При завершении (в finally)
_activeProcesses.TryRemove(cmd.CommandId, out _);

// Graceful shutdown
private async Task WaitForActiveProcessesAsync()
{
    var timeout = TimeSpan.FromSeconds(30);
    while (_activeProcesses.Count > 0 && elapsed < timeout)
    {
        await Task.Delay(500);
    }
    
    // Принудительное завершение если не успели
    foreach (var ctx in _activeProcesses.Values)
    {
        ctx.Process.Kill(true);
    }
}
```

### 4. FOR UPDATE SKIP LOCKED

**Проблема:** Несколько воркеров могут захватить одну и ту же команду.

**Решение:** Блокировка строк с пропуском занятых:

```sql
WITH selected AS (
    SELECT c.CommandId, ...
    FROM Commands c
    JOIN Sessions s ON s.SessionId = c.SessionId
    WHERE c.Status = 'pending'
      AND s.Status != 'Deleted'
    ORDER BY Priority DESC, CreatedAt ASC
    LIMIT @Limit
    FOR UPDATE SKIP LOCKED  -- ← Пропускает строки, заблокированные другими воркерами
)
UPDATE Commands c
SET Status = 'processing', Lease = @LeaseExpiry
FROM selected
WHERE c.CommandId = selected.CommandId
RETURNING ...;
```

**Преимущества:**
- Несколько воркеров могут работать параллельно
- Нет конфликтов блокировок
- Каждая команда захватывается ровно одним воркером

### 5. Отмена (CancellationToken)

**Проблема:** Нужно корректно завершить работу при остановке сервиса.

**Решение:** CancellationToken threading:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    _shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    
    try
    {
        await RunListenerLoopAsync(stoppingToken);
    }
    finally
    {
        await WaitForActiveProcessesAsync(); // Graceful shutdown
    }
}

private async Task ExecuteOneAsync(PendingCommand cmd, CancellationToken ct)
{
    try
    {
        // ...
        var completed = await Task.Run(() => process.WaitForExit(...), ct);
    }
    catch (OperationCanceledException) 
    {
        // Отмена: убиваем процесс
        if (process != null && !process.HasExited)
        {
            process.Kill(true);
        }
        throw; 
    }
}
```

### 6. Переподключение при потере связи

**Проблема:** Соединение с PostgreSQL может разорваться.

**Решение:** Outer retry loop:

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    try
    {
        await RunListenerLoopAsync(stoppingToken);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Connection lost. Reconnecting in {Delay}ms...", ReconnectDelayMs);
        await Task.Delay(ReconnectDelayMs, stoppingToken);
        // Повторная попытка → новый conn, новый LISTEN
    }
}
```

**При переподключении:**
1. Создаётся новое подключение
2. Выполняется `LISTEN new_command`
3. Очищаются истёкшие Lease (включая свои)
4. Цикл продолжается

### 7. Fallback poll (safety net)

**Проблема:** NOTIFY может быть потерян (баг, сеть, перезапуск БД).

**Решение:** Таймаут с периодической проверкой:

```csharp
await conn.WaitAsync(TimeSpan.FromSeconds(FallbackTimeoutSec), stoppingToken);
// FallbackTimeoutSec = 300 (5 минут)

// При TimeoutException:
// - Не выбрасываем исключение
// - Просто продолжаем цикл → ProcessBatchAsync()
```

**Результат:** Даже если NOTIFY потерян, воркер проверит очередь каждые 5 минут.

---

## Алгоритм работы Server (создание команд)

### 1. Определение параметров команды

- Определить партию на основе типа команды
- Определить приоритет на основе контекста (например, роль пользователя)

### 2. Сохранение команды в базу данных

- Создать сессию (если требуется)
- Вставить команду со статусом `pending`, указав партию и приоритет
- Все операции в одной транзакции

### 3. Уведомление Worker

- Отправить SQL-уведомление: `NOTIFY <channel_name>`
- Worker мгновенно просыпается и начинает обработку

---

## Конфигурация системы

### Параметры конфигурации (CommandExecutionService)

| Параметр | Константа | Значение | Описание |
|----------|-----------|----------|----------|
| `MaxConcurrentProcesses` | `MaxConcurrentProcesses` | 5 | Глобальный лимит одновременных процессов |
| `ProcessTimeoutSec` | `ProcessTimeoutSec` | 3600 (1 час) | Максимальное время выполнения команды |
| `CleanupIntervalSec` | `CleanupIntervalSec` | 60 | Интервал очистки истёкших Lease |
| `LeaseTimeoutMin` | `LeaseTimeoutMin` | 5 | TTL Lease в минутах (защита от сбоев) |
| `FallbackTimeoutSec` | `FallbackTimeoutSec` | 300 (5 мин) | Таймаут ожидания NOTIFY (safety net) |
| `ReconnectDelayMs` | `ReconnectDelayMs` | 5000 | Задержка перед переподключением к БД |

### Настройка через appsettings.json

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres"
  },
  "Worker": {
    "MaxConcurrentProcesses": 5,
    "ProcessTimeoutSeconds": 3600
  }
}
```

**Примечание:** В текущей реализации параметры заданы константами в коде. Для гибкой настройки можно вынести в конфигурацию.

---

## Структура данных

### Таблица Commands

| Поле | Тип | Описание |
|------|-----|----------|
| `CommandId` | SERIAL | Первичный ключ |
| `SessionId` | INT | Внешний ключ на сессию |
| `CommandText` | VARCHAR | Код типа команды (PDF, DWG, IFC, NWC, AUTORES) |
| `FilePath` | TEXT | Путь к файлу |
| `ExecutionOrder` | INT | Порядок выполнения в сессии |
| `Status` | VARCHAR | Статус: `pending`, `processing`, `Done`, `Failed`, `Deleted` |
| `CreatedAt` | TIMESTAMPTZ | Время создания |
| `StartedAt` | TIMESTAMPTZ | Время начала выполнения (NULL пока pending) |
| `CompletedAt` | TIMESTAMPTZ | Время завершения (NULL пока не Done/Failed) |
| `Lease` | INTEGER | Unix timestamp (секунды) — TTL для защиты от сбоев воркера |
| `Partition` | TEXT | Резерв для будущего расширения (партиции) |
| `Priority` | INT | Приоритет (по умолчанию 50, чем выше — тем важнее) |
| `ProcessId` | INT | PID процесса Windows (для мониторинга и Kill) |
| `ErrorMessage` | TEXT | Сообщение об ошибке (если Failed) |

### Индексы

- `(Status, Priority DESC, CreatedAt ASC) WHERE Status = 'pending'` — для быстрой выборки с приоритетами
- `(Status, Lease) WHERE Status = 'processing'` — для очистки истёкших Lease
- `(Partition, Status)` — для будущего расширения (партиции)
- `(SessionId)` — для выборки по сессии

---

## SQL-операции

### Вставка команды

```sql
INSERT INTO "Commands" 
    ("SessionId", "CommandText", "FilePath", "ExecutionOrder", "Priority")
VALUES 
    (@SessionId, @CommandText, @FilePath, @Order, 50);
```

### Захват команд (атомарный, с Lease)

```sql
WITH selected AS (
    SELECT c.CommandId, c.SessionId, c.CommandText, c.FilePath, 
           c.ExecutionOrder, s.UserId, s.Username, c.Partition, c.Priority
    FROM "Commands" c
    JOIN "Sessions" s ON s."SessionId" = c."SessionId"
    WHERE c."Status" = 'pending'
      AND s."Status" != 'Deleted'
    ORDER BY c."Priority" DESC, c."CreatedAt" ASC
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
UPDATE "Commands"
SET "Status" = @Status,
    "CompletedAt" = CASE 
        WHEN @Status IN ('Done', 'Failed') THEN NOW() 
        ELSE "CompletedAt" 
    END,
    "ProcessId" = @ProcessId,
    "ErrorMessage" = @ErrorMessage
WHERE "CommandId" = @CommandId;
```

### Отправка уведомления

```sql
NOTIFY new_command, @SessionId;
```

### Очистка истёкших Lease (crash recovery)

```sql
-- Каждые 60 секунд + при старте воркера
UPDATE "Commands"
SET "Status" = 'pending',
    "Lease" = NULL,
    "StartedAt" = NULL,
    "ErrorMessage" = 'Lease expired: worker crash or timeout'
WHERE "Status" = 'processing'
  AND "Lease" IS NOT NULL
  AND "Lease" < @CurrentTimeSec;
```

### Очистка команд по таймауту

```sql
-- Каждые 60 секунд (фоновая задача)
UPDATE "Commands"
SET "Status" = 'pending',
    "StartedAt" = NULL,
    "CompletedAt" = NULL,
    "ProcessId" = NULL,
    "ErrorMessage" = 'Timeout: process exceeded maximum execution time',
    "Lease" = NULL
WHERE "Status" = 'processing'
  AND "StartedAt" < NOW() - INTERVAL '@TimeoutSeconds seconds';
```

---

## Безопасность и надёжность

| Принцип | Реализация |
|---------|------------|
| **Логическое удаление** | Команды никогда не удаляются физически, только `Status = 'Deleted'` |
| **Транзакционность** | Захват команд — атомарная операция с `FOR UPDATE SKIP LOCKED` |
| **Ограничение нагрузки** | Пул процессов (SemaphoreSlim) не позволяет превысить лимит одновременных выполнений |
| **Приоритизация** | Высокоприоритетные команды выполняются первыми (`ORDER BY Priority DESC`) |
| **Lease-механизм** | Защита от сбоев воркера — команды возвращаются в очередь при истечении TTL |
| **Таймауты** | Принудительное завершение процессов при превышении лимита времени (`process.Kill(true)`) |
| **Трекинг PID** | Сохранение ProcessId для мониторинга и принудительного завершения |
| **Отказоустойчивость** | Переподключение при потере соединения с БД (5 сек задержка) |
| **Логирование** | Полное контекстное логирование всех операций и ошибок |
| **Изоляция компонентов** | Server и Worker независимы, общаются только через БД |
| **Graceful shutdown** | Корректное завершение активных процессов при остановке сервиса (30 сек таймаут) |
| **FOR UPDATE SKIP LOCKED** | Несколько воркеров могут работать параллельно без конфликтов |
| **Fallback poll** | Если NOTIFY потерян — проверка каждые 5 минут (safety net) |

---

## Выполнение внешнего процесса

### Общий алгоритм

1. Получить конфигурацию для типа команды (Revit/Navisworks/AI)
2. Создать `ProcessStartInfo` с параметрами:
   - `FileName` — путь к исполняемому файлу
   - `Arguments` — аргументы командной строки
   - `WorkingDirectory` — рабочая директория
   - `RedirectStandardOutput/Error = true` — для логирования
   - `UseShellExecute = false` — для перенаправления потоков
   - `CreateNoWindow = true` — без UI
3. `process.Start()` — запуск процесса
4. Сохранить в `_activeProcesses[CommandId]` для трекинга
5. Обновить статус: `processing`, `ProcessId = process.Id`
6. `WaitForExit(timeout)` — ожидание с таймаутом
7. Если таймаут — `process.Kill(true)` (дерево процессов)
8. Проверить `ExitCode`:
   - `0` → статус `Done`
   - `!= 0` → статус `Failed` + `ErrorMessage`
9. Удалить из `_activeProcesses`
10. `_processPool.Release()` — освободить слот

### Обработка результатов

```
try
    создать ProcessStartInfo
    process.Start()
    _activeProcesses[CommandId] = context
    UpdateCommandStatus(Processing, ProcessId=PID)
    WaitForExit(timeout)
    если timeout → Kill(true), UpdateCommandStatus(Failed, "Timeout")
    если exit_code == 0 → UpdateCommandStatus(Done)
    иначе → UpdateCommandStatus(Failed, "Exit code N")
catch (OperationCanceledException)
    если процесс активен → Kill(true)
    throw
catch (exception)
    записать ошибку в лог
    UpdateCommandStatus(Failed, errorMessage)
finally
    _activeProcesses.Remove(CommandId)
    _processPool.Release()  ← обязательно!
```

### Заглушки для типов команд

В текущей реализации методы создания `ProcessStartInfo` содержат TODO:

- `CreateRevitProcessStartInfo()` — Revit.exe с аргументами
- `CreateNavisworksProcessStartInfo()` — FileConvert.exe или COM API
- `CreateAiAgentProcessStartInfo()` — Python скрипт или HTTP-клиент

---

## Расширение системы (добавление новой команды)

### Шаг 1: Определить код новой команды

Добавить константу с уникальным идентификатором команды (например, `XLSEXPORT`).

### Шаг 2: Добавить обработку в CommandExecutionService

Добавить кейс в `ExecuteOneAsync`:

```csharp
"XLSEXPORT" => CreateExcelExportProcessStartInfo(cmd),
```

### Шаг 3: Реализовать метод-фабрику

```csharp
private ProcessStartInfo CreateExcelExportProcessStartInfo(PendingCommand cmd)
{
    return new ProcessStartInfo
    {
        FileName = "excel_exporter.exe",
        Arguments = $"--input \"{cmd.FilePath}\"",
        WorkingDirectory = Environment.CurrentDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
}
```

### Шаг 4: Настроить приоритет (опционально)

При создании команды указать приоритет:

```sql
INSERT INTO "Commands" (..., "Priority") VALUES (..., 75); -- Выше среднего
```

### Шаг 5: Обновить пользовательский интерфейс (при необходимости)

Добавить кнопку/команду выбора в интерфейс бота.

---

## Диагностика и мониторинг

### Запросы для анализа состояния

**Очередь pending-команд (с приоритетами):**
```sql
SELECT "CommandId", "SessionId", "CommandText", "Priority", "CreatedAt",
       EXTRACT(EPOCH FROM (NOW() - "CreatedAt")) as "AgeSec"
FROM "Commands"
WHERE "Status" = 'pending'
ORDER BY "Priority" DESC, "CreatedAt" ASC;
```

**Активные выполнения (с PID и длительностью):**
```sql
SELECT "CommandId", "CommandText", "ProcessId", "StartedAt",
       EXTRACT(EPOCH FROM (NOW() - "StartedAt")) as "DurationSec",
       "Lease",
       CASE WHEN "Lease" < EXTRACT(EPOCH FROM NOW()) THEN 'EXPIRED' ELSE 'OK' END as "LeaseStatus"
FROM "Commands"
WHERE "Status" = 'processing'
ORDER BY "StartedAt" ASC;
```

**История выполнений (за 24 часа):**
```sql
SELECT "CommandId", "CommandText", "Priority", "Status", 
       "CreatedAt", "StartedAt", "CompletedAt",
       EXTRACT(EPOCH FROM ("CompletedAt" - "StartedAt")) as "DurationSec",
       "ErrorMessage"
FROM "Commands"
WHERE "Status" IN ('Done', 'Failed')
  AND "CreatedAt" > NOW() - INTERVAL '24 hours'
ORDER BY "CreatedAt" DESC
LIMIT 20;
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

**Зависшие команды (превышен таймаут):**
```sql
SELECT "CommandId", "CommandText", "ProcessId", "StartedAt",
       EXTRACT(EPOCH FROM (NOW() - "StartedAt")) as "DurationSec"
FROM "Commands"
WHERE "Status" = 'processing'
  AND "StartedAt" < NOW() - INTERVAL '1 hour'
ORDER BY "StartedAt" ASC;
```

**Статистика по статусам:**
```sql
SELECT 
    "Status",
    COUNT(*) as "Count",
    AVG(EXTRACT(EPOCH FROM ("CompletedAt" - "StartedAt"))) FILTER (WHERE "Status" IN ('Done', 'Failed')) as "AvgDurationSec"
FROM "Commands"
WHERE "CreatedAt" > NOW() - INTERVAL '24 hours'
GROUP BY "Status";
```

**Проверка подписки на уведомления:**
```sql
SELECT * FROM pg_listening_channels();
-- Должен вернуть 'new_command'
```

**Мониторинг процессов (активные PID):**
```sql
SELECT "CommandId", "CommandText", "ProcessId", "StartedAt"
FROM "Commands"
WHERE "Status" = 'processing'
  AND "ProcessId" IS NOT NULL;
```

**Процессы в ОС (Windows PowerShell):**
```powershell
# Проверить, существует ли процесс
Get-Process -Id <ProcessId> -ErrorAction SilentlyContinue

# Все процессы Revit
Get-Process Revit* | Select-Object Id, StartTime, CPU
```

---

## Критерии корректной реализации

| № | Критерий | Описание |
|---|----------|----------|
| 1 | **Лимит процессов** | Никогда не выполняется более `MaxConcurrentProcesses` (5) одновременно |
| 2 | **Приоритизация** | Высокоприоритетные команды стартуют раньше низкоприоритетных |
| 3 | **Lease-механизм** | При сбое воркера команда возвращается в очередь после истечения Lease |
| 4 | **Таймауты** | Процессы, выполняющиеся дольше 1 часа, принудительно завершаются |
| 5 | **Трекинг PID** | ProcessId сохраняется для мониторинга и принудительного завершения |
| 6 | **FOR UPDATE SKIP LOCKED** | Несколько воркеров могут работать параллельно без конфликтов |
| 7 | **Graceful shutdown** | При остановке воркер завершает активные процессы (30 сек таймаут) |
| 8 | **Fallback poll** | Если NOTIFY потерян — проверка каждые 5 минут |
| 9 | **Восстановление** | При перезапуске Worker очищает истёкшие Lease и продолжает обработку |
| 10 | **Наблюдаемость** | Диагностические запросы показывают актуальное состояние (PID, Lease, длительность) |
