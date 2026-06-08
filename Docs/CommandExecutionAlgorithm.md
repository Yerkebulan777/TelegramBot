# Алгоритм выполнения команд (Sequence Diagram)

> Текстовое представление диаграммы `CommandExecutionAlgorithm.puml`
> Полная спецификация: [execution-algorithm.md](execution-algorithm.md)

---

## Участники

| Участник | Описание |
|----------|----------|
| **User** | Пользователь в Telegram |
| **Server** | `TelegramBotHostedService` — точка входа, polling |
| **App** | `CommandAppService` + `SlashCommandService` — логика команд |
| **DB** | PostgreSQL (очередь + LISTEN/NOTIFY) |
| **Worker** | `CommandExecutionService` — фоновое выполнение |
| **Process** | Внешний процесс (Revit / Navisworks / Python) |

---

## 1. Создание задачи (User → Server → DB)

```
User                          Server                    App                         DB
 │                              │                        │                           │
 │ /export → команды → Confirm  │                        │                           │
 │─────────────────────────────>│                        │                           │
 │                              │ ConfirmFileSelectionAsync()                       │
 │                              │───────────────────────>│                           │
 │                              │                        │ CollectRvtFiles()         │
 │                              │                        │─── (сбор .rvt файлов) ───>│
 │                              │                        │                           │
 │                              │                        │ CreateSessionWithCommandsAsync()
 │                              │                        │──────────────────────────>│
 │                              │                        │     ┌─────────────────────┤
 │                              │                        │     │ Batch INSERT:       │
 │                              │                        │     │ Session + Commands  │
 │                              │                        │     │ Status = 'pending'  │
 ││                              │     │ Priority из CommandPriorityMap │
 │                              │                        │     └─────────────────────┤
 │                              │                        │<──────────────────────────│ sessionId
 │                              │                        │                           │
 │                              │                        │ NotifyNewCommandsAsync()  │
 │                              │                        │──────────────────────────>│
 │                              │                        │     ┌─────────────────────┤
 │                              │                        │     │ NOTIFY new_command  │
 │                              │                        │     └─────────────────────┤
```

---

## 2. Worker просыпается

```
DB                          Worker
 │                            │
 │ NOTIFY new_command         │
 │═══════════════════════════>│  ┌──────────────────────────────────┐
 │                            │  │ conn.WaitAsync()                 │
 │                            │  │ — просыпается мгновенно         │
 │                            │  └──────────────────────────────────┘
 │                            │ ProcessBatchAsync()
 │                            │─── (внутренняя обработка) ─────────>│
```

---

## 3. Захват команд (Worker → DB)

```
Worker                                              DB
 │                                                    │
 │ ClaimPendingCommandsAsync(limit=50)                │
 │───────────────────────────────────────────────────>│
 │                  ┌─────────────────────────────────┤
 │                  │ WITH selected AS (               │
 │                  │   SELECT ... FROM Commands c     │
 │                  │   JOIN Sessions s ...            │
 │                  │   WHERE c.Status = 'pending'     │
 │                  │   ORDER BY Priority DESC,        │
 │                  │            CreatedAt ASC         │
 │                  │   LIMIT 50                       │
 │                  │   FOR UPDATE SKIP LOCKED         │
 │                  │ )                                │
 │                  │ UPDATE Commands c                │
 │                  │ SET Status = 'processing',       │
 │                  │     Lease = @LeaseExpiry,        │
 │                  │     StartedAt = NOW()            │
 │                  │ FROM selected                    │
 │                  │ WHERE c.CommandId = selected.Id  │
 │                  │ RETURNING ...;                   │
 │                  └─────────────────────────────────┤
 │<───────────────────────────────────────────────────│ List<PendingCommand>
```

---

## 4. Priority-based партиции

