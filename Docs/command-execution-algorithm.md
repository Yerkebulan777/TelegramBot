# Алгоритм выполнения команд (Command Execution Algorithm)

## Обзор

Фоновая служба `TelegramBot.Worker` выполняет команды для Revit/Navisworks/AI через механизм PostgreSQL LISTEN/NOTIFY с поддержкой:
- **Партиции (очереди по приоритетам)** — команды распределяются по партициям в зависимости от типа/приоритета
- **Пул процессов (N одновременных выполнений)** — параллельное выполнение N процессов с ожиданием завершения каждого процесса Windows
- **Очередь на основе приоритетов** — высокоприоритетные команды выполняются первыми

## Архитектура

### Общая схема

```
TelegramBot.Server          PostgreSQL          TelegramBot.Worker
       │                        │                        │
       │ 1. Создает сессию      │                        │
       │    и команды           │                        │
       │    (Priority=N)        │                        │
       ├───────────────────────>│                        │
       │                        │                        │
       │ 2. INSERT Commands     │                        │
       │    Status='pending'    │                        │
       │    Partition='revit'   │                        │
       │───────────────────────>│                        │
       │                        │                        │
       │ 3. NOTIFY new_command  │                        │
       │───────────────────────>│                        │
       │                        │ 4. conn.WaitAsync()    │
       │                        │ (блокировка до NOTIFY) │
       │                        ├───────────────────────>│
       │                        │                        │
       │                        │ 5. SELECT команды      │
       │                        │ WHERE Status='pending' │
       │                        │ ORDER BY Priority DESC │
       │<───────────────────────┤                        │
       │                        │                        │
       │                        │ 6. Распределение по    │
       │                        │    пулу процессов (N)  │
       │                        │                        │
       │                        │ 7. Выполнение команды  │
       │                        │ (Process.Start + Wait) │
       │                        │                        │
       │                        │ 8. UPDATE Status       │
       │                        │ 'Done' | 'Failed'      │
       │───────────────────────>│                        │
       │                        │                        │
```

### Схема партиций и пула процессов

```
┌─────────────────────────────────────────────────────────────────┐
│                    CommandExecutionService                      │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │              Partition Queue Manager                    │   │
│  │  ┌─────────────┬─────────────┬─────────────┐           │   │
│  │  │  revit      │  navis      │  ai         │           │   │
│  │  │  (Priority) │  (Priority) │  (Priority) │           │   │
│  │  │  [cmd1]     │  [cmd3]     │  [cmd5]     │           │   │
│  │  │  [cmd2]     │  [cmd4]     │  [cmd6]     │           │   │
│  │  └─────────────┴─────────────┴─────────────┘           │   │
│  └─────────────────────────────────────────────────────────┘   │
│                           │                                     │
│                           ▼                                     │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │              Process Pool (N slots)                     │   │
│  │  ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐     │   │
│  │  │Slot 1 │ │Slot 2 │ │Slot 3 │ │Slot 4 │ │Slot N │     │   │
│  │  │Process│ │Process│ │Process│ │Process│ │Process│     │   │
│  │  │Wait   │ │Wait   │ │Wait   │ │Wait   │ │Wait   │     │   │
│  │  └───────┘ └───────┘ └───────┘ └───────┘ └───────┘     │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

## Состояния команды

| Статус | Описание |
|--------|----------|
| `pending` | Команда создана, ожидает выполнения в очереди партиции |
| `queued` | Команда выбрана из БД, ожидает свободный слот в пуле процессов |
| `in_progress` | Команда выполняется (процесс Windows запущен, ожидается завершение) |
| `Done` | Команда успешно завершена |
| `Failed` | Команда завершена с ошибкой |
| `Deleted` | Команда удалена (soft-delete) |

## Партиции (очереди по приоритетам)

### Концепция

Партиции — это логические очереди для группировки команд по типу задачи:
- **revit** — команды для Revit (экспорт IFC, DWG, PDF)
- **navis** — команды для Navisworks (экспорт NWD, NWC, clash detection)
- **ai** — команды для AI-обработки (анализ, суммаризация)

Все команды должны быть явно отнесены к одной из партиций.

### Приоритеты внутри партиции

Каждая команда имеет поле `Priority` (int, default=0):
- Чем выше значение — тем выше приоритет
- Внутри партиции команды сортируются по `Priority DESC, CreatedAt ASC`
- Команды с одинаковым приоритетом выполняются в порядке FIFO

### Конфигурация партиций

```json
{
  "Worker": {
    "Partitions": {
      "revit": { "MaxConcurrent": 2, "Priority": 100 },
      "navis": { "MaxConcurrent": 1, "Priority": 90 },
      "ai": { "MaxConcurrent": 3, "Priority": 80 }
    }
  }
}
```

Где:
- `MaxConcurrent` — максимальное количество одновременных процессов для этой партиции
- `Priority` — приоритет партиции (влияет на порядок выбора между партициями)

## Алгоритм Worker (CommandExecutionService)

### 1. Инициализация подключения и пула процессов

```csharp
// Инициализация подключения
await using var conn = new NpgsqlConnection(connectionString);
await conn.OpenAsync();
await conn.ExecuteNonQueryAsync("LISTEN new_command;");

