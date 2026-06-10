# Алгоритм выполнения команд (Command Execution Algorithm)

> **Связанные документы:** [ROADMAP.md](../ROADMAP.md) — дорожная карта проекта | [README.md](../README.md) — обзор проекта | [CadIntegrationAlgorithm.md](CadIntegrationAlgorithm.md) — интеграция с CAD-плагинами

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
| **Competing Consumers** | Параллельная обработка (FOR UPDATE SKIP LOCKED) | Несколько Worker-ов конкурируют за команды, каждая выполняется ровно одним |
| **LISTEN/NOTIFY** | Очередь задач (PostgreSQL) | Worker подписан на `new_tasks` и мгновенно реагирует. Fallback polling — раз в 5 мин. Server слушает `command_completed` |
| **Bulkhead (изоляция)** | Priority-based партиции (`SemaphoreSlim`) | Каждый уровень приоритета имеет изолированный пул слотов |
| **Recovery loop** | Polling + обработка ошибок batch-а | При временной ошибке Worker логирует сбой и продолжает следующий цикл |
| **Retry with Exponential Backoff** | Повторные попытки (`MaxRetries=5`) | Задержка растёт экспоненциально: 60s → 120s → 240s → 480s → 960s |
| **Lease (аренда)** | Защита от сбоев воркеров (`Lease` + `StartedAt`) | Команда «арендуется» на время выполнения; при сбое воркера возвращается в очередь |
| **Soft Delete** | Логическое удаление (`Status = 'Deleted'`) | Строки никогда не удаляются физически |
| **Singleton** | DI-регистрация всех сервисов | Гарантирует единый экземпляр сервиса на всё приложение |

---

## Обзор

Система выполняет внешние команды (например, для CAD/CAE-приложений или AI-обработки) через асинхронную очередь на базе PostgreSQL с поллингом.

**Ключевые концепции:**
- **Пул процессов** — ограничение на количество одновременно выполняемых процессов защищает систему от перегрузки
- **Lease-механизм** — аренда команды воркером с TTL для защиты от сбоев
- **Таймауты** — принудительное завершение процессов при превышении лимита времени
- **Приоритеты** — команды с более высоким приоритетом выполняются первыми
- **Партиции** — приоритетные уровни: команды с высоким приоритетом имеют выделенные слоты выполнения
- **Отмена команд** — любой одобренный пользователь может отменить команду через `/status` → кнопка «⛔ Отменить»; Server мягко удаляет команду (`Status = 'Deleted'`). Все одобренные пользователи могут удалять чужие сессии и команды.

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
         │                        ││         │ 2. Worker просыпается    │                          │
         │                        │    по таймеру            │
         │                        │    (каждую минуту)       │
         │                        ├─────────────────────────>│
         │                        │                          │
         │                        │ 3. Захват команд         │
         │                        │    SELECT ... FOR UPDATE │
         │                        │    SKIP LOCKED           │
         │<───────────────────────┤                          │
         │                        │                          │
         │                        │ 4. Выполнить команду     │
         │                        │    (пул процессов)       │
         │                        │                          │
         │                        │ 5. Обновить статус       │
         │                        │    (Done / Failed)       │
         │<───────────────────────┤                          │
         │                        │                          │