```
Worker
 │
 │ ProcessWithPoolAsync(cmd)
 │─── (определение партиции) ──────────────────────────>
 │
 │   Priority ≤ 1  →  Critical →  3 слота (SemaphoreSlim)
 │   Priority ≤ 2  →  High     →  5 слотов (SemaphoreSlim)
 │   Priority ≤ 3  →  Medium   →  3 слота (SemaphoreSlim)
 │   Priority ≤ 4  →  Low      →  1 слот  (SemaphoreSlim)
 │   Priority ≤ 5  →  Lowest   →  1 слот  (SemaphoreSlim)
 │   (Чем меньше Priority, тем выше приоритет)
 │
 │   await pool.WaitAsync(ct)
 │   — ждёт свободный слот в своей партиции
 │
 │   ┌───┐  ┌───┐  ┌───┐                    Critical (≤1)
 │   │ P │  │ P │  │ P │
 │   └───┘  └───┘  └───┘
 │   ┌───┐  ┌───┐  ┌───┐  ┌───┐  ┌───┐      High (≤2)
 │   │ P │  │ P │  │ P │  │ P │  │ P │
 │   └───┘  └───┘  └───┘  └───┘  └───┘
 │   ┌───┐  ┌───┐  ┌───┐                    Medium (≤3)
 │   │ P │  │ P │  │ P │
 │   └───┘  └───┘  └───┘
 │   ┌───┐                                   Low (≤4)
 │   │ P │
 │   └───┘
 │   ┌───┐                                   Lowest (≤5)
 │   │ P │
 │   └───┘
```

---

## 5. Валидация FilePath

```
Worker
 │
 │ ValidateFilePath()
 │─── (проверки) ─────────────────────────────────────>
 │
 │   1. Path.GetFullPath() — защита от path traversal
 │   2. File.Exists()      — файл существует?
 │   3. AllowedExtensions  — расширение разрешено?
 │
 ├── Если ошибка ──> DB: UpdateCommandStatus(Failed)
 │                   └── Команда сразу получает Failed
 │
 └── Если OK ──> продолжаем
```

---

## 6. Запуск процесса

```
Worker                                              DB                    Process
 │                                                    │                       │
 │ CreateProcessStartInfo()                            │                       │
 │─── (подготовка) ──────────────────────────────────>│                       │
 │   ExecutablePath: "Revit.exe"                      │                       │
 │   Arguments: "/command PDF \"file.rvt\""            │                       │
 │   WorkingDirectory: из конфига                      │                       │
 │   RedirectStandardOutput/Error = true               │                       │
 │                                                    │                       │
 │ process.Start()                                     │                       │
 │─────────────────────────────────────────────────────────────────────────>│
 │                                                    │                       │
 │ UpdateCommandStatus(Processing, ProcessId=PID)      │                       │
 │───────────────────────────────────────────────────>│                       │
 │                                                    │                       │
 │ BeginOutputReadLine() + BeginErrorReadLine()        │                       │
 │─── (асинхронное чтение stdout/stderr) ────────────>│                       │
 │   ┌──────────────────────────────────────────┐     │                       │
 │   │ Асинхронное чтение stdout/stderr         │     │                       │
 │   │ в StringBuilder через событийные         │     │                       │
 │   │ хендлеры — предотвращает deadlock       │     │                       │
 │   │ при заполнении буфера 64KB              │     │                       │
 │   └──────────────────────────────────────────┘     │                       │
 │                                                    │                       │
 │                                                    │  Выполнение           │
 │                                                    │  (часы)               │
 │<──────────────────────────────────────────────────────────────────────────│
```

---

## 7. Завершение процесса