// Инициализация пула процессов
var processPool = new ProcessPool(maxConcurrent: N);
var partitionQueues = new ConcurrentDictionary<string, ConcurrentQueue<Command>>();
var cts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
```

### 2. Цикл ожидания команд с пулом процессов

```
┌─────────────────────────────────────────────────────────────────┐
│  Бесконечный цикл (внутри BackgroundService)                    │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  conn.WaitAsync()                                               │
│  (блокируется до NOTIFY или таймаута ~60 сек)                   │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼ (получено уведомление)
┌─────────────────────────────────────────────────────────────────┐
│  dataService.GetPendingCommandsAsync()                          │
│  SELECT * FROM Commands                                         │
│  WHERE Status = 'pending'                                       │
│  ORDER BY Priority DESC, CreatedAt ASC                          │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼ (команды найдены)
┌─────────────────────────────────────────────────────────────────┐
│  Распределение по партициям:                                    │
│  foreach (cmd in commands) {                                    │
│    partition = GetPartitionForCommand(cmd.Code)                 │
│    partitionQueues[partition].Enqueue(cmd)                      │
│  }                                                              │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  Запуск обработки партиций (параллельно):                       │
│  foreach (partition in partitionQueues.Keys) {                  │
│    Task.Run(() => ProcessPartitionAsync(partition, cts.Token))  │
│  }                                                              │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  ProcessPartitionAsync(partition):                              │
│  while (partitionQueues[partition].TryDequeue(out cmd)) {       │
│    // Ждём свободный слот в пуле для этой партиции              │
│    await processPool.WaitForSlotAsync(partition)                │
│                                                                   │
│    // Запускаем процесс в пуле                                  │
│    _ = Task.Run(async () => {                                   │
│      await ExecuteOneAsync(cmd)                                 │
│      processPool.ReleaseSlot(partition)                         │
│    })                                                           │
│  }                                                              │
└─────────────────────────────────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  ExecuteOneAsync(cmd):                                          │
│  try {                                                          │
│    UPDATE Status='in_progress' WHERE Id=@cmd.Id                 │
│                                                                   │
│    // Запуск процесса Windows                                   │
│    var psi = new ProcessStartInfo {                             │
│      FileName = GetExecutableForCommand(cmd.Code),              │
│      Arguments = BuildArguments(cmd),                           │
│      UseShellExecute = false,                                   │
│      RedirectStandardOutput = true,                             │
│      RedirectStandardError = true                               │
│    };                                                           │
│                                                                   │
│    using var process = Process.Start(psi);                      │
│    await process!.WaitForExitAsync();  // <-- БЛОКИРУЮЩЕЕ       │
│                                                                   │
│    if (process.ExitCode == 0) {                                 │
│      UPDATE Status='Done' WHERE Id=@cmd.Id                      │
│    } else {                                                     │
│      UPDATE Status='Failed' WHERE Id=@cmd.Id                    │
│    }                                                            │
│  } catch (Exception ex) {                                       │
│    LogError(ex)                                                 │
│    UPDATE Status='Failed' WHERE Id=@cmd.Id                      │
│    processPool.ReleaseSlot(cmd.Partition)                       │
│  }                                                              │
└─────────────────────────────────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  Продолжить цикл (ждать след. NOTIFY)                           │
└─────────────────────────────────────────────────────────────────┘
```

### 3. ProcessPool — управление пулом процессов

```csharp
public class ProcessPool
{
    private readonly int _maxConcurrent;
    private readonly SemaphoreSlim _semaphore;
    private readonly ConcurrentDictionary<string, int> _partitionSlots;
    
