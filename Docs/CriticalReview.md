# Critical Review: Критические замечания по решению TelegramBot

## Цель документа

Этот документ — результат аудита кодовой базы TelegramBot на предмет архитектурных,
логических и эксплуатационных проблем. **Зачем он нужен:**

1. **Зафиксировать найденные проблемы** — чтобы не забыть и не переоткрыть их после рефакторинга.
2. **Обосновать исправления** — каждая рекомендация подкреплена реальным сценарием ошибки,
   а не абстрактным «так не принято».
3. **Приоритизировать работу** — разделение на P1–P4 позволяет понимать, что горит,
   а что может подождать.
4. **Предотвратить регрессии** — зная, какие race conditions и узкие места были найдены,
   разработчик не повторит их в новом коде.
5. **Аудит для новых участников** — быстрый «рентген» проекта: где болит, что уже починили,
   что ещё можно улучшить.

---


## 🟠 P2 — Серьёзные проблемы

### 6. Нет durable completion notifications — безвозвратная потеря уведомлений

**Где:** `TelegramBot.Server/Services/Infrastructure/Telegram/CommandNotificationService.cs`

**Проблема:**
- Server только слушает `LISTEN command_completed;`.
- Если Server был перезапущен, потерял соединение, или не слушал канал в момент `NOTIFY` —
  уведомление о завершении сессии **теряется навсегда**.
- В отличие от Worker (у которого есть fallback polling для `new_tasks`), у Server **вообще нет
  replay/reconciliation механизма** для completion notification.
- Пользователь может никогда не получить сообщение «сессия завершена».

**Почему это проблема:** Для пользователя бот становится «чёрным ящиком»: он отправил 10 файлов
на экспорт, ждёт результата, но уведомления нет. Он не знает, завершились ли команды,
завис ли Worker, или просто бот не отвечает. Единственный способ проверить — `/status`,
но не все пользователи знают эту команду. Если Server перезапускался во время выполнения
сессии (плановый деплой), уведомление потеряно навсегда. Проблема усугубляется тем, что
Server может терять соединение с PostgreSQL на секунды — достаточные, чтобы пропустить NOTIFY.

**Рекомендация:**
- Использовать durable outbox:
  - Записывать completion event в таблицу (например, `NotificationOutbox`).
  - Sender читает неподтверждённые записи и отправляет.
  - После успешной отправки помечает как `Sent`.
- `NOTIFY` оставить как ускоритель (wake-up signal), но не как единственный источник событий.

---

### 7. LISTEN/NOTIFY gaps — команды могут простаивать до 5 минут

**Где:** `TelegramBot.Worker/Services/CommandExecutionService.cs:RunListenerLoopAsync()`

**Проблема:**
Последовательность:
1. `ProcessBatchAsync()` — claim команд.
2. `LISTEN new_tasks`.
3. `WaitAsync()` — ждать NOTIFY.

**Race:**
- Новая команда может быть вставлена в БД **после** `ProcessBatchAsync()`, но **до** `LISTEN`.
- Соответствующий `NOTIFY` будет отправлен, но Worker его не получит — ещё не слушает.
- Команда подхватится только через fallback polling (по умолчанию **5 минут**).

Дополнительно:
- Если producer отправляет **один NOTIFY** на пачку команд, а Worker за один wake-up берёт
  только один батч (5 команд) — остаток очереди может простаивать до следующего NOTIFY/поллинга.

**Почему это проблема:** Пользователь видит, что команды «повисли» — файлы отправлены,
бот ответил «команды поставлены в очередь», но обработка не начинается до 5 минут.
В production это означает жалобы «бот тормозит», хотя реальная проблема — рассинхронизация
LISTEN/NOTIFY. Со стороны Worker ничего не сломалось — он просто «не знает» о новых задачах.
5 минут — дефолт, но для пользователя, привыкшего к мгновенным ответам мессенджеров,
это вечность.