```

### Концепция priority-based партиций

```
┌─────────────────────────────────────────────────────────────────┐
│                    Служба выполнения команд                     │
│                                                                 │
│  ┌────────────────────┐ ┌──────────────┐ ┌──────────────┐      │
│  │ Priority 1 (Crit) │ │Priority 2    │ │Priority 3    │      │
│  │ SemaphoreSlim(3)   │ │SemaphoreSlim(5)│ SemaphoreSlim(3)│   │
│  │  ┌───┐┌───┐┌───┐  │ │ ┌───┐┌───┐┌───┐┌───┐┌───┐  │      │
│  │  │ P ││ P ││ P │  │ │ │ P ││ P ││ P ││ P ││ P │  │      │
│  │  └───┘└───┘└───┘  │ │ └───┘└───┘└───┘└───┘└───┘  │      │
│  └────────────────────┘ └──────────────┘ └──────────────┘      │
│  ┌────────────┐ ┌────────────┐                                  │
│  │Priority 4  │ │Priority 5+ │                                  │
│  │Semaphore(1)│ │Semaphore(1)│                                  │
│  │  ┌───┐     │ │  ┌───┐     │                                  │
│  │  │ P │     │ │  │ P │     │                                  │
│  │  └───┘     │ │  └───┘     │                                  │
│  └────────────┘ └────────────┘                                  │
│                           │                                     │
│                           ▼                                     │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │        Очередь команд (Priority ASC, общая)             │   │
│  │  [P=1] → [P=1] → [P=2] → [P=3] → [P=4] → ...          │   │
│  │     (Priority ASC, CreatedAt ASC, CommandId ASC)        │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  Лимит параллельности (первый threshold >= Priority):          │
│  Priority 1  → Critical, SemaphoreSlim(3)                      │
│  Priority 2  → High,     SemaphoreSlim(5)                      │
│  Priority 3  → Medium,   SemaphoreSlim(3)                      │
│  Priority 4  → Low,      SemaphoreSlim(1)                      │
│  Priority 5+ → Lowest,   SemaphoreSlim(1)                      │
└─────────────────────────────────────────────────────────────────┘
```

**Преимущества priority-based партиций (1 = наивысший приоритет):**
- Высокоприоритетные команды (Priority=1) имеют выделенные слоты и не ждут за низкоприоритетными
- Гарантированная пропускная способность для критических задач
- Low-priority команды не блокируют High-priority (даже если очередь забита)

---

## BimLib (BIM Integration) — встроен в Worker

BimLib — **Windows-only** набор модулей, расположенный внутри Worker-проекта (`TelegramBot.Worker/BimLib/`).
Используется `CommandExecutionService` при выполнении Revit/Navisworks-команд.

### Назначение

При запуске внешнего BIM-приложения (Revit, Navisworks) Worker должен:
1. **Определить версию Revit** по .rvt-файлу — чтобы запустить правильный Revit.exe
2. **Найти исполняемый файл** — Revit.exe или Navisworks.exe/FileConvert.exe в системе
3. **Мониторить процесс** — проверять отклик, автоматически закрывать диалоговые окна

Эти задачи решает BimLib.

### Состав и архитектура

```
┌──────────────────────────────────────────────────────────────┐
│           TelegramBot.Worker/BimLib/                        │
├──────────────────────────────────────────────────────────────┤
│  Services/                                                    │
│  ├── RevitVersionDetector  — OLE BasicFileInfo → "Format: YYYY"│
│  ├── RevitPathResolver     — Registry → путь к Revit.exe     │
│  └── NavisworksPathResolver — Registry → Navisworks/FileConvert│
├──────────────────────────────────────────────────────────────┤
│  Monitor/                                                     │
│  ├── RevitProcessTracker     — отклик, диалоги, PID          │
│  ├── NavisworksProcessTracker — трекинг Roamer/FileConvert    │
│  ├── DialogDismisser         — автозакрытие #32770           │
│  ├── ProcessHealthHelper     — общий хелпер CheckHealth()   │
│  ├── WindowUtil              — Win32-утилиты (HWND, клики)    │
│  └── WindowInfo              — информация об окне            │
├──────────────────────────────────────────────────────────────┤
│  Native/ — P/Invoke WinAPI (User32, Win32Consts)             │
│  Interfaces/ — 2 интерфейса: IRevitVersionDetector,         │
│                INavisworksPathResolver                       │
│  Models/ — RevitDetectedVersion, RevitProcessHealth          │
│  Config/ — BimIntegrationOptions                             │
└──────────────────────────────────────────────────────────────┘
```

> **Примечание:** BimLib — не отдельный проект. Это директория внутри Worker.
> Ранее существовавшие интерфейсы `IRevitPathResolver`, `IRevitProcessTracker`,
> `INavisworksProcessTracker` удалены — у них не было потребителей вне BimLib.
> DI-регистрация выполняется напрямую в `Worker/Program.cs` (без `AddBimIntegration()`).
> AUTORES не использует BimLib для резолвинга — его ExecutablePath = "python" берётся напрямую из конфигурации.

### Поток использования в Worker

```
CommandExecutionService (Worker)
    │
    ├── RevitVersionDetector.DetectVersionAsync(.rvt)
    │       └── RootStorage.OpenRead() → OpenStream("BasicFileInfo") → извлечение "Format: YYYY"
    │
    ├── RevitPathResolver.ResolveExecutablePath(year)
    │       └── HKLM\SOFTWARE\Autodesk\Revit\{version} → Revit.exe
    │
    ├── Запуск Revit.exe с аргументами команды
    │
    └── RevitProcessTracker.CheckHealth(process)
            └── Проверка Responding + автозакрытие диалогов через DialogDismisser