    public ProcessPool(int maxConcurrent)
    {
        _maxConcurrent = maxConcurrent;
        _semaphore = new SemaphoreSlim(maxConcurrent);
        _partitionSlots = new ConcurrentDictionary<string, int>();
    }
    
    public async Task WaitForSlotAsync(string partition)
    {
        // Глобальный лимит + лимит партиции
        await _semaphore.WaitAsync();
        
        // Проверяем лимит для конкретной партиции
        while (_partitionSlots.TryGetValue(partition, out var count) 
               && count >= GetPartitionMaxConcurrent(partition))
        {
            await Task.Delay(100);
        }
        
        _partitionSlots.AddOrUpdate(partition, 1, (_, v) => v + 1);
    }
    
    public void ReleaseSlot(string partition)
    {
        _semaphore.Release();
        _partitionSlots.AddOrUpdate(partition, 0, (_, v) => Math.Max(0, v - 1));
    }
}
```

### 4. Обработка ошибок подключения

При потере соединения с PostgreSQL:
1. Логирование ошибки
2. Пауза 5 секунд
3. Попытка переподключения
4. Повторное выполнение `LISTEN new_command`
5. Восстановление обработки очередей

## Алгоритм Server (создание команды)

### 1. Создание сессии и команд с приоритетом

```csharp
// SlashCommandService.ConfirmFileSelectionAsync()
var partition = GetPartitionForCommand(commandCode); // например: "revit"
var priority = GetPriorityForUser(userId); // например: 100 для админов

var sessionId = await dataService.CreateSessionWithCommandsAsync(
    userId,
    selectedFiles,
    commandCodes,     // например: ["EXPORT_IFC", "EXPORT_DWG"]
    partition,        // например: "revit"
    priority          // например: 100
);
```

### 2. INSERT команд в базу с партицией и приоритетом

```sql
INSERT INTO "Commands" 
    ("SessionId", "Code", "Status", "CreatedAt", "Partition", "Priority")
VALUES 
    (@SessionId, @Code, 'pending', NOW(), @Partition, @Priority)
RETURNING "Id";
```

### 3. Уведомление Worker

```csharp
await dataService.NotifyNewCommandsAsync();
// Выполняет: NOTIFY new_command;
```

## Ключевые компоненты

| Компонент | Проект | Описание |
|-----------|--------|----------|
| `CommandExecutionService` | TelegramBot.Worker | Основная служба выполнения с пулом процессов |
| `ProcessPool` | TelegramBot.Worker | Управление пулом процессов (N слотов) |
| `PartitionManager` | TelegramBot.Worker | Распределение команд по партициям |
| `PostgresDataService.GetPendingCommandsAsync` | TelegramBot.Data | Выборка pending команд с приоритетами |
| `PostgresDataService.NotifyNewCommandsAsync` | TelegramBot.Data | Отправка NOTIFY |
| `PostgresDataService.UpdateCommandStatusAsync` | TelegramBot.Data | Обновление статуса |
| `Sql/Commands.cs` | TelegramBot.Data | SQL-запросы (5 partial-методов) |

## Конфигурация Worker

### appsettings.json

```json
{
  "Worker": {
    "MaxConcurrentProcesses": 5,
    "Partitions": {
      "revit": {
        "MaxConcurrent": 2,
        "Priority": 100,
        "ExecutablePath": "C:\\Revit\\Revit.exe",
        "Timeout": 3600
      },
      "navis": {
        "MaxConcurrent": 1,
        "Priority": 90,
        "ExecutablePath": "C:\\Navisworks\\Navisworks.exe",
        "Timeout": 1800
      },
      "ai": {
        "MaxConcurrent": 3,
        "Priority": 80,
        "ExecutablePath": "python",
        "Timeout": 600
      }
    },
    "PostgresRetryDelaySeconds": 5,
    "NotifyTimeoutSeconds": 60
  }
}
```

### Параметры конфигурации

| Параметр | Описание | Default |
|----------|----------|---------|
| `MaxConcurrentProcesses` | Общее количество одновременных процессов (N) | 5 |
| `Partitions.{name}.MaxConcurrent` | Лимит процессов для партиции | 1 |
| `Partitions.{name}.Priority` | Приоритет партиции (выше = важнее) | 50 |
| `Partitions.{name}.ExecutablePath` | Путь к исполняемому файлу | - |
| `Partitions.{name}.Timeout` | Таймаут выполнения команды (сек) | 300 |
| `PostgresRetryDelaySeconds` | Задержка перед переподключением к БД | 5 |
| `NotifyTimeoutSeconds` | Таймаут ожидания NOTIFY | 60 |

## SQL-запросы

### Схема таблицы Commands

```sql
CREATE TABLE "Commands" (
    "Id" SERIAL PRIMARY KEY,
    "SessionId" INT NOT NULL REFERENCES "Sessions"("Id"),
    "Code" VARCHAR(50) NOT NULL,
    "Status" VARCHAR(20) NOT NULL DEFAULT 'pending',
    "Partition" VARCHAR(50) NOT NULL,
    "Priority" INT NOT NULL DEFAULT 0,
    "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    "StartedAt" TIMESTAMPTZ,
    "CompletedAt" TIMESTAMPTZ,
    "ErrorMessage" TEXT,
    "ProcessId" INT
);