**Рекомендация:**
- Делать `LISTEN` **перед** любым drain'ом.
- Надёжная схема:
  1. `LISTEN`.
  2. `drain pending` в цикле (пока есть команды).
  3. `WaitAsync(timeout)`.
  4. После wake-up — снова `drain pending`.
- Не полагаться на NOTIFY как на единственный механизм — использовать БД как source of truth
  и NOTIFY только как wake-up signal.

---

### 8. Worker shutdown не влезает в бюджет времени

**Где:** `TelegramBot.Worker/Services/CommandExecutionService.cs:PerformGracefulShutdownAsync()`

**Проблема:**
```csharp
// 1. Ждать 30 секунд (LogActiveProcessesOnShutdownAsync)
// 2. Для КАЖДОГО активного процесса:
//    - Kill(true)
//    - await Task.WhenAny(process.WaitForExitAsync, Task.Delay(10_000))
// 3. Ждать background tasks (до 15s на каждую)
```

Время **суммируется** последовательно. Если активных процессов 10:
- 30s wait.
- До 100s на sequential kill wait (10 × 10s).
- До 30s на фоновые таски (2 × 15s).
- **Итого: до 160 секунд.**

.NET host по умолчанию даёт ограниченное время на shutdown. Orchestration (Windows Service,
systemd, k8s) может оборвать процесс раньше, порождая именно те orphaned процессы, которых
код пытается избежать.

**Почему это проблема:** Весь graceful shutdown спроектирован, чтобы не оставлять orphaned
Revit/Navisworks процессов на сервере (это была P1-проблема в прошлом). Но из-за того, что
shutdown занимает до 160 секунд, orchestrator (Windows Service Manager, Docker, k8s) убивает
Worker принудительно через 30 секунд. В результате:
- Revit-процессы висят orphaned (ровно то, что пытались предотвратить).
- При следующем запуске Worker может не стартануть из-за занятых портов/файлов.
- Ирония: чем больше процессов, тем дольше shutdown, и тем выше шанс, что он будет прерван.

**Рекомендация:**
- Ввести **общий bounded budget** для shutdown (например, 30 секунд).
- **Сначала** отменить `_shutdownCts` — остановить фоновые задачи.
- Kill активных процессов делать **параллельно** (через `Task.WhenAll`), а не последовательно.
- Не ждать 30s + 10s × N без верхней границы.

---

### 9. Background задачи не отменяются перед shutdown

**Где:** `PerformanceGracefulShutdownAsync()`

**Проблема:**
```csharp
// Вызывается В КОНЦЕ:
if (_shutdownCts != null) await _shutdownCts.CancelAsync();
```

Пока выполняется `LogActiveProcessesOnShutdownAsync` (30s) и kill-цикл:
- `StartCleanupTaskAsync` и `StartHealthMonitoringTaskAsync` продолжают тикать.
- Health loop может ходить по тем же `Process` и логировать ошибки.
- Cleanup task может мешать освобождению ресурсов.

**Почему это проблема:** `StartHealthMonitoringTaskAsync` проверяет здоровье процессов
каждые 5 секунд. Во время shutdown (30+ секунд) она успевает сделать 6+ проверок,
логируя ошибки на уже убитых процессах — засоряет логи. Более серьёзно: `StartCleanupTaskAsync`
может попытаться освободить lease или изменить статус команды, пока shutdown пытается
её завершить. Это состояние гонки между двумя компонентами Worker, которые должны
работать последовательно, а не параллельно.

**Рекомендация:**
- Отменять `_shutdownCts` в **самом начале** shutdown.
- Потом заниматься процессами.

---

### 10. В multi-worker нет идемпотентности NOTIFY session_completed

**Где:** `SessionCompletionTracker.OnCommandCompletedAsync()`

**Проблема:**
- Локальный in-memory счётчик (`_sessionRemaining`) — per-worker, не глобальный.
- Если последние команды сессии завершаются почти одновременно на разных worker'ах:
  - Оба могут увидеть `remainingInDb == 0`.
  - Оба вызвать `NotifySessionCompletedAsync()`.
