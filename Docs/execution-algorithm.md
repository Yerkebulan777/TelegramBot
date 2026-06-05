# Алгоритм выполнения команд (Command Execution Algorithm)

> **Связанные документы:** [ROADMAP.md](../ROADMAP.md) — дорожная карта проекта | [README.md](../README.md) — обзор проекта

## Содержание

- [Архитектурные паттерны](#архитектурные-паттерны)
- [Архитектура](#архитектура)
- [Жизненный цикл команды](#жизненный-цикл-команды)
- [Алгоритм работы Worker](#алгоритм-работы-worker-службы-выполнения)
- [Защита от зависаний и сбоев](#защита-от-зависаний-и-сбоев)
- [Алгоритм работы Server](#алгоритм-работы-server-создание-команд)
- [Отмена команды пользователем](#отмена-команды-пользователем)
- [Уведомления пользователей](#уведомления-пользователей-telegram)
- [Конфигурация системы](#конфигурация-системы)
- [Как устроена база данных](#как-устроена-база-данных)
- [SQL-операции](#sql-операции)
- [Безопасность и надёжность](#безопасность-и-надёжность)
- [Выполнение внешнего процесса](#выполнение-внешнего-процесса)
- [Расширение системы](#расширение-системы-добавление-новой-команды)
- [Диагностика и мониторинг](#диагностика-и-мониторинг)
- [Критерии корректной реализации](#критерии-корректной-реализации)
- [Известные ограничения и технический долг](#известные-ограничения-и-технический-долг)

---

## Архитектурные паттерны

В системе реализованы следующие архитектурные паттерны:

| Паттерн | Применение | Описание |
|---------|-----------|----------|
| **Chain of Responsibility** | Обработка callback-запросов (`CallbackDispatcher`) | Каждый хендлер проверяет, может ли он обработать callback. Если нет — передаёт следующему |
| **Strategy** | Исполнение команд (`CommandConfig`) | Конфигурация команды определяет, какую стратегию запуска применить (Revit, Navisworks, Python) |
| **Observer (Pub/Sub)** | Очередь задач (PostgreSQL LISTEN/NOTIFY) | Server публикует NOTIFY, Worker подписан через LISTEN. 3 канала: `new_command`, `command_completed`, `command_cancel` |
| **Competing Consumers** | Параллельная обработка (FOR UPDATE SKIP LOCKED) | Несколько Worker-ов конкурируют за команды, каждая выполняется ровно одним |
| **CQRS (Command Query Responsibility Segregation)** | Отмена команд (Server → command_cancel → Worker) | Server изменяет статус в БД и отправляет NOTIFY, Worker получает команду и убивает процесс |
| **Bulkhead (изоляция)** | Priority-based партиции (`SemaphoreSlim`) | Каждый уровень приоритета имеет изолированный пул слотов |
| **Circuit Breaker** | Reconnect loop + fallback poll | При потере соединения — пауза 5 сек, затем восстановление |
| **Retry with Exponential Backoff** | Повторные попытки (`MaxRetries=5`) | Задержка растёт экспоненциально: 60s → 120s → 240s → 480s → 960s |
| **Lease (аренда)** | Защита от сбоев воркеров (`Lease` + `StartedAt`) | Команда «арендуется» на время выполнения; при сбое воркера возвращается в очередь |
| **Soft Delete** | Логическое удаление (`Status = 'Deleted'`) | Строки никогда не удаляются физически |
| **Singleton** | DI-регистрация всех сервисов | Гарантирует единый экземпляр сервиса на всё приложение |

---

## Обзор

Система выполняет внешние команды (например, для CAD/CAE-приложений или AI-обработки) через асинхронную очередь на базе PostgreSQL с механизмом LISTEN/NOTIFY.

**Ключевые концепции:**
- **Пул процессов** — ограничение на количество одновременно выполняемых процессов защищает систему от перегрузки
- **Lease-механизм** — аренда команды воркером с TTL для защиты от сбоев
- **Таймауты** — принудительное завершение процессов при превышении лимита времени
- **Приоритеты** — команды с более высоким приоритетом выполняются первыми
- **Партиции** — приоритетные уровни: команды с высоким приоритетом имеют выделенные слоты выполнения
- **Отмена команд** — пользователь может отменить команду через `/status` → кнопка «⛔ Отменить»; Server меняет статус на `Cancelled` и отправляет NOTIFY `command_cancel`; Worker убивает процесс

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

### Концепция priority-based партиций

```
┌─────────────────────────────────────────────────────────────────┐
│                    Служба выполнения команд                     │
│                                                                 │
│  ┌────────────────────────┐ ┌──────────────┐ ┌──────────────┐  │
│  │  Priority >= 80 (High) │ │Priority>=40  │ │Priority < 40 │  │
│  │  SemaphoreSlim(5)      │ │SemaphoreSlim(3)│ SemaphoreSlim(1)│ │
│  │  ┌───┐┌───┐┌───┐┌───┐ │ │ ┌───┐┌───┐  │ │ ┌───┐       │  │
│  │  │ P ││ P ││ P ││ P │ │ │ │ P ││ P │  │ │ │ P │       │  │
│  │  └───┘└───┘└───┘└───┘ │ │ └───┘└───┘  │ │ └───┘       │  │
│  └────────────────────────┘ └──────────────┘ └──────────────┘  │
│                           │                                     │
│                           ▼                                     │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │        Очередь команд (Priority DESC, общая)            │   │
│  │  [P=90] → [P=85] → [P=70] → [P=50] → [P=30] → ...     │   │
│  │     (Priority DESC, CreatedAt ASC)                      │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  Маршрутизация: cmd.Priority >= 80 → Partition 0 (High)       │
│                 80 > cmd.Priority >= 40 → Partition 1 (Medium)  │
│                 cmd.Priority < 40 → Partition 2 (Low)           │
└─────────────────────────────────────────────────────────────────┘
```

**Преимущества priority-based партиций:**
- Высокоприоритетные команды имеют выделенные слоты и не ждут за низкоприоритетными
- Гарантированная пропускная способность для критических задач
- Low-priority команды не блокируют High-priority (даже если очередь забита)

---

## Жизненный цикл команды

| Статус | Описание |
|--------|----------|
| `pending` | Команда создана и ожидает выполнения в очереди |
| `processing` | Команда захвачена воркером и выполняется (Lease установлен) |
| `Done` | Команда успешно завершена |
| `Failed` | Команда завершена с ошибкой |
| `Cancelled` | Команда отменена пользователем (через `/status` → кнопка «⛔ Отменить»). Статус устанавливается Server-ом, Worker при получении NOTIFY `command_cancel` убивает процесс |
| `Deleted` | Команда удалена (логическое удаление, soft-delete) |

**Примечание:** Статус `processing` устанавливается атомарно при захвате команды с использованием `SELECT ... FOR UPDATE SKIP LOCKED`.
Статус `Cancelled` является финальным — команда не может быть изменена после отмены.

---

## Алгоритм работы Worker (службы выполнения)

### 1. Инициализация

- Установление подключения к базе данных
- Подписка на уведомление через `LISTEN new_command` и `LISTEN command_cancel`
- Инициализация per-partition пулов (`SortedDictionary<int, SemaphoreSlim>`) из конфигурации (`WorkerOptions.Partitions`)
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
│  - LISTEN new_command, command_cancel                           │
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
│  Параллельная обработка с priority-based пулами                │
│  - Определение партиции по приоритету команды:                  │
│    _partitionPools.Keys.Reverse().First(t => cmd.Priority >= t) │
│  - Ожидание слота в своей партиции:                             │
│    _partitionPools[threshold].WaitAsync()                       │
│  - Каждая партиция (уровень приоритета) имеет свой лимит       │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  Выполнение одной команды:                                      │
│  1. Валидация FilePath                                         │
│  2. Создать ProcessStartInfo из конфигурации команды           │
│  3. process.Start()                                             │
│  4. Сохранить в _activeProcesses (трекинг)                     │
│  5. Обновить статус: 'processing', ProcessId = PID             │
│  6. Асинхронное чтение stdout/stderr (BeginOutputReadLine)     │
│  7. WaitForExit с таймаутом (ProcessTimeoutSeconds)             │
│  8. Логирование stdout/stderr (обрезка >4KB)                   │
│  9. Если таймаут → process.Kill(true)                          │
│  10. Per-command CTS + проверка отмены                        │
│  11. Если exit_code == 0: статус = 'Done'                      │
│  12. Если cmdCt.IsCancellationRequested → return (без update)  │
│  13. Иначе: статус = 'Failed' + errorMessage                   │
│  14. _activeProcesses.Remove() + _commandCts.Dispose()         │
│  15. partitionPool.Release() (в finally)                       │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  Вернуться к ожиданию следующего NOTIFY                         │
└─────────────────────────────────────────────────────────────────┘
```

### 3. Управление per-partition пулами процессов

**Назначение:** Ограничение количества одновременно выполняемых процессов для каждой партиции отдельно.

**Принцип работы:**
- `SortedDictionary<int, SemaphoreSlim>` — карта threshold приоритета → пул
- Инициализация из `WorkerOptions.Partitions` при старте воркера
- Ключ словаря = минимальный `Priority` (threshold), значение = `SemaphoreSlim`
- Перед запуском процесса: `_partitionPools[threshold].WaitAsync(ct)`
- После завершения (в `finally`): `_partitionPools[threshold].Release()`

**Определение партиции команды:**
- Находится highest threshold, где `cmd.Priority >= threshold`
- `_partitionPools.Keys.Reverse().FirstOrDefault(t => cmd.Priority >= t)`
- По умолчанию: Priority>=80 → pool(5), Priority>=40 → pool(3), Priority<40 → pool(1)
- Если threshold не найден (например, отрицательный Priority) — используется минимальный threshold (0)

**Алгоритм захвата слота:**
1. `threshold = _partitionPools.Keys.Reverse().First(t => cmd.Priority >= t)`
2. `_partitionPools[threshold].WaitAsync()` блокирует поток, пока слот не освободится
3. При отмене (CancellationToken) выбрасывает `OperationCanceledException`

**Алгоритм освобождения слота:**
1. В блоке `finally` `ProcessWithPoolAsync` (гарантированно)
2. Даже если процесс упал с исключением

### 4. Трекинг активных процессов

**Назначение:** Возможность принудительного завершения при graceful shutdown, таймауте или отмене команды пользователем.

**Реализация:**
```csharp
private readonly ConcurrentDictionary<int, Process> _activeProcesses;

// Перед запуском
_activeProcesses[cmd.CommandId] = process;

// После завершения
_activeProcesses.TryRemove(cmd.CommandId, out _);

// Graceful shutdown
private async Task WaitForActiveProcessesAsync()
{
    var timeout = TimeSpan.FromSeconds(30);
    var start = DateTime.UtcNow;
    while (_activeProcesses.Count > 0 && ... < timeout)
    {
        await Task.Delay(500);
    }
    foreach (var process in _activeProcesses.Values)
    {
        try { process.Kill(true); } catch { }
    }
}
```

`Process` хранится напрямую, без класса-обёртки. `Stopwatch` и `CommandId` — локальные переменные в `ExecuteOneAsync`.

### 4a. Per-command CancellationTokenSource (отмена команд)

**Назначение:** Защита от race condition при отмене команды пользователем. Без этого механизма Worker может перезаписать статус `Cancelled` на `Failed` после принудительного завершения процесса.

**Реализация:**
```csharp
private readonly ConcurrentDictionary<int, CancellationTokenSource> _commandCts = new();

// В ExecuteOneAsync — создание linked CTS
var cmdCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
_commandCts[cmd.CommandId] = cmdCts;
var cmdCt = cmdCts.Token;

// В HandleCancelNotificationAsync — отмена CTS перед Kill
if (_commandCts.TryRemove(commandId, out var cts))
{
    cts.Cancel();  // Флаг IsCancellationRequested = true
    cts.Dispose();
}

// В ExecuteOneAsync — проверка после WaitForExit
if (cmdCt.IsCancellationRequested)
{
    // Статус уже 'Cancelled' в БД — ничего не делаем
    return;
}
```

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

**Решение:** `ConcurrentDictionary<int, Process>` для трекинга:

```csharp
private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();

// При запуске процесса
_activeProcesses[cmd.CommandId] = process;

// При завершении (в finally)
_activeProcesses.TryRemove(cmd.CommandId, out _);

// Graceful shutdown — ждёт до 30 сек, затем убивает оставшиеся
private async Task WaitForActiveProcessesAsync()
{
    var timeout = TimeSpan.FromSeconds(30);
    var start = DateTime.UtcNow;
    while (_activeProcesses.Count > 0 && (DateTime.UtcNow - start) < timeout)
    {
        await Task.Delay(500);
    }
    foreach (var process in _activeProcesses.Values)
    {
        try { process.Kill(true); } catch { }
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

### 6. Валидация FilePath

**Проблема:** Некорректный путь, path traversal или неверное расширение файла.

**Решение:** Каждая команда проверяется перед запуском:
- Путь не пустой
- Канонический путь не отличается от исходного (защита от `../` traversal)
- Файл существует
- Расширение файла входит в `AllowedExtensions` (если указаны)

### 7. Переподключение при потере связи

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
    }
}
```

**При переподключении:**
1. Создаётся новое подключение
2. Выполняется `LISTEN new_command`
3. Очищаются истёкшие Lease
4. Цикл продолжается

### 8. Fallback poll (safety net)

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

- Определить приоритет команды на основе контекста (тип задачи, роль пользователя)
- Партиция вычисляется автоматически воркером из поля `Priority` (не задаётся на сервере)

### 2. Сохранение команды в базу данных

- Создать сессию (если требуется)
- Вставить команду со статусом `pending`, указав приоритет
- Все операции в одной транзакции

### 3. Уведомление Worker

- Отправить SQL-уведомление: `NOTIFY new_command`
- Worker мгновенно просыпается и начинает обработку

---

## Отмена команды пользователем

Пользователь может отменить команду через интерфейс `/status`. Отмена проходит в два этапа:
сначала **диалог подтверждения** на стороне Server, затем — принудительное завершение процесса
на стороне Worker.

### Схема взаимодействия

```
┌───────────────────┐         ┌──────────────┐         ┌──────────────────────┐
│     Server        │         │  PostgreSQL   │         │       Worker        │
│  (Telegram bot)   │         │   (очередь)   │         │   (выполнение)      │
└────────┬──────────┘         └──────┬───────┘         └──────────┬───────────┘
         │                          │                             │
         │ 1. Кнопка «⛔ Отменить»   │                             │
         │    → CANCELCMD:{cmdId}    │                             │
         │                          │                             │
         │ 2. Диалог подтверждения   │                             │
         │    «Да, отменить / Нет»   │                             │
         │                          │                             │
         │ 3. «Да, отменить»         │                             │
         │    → CONFIRM_CANCEL:{cmdId}│                            │
         │                          │                             │
         │ 4. UPDATE Status='Cancelled'│                           │
         ├──────────────────────────>│                             │
         │                          │                             │
         │ 5. NOTIFY command_cancel  │                             │
         ├──────────────────────────>│                             │
         │                          │ 6. Уведомление Worker       │
         │                          ├────────────────────────────>│
         │                          │                             │
         │                          │                             │ 7. Отмена CTS
         │                          │                             │    + process.Kill()
         │                          │                             │
         │ 8. Уведомление           │                             │
         │    пользователю          │                             │
         │    «Команда отменена»     │                             │
         │                          │                             │
```

### Этап 1: Диалог подтверждения (Server)

При нажатии кнопки «⛔ Отменить» в списке команд сессии Server не отменяет команду сразу,
а показывает диалог подтверждения:

**Callback-запрос:** `CANCELCMD:{commandId}`

**Обработчик:** `SessionManagementHandler.HandleCancelCommandAsync`

1. Проверка, принадлежит ли команда пользователю (`GetCommandByIdAsync`)
2. Если команда не найдена — отказ с логированием
3. Отображение InlineKeyboard с двумя кнопками:
   - `✅ Да, отменить` → `CONFIRM_CANCEL:{commandId}`
   - `❌ Нет` → `SESSIONDETAILS:{sessionId}` (возврат к списку команд)

Перед показом диалога флаг `IsInStatusView` устанавливается в `false`, чтобы кнопка «Нет»
корректно направляла в ветку показа команд сессии (через `HandleSessionDetailsAsync`).

### Этап 2: Исполнение отмены (Server)

**Callback-запрос:** `CONFIRM_CANCEL:{commandId}`

**Обработчик:** `SessionManagementHandler.HandleConfirmCancelAsync`

1. Повторная проверка принадлежности команды пользователю (`GetCommandByIdAsync`)
2. Обновление статуса в БД:
   - Запрос: `Commands.Status = 'Cancelled'`
   - Условия: только если `Status IN ('pending', 'processing')`
   - Дополнительно: `SessionId` должен принадлежать пользователю
3. Отправка NOTIFY:
   ```sql
   NOTIFY command_cancel, 'CommandId';
   ```
4. Уведомление пользователя через Telegram: `⛔ Команда #{commandId} ({commandText}) отменена.`
5. Обновление отображения сессии:
   - Если остались активные команды — обновляется InlineKeyboard списка команд
   - Если активных команд не осталось — показывается статус сессии с прогресс-баром

### Этап 3: Обработка отмены на стороне Worker

**Канал:** `LISTEN command_cancel`

**Обработчик:** `CommandExecutionService.OnNotificationReceived` → `HandleCancelNotificationAsync`

1. Парсинг payload: `commandId` (INT)
2. Поиск per-command `CancellationTokenSource` в `_commandCts`
3. Отмена CTS: `cts.Cancel()` — устанавливает флаг `IsCancellationRequested = true`
4. Поиск активного процесса в `_activeProcesses`
5. Принудительное завершение: `process.Kill(true)`
6. Очистка: `_commandCts.TryRemove()`, `_activeProcesses.TryRemove()`, `process.Dispose()`

### Защита от race condition

**Проблема:** Worker может перезаписать статус `Cancelled` на `Failed` после того, как Server
уже установил `Cancelled`, но процесс ещё не завершён.

**Решение:**

```csharp
// В ExecuteOneAsync
var cmdCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
_commandCts[cmd.CommandId] = cmdCts;
var cmdCt = cmdCts.Token;

// ... WaitForExit ...

// Проверка после завершения процесса
if (cmdCt.IsCancellationRequested)
{
    // Статус уже 'Cancelled' в БД — ничего не делаем
    return;
}
```

### Обработка ошибок

| Ситуация | Действие |
|----------|----------|
| Команда уже завершена (Done/Failed) | `CancelCommandAsync` возвращает false, пользователь видит сообщение «Не удалось отменить» |
| Команда принадлежит другому пользователю | `GetCommandByIdAsync` возвращает null, запрос игнорируется с предупреждением в лог |
| NOTIFY не дошёл до Worker | Fallback poll каждые 5 мин + очистка истёкших Lease |
| Worker не успел отменить CTS до завершения процесса | Проверка `cmdCt.IsCancellationRequested` после `WaitForExit` защищает от перезаписи статуса |

---

## Уведомления пользователей (Telegram)

После завершения команды (`Done`) или окончательной ошибки (`Failed` после исчерпания retry) Worker отправляет уведомление пользователю через отдельный канал LISTEN/NOTIFY.

### Схема

```
┌─────────────────┐         ┌─────────────┐         ┌─────────────────┐
│     Worker      │         │ PostgreSQL  │         │     Server      │
│  (выполнение)   │         │             │         │  (telegram bot) │
└────────┬────────┘         └──────┬──────┘         └────────┬────────┘
         │                        │                          │
         │ 1. Done/Failed         │                          │
         │    NOTIFY              │                          │
         │    command_completed   │                          │
         ├───────────────────────>│                          │
         │                        │                          │
         │                        │ 2. Пробуждение           │
         │                        │    CommandNotificationSvc│
         │                        ├─────────────────────────>│
         │                        │                          │
         │                        │                          │ 3. SendMessageAsync
         │                        │                          │    userId, текст
         │                        │                          ├────────> Telegram
         │                        │                          │
```

### Payload уведомления

```
NOTIFY command_completed, 'UserId|CommandId|CommandText|Status|ErrorMessage'
```

Формат: pipe-разделённые поля (5 частей, `ErrorMessage` может содержать `|`).

| Поле | Тип | Описание |
|------|-----|----------|
| `UserId` | BIGINT | Telegram ID пользователя |
| `CommandId` | INT | ID команды |
| `CommandText` | TEXT | Тип команды (PDF, DWG, AUTORES...) |
| `Status` | TEXT | `Done` или `Failed` |
| `ErrorMessage` | TEXT | Пусто при `Done`, текст ошибки при `Failed` |

### Отправка

**Сторона Worker** (`CommandExecutionService`):
- После `UpdateCommandStatusAsync(Done)` → `NotifyCommandCompletedAsync(userId, ..., "Done")`
- После `UpdateCommandStatusAsync(Failed)` при exhaustion retry → `NotifyCommandCompletedAsync(userId, ..., "Failed", errorMessage)`
- Промежуточные retry не отправляют уведомления (пользователь видит только финальный результат)

**Сторона Server** (`CommandNotificationService`):
- `BackgroundService`, подписан на `LISTEN command_completed`
- При получении NOTIFY парсит payload через `Split('|', 5)`
- Отправляет сообщение через `ITelegramOutputService.SendMessageAsync()`
- MarkdownV2 экранирование: `✅ *Команда* завершена` / `❌ *Команда* — ошибка: текст`

### NOTIFY после retry

При `ScheduleRetryAsync` Worker также отправляет `NOTIFY new_command`, чтобы воркер (или другой воркер) проверил очередь. Команда будет пропущена фильтром `NextRetryAt <= NOW()` до наступления времени retry.

---

## Конфигурация системы

### Параметры конфигурации (CommandExecutionService)

| Параметр | Откуда | Значение по умолч. | Описание |
|----------|--------|-------------------|----------|
| `Partitions` | `WorkerOptions.Partitions` | `{80→5, 40→3, 0→1}` | Priority threshold → макс. процессов. Команда с Priority >= threshold попадает в эту партицию |
| `ProcessTimeoutSeconds` | `WorkerOptions.ProcessTimeoutSeconds` | 10800 (3 часа) | Максимальное время выполнения команды |
| `CleanupIntervalSec` | константа | 60 | Интервал очистки истёкших Lease |
| `LeaseTimeoutMin` | вычисляется | `ProcessTimeoutSeconds + 5 мин` | TTL Lease (защита от сбоев воркера) |
| `FallbackTimeoutSec` | константа | 300 (5 мин) | Таймаут ожидания NOTIFY (safety net) |
| `ReconnectDelayMs` | константа | 5000 | Задержка перед переподключением к БД |

### Настройка через appsettings.json

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres"
  },
  "Worker": {
    "ProcessTimeoutSeconds": 10800,
    "Partitions": {
      "80": 5,
      "40": 3,
      "0": 1
    },
    "Commands": {
      "PDF": {
        "ExecutablePath": "Revit.exe",
        "ArgumentsTemplate": "/command \"{CommandText}\" \"{FilePath}\"",
        "AllowedExtensions": [".rvt", ".rfa"]
      },
      "DWG": {
        "ExecutablePath": "Revit.exe",
        "ArgumentsTemplate": "/command \"{CommandText}\" \"{FilePath}\"",
        "AllowedExtensions": [".rvt", ".rfa"]
      },
      "NWC": {
        "ExecutablePath": "FileConvert.exe",
        "ArgumentsTemplate": "/command \"{CommandText}\" \"{FilePath}\"",
        "AllowedExtensions": [".nwc", ".nwd", ".nwf"]
      },
      "AUTORES": {
        "ExecutablePath": "python",
        "ArgumentsTemplate": "ai_agent.py --command \"{CommandText}\" --file \"{FilePath}\"",
        "AllowedExtensions": [".rvt", ".ifc", ".nwc"],
        "WorkingDirectory": "."
      }
    }
  }
}
```

**Примечание:** `ProcessTimeoutSeconds` задаётся в секции `Worker`. Если не указан — по умолчанию 10800 сек (3 часа). Каждая команда настраивается отдельно в словаре `Commands` (без привязки к партиции). Партиции настраиваются в секции `Partitions`: ключ — минимальный Priority (threshold), значение — макс. процессов. Чтобы добавить новую команду — достаточно записи в JSON, код менять не нужно.

---

## Как устроена база данных

В этом разделе мы разберём, как устроена база данных простыми словами.

### Какие таблицы есть и зачем они нужны

В системе **4 таблицы**. Каждая отвечает за свою часть:

| Таблица | Что хранит | Пример записи |
|---------|-----------|---------------|
| `BotUsers` | Кто пользовался ботом, какой у него статус (одобрен/заблокирован) | `UserId: 12345, Status: Approved` |
| `Sessions` | Сессии — «папки» для групп команд (одна сессия = один раз выбрали проект и нажали «Подтвердить») | `SessionId: 42, UserId: 12345, CreatedAt: 2025-01-15` |
| `Commands` | Отдельные задачи внутри сессии (каждая строчка = одна команда для одного файла) | `CommandId: 100, SessionId: 42, Status: Done` |
| `TrackedMessages` | Какие сообщения от бота нужно удалить при очистке чата | `UserId: 12345, MessageId: 555` |

### Как таблицы связаны между собой

```
┌──────────────┐       ┌──────────────┐       ┌──────────────┐
│   BotUsers   │ 1──N  │   Sessions   │ 1──N  │   Commands   │
│  (пользоват.)│──────>│   (сессии)   │──────>│  (команды)   │
└──────┬───────┘       └──────────────┘       └──────┬───────┘
       │              ┌─────────────────┐            │
       └──────────────│  TrackedMessages│            │
                      │ (сообщения для  │            │
                      │    очистки)     │            │
                      └─────────────────┘            │
                                                     │
                                                     ▼
                                            ┌───────────────────────┐
                                            │  Status lifecycle      │
                                            │  (поле Status в таблице│
                                            │  Commands)             │
                                            │                       │
                                            │  pending ──▶ processing│
                                            │               ├──▶ Done│
                                            │               └──▶ Failed│
                                            │  pending ──▶ Cancelled │
                                            │  (любой) ──▶ Deleted   │
                                            └────────────────────────┘
```

- **Один пользователь** → может иметь **много сессий**
- **Одна сессия** → может содержать **много команд**
- `TrackedMessages` — отдельная табличка, привязана только к `UserId`

### Что такое «сессия»?

Представьте, что вы зашли в бот, выбрали проект и нажали «Подтвердить». Бот создаёт **сессию** — это как заказ в интернет-магазине. Внутри этого заказа (сессии) лежат **команды** — отдельные задачи: например, «Экспортировать файл A.rvt в PDF» и «Экспортировать файл B.rvt в DWG».

### Как данные путешествуют по системе (пошагово)

```
Шаг 1: Пользователь выбирает проект и команды в Telegram
        │
Шаг 2: Server создаёт Сессию (Sessions) и
        │     внутри неё — несколько Команд (Commands)
        │     Статус каждой команды: 'pending' (ждёт очереди)
        ▼
Шаг 3: Server шлёт сигнал «NOTIFY new_command»
        │     (как крикнуть «Эй, есть работа!»)
        ▼
Шаг 4: Worker слышит сигнал, просыпается и
        │     забирает pending-команды себе
        │     Статус: 'processing' (выполняется)
        ▼
Шаг 5: Worker запускает Revit (или другую программу)
        │     и ждёт результат
        ▼
Шаг 6: Готово! Worker обновляет статус:
          'Done' (успешно) или 'Failed' (ошибка)
          И шлёт уведомление пользователю
```

### Важные понятия простыми словами

#### Soft-delete («мягкое удаление»)

Мы **никогда** не удаляем строки из БД физически.
Вместо `DELETE FROM Commands` мы пишем:
```sql
UPDATE Commands SET Status = 'Deleted' WHERE ...
```

**Зачем?**
- Если что-то пошло не так, данные можно восстановить
- Можно посмотреть историю: кто, когда и что делал
- Данные остаются для статистики и отладки

#### LISTEN/NOTIFY — как рация

Представьте, что Server и Worker — это два человека с рациями:
- **NOTIFY** = Server нажимает кнопку на рации и кричит: «Новая работа!»
- **LISTEN** = Worker держит рацию включённой и ждёт сигнала

Это гораздо быстрее, чем если бы Worker каждые 5 секунд проверял: «Ну что, есть работа? Есть работа?»

**Важно:** Если сигнал потерялся (рация забавкала) — Worker всё равно раз в 5 минут проверяет очередь сам (это называется **fallback poll**).

#### FOR UPDATE SKIP LOCKED — очередь в магазине

Если у вас **два Worker** (два кассира), они не должны взять один и тот же товар (команду).

`FOR UPDATE SKIP LOCKED` работает как очередь:
- Первый Worker забирает команды, которые свободны
- Второй Worker видит только то, что ещё не забрал первый
- Они не мешают друг другу

#### Lease — страховка от падения Worker

Когда Worker забирает команду, он говорит:
> «Я забрал эту команду. Если через 5 минут я не отвечу — значит, я упал, забирайте её обратно в очередь»

Это защита от ситуации, когда Worker выключился, а команда навсегда зависла в статусе «выполняется».

### Таблица Commands (подробно)

Это главная таблица. Вот что хранит каждая колонка:

| Поле | Смысл простыми словами |
|------|----------------------|
| `CommandId` | Уникальный номер команды (1, 2, 3...) |
| `SessionId` | Номер сессии, к которой относится команда |
| `CommandText` | Тип задачи: `PDF`, `DWG`, `NWC`, `IFC`, `BIMDOC` и т.д. |
| `FilePath` | Какой файл нужно обработать (например, `B:\Project\01_RVT\building.rvt`) |
| `ExecutionOrder` | В каком порядке выполнять в сессии (1, 2, 3...) |
| `Status` | Где сейчас команда: `pending` → `processing` → `Done` / `Failed` / `Cancelled` / `Deleted` |
| `CreatedAt` | Когда создали команду |
| `StartedAt` | Когда Worker начал выполнять (`NULL` — пока не начали) |
| `CompletedAt` | Когда закончили (`NULL` — пока не закончили) |
| `Lease` | Срок аренды (см. Lease выше). Unix-время в секундах |
| `Priority` | Насколько задача важная (0–100, чем выше — тем важнее). По умолчанию 50 |
| `ProcessId` | ID процесса Windows (чтобы можно было «убить» программу, если что-то пошло не так) |
| `ErrorMessage` | Если команда упала с ошибкой — тут текст ошибки |

### Индексы — ускорители поиска

Чтобы БД не перебирала все строки подряд, мы добавляем **индексы**. Это как оглавление в книге:

| Индекс | Зачем нужен |
|--------|-------------|
| `(Status, Priority, CreatedAt)` | Быстро находить, какие команды ждут в очереди, и сортировать по важности |
| `(Status, Lease) WHERE Status = 'processing'` | Быстро находить «зависшие» команды (у которых истёк Lease) |
| `(SessionId)` | Быстро искать все команды одной сессии |
| `(Status) WHERE Status = 'Cancelled'` | Быстро считать статистику по отменённым командам |
| `(UserId, CreatedAt DESC)` | Быстро показывать пользователю список его сессий |

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

### Отправка уведомления Worker

```sql
NOTIFY new_command, @SessionId;
```

### Отправка уведомления пользователю

```sql
NOTIFY command_completed, 'UserId|CommandId|CommandText|Status|ErrorMessage';
```

Payload генерируется в `PostgresDataService.NotifyCommandCompletedAsync()`: `$"{userId}|{commandId}|{commandText}|{status}|{errorMessage ?? ""}"`.

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

### Отмена команды пользователем (CancelCommand)

```sql
-- Server: обновляет статус на Cancelled (только если pending или processing)
UPDATE Commands
SET Status = 'Cancelled',
    CompletedAt = NOW(),
    ErrorMessage = 'Cancelled by user'
WHERE CommandId = @CommandId
  AND SessionId IN (SELECT SessionId FROM Sessions WHERE UserId = @UserId)
  AND Status IN ('pending', 'processing')
RETURNING CommandId;
```

### Уведомление Worker об отмене

```sql
NOTIFY command_cancel, 'CommandId';
```

Payload: ID команды в текстовом виде.

---

## Безопасность и надёжность

| Принцип | Реализация |
|---------|------------|
| **Логическое удаление** | Команды никогда не удаляются физически, только `Status = 'Deleted'` |
| **Транзакционность** | Захват команд — атомарная операция с `FOR UPDATE SKIP LOCKED` |
| **Ограничение нагрузки** | Per-partition пулы процессов (SortedDictionary<int, SemaphoreSlim>) — каждая партиция имеет свой лимит |
| **Приоритизация** | Высокоприоритетные команды выполняются первыми (`ORDER BY Priority DESC`) |
| **Lease-механизм** | Защита от сбоев воркера — команды возвращаются в очередь при истечении TTL |
| **Таймауты** | Принудительное завершение процессов при превышении лимита времени (`process.Kill(true)`) |
| **Трекинг PID** | Сохранение ProcessId для мониторинга и принудительного завершения |
| **Отказоустойчивость** | Переподключение при потере соединения с БД (5 сек задержка) |
| **Логирование** | Полное контекстное логирование всех операций и ошибок, включая stdout/stderr процессов |
| **Изоляция компонентов** | Server и Worker независимы, общаются только через БД |
| **Graceful shutdown** | Корректное завершение активных процессов при остановке сервиса (30 сек таймаут) |
| **FOR UPDATE SKIP LOCKED** | Несколько воркеров могут работать параллельно без конфликтов |
| **Lease (долгий TTL)** | Lease устанавливается на `ProcessTimeoutSeconds + 5 мин`, команда не вернётся в очередь раньше таймаута |
| **Валидация FilePath** | Проверка существования, расширения (из `AllowedExtensions`) и защита от path traversal перед запуском процесса |
| **Асинхронное чтение stdout/stderr** | Предотвращает deadlock при заполнении буфера вывода (64KB) |
| **Уведомления пользователей** | Worker шлёт NOTIFY `command_completed`, Server (`CommandNotificationService`) слушает и отправляет Telegram-сообщение через `ITelegramOutputService` |
| **Отмена команд** | Пользователь отменяет команду через UI `/status` → кнопка «⛔ Отменить». Server обновляет статус на `Cancelled` и шлёт NOTIFY `command_cancel`. Worker получает, отменяет per-command CTS и убивает процесс. После `WaitForExit` проверяется `cmdCt.IsCancellationRequested` — статус не перезаписывается |
| **Fallback poll** | Если NOTIFY потерян — проверка каждые 5 минут (safety net) |

---

## Выполнение внешнего процесса

### Общий алгоритм (`ExecuteOneAsync`)

1. **Валидация FilePath** — существование, расширение, path traversal
2. **Поиск конфигурации** — `WorkerOptions.Commands.TryGetValue(CommandText)` → `CommandConfig`
3. **Создание `ProcessStartInfo`** — `CreateProcessStartInfo(cmd, commandCfg)`:
   - `FileName` из `ExecutablePath`
   - `Arguments` из `ArgumentsTemplate` (с подстановкой `{CommandText}`, `{FilePath}`)
   - `WorkingDirectory` из `WorkingDirectory` / папка файла / `Environment.CurrentDirectory`
   - `RedirectStandardOutput/Error = true`, `UseShellExecute = false`, `CreateNoWindow = true`
4. **Запуск** — `process.Start()`
5. **Трекинг** — `_activeProcesses[CommandId] = process`
6. **Статус** — `UpdateCommandStatus(Processing, ProcessId=PID)`
7. **stdout/stderr** — асинхронное чтение через `BeginOutputReadLine / BeginErrorReadLine`
8. **Ожидание** — `WaitForExit(ProcessTimeoutSeconds)`
9. **Логирование** — stdout/stderr (обрезка >4KB)
10. **Per-command CTS**: создаётся `CancellationTokenSource.CreateLinkedTokenSource(ct)`, сохраняется в `_commandCts`
11. **Результат**:
    - Если `cmdCt.IsCancellationRequested` → return (статус `Cancelled` уже установлен Server-ом)
    - Таймаут → `Kill(true)`, статус `Failed`
    - `ExitCode == 0` → статус `Done`
    - Иначе → статус `Failed`
12. **Очистка** (в `finally`) — `partitionPool.Release()`, `_activeProcesses.TryRemove()`, `_commandCts.TryRemove().Dispose()`

### Универсальное создание процесса

Вся конфигурация берётся из `WorkerOptions.Commands[CommandText]` — словаря, где ключ — код команды из БД, значение — `CommandConfig`:

| Поле | Описание |
|------|----------|
| `ExecutablePath` | Исполняемый файл (например, `Revit.exe`, `python`) |
| `ArgumentsTemplate` | Шаблон аргументов. `{CommandText}` и `{FilePath}` подставляются из команды |
| `AllowedExtensions` | Разрешённые расширения файлов. `null` — любое |
| `WorkingDirectory` | Рабочая директория. `null` — папка файла. `"."` — корень процесса |

```csharp
private static ProcessStartInfo CreateProcessStartInfo(PendingCommand cmd, CommandConfig cfg)
{
    var args = cfg.ArgumentsTemplate
        .Replace("{CommandText}", cmd.CommandText)
        .Replace("{FilePath}", cmd.FilePath);

    var workingDir = cfg.WorkingDirectory switch
    {
        null or "" => Path.GetDirectoryName(cmd.FilePath),
        "." => Environment.CurrentDirectory,
        var dir => dir
    } ?? Environment.CurrentDirectory;

    return new ProcessStartInfo
    {
        FileName = cfg.ExecutablePath,
        Arguments = args,
        WorkingDirectory = workingDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };
}
```

---

## Расширение системы (добавление новой команды)

### Шаг 1: Добавить конфигурацию в appsettings.json

```json
"Commands": {
  "XLSEXPORT": {
    "ExecutablePath": "excel_exporter.exe",
    "ArgumentsTemplate": "--input \"{FilePath}\"",
    "AllowedExtensions": [".xlsx", ".xls"]
  }
}
```

**Партиция** не указывается в команде — определяется автоматически по полю `Priority` из БД. Если нужно изменить пул для уровня приоритета — правим секцию `Partitions`.

**Ни строчки C# менять не нужно.** Команда `XLSEXPORT` из БД автоматически подхватится через `TryGetValue`, партиция определится по её `Priority`.

### Шаг 2: Настроить приоритет (при создании команды)

Приоритет задаётся в момент создания команды в БД. SQL:
```sql
INSERT INTO "Commands" (..., "Priority") VALUES (..., 75); -- Выше среднего
```

Значение `Priority` определяет, в какую партицию попадёт команда:
- `Priority >= 80` → High (до 5 одновременных)
- `Priority >= 40` → Medium (до 3)
- `Priority < 40` → Low (до 1)

### Шаг 3 (опционально): Настроить лимиты партиций

Если стандартные лимиты не подходят:
```json
"Partitions": {
  "80": 8,  // Больше высокоприоритетных слотов
  "40": 4,
  "0": 2
}
```

### Шаг 4 (опционально): Обновить пользовательский интерфейс

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

**Отменённые команды (статистика):**
```sql
SELECT "CommandId", "CommandText", "SessionId", "CreatedAt", "CompletedAt"
FROM "Commands"
WHERE "Status" = 'Cancelled'
ORDER BY "CompletedAt" DESC
LIMIT 20;
```

**Проверка подписки на уведомления:**
```sql
SELECT * FROM pg_listening_channels();
-- Должен вернуть 'new_command', 'command_cancel' (Worker) и 'command_completed' (Server)
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
| 1 | **Лимит процессов** | Для каждого уровня приоритета не выполняется более его лимита одновременно (по умолчанию: High>=80 → 5, Medium>=40 → 3, Low<40 → 1) |
| 2 | **Приоритизация** | Высокоприоритетные команды стартуют раньше низкоприоритетных |
| 3 | **Lease-механизм** | При сбое воркера команда возвращается в очередь после истечения Lease |
| 4 | **Таймауты** | Процессы, выполняющиеся дольше `ProcessTimeoutSeconds` (по умолчанию 3 часа), принудительно завершаются |
| 5 | **Трекинг PID** | ProcessId сохраняется для мониторинга и принудительного завершения |
| 6 | **FOR UPDATE SKIP LOCKED** | Несколько воркеров могут работать параллельно без конфликтов |
| 7 | **Graceful shutdown** | При остановке воркер завершает активные процессы (30 сек таймаут) |
| 8 | **Fallback poll** | Если NOTIFY потерян — проверка каждые 5 минут |
| 9 | **Восстановление** | При перезапуске Worker очищает истёкшие Lease и продолжает обработку |
| 10 | **Наблюдаемость** | Диагностические запросы показывают актуальное состояние (PID, Lease, длительность) |
| 11 | **Отмена команд** | Пользователь может отменить команду через `/status`. Server меняет статус на `Cancelled` и шлёт NOTIFY `command_cancel`. Worker получает NOTIFY, отменяет CTS, убивает процесс. Проверка `cmdCt.IsCancellationRequested` предотвращает перезапись статуса |

---

## Известные ограничения и технический долг

В этом разделе зафиксированы выявленные недочёты архитектуры и реализации, которые требуют проработки в будущих версиях.

| ID | Описание | Влияние | Приоритет | Статус |
|----|----------|---------|-----------|--------|
| DOC-001 | **Lease (5 мин) < ProcessTimeout (1 час)** — не описан механизм продления Lease во время длительного выполнения | Команда может быть ошибочно возвращена в очередь другим воркером во время выполнения | 🔴 HIGH | ✅ Исправлено (v1.1) |
| DOC-002 | **Не описано чтение stdout/stderr** процессов — указано `RedirectStandardOutput/Error = true`, но нет асинхронного чтения | Риск deadlock при заполнении буфера вывода (64KB) | 🔴 HIGH | ✅ Исправлено (v1.1) |
| DOC-003 | **Нет валидации FilePath** — отсутствует защита от path traversal атак и проверка существования файлов | Потенциальная уязвимость безопасности | 🔴 HIGH | ✅ Исправлено (v1.1) |
| DOC-004 | **Партиции (priority-based)** — `SortedDictionary<int, SemaphoreSlim>` с threshold приоритета как ключ. Команды сортируются по `Priority`: High (>=80) → 5 слотов, Medium (>=40) → 3, Low (<40) → 1 | Высокоприоритетные команды не ждут за низкоприоритетными | 🟠 MEDIUM | ✅ Реализовано (v1.1) |
| DOC-005 | **Retry logic** — экспоненциальная задержка (base*2^attempt), лимит попыток (MaxRetries=5). Команда возвращается в `pending` с `NextRetryAt` | Самовосстановление при временных ошибках (файл заблокирован, сеть недоступна) | 🟠 MEDIUM | ✅ Реализовано (v1.2) |
| DOC-006 | **Нет автоматических метрик** (Prometheus/Grafana) — только ручные SQL-запросы | Ограниченный мониторинг в production, сложность-alerting | 🟠 MEDIUM | В планах (v1.2) |
| DOC-007 | **Координация очистки Lease** — `pg_try_advisory_lock(1234567)` перед каждой очисткой. Только один воркер выполняет `ReleaseExpiredLeasesAsync`/`ReleaseTimeoutCommandsAsync`, остальные пропускают цикл | Снижение нагрузки на БД при нескольких воркерах | 🟡 LOW | ✅ Реализовано (v1.2) |
| DOC-008 | **Graceful shutdown deadlock** — если процесс не реагирует на `Kill(true)`, цикл ожидания может заблокироваться | Воркер не завершится корректно при остановке | 🟡 LOW | Улучшение |
| DOC-009 | **Нет health checks** для Worker — нет эндпоинтов или механизмов проверки здоровья сервиса | Сложность мониторинга доступности в orchestration-системах | 🟡 LOW | Улучшение |
| DOC-010 | **Нет ограничения очереди** — не описан лимит на количество pending-команд на пользователя/сессию | Риск разрастания таблицы при аномальной нагрузке | 🟡 LOW | Улучшение |
| DOC-011 | **Не описаны runbook** для типичных инцидентов (завис процесс, заполнилась очередь, упал Worker) | Увеличенное время восстановления при инцидентах | 🟡 LOW | Улучшение |

### Приоритеты исправлений

| Приоритет | Описание | Действия |
|-----------|----------|----------|
| 🔴 **HIGH** | Критические проблемы, влияющие на корректность работы или безопасность | Исправить до production-развёртывания |
| 🟠 **MEDIUM** | Архитектурный долг, ограничивающий масштабируемость или наблюдаемость | Запланировать на ближайшие спринты (v1.1–v1.2) |
| 🟡 **LOW** | Улучшения операционных характеристик и документации | Выполнить по мере доступности ресурсов |

### План работ → см. [ROADMAP.md](../ROADMAP.md)

Полная дорожная карта проекта, включая:
- v1.0 — базовая функциональность ✅
- v1.1 — надёжность и масштабирование ✅
- v1.2 — метрики, лимиты, операционные улучшения 🟡
- v2.0+ — долгосрочные планы ⚪
- Текущий спринт 🔄

Актуальный статус всех пунктов поддерживается в [ROADMAP.md](../ROADMAP.md).