-- Индексы для ускорения выборки pending команд
CREATE INDEX "IX_Commands_Status_Priority" ON "Commands" ("Status", "Priority" DESC, "CreatedAt" ASC);
CREATE INDEX "IX_Commands_Partition_Status" ON "Commands" ("Partition", "Status");
```

### Получение pending команд (с партициями и приоритетами)

```sql
SELECT 
    c."Id", c."SessionId", c."Code", c."Status", c."Partition", 
    c."Priority", c."CreatedAt", c."ProcessId",
    s."UserId", s."SelectedFiles"
FROM "Commands" c
JOIN "Sessions" s ON c."SessionId" = s."Id"
WHERE c."Status" = 'pending'
ORDER BY 
    c."Priority" DESC,   -- Сначала высокоприоритетные
    c."CreatedAt" ASC;   -- Затем старые (FIFO)
```

### Обновление статуса (с таймстемпами и ProcessId)

```sql
UPDATE "Commands"
SET "Status" = @Status,
    "StartedAt" = CASE 
        WHEN @Status = 'in_progress' THEN NOW()
        ELSE "StartedAt"
    END,
    "CompletedAt" = CASE 
        WHEN @Status IN ('Done', 'Failed') THEN NOW()
        ELSE "CompletedAt"
    END,
    "ErrorMessage" = @ErrorMessage,
    "ProcessId" = @ProcessId
WHERE "Id" = @CommandId;
```

### Получение команд для партиции (с лимитом)

```sql
SELECT * FROM "Commands"
WHERE "Partition" = @Partition 
  AND "Status" = 'pending'
ORDER BY "Priority" DESC, "CreatedAt" ASC
LIMIT @MaxConcurrent;
```

### Отправка уведомления

```sql
NOTIFY new_command;
```

### Очистка зависших команд (in_progress > timeout)

```sql
-- Вернуть в pending команды, которые выполняются дольше таймаута
UPDATE "Commands"
SET "Status" = 'pending',
    "StartedAt" = NULL,
    "ErrorMessage" = 'Timeout: process exceeded maximum execution time'
WHERE "Status" = 'in_progress'
  AND "StartedAt" < NOW() - INTERVAL '@TimeoutSeconds seconds';