```
Worker                                                     DB
 │                                                           │
 │ process.WaitForExit(timeoutMs)                            │
 │─── (ожидание с таймаутом) ──────────────────────────────>│
 │                                                           │
 ├── Timeout ───────────────────────────────────────────────┤
 │   process.Kill(true)                                      │
 │   UpdateCommandStatus(Failed, "Timeout...")               │
 │   ┌────────────────────────────────────┐                  │
 │   │ Принудительно завершаем процесс    │                  │
 │   │ и всё дерево потомков              │                  │
 │   └────────────────────────────────────┘                  │
 │                                                           │
 ├── ExitCode == 0 (успех) ────────────────────────────────┤
 │   UpdateCommandStatus(Done)                               │
 │   ┌────────────────────────────────────┐                  │
 │   │ Команда выполнена успешно!         │                  │
 │   └────────────────────────────────────┘                  │
 │   NotifyCommandCompletedAsync()                           │
 │   ── NOTIFY command_completed ──────────────────────────>│
 │                                                           │
 ├── ExitCode != 0 (ошибка) ───────────────────────────────┤
 │                                                           │
 │   ScheduleRetryAsync()                                    │
 │   Retry #1:  +60s      (base * 2^0)                      │
 │   Retry #2:  +120s     (base * 2^1)                      │
 │   Retry #3:  +240s     (base * 2^2)                      │
 │   Retry #4:  +480s     (base * 2^3)                      │
 │   Retry #5:  +960s     (base * 2^4)                      │
 │                MaxRetries = 5                             │
 │                                                           │
 │   UPDATE Status='pending', NextRetryAt=...                │
 │   NOTIFY new_command (будим воркер)                       │
 │─────────────────────────────────────────────────────────>│
 │                                                           │
 │   └── Если все retry исчерпаны ─────────────────────────┤
 │       UpdateCommandStatus(Failed, errorMessage)           │
 │       NotifyCommandCompletedAsync()                      │
 │       ── NOTIFY command_completed ──────────────────────>│
```

---

## 8. Уведомление пользователя

```
DB                        Server                        User
 │                          │                             │
 │ NOTIFY command_completed │                             │
 │══════════════════════════>│                             │
 │                          │ Parse payload               │
 │                          │ (UserId|CmdId|CmdText       │
 │                          │  |Status|Error)             │
 │                          │                             │
 │                          │ Send Telegram message       │
 │                          │────────────────────────────>│
 │                          │  ┌──────────────────────┐   │
 │                          │  │ ✅ *PDF* завершена   │   │
 │                          │  │ или                  │   │
 │                          │  │ ❌ *PDF* — ошибка    │   │
 │                          │  │ MarkdownV2 экранир.  │   │
 │                          │  └──────────────────────┘   │
```

---

## 9. Cleanup (Worker)

```
Worker
 │
 │ _activeProcesses.TryRemove(commandId)
 │ pool.Release()
 │
 │   ┌──────────────────────────────────────┐
 │   │ Освобождаем ресурсы и слот партиции  │
 │   └──────────────────────────────────────┘
```

---

## Фоновые задачи (каждые 60 секунд)

```
Worker                                              DB
 │                                                    │
 │═══════════════════ BACKGROUND CLEANUP ═════════════╡
 │                                                    │
 │ ReleaseExpiredLeasesAsync()                        │
 │───────────────────────────────────────────────────>│
 │   UPDATE Commands                                  │
 │   SET Status='pending', Lease=NULL, ...            │
 │   WHERE Status='processing' AND Lease < @Now       │
 │                                                    │
 │   ┌──────────────────────────────────────┐         │
 │   │ Защита от сбоев воркеров: если      │         │
 │   │ Worker упал — команды возвращаются  │         │
 │   │ в очередь. Используется             │         │
 │   │ pg_try_advisory_lock(1234567) для   │         │
 │   │ предотвращения дублирования         │         │
 │   └──────────────────────────────────────┘         │
 │                                                    │
 │ ReleaseTimeoutCommandsAsync()                      │
 │───────────────────────────────────────────────────>│
 │   UPDATE Commands                                  │
 │   SET Status='pending', ...                        │
 │   WHERE Status='processing'                        │
 │     AND StartedAt < NOW() - INTERVAL               │
 │                                                    │
```

