# Как работает Worker

Worker — это отдельный .NET-процесс (`TelegramBot.Worker`), который забирает BIM/AI-задачи из общей
PostgreSQL-БД и запускает их. Server кладёт команды в таблицу `Commands`, Worker их подхватывает, исполняет
и пишет результат обратно. Windows-only (использует реестр и P/Invoke).

## Архитектура в одном абзаце

Worker — событийный orchestrator поверх PostgreSQL-очереди. `LISTEN/NOTIFY` будит drain-loop, который
просит БД отдать следующие команды до общего лимита параллельности. Порядок очереди, partition-gating и
защита от гонок живут в одном SQL claim-запросе. `ProcessRunner` запускает
внешние BIM-процессы, обмениваясь с ними JSON-файлами в `TaskDirectory`. Результат классифицируется через
`ErrorClassifier` (permanent → Failed, transient → retry с экспонентой), а `SessionCompletionTracker` пачкой
уведомляет Server о завершении сессии через durable `NotificationOutbox`.

---

## Главный цикл: `CommandExecutionService`

`ExecuteAsync` запускает три параллельных потока:

1. **Listener loop** — слушает PostgreSQL-канал `new_tasks` через `LISTEN`. Когда Server делает
   `pg_notify('new_tasks', correlationId)`, Worker мгновенно просыпается.
2. **Cleanup loop** — каждые 5 минут освобождает «протухшие» Lease. Если Worker упал с активной командой,
   lease истечёт через `ProcessTimeoutMinutes + 5 мин`, и другой воркер заберёт команду.
3. **Health loop** — каждые 30 сек проверяет здоровье активных процессов (`ProcessHealthHelper`) и
   автоматически закрывает модальные окна Revit/Navisworks (`DialogDismisser`).

**Fallback polling** каждые 5 минут на случай потери `NOTIFY` (если связь с PostgreSQL восстановилась без
активного соединения).

---

## Drain loop — главная фича v1.7

`DrainPendingCommandsAsync` устраняет head-of-line blocking:

```csharp
while (есть свободные слоты):
    claimed = ClaimPendingCommandsAsync(min(5, доступные_слоты))
    if claimed пустой: выход
    for each cmd in claimed:
        запустить ProcessWithPoolAsync(cmd)  ← не ждём!
    продолжить цикл, claim'нуть ещё
```

Батч из 5 команд запускается **параллельно как фоновые `Task`**, drain сразу пытается взять следующую
пачку. До v1.7 одна долгая команда (3 часа Revit-экспорта) блокировала обработку остальных 4 из батча.
Сейчас команды стартуют сразу (если хватает слотов), а после завершения каждой `ContinueWith` будит
drain снова.

`_drainGate` (SemaphoreSlim) защищает от повторного входа drain, `_runningTasks` (HashSet под lock) — для
корректного ожидания при shutdown.

## LaunchStaggerGate — защита от коллизии CEF-порта Revit

`ProcessRunner._launchGate` (`SemaphoreSlim(1, 1)`) сериализует момент `Process.Start()` для **всех**
внешних процессов. Это предотвращает коллизию devtools-порта встроенного Chromium (CEF) при параллельном
старте нескольких `Revit.exe`, которая приводила к `ACCESS_VIOLATION (0xC0000005)` сразу после старта.

```csharp
await _launchGate.WaitAsync(ct);
try
{
    process.Start();
    _activeProcesses[cmd.CommandId] = process;  // регистрация ДО задержки!
    if (_workerOptions.LaunchStaggerSeconds > 0)
    {
        await Task.Delay(TimeSpan.FromSeconds(_workerOptions.LaunchStaggerSeconds), CancellationToken.None);
    }
}
finally
{
    _ = _launchGate.Release();
}
```

**Важные детали:**
- Процесс регистрируется в `_activeProcesses` **до** stagger-задержки, чтобы health-check и shutdown-kill
  (`PerformGracefulShutdownAsync`) видели процесс сразу.
- `Task.Delay` использует `CancellationToken.None`, а не `ct` — gate всегда освобождается даже при shutdown.
  Иначе следующий старт зависнет на disposed/cancelled semaphore.
- Gate применяется ко всем типам команд, без спец-кейсов по `CommandText` — лишняя пауза для не-Revit
  процессов не критична.