```

## Безопасность и надёжность

1. **Soft-delete**: Команды никогда не удаляются физически, только `Status = 'Deleted'`
2. **Транзакции**: Создание сессии и команд — в одной транзакции
3. **Пул процессов**: Ограничение N одновременных процессов предотвращает перегрузку системы
4. **Партиции**: Изоляция типов команд — долгая обработка Revit не блокирует AI-команды
5. **Приоритеты**: Высокоприоритетные команды (админы, срочные задачи) выполняются первыми
6. **Ожидание процесса**: `Process.WaitForExitAsync()` гарантирует завершение процесса перед следующей командой
7. **Таймауты**: Принудительное завершение процессов, выполняющихся дольше настроенного таймаута
8. **Повторные попытки**: Worker переподключается при потере соединения с PostgreSQL
9. **Логирование**: Все ошибки логируются с полным контекстом (Id команды, код, UserId, Partition, ProcessId)
10. **Изоляция**: Worker и Server работают независимо, общаются только через БД
11. **Graceful shutdown**: При остановке Worker дожидается завершения активных процессов

## Реализация процесса Windows

### Запуск и ожидание

```csharp
private async Task<CommandResult> ExecuteProcessAsync(Command command)
{
    var config = _partitionConfig[command.Partition];
    
    var psi = new ProcessStartInfo
    {
        FileName = config.ExecutablePath,
        Arguments = BuildArguments(command),
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        WorkingDirectory = config.WorkingDirectory
    };
    
    using var process = new Process { StartInfo = psi };
    
    // Подписка на вывод процесса
    var outputBuilder = new StringBuilder();
    var errorBuilder = new StringBuilder();
    
    process.OutputDataReceived += (s, e) => 
    {
        if (e.Data != null) outputBuilder.AppendLine(e.Data);
    };
    process.ErrorDataReceived += (s, e) => 
    {
        if (e.Data != null) errorBuilder.AppendLine(e.Data);
    };
    
    process.Start();
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    
    // Ожидание завершения с таймаутом
    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(config.Timeout));
    var waitForExitTask = process.WaitForExitAsync();
    
    var completedTask = await Task.WhenAny(timeoutTask, waitForExitTask);
    
    if (completedTask == timeoutTask)
    {
        // Превышен таймаут — убиваем процесс
        _logger.LogWarning("Command {CommandId} timed out, killing process", command.Id);
        process.Kill(true); // Убить дерево процессов
        await process.WaitForExitAsync();
        
        return new CommandResult
        {
            Success = false,
            ExitCode = -1,
            Error = $"Timeout: process exceeded {config.Timeout} seconds"
        };
    }
    
    return new CommandResult
    {
        Success = process.ExitCode == 0,
        ExitCode = process.ExitCode,
        Output = outputBuilder.ToString(),
        Error = errorBuilder.ToString()
    };
}
```

### Освобождение слота пула

```csharp
try
{
    var result = await ExecuteProcessAsync(command);
    await dataService.UpdateCommandStatusAsync(
        command.Id, 
        result.Success ? "Done" : "Failed",
        result.Error
    );
}
catch (Exception ex)
{
    _logger.LogError(ex, "Command {CommandId} failed", command.Id);
    await dataService.UpdateCommandStatusAsync(
        command.Id, 
        "Failed", 
        ex.Message
    );
}
finally
{
    // Освобождаем слот в пуле — обязательно!
    processPool.ReleaseSlot(command.Partition);
}
```

## Расширение (добавление новой команды)

### 1. Добавить код команды

```csharp
// TelegramBot.Core/Constants/CommandCodes.cs
public static class CommandCodes
{
    public const string ExportIfc = "EXPORT_IFC";
    public const string ExportDwg = "EXPORT_DWG";
    public const string ExportNwd = "EXPORT_NWD";  // Новая команда
}
```

### 2. Настроить партицию для новой команды

```csharp
// TelegramBot.Worker/Services/PartitionManager.cs
private string GetPartitionForCommand(string code) => code switch
{
    CommandCodes.ExportIfc or CommandCodes.ExportDwg => "revit",
    CommandCodes.ExportNwd => "navis",
    CommandCodes.AiSummary => "ai",
    _ => throw new InvalidOperationException($"No partition configured for command: {code}")
};
```

### 3. Добавить конфигурацию партиции

```json
// appsettings.json
{
  "Worker": {
    "Partitions": {
      "navis": {
        "MaxConcurrent": 1,
        "Priority": 90,
        "ExecutablePath": "C:\\Navisworks\\Navisworks.exe",
        "Timeout": 1800
      }
    }
  }
}
```

### 4. Добавить обработку в ExecuteOneAsync

```csharp
// TelegramBot.Worker/Services/CommandExecutionService.cs
private async Task ExecuteOneAsync(Command command)
{
    await processPool.WaitForSlotAsync(command.Partition);
    
    try
    {
        await dataService.UpdateCommandStatusAsync(command.Id, "in_progress", null);
        
        var result = command.Code switch
        {
            CommandCodes.ExportIfc => await ExecuteRevitExportAsync(command, "IFC"),
            CommandCodes.ExportDwg => await ExecuteRevitExportAsync(command, "DWG"),
            CommandCodes.ExportNwd => await ExecuteNavisExportAsync(command), // Новый метод
            CommandCodes.AiSummary => await ExecuteAiSummaryAsync(command),
            _ => throw new InvalidOperationException($"Unknown command: {command.Code}")
        };
        
        await dataService.UpdateCommandStatusAsync(
            command.Id, 
            result.Success ? "Done" : "Failed",
            result.Error
        );
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Command {CommandId} failed", command.Id);
        await dataService.UpdateCommandStatusAsync(command.Id, "Failed", ex.Message);
    }
    finally
    {
        processPool.ReleaseSlot(command.Partition);
    }
}
```

### 5. При необходимости — обновить UI в TelegramBot.Server

- Добавить кнопку выбора команды в клавиатуру
- Обновить `CallbackPrefixes` и обработчики при необходимости

## Диагностика

### Проверка pending команд по партициям

```sql
SELECT "Partition", COUNT(*) as "Count", AVG("Priority") as "AvgPriority"
FROM "Commands"
WHERE "Status" = 'pending'
GROUP BY "Partition"
ORDER BY "Count" DESC;
```

### Статус пула процессов (активные команды)

```sql
SELECT 
    "Partition",
    "Status",
    COUNT(*) as "Count",
    MIN("StartedAt") as "OldestStarted"