- Из текущего кода не видно механизма «notify only once».

**Почему это проблема:** В production планируется 2+ Worker для отказоустойчивости.
Если последние две команды сессии выполняются на разных Worker'ах, оба увидят
`remainingInDb == 0` между своим декрементом и проверкой. Оба отправят NOTIFY.
Server получит два уведомления и пошлёт пользователю **два идентичных сообщения**
«Сессия завершена: 5 ✅, 0 ❌». Пользователь в замешательстве, а логи показывают
«notified twice». Влияние не катастрофично, но снижает доверие к боту.

**Важно:** Для исправления потребуется добавить колонку `completion_notified BOOLEAN`
в таблицу `Sessions` (миграция БД). Сейчас такой колонки нет.

**Рекомендация:**
На стороне БД нужна атомарная идемпотентность:
```sql
UPDATE sessions
SET completion_notified = true
WHERE id = @id AND completion_notified = false
RETURNING id;
```
- NOTIFY слать только если `RETURNING` вернул строку.

---

### 11. async void в CommandNotificationService — неограниченные continuations

**Где:** `CommandNotificationService.OnNotificationReceived()`

**Проблема:**
```csharp
private async void OnNotificationReceived(object sender, NpgsqlNotificationEventArgs e)
{
    ...
    await notificationChannel.Writer.WriteAsync(item).AsTask();
}
```

Если sender тормозит, а channel заполнен:
- Каждый PostgreSQL notification создаёт `async void` continuation.
- Они будут ждать освобождения места в канале.
- Количество ожидающих не ограничено.

**Последствия:** memory growth при notification storm, потеря управляемости при shutdown.

**Почему это проблема:** Представьте: Admin запускает скрипт, который создаёт 500 команд.
Worker подтверждает их, и `NOTIFY command_completed` летит 500 раз. Если Server не успевает
обрабатывать (`Channel` полон), `async void` хендлеры накапливаются в памяти — каждый
хранит захваченный контекст, cancellation token, объект события. Это ведёт к:
1. Росту памяти (OutOfMemory при 10K+ уведомлений).
2. Невозможности контролировать shutdown — `async void` не отслеживается, и завершить их
   штатно нельзя. Server может упасть с `AppDomainUnhandledException` или зависнуть.
3. Потере самих уведомлений — если их слишком много, `Channel` переполнен, `WriteAsync`
   блокирует event handler, который блокирует PostgreSQL connection — deadlock.

**Рекомендация:**
- Event handler должен делать только `TryWrite()` или будить отдельный consumer.
- Backpressure организовать через background loop, не через `async void`.

---

## 🟡 P3 — Важные проблемы

### 12. ErrorClassifier не используется для plugin-reported failures

**Где:** `ProcessRunner.WaitAndHandleResultAsync()`

**Проблема:**
```csharp
// Если plugin вернул result file:
var status = result.Status == "done" ? Statuses.Done : Statuses.Failed;
await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, status, errorMessage: errorMsg);
await sessionCompletionTracker.OnCommandCompletedAsync(cmd);
return; // ← HandleFailureAsync с retry НЕ вызывается
```

- Plugin мог вернуть recoverable error.
- Но команда сразу помечается `Failed`, без retry, без `ErrorClassifier`.
- `HandleFailureAsync()` с экспоненциальной задержкой обходится стороной.

**Почему это проблема:** Плагин Revit может вернуть `"status": "failed"` с сообщением
`"network timeout while exporting"` — временная ошибка, которая пройдёт при retry.
Но Worker не пытается retry, потому что обходит `HandleFailureAsync` для plugin-reported failures.
Пользователь видит «Ошибка: network timeout» и должен вручную переотправлять файл.
Это противоречит смыслу автоматики: бот должен retry'ить временные сбои сам.

**Рекомендация:**
- Определить контракт: `failed` из plugin — всегда permanent? Или plugin должен отдавать
  `errorType` (transient/permanent)?
- Если retry нужен — routing через `HandleFailureAsync()`.

---

### 13. Нет trust boundary для FilePath