```

### Ключевые интерфейсы

| Интерфейс | Методы | Назначение |
|-----------|--------|------------|
| `IRevitVersionDetector` | `DetectVersionAsync(filePath)` | Определить версию Revit по .rvt-файлу |
| `INavisworksPathResolver` | `GetInstalledVersions()`, `ResolveNavisworksPath(year)`, `ResolveFileConvertPath(year)` | Найти Navisworks.exe / FileConvert.exe |

### DI-регистрация

Сервисы BimLib регистрируются напрямую в `Worker/Program.cs`:
```csharp
services.AddSingleton<IRevitVersionDetector, RevitVersionDetector>();
services.AddSingleton<RevitPathResolver>();
services.AddSingleton<RevitProcessTracker>();
services.AddSingleton<DialogDismisser>();
services.AddSingleton<INavisworksPathResolver, NavisworksPathResolver>();
services.AddSingleton<NavisworksProcessTracker>();
```
Требуется секция `BimIntegration` в `appsettings.json`:
```json
{
  "BimIntegration": {
    "MinSupportedVersion": 2018,
    "MaxSupportedVersion": 2026,
    "RevitInstallRoot": "C:\\Program Files\\Autodesk"
  }
}
```

### Важные замечания для разработчика

- BimLib помечена `[SupportedOSPlatform("windows")]` — работает только на Windows
- OpenMcdf 3.x парсит OLE Structured Storage (.rvt). API: `RootStorage.OpenRead()` → `OpenStream()` → `stream.Read()`
- Доступ к реестру Windows через `Microsoft.Win32.Registry`
- P/Invoke — в `Native/User32.cs` (поиск окон, клики, закрытие диалогов)
- `RevitProcessStatus` содержит 3 значения: `Healthy`, `NotResponding`, `Error`

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
Статус `Deleted` является финальным — Worker не должен перезаписывать мягко удалённую команду.

---

## Алгоритм работы Worker (службы выполнения)

### 1. Инициализация

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
│  Очистка истёкших Lease (фоновая задача каждые 5 мин)          │
│  - ReleaseExpiredLeasesAsync()                                  │
│  - ReleaseTimeoutCommandsAsync()                                │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  Мониторинг здоровья процессов (фоновая задача 30 сек)          │
│  - ProcessHealthHelper.CheckHealth()                            │
│  - DialogDismisser.DismissDialogsForProcess()                   │
└────────────────────────────┬────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  Ожидание уведомлений: LISTEN new_tasks + fallback polling    │
│  - Worker подписан на канал new_tasks, мгновенно реагирует    │
│  - Fallback polling (Task.Delay) срабатывает раз в 5 мин       │
└────────────────────────────┬────────────────────────────────────┘
```
┌─────────────────────────────────────────────────────────────────┐
│  Цикл выполнения (фоновая служба)                               │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  Очистка истёкших Lease (фоновая задача каждые 5 мин)          │
│  - ReleaseExpiredLeasesAsync()                                  │
│  - ReleaseTimeoutCommandsAsync()                                │
└────────────────────────────┬────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  Ожидание уведомлений: LISTEN new_tasks + fallback polling    │
│  - Worker подписан на канал new_tasks, мгновенно реагирует    │
│  - Fallback polling (Task.Delay) срабатывает раз в 5 мин       │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  Захват pending-команд из БД (до DefaultBatchSize=5)           │
│  - SELECT ... FOR UPDATE SKIP LOCKED                            │
│  - ORDER BY Priority ASC, CreatedAt ASC, CommandId ASC         │
│  - Статус → 'processing', Lease = timestamp                     │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  Параллельная обработка с priority-based пулами                │
│  - Определение партиции по приоритету команды:                  │
│    первый partition threshold >= Priority                       │
│  - Ожидание слота в своей партиции:                             │
│    _partitionPools[threshold].WaitAsync()                       │
│  - Каждая партиция (уровень приоритета) имеет свой лимит       │
│  - Чем меньше Priority, тем выше приоритет (1=Critical, 5=Lowest)
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
│  10. Status = 'Done' или 'Failed' (retry если не исчерпаны)    │
│  11. _activeProcesses.Remove() + partitionPool.Release()        │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  Вернуться к ожиданию (Task.Delay)                              │
└─────────────────────────────────────────────────────────────────┘
```

### 3. Управление per-partition пулами процессов

**Назначение:** Ограничение количества одновременно выполняемых процессов для каждой партиции отдельно.

**Принцип работы:**
- `SortedDictionary<int, SemaphoreSlim>` — карта threshold приоритета → пул
- Инициализация из `WorkerOptions.Partitions` при старте воркера
- Ключ словаря = максимальный `Priority` (threshold), значение = `SemaphoreSlim`
- **Чем меньше Priority, тем выше приоритет** (1 = Critical, 5 = Lowest)
- Перед запуском процесса: `_partitionPools[threshold].WaitAsync(ct)`
- После завершения (в `finally`): `_partitionPools[threshold].Release()`

**Определение партиции команды (ищем первый threshold, где threshold >= Priority):**
- `GetPartitionThreshold(priority)` линейно проходит thresholds по возрастанию
- Thresholds кешируются по возрастанию: `[1, 2, 3, 4, 5]`
- По умолчанию: Priority 1 → pool(3), Priority 2 → pool(5), Priority 3 → pool(3), Priority 4 → pool(1), Priority 5+ → pool(1)
- Если threshold не найден — fallback на последний threshold

**Алгоритм захвата слота:**
1. `threshold = GetPartitionThreshold(cmd.Priority)` выбирает первый threshold в [1,2,3,4,5], где threshold >= Priority
2. Если подходящего threshold нет — fallback: `threshold = _partitionThresholds[^1]` (5)
3. `_partitionPools[threshold].WaitAsync()` блокирует поток, пока слот не освободится
4. При отмене (CancellationToken) выбрасывает `OperationCanceledException`

**Алгоритм освобождения слота:**
1. В блоке `finally` `ProcessWithPoolAsync` (гарантированно)
2. Даже если процесс упал с исключением

### 4. Трекинг активных процессов

**Назначение:** мониторинг PID, диагностика зависаний и принудительное завершение процессов только по бизнес-таймауту команды (`ProcessTimeoutSeconds`).

**Graceful Shutdown (Worker):** При остановке Worker выполняет `PerformGracefulShutdownAsync()`:
- Логирует количество активных процессов
- Ждёт до 30 секунд, давая процессам шанс завершиться самостоятельно
- Активные Revit/Navisworks-процессы не завершаются принудительно — их команды
  подхватываются при следующем запуске через Crash Recovery (истёкший Lease)
- Фоновые задачи (cleanup, health monitoring) ожидаются с таймаутом 15 сек
- Per-partition пулы освобождаются

**Реализация:**
```csharp
private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();

// Перед запуском
_activeProcesses[cmd.CommandId] = process;

// После завершения (в finally)
_activeProcesses.TryRemove(cmd.CommandId, out _);