FROM "Commands"
WHERE "Status" IN ('in_progress', 'queued')
GROUP BY "Partition", "Status";
```

### Последние выполненные команды

```sql
SELECT 
    "Id", "Code", "Partition", "Priority", "Status", 
    "CreatedAt", "StartedAt", "CompletedAt",
    EXTRACT(EPOCH FROM ("CompletedAt" - "StartedAt")) as "DurationSec"
FROM "Commands"
WHERE "Status" IN ('Done', 'Failed')
ORDER BY "CreatedAt" DESC
LIMIT 20;
```

### Зависшие команды (in_progress дольше таймаута)

```sql
SELECT * FROM "Commands"
WHERE "Status" = 'in_progress'
  AND "StartedAt" < NOW() - INTERVAL '30 minutes'
ORDER BY "StartedAt" ASC;
```

### Статистика выполнения по партициям

```sql
SELECT 
    "Partition",
    COUNT(*) FILTER (WHERE "Status" = 'Done') as "Success",
    COUNT(*) FILTER (WHERE "Status" = 'Failed') as "Failed",
    COUNT(*) FILTER (WHERE "Status" = 'pending') as "Pending",
    COUNT(*) FILTER (WHERE "Status" = 'in_progress') as "InProgress",
    AVG(EXTRACT(EPOCH FROM ("CompletedAt" - "StartedAt"))) FILTER (WHERE "Status" = 'Done') as "AvgDurationSec"
FROM "Commands"
WHERE "CreatedAt" > NOW() - INTERVAL '24 hours'
GROUP BY "Partition";
```

### Проверка LISTEN-подписки

```sql
SELECT * FROM pg_listening_channels();
-- Должен вернуть: new_command
```

### Мониторинг активных процессов Windows

```sql
SELECT "Id", "Code", "Partition", "ProcessId", "StartedAt"
FROM "Commands"
WHERE "Status" = 'in_progress'
  AND "ProcessId" IS NOT NULL;

-- Затем проверить в Windows:
-- tasklist /FI "PID eq <ProcessId>"
```

---

## План реализации для ИИ-агента

### Этап 1: Изменение схемы базы данных

**Задача**: Добавить поля `Partition`, `Priority`, `StartedAt`, `ProcessId` в таблицу `Commands`

**Файлы**:
- `TelegramBot.Data/Sql/Commands.cs` — SQL-запросы для миграции
- `TelegramBot.Core/Models/Command.cs` — обновить модель

**SQL миграция**:
```sql
ALTER TABLE "Commands" 
    ADD COLUMN "Partition" VARCHAR(50) NOT NULL,
    ADD COLUMN "Priority" INT NOT NULL DEFAULT 0,
    ADD COLUMN "StartedAt" TIMESTAMPTZ,
    ADD COLUMN "ProcessId" INT;

CREATE INDEX IF NOT EXISTS "IX_Commands_Status_Priority" 
    ON "Commands" ("Status", "Priority" DESC, "CreatedAt" ASC);

CREATE INDEX IF NOT EXISTS "IX_Commands_Partition_Status" 
    ON "Commands" ("Partition", "Status");