---

## Отмена команды пользователем

```
User                     Server                    DB                      Worker              Process
 │                         │                        │                        │                    │
 │ /status → кнопка        │                        │                        │                    │
 │ "⛔ Отменить"            │                        │                        │                    │
 │────────────────────────>│                        │                        │                    │
 │                         │ DeleteCommandAsync()   │                        │                    │
 │                         │───────────────────────>│                        │                    │
 │                         │  ┌─────────────────────┤                        │                    │
 │                         │  │ UPDATE Commands     │                        │                    │
 │                         │  │ SET Status='Deleted' │                        │                    │
 │                         │  │ WHERE CommandId=@Id │                        │                    │
 │                         │  └─────────────────────┤                        │                    │
 │                         │                        │                        │                    │
 │                         │                        │                        │ Если процесс уже   │
 │                         │                        │                        │ выполняется, он    │
 │                         │                        │                        │ завершится штатно  │
 │                         │                        │                        │                    │
 │                         │                        │  ┌─────────────────────┤                    │
 │                         │                        │  │ Проверка            │                    │
 │                         │                        │  │ UpdateStatus        │                    │
 │                         │                        │  │ не перезаписывает   │                    │
 │                         │                        │  │ Deleted             │                    │
 │                         │                        │  └─────────────────────┤                    │
```

---

## Полный жизненный цикл статусов команды

```
                         ┌──────────┐
                         │  pending │ ◄────── Создана (INSERT в БД)
                         └────┬─────┘
                              │
                              ▼
                      ┌───────────────┐
                      │  processing   │ ◄────── Захвачена Worker-ом
                      └───────┬───────┘
                              │
              ┌───────────┬───┴───────────┐
              │           │               │
              ▼           ▼               ▼
         ┌────────┐ ┌────────┐      ┌──────────┐
         │  Done  │ │ Failed │      │ Deleted  │
         └────────┘ └────────┘      └──────────┘
              │           │
              │           ▼
              │     ┌──────────────┐
              │     │  (retry)     │
              │     │  → pending   │ ◄────── Экспоненциальная задержка
              │     └──────────────┘
              │
              ▼
     ┌──────────────────┐
     │ NOTIFY           │
     │ command_completed│ ──────► Server → Telegram-уведомление
     └──────────────────┘
```

---

## Priority-based партиции (схема)

```
Очередь команд (Order by Priority ASC, CreatedAt ASC)
┌──────────────────────────────────────────────────┐
│ [P=1] → [P=1] → [P=2] → [P=3] → [P=4] → [P=5]  │
│ (Чем меньше Priority, тем выше приоритет)        │
└──────────────────────────────────────────────────┘
                        │
                        ▼
         ┌──────────────────────────────┐
         │   Маршрутизация по порогам   │
         │ (ищем первый threshold, где  │
         │  Priority <= threshold)      │
         └──────────────────────────────┘

  P <= 1 ──────► Critical ──► SemaphoreSlim(3)
  P <= 2 ──────► High     ──► SemaphoreSlim(5)
  P <= 3 ──────► Medium   ──► SemaphoreSlim(3)
  P <= 4 ──────► Low      ──► SemaphoreSlim(1)
  P <= 5 ──────► Lowest   ──► SemaphoreSlim(1)
```

---

## Lease-механизм (защита от сбоев)

```
Worker захватывает команду:
  Lease = ProcessTimeoutSeconds + 5 мин (в секундах Unix)

При краше Worker-а:
  ┌─────────────────────────────────────────────┐
  │ Другой Worker через 60 секунд:              │
  │                                             │
  │ UPDATE Commands                             │
  │ SET Status='pending',                       │
  │     Lease=NULL,                             │
  │     ErrorMessage='Lease expired: ...'       │
  │ WHERE Status='processing'                   │
  │   AND Lease < @CurrentTimeSec               │
  └─────────────────────────────────────────────┘
```