// При остановке
private async Task PerformGracefulShutdownAsync()
{
    logger.LogInformation("Worker stopping: activeProcesses={Count}", _activeProcesses.Count);
    await LogActiveProcessesOnShutdownAsync();
    _shutdownCts?.Cancel();
    await WaitForBackgroundTaskCompletionAsync(_cleanupTask, "Cleanup task");
    await WaitForBackgroundTaskCompletionAsync(_healthTask, "Health monitoring task");
}
```

`Process` хранится напрямую, без класса-обёртки. `Stopwatch` и `CommandId` — локальные переменные в `ExecuteOneAsync`.

### 4a. Отмена команд

**Назначение:** При отмене команды пользователем Server устанавливает статус `Deleted` в БД.
Отдельного промежуточного статуса отмены, per-command CTS и отдельного cancel-уведомления нет.

**Процесс отмены:**
1. Пользователь нажимает «⛔ Отменить» в Telegram
2. Server обновляет статус команды на `Deleted` в БД
3. Worker не включает удалённую команду в выборку `ClaimPendingCommandsAsync` (фильтр `Status = 'pending'`)
4. Если команда уже в статусе `processing` (выполняется), она продолжит выполнение,
   но её результат (`Done`/`Failed`) не перезапишет soft-delete — `UpdateStatus` имеет защиту:
   `WHERE Status != 'Deleted'`

### 5. Обработка ошибок подключения

При потере соединения с базой данных:
1. Зафиксировать ошибку в логе
2. Выждать паузу 5 сек
3. Восстановить подключение
4. Продолжить обработку очередей

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
-- Каждые 5 минут + при старте воркера
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
- `Lease` — устанавливается на `ProcessTimeoutSeconds + 5 мин` (долгий TTL), команда не вернётся в очередь раньше таймаута
- `CleanupIntervalSec = 300` — проверка каждые 5 минут (фоновая задача)

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
    await dataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed,
        errorMessage: $"Timeout: process exceeded {ProcessTimeoutSec}s limit");
}
```

**Дополнительная защита (SQL):**
```sql
-- Фоновая задача каждые 5 минут
UPDATE Commands
SET Status = 'pending',
    StartedAt = NULL,
    ProcessId = NULL,
    ErrorMessage = 'Timeout: process exceeded maximum execution time'
WHERE Status = 'processing'
  AND StartedAt < NOW() - INTERVAL '@TimeoutSeconds seconds';
```

### 3. Трекинг активных процессов

**Проблема:** Нужно знать PID активных процессов для мониторинга, диагностики и timeout-логики.

**Решение:** `ConcurrentDictionary<int, Process>` для трекинга:

```csharp
private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();

// При запуске процесса
_activeProcesses[cmd.CommandId] = process;

// При завершении (в finally)
_activeProcesses.TryRemove(cmd.CommandId, out _);

// Graceful shutdown не нужен:
// при остановке Worker не выполняет отдельное ожидание/убийство активных процессов
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
    ORDER BY Priority ASC, CreatedAt ASC, CommandId ASC
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

**Проблема:** Нужно корректно остановить основной цикл Worker при остановке сервиса.

**Решение:** CancellationToken threading без отдельного graceful shutdown для внешних процессов:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    _shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    
    try
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunListenerLoopAsync(stoppingToken);
        }
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
2. Очищаются истёкшие Lease
3. Цикл продолжается

---

## Алгоритм работы Server (создание команд)

### 1. Определение параметров команды

- Определить приоритет команды на основе контекста (тип задачи, роль пользователя)
- Партиция вычисляется автоматически воркером из поля `Priority` (не задаётся на сервере)

### 2. Сохранение команды в базу данных

- Создать сессию (если требуется)
- Вставить команду со статусом `pending`, указав приоритет
- Все операции в одной транзакции

### 3. Ожидание Worker

- Worker забирает команды при получении уведомления `new_tasks` (мгновенно) или при fallback polling (до 5 мин)
- Worker подписан на `LISTEN new_tasks`, fallback polling — раз в 5 минут

---

## Отмена команды пользователем

Любой одобренный пользователь может отменить команду через интерфейс `/status`. В `/status` отображаются **все сессии всех пользователей** (глобальный статус), с указанием `[username]` рядом с каждой сессией.

### Процесс отмены

1. **Исполнение отмены** — при нажатии кнопки «⛔ Отменить» Server обновляет статус команды на `Deleted` в БД:
   ```sql
   UPDATE Commands
   SET Status = 'Deleted'
   WHERE CommandId = @CommandId
     AND (SessionId IN (SELECT SessionId FROM Sessions WHERE UserId = @UserId)
          OR @IsAdmin = true);
   ```
2. **Worker** не включает удалённую команду в выборку (`Filter: Status = 'pending'`).
   Если команда уже `processing` — `UpdateStatus` не перезаписывает `Deleted`
   (защита `WHERE Status != 'Deleted'`).

### Отмена без NOTIFY

Ранее отмена включала отдельный промежуточный статус и отдельное cancel-уведомление.
В текущей реализации отмена является soft-delete команды. Это упрощает архитектуру и исключает
race condition с per-command CTS.

---

## Уведомления пользователей (Telegram)

После завершения **всей сессии** (не отдельной команды) Worker отправляет сводку пользователю
через отдельный канал LISTEN/NOTIFY. Уведомление содержит количество обработанных файлов,
имя проекта и список файлов с ошибками (если есть).

### Схема

```
┌─────────────────┐         ┌─────────────┐         ┌─────────────────────┐
│     Worker      │         │ PostgreSQL  │         │  Server              │
│  (выполнение)   │         │             │         │  (CommandNotif. Svc) │
└────────┬────────┘         └──────┬──────┘         └──────────┬──────────┘
         │                        │                           │
         │ 1. Последняя команда    │                           │
         │    сессии завершена    │                           │
         │  (remaining == 0)      │                           │
         │                        │                           │
         │ 2. NOTIFY              │                           │
         │    command_completed   │                           │
         │    (Done|Total|ProjNm) │                           │
         ├───────────────────────>│                           │