```

---

### Этап 2: Обновление моделей и DTO

**Задача**: Добавить свойства Partition и Priority в модели

**Файлы**:
- `TelegramBot.Core/Models/Command.cs`
- `TelegramBot.Core/DTOs/CommandDto.cs`

**Изменения**:
```csharp
public class Command
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public string Code { get; set; } = null!;
    public string Status { get; set; } = "pending";
    public string Partition { get; set; } = null!;
    public int Priority { get; set; } = 0;
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public int? ProcessId { get; set; }
}
```

---

### Этап 3: Реализация ProcessPool

**Задача**: Создать класс управления пулом процессов с лимитами на партиции

**Файл**: `TelegramBot.Worker/Services/ProcessPool.cs` (новый)

**Требования**:
- Глобальный лимит одновременных процессов (N)
- Лимит процессов на партицию
- Thread-safe реализация
- Методы: `WaitForSlotAsync(partition)`, `ReleaseSlot(partition)`
- Поддержка graceful shutdown

---

### Этап 4: Реализация PartitionManager

**Задача**: Создать класс распределения команд по партициям

**Файл**: `TelegramBot.Worker/Services/PartitionManager.cs` (новый)

**Требования**:
- Маппинг CommandCode → Partition
- Конфигурация через appsettings.json
- Методы: `GetPartition(code)`, `GetPartitionConfig(partition)`

---

### Этап 5: Обновление PostgresDataService

**Задача**: Обновить методы работы с командами

**Файл**: `TelegramBot.Data/PostgresDataService.cs`

**Методы для обновления**:
- `CreateSessionWithCommandsAsync()` — добавить параметры partition, priority
- `GetPendingCommandsAsync()` — ORDER BY Priority DESC, CreatedAt ASC
- `UpdateCommandStatusAsync()` — добавить StartedAt, ProcessId, ErrorMessage

**Файл**: `TelegramBot.Data/Sql/Commands.cs`
- Обновить SQL-запросы в соответствии с новой схемой

---

### Этап 6: Рефакторинг CommandExecutionService

**Задача**: Полная переработка службы выполнения с поддержкой партиций и пула

**Файл**: `TelegramBot.Worker/Services/CommandExecutionService.cs`

**Ключевые изменения**:
1. Внедрение зависимостей: `ProcessPool`, `PartitionManager`
2. Изменение цикла обработки — распределение по партициям
3. Запуск `ProcessPartitionAsync()` для каждой партиции
4. Реализация `ExecuteProcessAsync()` с `Process.WaitForExitAsync()`
5. Обработка таймаутов и убийство процессов
6. Graceful shutdown — ожидание завершения активных процессов

---

### Этап 7: Обновление конфигурации

**Задача**: Добавить секцию Worker в appsettings.json

**Файл**: `TelegramBot.Worker/appsettings.json`

**Содержание**:
- MaxConcurrentProcesses (общий лимит N)
- Конфигурация каждой партиции (MaxConcurrent, Priority, ExecutablePath, Timeout)
- Таймауты и настройки переподключения

---

### Этап 8: Обновление Server (создание команд)

**Задача**: Добавить partition и priority при создании команд

**Файл**: `TelegramBot.Server/Services/Application/SlashCommandService.cs`

**Изменения**:
- Определение партиции на основе типа команды
- Определение приоритета на основе роли пользователя (админ = высокий приоритет)
- Передача параметров в `CreateSessionWithCommandsAsync()`

---

### Этап 9: Тестирование и валидация

**Чек-лист**:
- [ ] `dotnet build TelegramBot.slnx` — успешная сборка
- [ ] Worker запускается без ошибок
- [ ] Команды распределяются по партициям
- [ ] Лимит одновременных процессов соблюдается
- [ ] Высокоприоритетные команды выполняются первыми
- [ ] Процесс Windows корректно ожидает завершения
- [ ] Таймауты работают, процессы убиваются при превышении
- [ ] Graceful shutdown дожидается завершения процессов
- [ ] Ошибки логируются с полным контекстом

---

## Критерии приёмки

1. **Партиции**: Команды разных типов (Revit/Navisworks/AI) не блокируют друг друга
2. **Пул процессов**: Не более N процессов выполняются одновременно
3. **Приоритеты**: Команды с высоким приоритетом выполняются первыми внутри очереди
4. **Ожидание процесса**: Worker ждёт завершения процесса Windows перед следующей командой
5. **Таймауты**: Процессы, выполняющиеся дольше настроенного времени, принудительно завершаются
6. **Надёжность**: При падении Worker возобновляет работу с момента переподключения
7. **Мониторинг**: SQL-запросы диагностики показывают актуальное состояние очередей и процессов