- `LaunchStaggerSeconds` (default `5`) настраивается через `Worker:LaunchStaggerSeconds` в `appsettings.json`.
  `0` отключает gate (эквивалент старому поведению).

---

## DB scheduler и partition-gating

Worker не маршрутизирует команды сам. Он считает свободные слоты и вызывает
`CommandDataService.ClaimPendingCommandsAsync(limit, leaseTimeoutMinutes)`. PostgreSQL выбирает очередь
атомарно внутри claim-запроса: команды одного RVT/NWC-файла идут последовательно, разные файлы выполняются
параллельно до общего лимита worker-а. Канонический алгоритм: [ExecutionAlgorithm.md](ExecutionAlgorithm.md#захват-команд-атомарный-db-scheduler).

---

## Лимит параллельности

Worker использует один `SemaphoreSlim` внутри `CommandExecutionService`. `Worker:Partitions` оставлен для
обратной совместимости с конфигом; ключи словаря не используются для routing, итоговый лимит равен сумме
значений. Логические партиции очереди хранятся в `Commands.Partition` и обслуживаются БД внутри claim-запроса.

---

## Выполнение одной команды: `ProcessRunner.RunAsync`

Полный цикл:

```
1. attemptToken = GUID без дефисов
   (защита от race между retry одной команды и атак с предсказуемыми именами файлов)

2. CommandPreparer.PrepareAsync:
   - проверить что CommandText известен в Worker:Commands
   - ValidateFilePath: path traversal, reparse point, расширение, root containment
   - ResolveExecutablePathAsync:
       * PDF/DWG/IFC/BIMDOC/NWC → Revit через BimLib (OLE-stream + реестр)
       * CLASHREP → Navisworks через BimLib
       * остальные → configured path из appsettings
   - клонировать CommandConfig (не мутировать shared IOptions!)
   - **Если PrepareAsync вернул null** — команда уже помечена Failed в БД (например, неизвестный
     CommandText). `OnCommandCompletedAsync` вызывается, выполнение прерывается.

3. StartProcessAsync (объединяет шаги 3–9):

   3a. CommandPreparer.CreateTaskFile:
       - atomic write: .tmp → File.Move(overwrite: true)
       - путь: %USERPROFILE%\Documents\TelegramBot\TaskDirectory\task_{Id}_{token}.json
       - **Если CreateTaskFile вернул false** — бросается IOException (fail-fast,
         ErrorClassifier классифицирует как permanent failure без retry; проблема
         инфраструктурная — диск/права/антивирус)

   3b. Захват _launchGate (SemaphoreSlim 1/1)

   3c. Process.Start с ArgumentsTemplate
       (подстановка {CommandText}/{FilePath}/{CommandId}/{TaskFilePath}/{ResultFilePath};
        для Revit AddIn args[2] всегда WORKER (RevitDispatcherCommand), реальная команда в TaskFile.commandText)
       - **Process.Start() неудачен** → catch Win32Exception/InvalidOperationException → process.Dispose()
         → throw; outer catch передаёт в HandleFailureAsync

   3d. Регистрация process в _activeProcesses (ДО stagger-задержки!)

   3e. Task.Delay(LaunchStaggerSeconds) — CEF биндит порт
       (CancellationToken.None: gate освобождается даже при shutdown)

   3f. Освобождение _launchGate

   3g. UpdateCommandStatus(Processing) + NotifySessionStartedAsync
       → pg_notify 'session_started' → Server шлёт "⚙️ Задание запущено"

4. WaitAndHandleResultAsync:

   4a. **Захват stdout/stderr** — OutputDataReceived/ErrorDataReceived с double-checked lock
       и лимитом 64KB на каждый канал. При превышении — флаг `truncated`. Обработчики
       отписываются в `finally` для предотвращения утечек.

   4b. WaitForExitAsync с timeout (default 180 мин). Timeout реализован через
       `CancellationTokenSource.CreateLinkedTokenSource` — при срабатывании `OperationCanceledException`
       ловится в outer catch как таймаут (не shutdown).

   4c. Прочитать result_{Id}_{token}.json от плагина (ResultFileReadStatus: NotFound/Valid/Invalid):
       - Valid + status="done" → Status=Done
       - Valid + status="failed" → HandleFailureAsync (с `errorMessage` из файла, `errorDetails` в Debug-лог)
       - Valid + status="cancelled" → **прямой** Status=Failed (минуя ErrorClassifier и HandleFailureAsync),
         permanent failure без retry
       - Invalid/битый JSON → rename в .bad + HandleFailureAsync
       - NotFound → fallback на exit code: 0 = Done (с громким warning о нарушении контракта AddIn),
         иначе HandleFailureAsync с FormatExitCode (decimal + hex + NTSTATUS имя краша, например ACCESS_VIOLATION)

5. HandleFailureAsync:
   - Вызов: `ErrorClassifier.IsPermanentFailure(message, exitCode, PermanentFailureExitCodes, ex)`
     (4 параметра: текст ошибки, exit code, список permanent-кодов, исключение)
   - InvalidFileError (паттерны EN+RU, exit code, тип исключения)
     → сразу Failed, без retry
   - ProcessCrashError + retry < MaxRetries
     → ScheduleRetry с экспоненциальной задержкой (60s → 120s → 240s → 480s → 960s)
   - retry исчерпаны → Failed

6. finally: CleanupTempFiles
   (удаляет task и result JSON этой попытки — per-attempt)
   + TryRemove из _activeProcesses + process.Dispose() (только если не shutdown)
```

### Стриминг stdout/stderr

`OutputDataReceived`/`ErrorDataReceived` с double-checked lock (`lock(builder)`) и лимитом 64KB (`MaxOutputChars`)
на каждый канал. При превышении лимита ставится флаг `truncated`, данные дописываются сколько влезает.
В лог пишется `[TRUNCATED: 64KB limit reached]`. Обработчики отписываются в `finally` для предотвращения
утечек. Лог-вывод дополнительно обрезается до 4KB через `TruncateOutput()`. Это предотвращает OOM, если
плагин начнёт лить бесконечный вывод.

### stdout/stderr в зависимости от типа команды

| Тип | stdout/stderr | Result mechanism |
|-----|---------------|------------------|
| `PDF`, `DWG`, `IFC`, `BIMDOC` | **Нет** — GUI приложение (Revit) | TaskFile + ResultFile JSON |
| `NWC` | **Нет** — GUI приложение (Revit) | TaskFile + ResultFile JSON |
| `CLASHREP` | Обычно есть (CLI wrapper) | TaskFile + ResultFile, иначе fallback на exit code |
| `AUTORES` | **Да** — консольный python скрипт | TaskFile + ResultFile, иначе fallback на exit code |

---

## `SessionCompletionTracker` — in-memory счётчик

`ConcurrentDictionary<int, int>` где ключ — `SessionId`, значение — сколько команд осталось обработать.

```csharp
// При claim'е батча
TrackClaimedCommands(claimed):
    AddOrUpdate(_sessionRemaining, +N) по GroupBy(SessionId)

// При завершении каждой команды
OnCommandCompletedAsync(cmd):
    AddOrUpdate(_sessionRemaining, -1)
    if (remaining == 0):
        CountPendingProcessingBySessionAsync()  // DB confirmation
        → NotifySessionCompletedOnceAsync
```

**Зачем in-memory счётчик:** позволяет не делать SQL-запрос после каждой команды для проверки «а все ли
команды сессии завершены?». Счётчик обнуляется ровно один раз — идемпотентность через
`Sessions.CompletionNotified` + INSERT в `NotificationOutbox` + `pg_notify('command_completed')`.

Durable outbox нужен на случай, если Server упадёт между `pg_notify` и реальной отправкой в Telegram — после
restart `NotificationSenderService` подхватит все неотправленные события.

---

## Graceful shutdown

При получении `stoppingToken`:

1. `_shutdownCts.CancelAsync()` — останавливает background loops
2. **Параллельный** `Kill(entireProcessTree: true)` всех активных процессов в общем shutdown-бюджете
   **30 сек** (per-process 10 сек через `ProcessKillHelper.KillAsync`)
3. Wait for cleanup + health tasks (15 сек каждый)
4. Wait for running tasks (15 сек)
5. Dispose семафоров

Важно: при shutdown процессы **не удаляются** из `_activeProcesses` сразу — `LogActiveProcessesOnShutdownAsync`
должен увидеть все активные процессы до остановки.

---

## BimLib — Windows-only инфраструктура

Подмножество внутри Worker (`TelegramBot.Worker/BimLib/`) для интеграции с Revit/Navisworks:

| Сервис | Назначение |
|--------|-----------|
| `RevitVersionDetector` | Парсит OLE-stream `BasicFileInfo` из `.rvt`/`.rfa` через **OpenMcdf** (без запуска Revit), извлекает год (`Format: YYYY`). Поддерживает 2017–2026 |
| `RevitPathResolver` | Ищет `Revit.exe` в реестре Windows: `HKLM\SOFTWARE\Autodesk\Revit\{version}` (fallback `\Revit{version}` и `WOW6432Node`) |
| `NavisworksPathResolver` | То же для `FileConvert.exe` / `Roamer.exe` / `Navisworks.exe` |
| `DialogDismisser` | Находит модальные окна Revit через `EnumWindows` (класс `#32770`) и кликает «OK»/«Close»/«Cancel» по тексту. Исключает информационные диалоги через `ExclusionDialogTitles` |
| `ProcessHealthHelper` | `CheckHealth(Process, logger, context)`: `Healthy` / `NotResponding` / `Error` по `IsResponding` + memory sampling |

Логи BimLib идут в отдельный файл `Worker/BimLib/log-{date}.txt` через `BimLibLogFilter` (фильтр по
`SourceContext` начинающемуся на `TelegramBot.Worker.BimLib`).

Помечено `[SupportedOSPlatform("windows")]` — Windows-only. Worker тоже помечен, плюс
`RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` runtime check.

---

## Контракт с BIM-плагинами

> ⚠️ **Эталон** живёт в `RevitBIMFusion/Docs/BimPluginContract.md` + JSON-схемы `TaskFile.schema.json` /
> `ResultFile.schema.json`. Наш `Docs/BimPluginContract.md` — worker-side отражение. При изменениях в
> `TaskFile` / `ResultFile` / `ArgumentsTemplate` / `CreateTaskFile` / `TryReadResultFile` — обновлять
> эталон + плагин + код **синхронно**.

### TaskFile (Worker → плагин)

```json
{
  "commandId": 42,
  "commandText": "PDF",
  "filePath": "B:\\project.rvt",
  "resultFilePath": "C:\\...\\TaskDirectory\\result_42_6f1c2b3a.json",
  "options": null
}
```

- `filePath` — НЕ передаётся в CLI args (только в TaskFile), чтобы избежать двойной подстановки
- `options` — `JsonElement?`, closed whitelist (сейчас только `continueOnError` для PDF/DWG)

### ResultFile (плагин → Worker)

```json
{
  "status": "done",
  "errorMessage": null,
  "errorDetails": null,
  "outputFiles": "B:\\project.pdf"
}
```

- `status` — enum `ResultStatus { Done, Failed, Cancelled }`, camelCase через `JsonStringEnumConverter`
- `errorMessage` — короткое сообщение (при `failed`/`cancelled`)
- `errorDetails` — полный stack trace (для неожиданных исключений)
- `outputFiles` — `string?` (путь к выходному файлу при `done`; несмотря на множественное число в имени — **одна строка**, не массив, соответствует канону в `…\RevitBIMFusion\Docs\ResultFile.schema.json`)

### Без AddIn (broken flow)

```
Worker → Revit.exe открывается как GUI
       ↓
       Revit сидит, показывает пустой проект
       ↓
       3 часа → Worker kill'ит → timeout → Failed
```

`DialogDismisser` закрывает только известные модальные окна. Без Revit AddIn Revit не знает что делать с
`/command` и просто открывается GUI, игнорируя аргументы.

---

## Куда Worker скидывает TaskFile

Путь формируется в `TelegramBot.Core/Config/FileSystemOptions.cs` → `GetEffectiveTaskDirectory()` и
используется в `CommandPreparer.GetTaskFilePaths()`.

### Формула пути

```
<TaskDirectory> / task_{CommandId}_{AttemptToken}.json
<TaskDirectory> / result_{CommandId}_{AttemptToken}.json
```

Где `<TaskDirectory>` это:

1. **Если в `appsettings.json` задан `FileSystem:TaskDirectory`** → используется этот путь как есть.
2. **Иначе** (по умолчанию) — собирается через `Path.Combine`:

   ```csharp
   Path.Combine(
       Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),  // %USERPROFILE%
       "Documents",
       "TelegramBot",
       "TaskDirectory")
   ```

**Дефолтный путь (Windows):**

```
C:\Users\<USER>\Documents\TelegramBot\TaskDirectory\
```

### Конкретные имена файлов

Из `CommandPreparer.GetTaskFilePaths` (строки 33–38):

```csharp
var resultFilePath = Path.Combine(_taskDirectory, $"result_{commandId}_{attemptToken}.json");
var taskFilePath   = Path.Combine(_taskDirectory, $"task_{commandId}_{attemptToken}.json");
```

Где `attemptToken` — `Guid.NewGuid().ToString("N")` (32 hex символа без дефисов), генерируется в
`ProcessRunner.RunAsync` на каждую попытку (включая retry).

### Пример для команды №42 с токеном `6f1c2b3a4d5e6f708192a3b4c5d6e7f8`

```
C:\Users\y.zhumabayev\Documents\TelegramBot\TaskDirectory\task_42_6f1c2b3a4d5e6f708192a3b4c5d6e7f8.json
C:\Users\y.zhumabayev\Documents\TelegramBot\TaskDirectory\result_42_6f1c2b3a4d5e6f708192a3b4c5d6e7f8.json
```

### Override через `appsettings.json`

```json
{
  "FileSystem": {
    "RootPath": "B:\\",
    "TaskDirectory": "D:\\TelegramBot\\Worker\\TaskDirectory"
  }
}
```

Или через env var: `FileSystem__TaskDirectory=D:\TelegramBot\Worker\TaskDirectory`.

### Жизненный цикл файлов

| Файл | Создаётся | Удаляется |
|------|-----------|-----------|
| `task_{Id}_{token}.json` | Worker в `CommandPreparer.CreateTaskFile` (atomic write `.tmp` → `File.Move`) | В `finally` блоке `ProcessRunner.RunAsync` через `CommandPreparer.CleanupTempFiles` — **после** завершения процесса (timeout/fail/done) |
| `result_{Id}_{token}.json` | BIM-плагином по пути из `task.resultFilePath` | Worker в `TryReadResultFile` — **только после успешного парсинга** (иначе rename в `.bad` для диагностики) |
| `result_{Id}_{token}.json.bad` | Worker'ом при битом JSON / невалидном status | Никогда автоматически — остаётся для ручной диагностики |
| `task_{Id}_{token}.json.tmp` | Worker при atomic write | Сразу же через `File.Move(overwrite: true)` |

---

## TaskDirectory — папка обмена

`TaskDirectory` — это **часть контракта** Worker ↔ BIM-плагин, а не деталь реализации (см.
[Docs/BimPluginContract.md §Расположение файлов](BimPluginContract.md#расположение-файлов-taskdirectory) и
эталон `…\RevitBIMFusion\Docs\BimPluginContract.md` §TaskFile Location). Worker и AddIn **обязаны**
использовать одну и ту же директорию; подменять её на собственный `Path.GetTempPath()` недопустимо —
`%TEMP%` у Worker'а (Windows-сервис) и у AddIn'а (интерактивная сессия Revit) — это разные директории, и
стороны просто не найдут файлы друг друга.

**Дефолтный путь:** `%USERPROFILE%\Documents\TelegramBot\TaskDirectory\`

Override через `FileSystem:TaskDirectory` в `appsettings.json`. Worker создаёт папку автоматически на старте;
если создать не удалось — процесс падает с понятной ошибкой (`Worker/Program.cs` строки 132–145).

**Структура:**

```
%USERPROFILE%\Documents\TelegramBot\
├── Logs\
│   ├── Server\
│   └── Worker\
│       ├── BimLib\              # BIM-специфичные логи
│       └── log-{date}.txt       # Serilog-логи Worker
└── TaskDirectory\               # task_{Id}_{token}.json + result_{Id}_{token}.json
```

---

## DI registration

В `Worker/Program.cs` (порядок соответствует исходному коду):

```csharp
// Data-сервисы
_=services.AddSingleton<UserDataService>();
_=services.AddSingleton<CommandDataService>();
_=services.AddSingleton<SessionDataService>();
_=services.AddSingleton<MessageTrackingDataService>();
_=services.AddSingleton<DatabaseInitializerService>();

// WorkerOptions — валидация на старте
_=services.AddOptions<WorkerOptions>()
    .Bind(context.Configuration.GetSection(WorkerOptions.SectionName))
    .Validate(options => options.ProcessTimeoutMinutes > 0, ...)
    .Validate(options => options.MaxRetries >= 0, ...)
    .Validate(options => options.RetryDelayBaseSeconds > 0, ...)
    .Validate(options => options.FallbackPollingIntervalSeconds > 0, ...)
    .Validate(options => options.LaunchStaggerSeconds >= 0, ...)
    .Validate(options => options.Partitions.Count > 0, ...)
    .Validate(options => options.Partitions.All(p => p.Value > 0), ...)
    .Validate(options => options.Commands.Count > 0, ...)
    .Validate(options => options.Commands.All(c => !string.IsNullOrWhiteSpace(c.Value.ExecutablePath)), ...)
    .Validate(options => options.Commands.All(c => !string.IsNullOrWhiteSpace(c.Value.ArgumentsTemplate)), ...)
    .ValidateOnStart();

// Config-секции
_=services.Configure<BimIntegrationOptions>(context.Configuration.GetSection(BimIntegrationOptions.SectionName));
_=services.Configure<DialogDismisserOptions>(context.Configuration.GetSection(DialogDismisserOptions.SectionName));
_=services.Configure<FileSystemOptions>(context.Configuration.GetSection(FileSystemOptions.SectionName));

// BIM-интеграция (Revit + Navisworks)
_=services.AddSingleton<RevitVersionDetector>();
_=services.AddSingleton<NavisworksPathResolver>();   // Navisworks ДО RevitPathResolver
_=services.AddSingleton<RevitPathResolver>();
_=services.AddSingleton<DialogDismisser>();

// Компоненты выполнения
_=services.AddSingleton<SessionCompletionTracker>();
_=services.AddSingleton<CommandPreparer>();
_=services.AddSingleton<ProcessRunner>();

// Hosted services
_=services.AddHostedService<CommandExecutionService>();
_=services.AddHostedService<SessionCleanupService>();

// Health check HTTP-сервер (через фабрику, не прямая регистрация HealthCheckHostedService)
_=services.AddOptions<HealthCheckOptions>()
    .Bind(context.Configuration.GetSection(HealthCheckOptions.SectionName))
    .Validate(options => options.Port is >0 and <=65535, ...);
_=services.AddHostedService(sp =>
{
    var options = sp.GetRequiredService<IOptions<HealthCheckOptions>>();
    var logger = sp.GetRequiredService<ILogger<HealthCheckHostedService>>();
    return HealthCheckServiceFactory.Create(options, logger, connectionString);
});
```

---

## Health Check

Worker поднимает HTTP-сервер на порту **5001** (Server — на 5000). Дополнительные checks в
`AdditionalChecks`:

| Check | Описание | Unhealthy |
|-------|----------|-----------|
| `bimInstallRoot` | Проверяет существование `BimIntegration:RevitInstallRoot` | Директория не найдена |
| `activeProcesses` | Количество активных внешних процессов (информационно) | Всегда `healthy` |

Endpoints: `/health/live` (всегда 200), `/health/ready` (с DB-проверкой), `/health` (подробный JSON).

---

## `SessionCleanupService` — авто-архивация

Отдельный `BackgroundService`. Раз в `CleanupIntervalSeconds` soft-deleted сессий старше
`CompletedSessionRetentionDays` (default 30) без `pending`/`processing` команд + cascade soft-delete их
команд. `0` отключает.

Soft-delete only: `SET Status = 'Deleted'`, никогда `DELETE FROM`.

---

## Корреляция событий

Каждая команда и сессия имеют `CorrelationId` (GUID без дефисов), который проходит через весь pipeline:

```
Server.CreateSessionWithCommandsAsync
  → INSERT Sessions + Commands
  → pg_notify('new_tasks', correlationId)
    → Worker.ClaimPendingCommandsAsync (claim'ит по batch'у)
      → ProcessRunner.RunAsync
        → Process.Start + UpdateCommandStatus(Processing) + pg_notify('session_started', correlationId)
          → Server "⚙️ Задание запущено"
        → ResultFile от плагина → Status=Done/Failed
          → SessionCompletionTracker.OnCommandCompletedAsync
            → remaining == 0 → NotifySessionCompletedOnceAsync
              → CompletionNotified=TRUE + INSERT NotificationOutbox + pg_notify('command_completed')
                → Server читает outbox → Telegram summary
```

Используется в логах для сквозной трассировки конкретной задачи через все компоненты.