│                        │                           │
│                        │ 3. Пробуждение            │
│                        │    CommandNotificationSvc │
│                        ├──────────────────────────>│
│                        │                           │
│                        │                           │ 4. Enqueue в
│                        │                           │    Channel<NotificationItem>
│                        │                           │    (256 capacity)
│                        │                           │
│                        │                           │ 5. NotificationSenderService
│                        │                           │    читает канал → запрос
│                        │                           │    Failed-файлов и длительности
│                        │                           │
│                        │                           │ 6. SendMessageAsync
│                        │                           │    userId, сводка
│                        │                           ├────────> Telegram
```

### Payload уведомления

```
NOTIFY command_completed, 'UserId|SessionId|Done|Total|ProjectName'
```

Формат: pipe-разделённые поля (`Split('|', 5)`).

| Поле | Тип | Описание |
|------|-----|----------|
| `UserId` | BIGINT | Telegram ID пользователя |
| `SessionId` | INT | ID сессии (для запроса списка Failed-файлов) |
| `Done` | INT | Количество успешно выполненных команд |
| `Total` | INT | Общее количество команд в сессии |
| `ProjectName` | TEXT | Имя проекта (пусто для старых сессий) |

### Формат сообщения

```
✅ ProjectA — сессия завершена — все 5 файлов обработано

❌ ProjectA — сессия завершена — все 3 файлов с ошибками

⚠️ ProjectA — сессия завершена: 3 ✅, 2 ❌ из 5

Ошибки:
- model.rvt
- another.rvt
```

При отсутствии `ProjectName` (старые сессии) префикс не добавляется:
```
✅ сессия завершена — все 3 файлов обработано
```

### In-memory счётчик сессий (Worker side)

Уведомления отправляются **только когда вся сессия завершена** (все команды обработаны).
Worker ведёт in-memory счётчик вместо per-command SQL запроса:

```csharp
private readonly ConcurrentDictionary<int, int> _sessionRemaining = new();

// При ClaimPendingCommandsAsync — добавляем claimed-команды текущего batch-а
foreach (var group in claimed.GroupBy(c => c.SessionId))
    _sessionRemaining.AddOrUpdate(group.Key, group.Count(), (_, existing) => existing + group.Count());

// При выходе каждой захваченной команды из processing — CompleteClaimedCommandAsync
var newRemaining = _sessionRemaining.AddOrUpdate(
    cmd.SessionId, _ => 0, (_, current) => current - 1);

if (newRemaining == 0) // batch по сессии закончился → проверяем БД
{
    // Проверяем БД на предмет оставшихся pending/processing команд
    var remainingInDb = await dataService.CountPendingProcessingBySessionAsync(cmd.SessionId);
    if (remainingInDb == 0)
    {
        var status = await dataService.GetSessionsStatusAsync(cmd.SessionId);
        await dataService.NotifyCommandCompletedAsync(
            cmd.UserId, cmd.SessionId, status.DoneFiles, status.TotalFiles, status.ProjectName);
    }
}
```

**Преимущества:**
- Нет per-command SQL запросов (удалён `GetSessionProgressAsync`)
- Атомарный `AddOrUpdate` — только один поток отправляет уведомление
- Retry тоже декрементит счётчик текущего claim-а; сама команда снова становится `pending`, поэтому уведомление не уйдёт до следующей проверки БД
- `CountPendingProcessingBySessionAsync` корректно обрабатывает случай, когда команд в сессии > DefaultBatchSize, retry и несколько Worker-процессов
- Lock-free, не требует блокировок

### Запрос длительности и списка Failed-файлов (Server side)

При получении NOTIFY `CommandNotificationService` проверяет `failed = total - done`.
Сначала запрашивается длительность сессии:

```sql
SELECT EXTRACT(EPOCH FROM (MAX(CompletedAt) - MIN(StartedAt)))::int
FROM Commands
WHERE SessionId = @SessionId
  AND Status != 'Deleted'
  AND StartedAt IS NOT NULL
  AND CompletedAt IS NOT NULL;