**Где:** `CommandPreparer.ValidateFilePath()`

**Проблема:**
- Есть проверка `File.Exists`, расширения, path traversal через `Path.GetFullPath`.
- **Нет** проверки, что путь находится в разрешённом root-каталоге.
- **Нет** защиты от junction/symlink/reparse point.

**Риск:** если кто-то сможет записать команду напрямую в БД (обход UI, внутренний баг) —
Worker сможет читать/обрабатывать любой локальный файл, доступный сервисному аккаунту.

**Почему это проблема:** Сервисный аккаунт Worker имеет доступ к `B:\Projects\` (BIM-файлы),
но, вероятно, и к другим сетевым шарикам. Если злоумышленник (или баг в Server) запишет
команду с `FilePath = "C:\Windows\System32\config\SAM"` — Worker попытается открыть этот файл
Revit'ом. Revit, конечно, упадёт с ошибкой, но сам факт, что Worker пытается обращаться
к системным файлам — брешь безопасности. Особенно опасно с AUTORES (AI-agent, который
выполняет произвольный Python-скрипт с доступом к файлу).

**Рекомендация:**
- Проверять, что путь лежит под разрешённым каталогом (`FileSystemOptions.RootPath`).
- Запрещать reparse points, если это важно.
- Валидацию делать не только на Server (UI), но и в Worker.

---

### 14. TryReadResultFile проглатывает parse/read ошибки

**Где:** `ProcessRunner.TryReadResultFile()`

**Проблема:**
```csharp
catch
{
    result = null!;
    return false; // ← неотличимо от "файла нет"
}
```

- Parse error неотличима от «файла нет».
- Код падает в fallback на `ExitCode`.
- Если process вышел с `0`, а result file был битой и содержал реальную ошибку — **ложный успех**.

**Почему это проблема:** Плагин написал `{"status": "failed", "errorMessage": "..."}`,
но запись была частичной (сбой диска, нехватка места). `JsonException` → `return false`.
Worker видит exit code = 0 (процесс завершился успешно, хоть и записал битой JSON).
Fallback: exit code 0 → `Done`. Команда помечена успешной, хотя плагин сообщал об ошибке.
Пользователь получает «Сессия завершена: 5 ✅», а реально 5 файлов повреждены.
**Это worst-case молчаливой потери данных.**

**Рекомендация:**
- Разделять кейсы:
  - File not found.
  - File exists but unreadable (IOException).
  - File exists but invalid JSON (JsonException).
  - File exists but invalid status.
- Логировать каждый кейс отдельно. Malformed result трактовать жёстче.

---

### 15. Начальный drain до LISTEN создаёт окно для потери команд

**Где:** `CommandExecutionService.RunListenerLoopAsync()`

**Проблема:**
```csharp
await commandDataService.ReleaseExpiredLeasesAsync();
await ProcessBatchAsync();     // ← drain до LISTEN
await using var cmd = new NpgsqlCommand($"LISTEN {ListenChannel};", conn);
```

Между `ProcessBatchAsync()` и `LISTEN`:
- Другие задачи могут быть вставлены.
- Worker о них не узнает, пока не получит следующий NOTIFY (или fallback).

**Почему это проблема:** Это вариация #7, но уже на старте Worker.
При запуске Worker (после деплоя или сбоя) сначала drain'ит очередь, а потом подписывается
на LISTEN. Если в этот момент приходят новые команды — `NOTIFY` будет отправлен, но
Worker его не получит. Придётся ждать fallback polling 5 минут. Учитывая, что Worker
может перезапускаться несколько раз за день (деплои) — это систематическая задержка
начала обработки.

**Рекомендация:**
- Сначала `LISTEN`, потом `drain` в цикле.
- Или сделать drain и после `LISTEN`.

---

### 16. PartitionPoolManager не валидирует конфигурацию

**Где:** `PartitionPoolManager.Initialize()`

**Проблема:**
- Нет проверки, что pool size > 0.
- Нет проверки, что thresholds монотонны.
- Если задать `0` или отрицательное значение — `SemaphoreSlim(0, 0)` или исключение.
- При пустом `_partitionPools` создаётся дефолтный пул, но конфиг мог быть битым.

**Почему это проблема:** Опечатка в `appsettings.json` — `PoolSizes: [5, 0, 2, 1]` (вместо 3).
`SemaphoreSlim(0, 0)` создаст семафор, который никогда не пропустит процесс — High priority
команды (Priority 1) будут вечно ждать. Администратор не заметит опечатку, потому что
валидации нет. Диагностика будет выглядеть как «High priority команды не запускаются»,
а причина — `SemaphoreSlim(0)`, который не даёт ни одного процесса.

**Рекомендация:**
- `ValidateOnStart()` для `WorkerOptions`.
- Проверить pool sizes > 0, thresholds monotonic, хотя бы один partition.

---

### 17. Конфигурационный дрейф WorkingDirectory для AUTORES

**Где:** `WorkerOptions.cs` vs `appsettings.json`

**Проблема:**
- В дефолтных `WorkerOptions` у `AUTORES` есть `WorkingDirectory = "."`.
- В `appsettings.json` у `AUTORES` это поле **не задано**.
- В зависимости от того, как Options Binder пересоздаёт dictionary values, `WorkingDirectory`
  может стать `null`.
- Тогда `CommandPreparer.CreateProcessStartInfo()` применит `Path.GetDirectoryName(cmd.FilePath)`,
  а не каталог приложения.

**Почему это проблема:** AUTORES запускает Python AI-агент, который ищет свои модули
относительно `WorkingDirectory`. Если он `null`, процесс стартует в директории BIM-файла
(`B:\Projects\ClientA\`), где нет Python-скриптов. AI-агент падает с `ModuleNotFoundError`,
команда уходит в Failed. Администратор смотрит конфиг — в коде есть `WorkingDirectory = "."`,
но он не срабатывает. Причина: `IOptions<T>.Value` возвращает snapshot из конфигурации;
поскольку `appsettings.json` не содержит `WorkingDirectory`, JSON-десериализация
перетирает C#-дефолт (`"."`) в `null`. Баг плавающий, зависит от порядка биндинга Options.

**Рекомендация:**
- Либо явно задать `WorkingDirectory` в appsettings.json.
- Либо post-configure validation для автодефолтов.
- Либо не хранить критические default-only значения внутри mutable dictionary.

---

### 18. ErrorClassifier хрупок для production-решений

**Где:** `ErrorClassifier.cs`

**Проблема:**
- Substring matching по 12 английским шаблонам:
  - Легко даёт false positive/false negative.
  - Не покрывает русские/специфические ошибки plugin'ов (Revit, Navisworks).
  - Семантика permanent/transient у BIM-инструментов обычно богаче.
- `permanentExitCodes` по умолчанию пустой (`HashSet<int>`).

**Почему это проблема:** Revit выплёвывает ошибки на русском («Файл не найден») или
специфические (`"element id 12345 is no longer valid"` — transient, можно retry).
ErrorClassifier видит только английские `"not found"`, `"access denied"` и т.д.
Русские сообщения проходят мимо классификации, и transient ошибка трактуется как permanent
(или наоборот). Из-за пустого `PermanentFailureExitCodes` при получении специфического
exit code от Revit (3 = «не удалось загрузить плагин») ErrorClassifier считает это transient
и трижды retry'ит, хотя ошибка permanent.

**Рекомендация:**
- Использовать более структурированную классификацию ошибок.
- Покрыть хотя бы основные сообщения Revit-ошибок.
- Конфигурация permanent-паттернов должна быть externalizable.

## Связанные документы

- [AGENTS.md](../AGENTS.md) — архитектура проекта, BimLib, DI, code style
- [ROADMAP.md](../ROADMAP.md) — дорожная карта, статусы версий
- [ExecutionAlgorithm.md](./ExecutionAlgorithm.md) — спецификация алгоритма выполнения команд