```

Если есть ошибки — открывает отдельное подключение и запрашивает имена файлов:

```sql
SELECT FilePath FROM Commands
WHERE SessionId = @SessionId AND Status = 'Failed';
```

Имена файлов извлекаются через `Path.GetFileName()` и добавляются в сообщение:
```
\n\nОшибки:\n- model.rvt\n- another.rvt
```

### Отправка

**Сторона Worker** (`CommandExecutionService.CompleteClaimedCommandAsync`):
- Вызывается после `Done`, `Failed`, unknown/invalid command и после планирования retry
- Декрементит in-memory счётчик `_sessionRemaining` для текущего claim-а
- Если `newRemaining == 0` — проверяет `CountPendingProcessingBySessionAsync`
- Если в БД нет `pending`/`processing` — запрашивает `GetSessionsStatusAsync` и шлёт `NotifyCommandCompletedAsync`
- Промежуточные команды и retry не отправляют пользовательских уведомлений

**Сторона Server (двухступенчатая обработка):**
1. `CommandNotificationService`: `BackgroundService`, подписан на `LISTEN command_completed`.
   При получении NOTIFY парсит payload через `Split('|', 5)` и ставит задачу в `Channel<NotificationItem>` (256 capacity, bounded).
2. `NotificationSenderService`: `BackgroundService`, читает `Channel<NotificationItem>` через `ReadAllAsync()`.
   Запрашивает длительность сессии по `MIN(StartedAt)` / `MAX(CompletedAt)`.
   Если `failed > 0` — запрашивает Failed-файлы из БД через отдельное подключение.
   Отправляет сводку через `ITelegramOutputService.SendMessageAsync()`.
   Markdown-форматирование используется (`ParseMode.MarkdownV2` через `EscapeMarkdownV2()`).

---

## Конфигурация системы

### Параметры конфигурации (CommandExecutionService)

| Параметр | Откуда | Значение по умолч. | Описание |
|----------|--------|-------------------|----------|
| `Partitions` | `WorkerOptions.Partitions` | `{1→3, 2→5, 3→3, 4→1, 5→1}` | Priority threshold → макс. процессов. Команда попадает в первый threshold >= Priority. Чем меньше Priority, тем выше приоритет (SortedDictionary) |
| `ProcessTimeoutSeconds` | `WorkerOptions.ProcessTimeoutSeconds` | 10800 (3 часа) | Максимальное время выполнения команды |
| `MaxRetries` | `WorkerOptions.MaxRetries` | 5 | Максимальное количество попыток retry |
| `RetryDelayBaseSeconds` | `WorkerOptions.RetryDelayBaseSeconds` | 60 | Базовая задержка для экспоненциального backoff |
| `CompletedSessionRetentionDays` | `WorkerOptions.CompletedSessionRetentionDays` | 30 | Через сколько дней мягко удалять старые сессии без `pending`/`processing`; `0` отключает автоочистку |
| `CleanupIntervalSec` | константа | 300 (5 мин) | Интервал очистки истёкших Lease |
| `HealthCheckIntervalSec` | константа | 30 | Интервал мониторинга здоровья процессов |
| `FallbackTimeoutSec` | константа | 300 (5 мин) | Интервал fallback-поллинга очереди |
| `ReconnectDelayMs` | константа | 5000 | Задержка перед переподключением к БД |

### Настройка через appsettings.json

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres"
  },
  "Worker": {
    "ProcessTimeoutSeconds": 10800,
    "CompletedSessionRetentionDays": 30,
    "Partitions": {
      "1": 3,
      "2": 5,
      "3": 3,
      "4": 1,
      "5": 1
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

**Примечание:** `ProcessTimeoutSeconds` задаётся в секции `Worker`. Если не указан — по умолчанию 10800 сек (3 часа). Каждая команда настраивается отдельно в словаре `Commands` (без привязки к партиции). Партиции настраиваются в секции `Partitions`: ключ — максимальный Priority threshold, значение — макс. процессов. **Чем меньше Priority, тем выше приоритет.** Команда попадает в первый threshold >= Priority.

**Приоритеты команд (CommandPriorityMap в `SlashCommandService.cs`):**

| Команда | Priority | Партиция |
|---------|----------|----------|
| PDF | 1 | Critical |
| DWG | 2 | High |
| NWC, IFC, BIMDOC, CLASHREP | 3 | Medium |
| AUTORES | 4 | Low |
| Не указана в мапе | 50 | Lowest (fallback) |

Чтобы добавить новую команду — достаточно записи в JSON + записи в `CommandPriorityMap` (Server), если нужен особый приоритет.

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
| `TrackedMessages` | Отслеживаемые сообщения Telegram для очистки истории | `MessageId: 500, SessionId: 42, ChatId: 12345, MessageIdPg: 1001` |

### Как таблицы связаны между собой

```
┌──────────────┐       ┌──────────────┐       ┌──────────────┐
│   BotUsers   │ 1──N  │   Sessions   │ 1──N  │   Commands   │
│  (пользоват.)│──────>│   (сессии)   │──────>│  (команды)   │
└──────────────┘       └──────────────┘       └──────┬───────┘
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
                                            │  (любой) ──▶ Deleted   │
                                            └────────────────────────┘
```

- **Один пользователь** → может иметь **много сессий**
- **Одна сессия** → может содержать **много команд**

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
Шаг 3: Worker получает NOTIFY new_tasks (мгновенно)
        │     или fallback polling раз в 5 минут
        ▼
Шаг 4: Worker забирает pending-команды себе
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

#### Очередь задач — LISTEN/NOTIFY + fallback polling

Worker подписан на канал `new_tasks` через PostgreSQL `LISTEN/NOTIFY` и мгновенно реагирует на новые задачи. Fallback polling срабатывает раз в 5 минут при потере соединения:

```sql
SELECT ...
FROM Commands
WHERE Status = 'pending'
ORDER BY Priority ASC, CreatedAt ASC, CommandId ASC
FOR UPDATE SKIP LOCKED;
```

Конкурентность обеспечивается SQL-блокировками `FOR UPDATE SKIP LOCKED`.

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
| `Status` | Где сейчас команда: `pending` → `processing` → `Done` / `Failed` / `Deleted` |
| `CreatedAt` | Когда создали команду |
| `StartedAt` | Когда Worker начал выполнять (`NULL` — пока не начали) |
| `CompletedAt` | Когда закончили (`NULL` — пока не закончили) |
| `Lease` | Срок аренды (см. Lease выше). Unix-время в секундах |
| `Priority` | Насколько задача важная (1–5, чем **меньше** — тем важнее). По умолчанию 50 (из CommandPriorityMap: PDF=1, DWG=2, NWC/IFC/BIMDOC/CLASHREP=3, AUTORES=4) |
| `Partition` | Partition threshold для priority-based пулов процессов |
| `ProcessId` | ID процесса Windows (чтобы можно было «убить» программу, если что-то пошло не так) |
| `ErrorMessage` | Если команда упала с ошибкой — тут текст ошибки |
| `RetryCount` | Сколько раз уже пытались выполнить команду |
| `NextRetryAt` | Когда следующая попытка (NULL — если не запланирована) |
| `Progress` | Прогресс выполнения (0–100) |
| `Result` | Результат выполнения (текст) |

### Индексы — ускорители поиска

Чтобы БД не перебирала все строки подряд, мы добавляем **индексы**. Это как оглавление в книге:

| Индекс | Зачем нужен |
|--------|-------------|
| `(Status, Priority, CreatedAt)` | Быстро находить, какие команды ждут в очереди, и сортировать по важности |
| `(Status, Lease) WHERE Status = 'processing'` | Быстро находить «зависшие» команды (у которых истёк Lease) |
| `(SessionId)` | Быстро искать все команды одной сессии |
| `(UserId, CreatedAt DESC)` | Быстро показывать пользователю список его сессий |

---

## SQL-операции

### Вставка команды

```sql
INSERT INTO "Commands" 
    ("SessionId", "CommandText", "FilePath", "ExecutionOrder", "Priority")
SELECT 
    @SessionId, unnest(@CommandTexts::text[]), unnest(@FilePaths::text[]), unnest(@Orders::int[]), unnest(@Priorities::int[]);
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

### Отправка уведомления пользователю (Server)

```sql
NOTIFY command_completed, 'UserId|SessionId|Done|Total|ProjectName';
```

Payload генерируется в `SessionDataService.NotifyCommandCompletedAsync()` (реализует `INotificationDataService`).

### Очистка истёкших Lease (crash recovery)

```sql
-- Каждые 5 минут + при старте воркера
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
-- Каждые 5 минут (фоновая задача)
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

### Отмена команды пользователем

```sql
-- Server: soft-delete команды
-- Любой одобренный пользователь может удалить чужую команду (@IsAdmin = true)
UPDATE Commands
SET Status = 'Deleted'
WHERE CommandId = @CommandId
  AND (SessionId IN (SELECT SessionId FROM Sessions WHERE UserId = @UserId)
       OR @IsAdmin = true);
```



---

## Безопасность и надёжность

| Принцип | Реализация |
|---------|------------|
| **Логическое удаление** | Команды никогда не удаляются физически, только `Status = 'Deleted'` |
| **Транзакционность** | Захват команд — атомарная операция с `FOR UPDATE SKIP LOCKED` |
| **Ограничение нагрузки** | Per-partition пулы процессов (SortedDictionary<int, SemaphoreSlim>) — каждая партиция имеет свой лимит |
| **Приоритизация** | Высокоприоритетные команды (Priority=1) выполняются первыми (`ORDER BY Priority ASC, CreatedAt ASC, CommandId ASC`) |
| **Lease-механизм** | Защита от сбоев воркера — команды возвращаются в очередь при истечении TTL |
| **Таймауты** | Принудительное завершение процессов при превышении лимита времени (`process.Kill(true)`) |
| **Трекинг PID** | Сохранение ProcessId для мониторинга и принудительного завершения |
| **Отказоустойчивость** | Переподключение при потере соединения с БД (5 сек задержка) |
| **Логирование** | Полное контекстное логирование всех операций и ошибок, включая stdout/stderr процессов |
| **Изоляция компонентов** | Server и Worker независимы, общаются только через БД |
| **Shutdown Worker** | Worker выполняет `PerformGracefulShutdownAsync()` при остановке: логирует активные процессы, ждёт до 30 сек их завершения, ожидает фоновые задачи (15 сек таймаут). Активные Revit/Navisworks не принудительно завершаются — их команды подхватываются при следующем запуске через Crash Recovery |
| **FOR UPDATE SKIP LOCKED** | Несколько воркеров могут работать параллельно без конфликтов |
| **Lease (долгий TTL)** | Lease устанавливается на `ProcessTimeoutSeconds + 5 мин`, команда не вернётся в очередь раньше таймаута |
| **Валидация FilePath** | Проверка существования, расширения (из `AllowedExtensions`) и защита от path traversal перед запуском процесса |
| **Асинхронное чтение stdout/stderr** | Предотвращает deadlock при заполнении буфера вывода (64KB) |
| **Уведомления пользователей** | Worker шлёт NOTIFY `command_completed`, Server (`CommandNotificationService`) слушает и отправляет Telegram-сообщение через `ITelegramOutputService` с длительностью сессии и списком ошибочных файлов |
| **Отмена команд** | Пользователь отменяет команду через UI `/status` → кнопку «⛔ Отменить». Server показывает подтверждение и выполняет soft-delete (`Status = 'Deleted'`), а Worker не перезаписывает `Deleted` после завершения процесса |
| **Автоочистка сессий** | Worker мягко удаляет старые сессии без `pending`/`processing` старше `CompletedSessionRetentionDays` |
| **Очередь задач** | Worker подписан на `LISTEN new_tasks` и мгновенно реагирует на уведомления; fallback polling срабатывает раз в 5 минут при потере соединения |

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
10. **Результат**:
    - Таймаут → `Kill(true)`, статус `Failed`
    - `ExitCode == 0` → статус `Done`
    - Иначе → статус `Failed`
11. **Очистка** (в `finally`) — `partitionPool.Release()`, `_activeProcesses.TryRemove()`

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

Для новой команды нужно **добавить запись в `CommandPriorityMap`** в `SlashCommandService.cs`, если нужен особый приоритет. Если не добавить — команда получит Priority=50 и попадёт в Lowest (50 > 5).

### Шаг 2: Настроить приоритет (при создании команды)

Приоритет задаётся в момент создания команды через `CommandPriorityMap` в `SlashCommandService.cs`. 
Если команды нет в мапе — по умолчанию Priority=50 (попадёт в Lowest, т.к. 50 > 5).

Значение `Priority` (1 = наивысший) определяет, в какую партицию попадёт команда:
- `Priority 1` → Critical (до 3 одновременных)
- `Priority 2` → High (до 5)
- `Priority 3` → Medium (до 3)
- `Priority 4` → Low (до 1)
- `Priority 5+` → Lowest (fallback, до 1)

### Шаг 3 (опционально): Настроить лимиты партиций

Если стандартные лимиты не подходят:
```json
"Partitions": {
  "1": 3,   // Critical: 3 слота
  "2": 5,   // High:    5 слотов
  "3": 3,   // Medium:  3 слота
  "4": 1,   // Low:     1 слот
  "5": 1    // Lowest:  1 слот
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
ORDER BY "Priority" ASC, "CreatedAt" ASC;
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

**Удалённые команды (статистика):**
```sql
SELECT "CommandId", "CommandText", "SessionId", "CreatedAt"
FROM "Commands"
WHERE "Status" = 'Deleted'
ORDER BY "CreatedAt" DESC
LIMIT 20;
```

**Проверка подписки Server на уведомления о завершении:**
```sql
SELECT * FROM pg_listening_channels();
-- Для Server должен вернуть 'command_completed'
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
| 1 | **Лимит процессов** | Для каждого уровня приоритета не выполняется более его лимита одновременно (по умолчанию: Critical=1 → 3, High=2 → 5, Medium=3 → 3, Low=4 → 1, Lowest=5+ → 1) |
| 2 | **Приоритизация** | Высокоприоритетные команды стартуют раньше низкоприоритетных |
| 3 | **Lease-механизм** | При сбое воркера команда возвращается в очередь после истечения Lease |
| 4 | **Таймауты** | Процессы, выполняющиеся дольше `ProcessTimeoutSeconds` (по умолчанию 3 часа), принудительно завершаются |
| 5 | **Трекинг PID** | ProcessId сохраняется для мониторинга и принудительного завершения |
| 6 | **FOR UPDATE SKIP LOCKED** | Несколько воркеров могут работать параллельно без конфликтов |
| 7 | **Shutdown Worker** | Graceful shutdown для внешних процессов не реализуется; остановка Worker не является отдельным сценарием завершения Revit/Navisworks |
| 8 | **Очередь задач** | Worker подписан на `LISTEN new_tasks` и мгновенно реагирует; fallback polling — раз в 5 минут |
| 9 | **Восстановление** | При перезапуске Worker очищает истёкшие Lease и продолжает обработку |
| 10 | **Наблюдаемость** | Диагностические запросы показывают актуальное состояние (PID, Lease, длительность) |
| 11 | **Отмена команд** | Пользователь может отменить команду через `/status`. Server мягко удаляет команду (`Status = 'Deleted'`), а Worker не перезаписывает этот статус после завершения процесса |

---

## Известные ограничения и технический долг

В этом разделе зафиксированы выявленные недочёты архитектуры и реализации, которые требуют проработки в будущих версиях.

| ID | Описание | Влияние | Приоритет | Статус |
|----|----------|---------|-----------|--------|
| DOC-001 | **Lease (5 мин) < ProcessTimeout (1 час)** — не описан механизм продления Lease во время длительного выполнения | Команда может быть ошибочно возвращена в очередь другим воркером во время выполнения | 🔴 HIGH | ✅ Исправлено (v1.1) |
| DOC-002 | **Не описано чтение stdout/stderr** процессов — указано `RedirectStandardOutput/Error = true`, но нет асинхронного чтения | Риск deadlock при заполнении буфера вывода (64KB) | 🔴 HIGH | ✅ Исправлено (v1.1) |
| DOC-003 | **Нет валидации FilePath** — отсутствует защита от path traversal атак и проверка существования файлов | Потенциальная уязвимость безопасности | 🔴 HIGH | ✅ Исправлено (v1.1) |
| DOC-004 | **Партиции (priority-based)** — `SortedDictionary<int, SemaphoreSlim>` с threshold приоритета как ключ. Команды сортируются по `Priority ASC` (1=наивысший). Partition: Critical(1, 3 слота), High(2, 5), Medium(3, 3), Low(4, 1), Lowest(5+, 1) | Высокоприоритетные команды не ждут за низкоприоритетными | 🟠 MEDIUM | ✅ Реализовано (v1.1) |
| DOC-005 | **Retry logic** — экспоненциальная задержка (base*2^attempt), лимит попыток (MaxRetries=5). Команда возвращается в `pending` с `NextRetryAt` | Самовосстановление при временных ошибках (файл заблокирован, сеть недоступна) | 🟠 MEDIUM | ✅ Реализовано (v1.2) |
| DOC-006 | **Нет автоматических метрик** (Prometheus/Grafana) — только ручные SQL-запросы | Ограниченный мониторинг в production, сложность-alerting | 🟠 MEDIUM | В планах (v1.2) |
| DOC-007 | **Координация очистки Lease** — `pg_try_advisory_lock(1234567)` перед каждой очисткой. Только один воркер выполняет `ReleaseExpiredLeasesAsync`/`ReleaseTimeoutCommandsAsync`, остальные пропускают цикл | Снижение нагрузки на БД при нескольких воркерах | 🟡 LOW | ✅ Реализовано (v1.2) |
| DOC-008 | **Graceful shutdown реализован** — Worker выполняет `PerformGracefulShutdownAsync()` при остановке: логирует активные процессы, ждёт до 30 сек их завершения, ожидает фоновые задачи (15 сек таймаут). Revit/Navisworks не принудительно завершаются — их команды подхватываются при следующем запуске через Crash Recovery | Риск deadlock на shutdown минимален: процессы не убиваются, но Worker ждёт их естественного завершения | 🟢 NONE | Зафиксировано |
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
