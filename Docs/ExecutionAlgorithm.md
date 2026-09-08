# Алгоритм выполнения команд

Semantics pipeline Server → PostgreSQL → Worker → Telegram. Точный SQL — в `TelegramBot.Data/Sql/`.

## Общий поток

```text
Telegram update → Server → Sessions + Commands (1 транзакция)
→ Worker polling → claim → process + TaskFile/ResultFile → Done/retry/Failed → NotificationOutbox → Server отправляет итог
```

## 0. Старт Server и инициализация схемы

`DatabaseInitializerService` — hosted service (`BackgroundService`), зарегистрирован первым среди hosted-сервисов в `AddTelegramBotServer`. Создаёт схему (таблицы, индексы, constraints, legacy soft-delete) в одной транзакции с rollback при ошибке.

Ключевое: **инициализация не блокирует старт хоста**. Раньше вызывалась синхронно в `Program.Main` до `host.RunAsync()` — пока PostgreSQL (в Docker) не поднимался при загрузке машины, инициализация висела > 60 c и SCM убивал старт службы по таймауту (event 7009/7000). Теперь схема создаётся в `ExecuteAsync` с retry (2 → 5 → 15 c, бесконечно до успеха), а хост рапортует SCM «started» немедленно.

Hosted-сервисы стартуют параллельно (fire-and-forget `ExecuteAsync`), поэтому каждый сам толерантен к временно недоступной БД: `CommandNotificationService` — reconnect-циклом, `NotificationSenderService` — изолированным стартовым drain + polling (outbox retry'ется), `TelegramBotHostedService` — пер-апдейтным catch. Стартовое окно без схемы не теряет данные: update'ы буферизуются каналом, outbox и LISTEN переподключаются.

## 1. Создание задания

`SlashCommandService` проверяет команды и разделы, сканирует `01_RVT`, дедуплицирует, проверяет дневной лимит и дубликаты.

В одной DB-транзакции создаётся `Sessions` и Cartesian product команд×файлов в `Commands`.
`ON CONFLICT (CommandText, FilePath) WHERE Status IN ('pending', 'processing') DO NOTHING`
пропускает только уже активные пары, глобально по всем пользователям. Остальные операции добавляются.
Если все пары пропущены, транзакция откатывается. При частичном добавлении `FilesAmount` учитывает
только файлы с добавленными операциями.

Для пропусков Server читает снимок прежней активной команды: `CommandId`, `SessionId`, `Status`,
`CreatedAt`. Сообщение показывает до восьми подробностей и общее количество пропущенных операций.
Если конфликтующая команда завершилась между INSERT и чтением, сообщение сообщает об изменении
её статуса. Уведомление `new_tasks` больше не отправляется: Worker читает очередь самостоятельно.

### Priority

Меньшее число — раньше: `PDF/DWG` (1) → `NWC` (2) → `IFC` (3) → `DATA` (4) → default/остальные команды (5).

### Partition

```text
Partition = "file:" + md5(lower(FilePath))
```

Одна команда на partition за раз. Операции одного файла — последовательно, разных файлов — параллельно.

## 2. Claim очереди

`CommandExecutionService` выполняет один цикл: очистка истёкших lease (на старте и по
`CleanupIntervalSeconds`) → `CommandOrchestrator.TriggerDrainAsync` → пауза. При ошибке обращения
к БД цикл пишет предупреждение и повторяет попытку после паузы, с новым подключением.

Интервал — `Worker:FallbackPollingIntervalSeconds`, по умолчанию 10 секунд. Старое имя ключа
сохранено для совместимости; значение 0 также означает 10 секунд. Существующие положительные
переопределения сохраняют своё значение. `LISTEN/NOTIFY`, таймеры отдельных retry и запуск drain
из continuation завершённой команды удалены. Мониторинг процессов работает независимо.

Drain: `availableSlots = MaxConcurrentCommands - runningTaskCount`. Claim атомарно выбирает
`pending` с наступившим `NextRetryAt`, исключая partition с `processing`, используя
`FOR UPDATE SKIP LOCKED` и partition advisory xact lock. Сортировка по `Priority`, `CreatedAt`,
`CommandId`. Незавершённые tasks учитываются до окончания обработки, включая запись результата.
Новые команды и retries подбираются очередным циклом при наличии слотов.

Аварийная lease остаётся `ProcessTimeoutMinutes + 5` минут с момента claim (185 минут по
умолчанию). Перезапуск Worker не снимает неистёкшие lease: по одной записи БД нельзя безопасно
решить, что прежний процесс уже остановлен. Эта схема не гарантирует exactly-once экспорт при
аварии между выполнением внешней операции и фиксацией результата.

## Корневой UNC-путь

`RuntimeSettings.root_path` — единый корень для Server; `RuntimeSettings.root_path_admin_user_id` — Telegram ID единственного администратора. Первый пользователь, успешно сохранивший валидный путь, атомарно закрепляется администратором; далее менять корень может только этот пользователь. Пользователь отправляет из `/help` букву подключённого сетевого диска (`Z:\`), вложенную папку (`Z:\Проекты`) или готовый `\\сервер\шара`. `UncPathResolver` сначала использует `WNetGetUniversalName` в сессии Server, а затем ищет `RemotePath` для буквы в загруженных профилях `HKEY_USERS`; принимается только единственный различающийся UNC. Сохраняется только существующий и доступный учётной записи службы Server UNC (проверки: полнота `\\сервер\шара`, `Directory.Exists`, отсутствие reparse-point); в Telegram он не показывается. Профиль с буквой должен быть загружен на машине Server, а Worker также обязан иметь доступ к сохранённому UNC. Локальные диски не принимаются. При создании команды это значение записывается в `Commands.RootPath`. Смена глобального значения немедленно влияет на новые выборы файлов, но Worker валидирует queued/processing-команду по её неизменяемому снимку.

`TelegramBot.RootPathSetup` запускается из меню «Пуск» в интерактивной сессии Windows на машине Server. Он преобразует выбранный сетевой диск через `WNetGetUniversalName` и сохраняет одну заявку в `RuntimeSettings.pending_root_path_change`; активный путь утилита не меняет. Заявка действует 30 минут и заменяется следующей. Администратор бота видит в `/help` только факт ожидающей заявки и может подтвердить или отменить её. При подтверждении Server повторно проверяет UNC и в одной транзакции под существующей advisory lock меняет `root_path`, закрепляет первого администратора при необходимости и закрывает заявку. Созданные команды продолжают использовать свой снимок `Commands.RootPath`.

## 3. Подготовка и запуск

`CommandPreparer.PrepareAsync`:
- находит `CommandConfig`
- валидирует FilePath (снимок RootPath на момент постановки в очередь, reparse point, extension, существование)
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
| `status=done` + `warningMessage` | `Done`; текст warning пишется в `Commands.ErrorMessage` (это не failure) |
| `status=failed` (plugin) | permanent `Failed`, без retry; исключение: Revit `InternalException` при `OpenAndActivateDocument` в `errorDetails` → один retry через 10 с |
| `status=cancelled` | `Failed`, без retry |
| invalid XML | `.bad`, failure → retry policy |
| Revit без ResultFile | failure → retry policy |
| wrapper без ResultFile, exit 0 | `Done` fallback |
| wrapper без ResultFile, exit ≠ 0 | failure → retry/permanent по классификатору |
| timeout | process kill, `Failed` (без retry) |

stdout/stderr: 64 KiB capture, 4 KiB в лог. Валидный ResultFile удаляется вместе с TaskFile после успешной обработки результата. При ошибке записи в БД или остановке Worker файлы сохраняются; перед новой попыткой прежний ResultFile переносится в `.previous` (одна последняя копия), чтобы не принять его за новый результат. При отрицательном exit code — Revit journal evidence.

`Commands.ErrorMessage` при `Status='Failed'` — причина сбоя; при `Status='Done'` — опциональный `ResultFile.warningMessage` (whitespace-only не сохраняется). Выборки warned-команд всегда фильтруют `Status = 'Done'`.

### Ошибка сохранения результата

Финальный статус записывается с четырьмя попытками при временной ошибке БД: до 10 секунд
на подключение и запрос каждой попытки, паузы 2, 4 и 8 секунд. Нетранзиентная ошибка не повторяется.
После исчерпания попыток выбрасывается `CommandPersistenceException`; она не классифицируется
как ошибка Revit и не вызывает немедленный повтор экспорта. Результат сохраняется для диагностики,
а запись БД остаётся для существующего lease recovery. Это ограниченные повторы, не durable outbox
результатов. Нулевая затронутая строка (например, soft-delete) отдельно записывается в лог.

Ошибка `ScheduleRetryAsync` также отделена от BIM-ошибок. Неоднозначный результат SQL не
повторяется автоматически, поскольку счётчик попыток мог уже увеличиться.

## 5. Retry

Permanent (без retry): plugin `status=failed`/`cancelled`, `PermanentFailureExitCodes`, invalid input, validation errors, non-transient exceptions, timeout. Исключение: Revit-команда с plugin `status=failed`, когда `errorDetails` содержит `Autodesk.Revit.Exceptions.InternalException` и `UIApplication.OpenAndActivateDocument`, при `RetryCount = 0` повторяется один раз через 10 с. `ProcessRunner` завершает текущую попытку; следующий polling-цикл подбирает retry не раньше `NextRetryAt`, когда есть свободный слот. Второй такой сбой становится `Failed`.

Transient: `delay = RetryDelayBaseSeconds × 2^RetryCount + jitter`. После `MaxRetries` → `Failed`.

## 6. Завершение сессии

После terminal transition команды — проверка `pending`/`processing` в сессии. Если нет — один SQL: `CompletionNotified = TRUE`, INSERT в `NotificationOutbox`, `pg_notify('command_completed')`.

Server:
- `CommandNotificationService` слушает `session_started` и `command_completed`
- `NotificationSenderService` drain-ит outbox (при старте, по wake-up, каждые 30 с)
- advisory lock на sender для Server replicas
- completion notify: секции «Ошибки» (`Failed`) и «Предупреждения» (`Done` + non-empty `ErrorMessage`)

## 7. Cleanup и shutdown

- **Lease recovery**: каждые `CleanupIntervalSeconds` — expired `processing` → `pending`
- **Process health monitoring**: каждые `ProcessMonitorIntervalSeconds` — проверка `Process` внутри `CommandExecutionService` + `DialogDismisser`
- **Session retention**: `SessionCleanupService` — soft-delete сессий старше `CompletedSessionRetentionDays`
- **Telegram message cleanup**: Server `TrackedMessageCleanupService` каждые `MessageCleanup:IntervalMinutes` удаляет tracking-сообщения старше `RetentionHours`, но младше `MaximumDeletionAgeHours` (по умолчанию 24–47 ч). Неудалённые сообщения остаются для повторной попытки; после окна Telegram запись удаляется только из `TrackedMessages` с Warning.
  - Решение rate-limit принимается до первого I/O в `CommandAppService`; входящие сообщения регистрируются после проверки, включая отклонённые. Ответы гейтов также отслеживаются; лимитер атомарно разрешает не более одного предупреждения пользователю за `WindowSeconds`. Soft-delete сессии сохраняет tracking для интерактивной/фоновой очистки.
  - Интерактивная и фоновая очистка используют общий механизм: distinct ID, пакеты до 100, максимум две повторные попытки для 429/5xx/сетевых сбоев; 429 учитывает `RetryAfter`, остальные — exponential backoff. Одиночный fallback применяется только к ошибкам конкретных сообщений (400), а не к лимитам и недоступности чата.
  - Tracking удаляется после каждого пакета только для подтверждённых удалений (включая уже отсутствующие сообщения). Частичный успех сохраняется даже при ошибке/отмене fallback. Сбой одного чата не прерывает обработку остальных; исключения `exceptMessageIds` действуют и для последнего входящего сообщения.
  - Обработка пакета возвращает подтверждённые ID и признак отложенной работы; сохранение tracking выполняется отдельно. Фоновый цикл логирует число выбранных и подтверждённо удалённых сообщений. `LastUserMessageId` очищается только после подтверждённого удаления, а не при `UserSession.Reset`. Токен отмены передаётся из обработчиков во все вызовы очистки, Telegram-запросы и задержки retry; при отмене сначала сохраняется частичный результат пакета.
- **Worker shutdown**: остановка циклов → process-tree kill (30s budget) → освобождение ресурсов

## 8. Повторный запуск из /status

Кнопка с именем файла в `/status` (для `processing`/`Done`/`Failed`, не `pending`) шлёт `RERUNCMD:{commandId}:{filter}` → `SessionManagementHandler.HandleRerunCommandAsync`.

`CommandDataService.RequeueCommandAsync` (SQL `Requeue`, `Queries.Commands.cs`):
- если `Status == 'processing'` — no-op, возвращает `RequeueOutcome.Processing`, юзер видит toast «⏳ Уже выполняется» (блок дубликата выполнения того же CommandId)
- иначе — сброс той же строки в `pending` (`Lease`/`ProcessId`/`ErrorMessage`/`NextRetryAt`/`CompletedAt` в NULL, `RetryCount` не трогается — это ручной rerun, не авто-retry), подхват очередным polling-циклом, `RequeueOutcome.Requeued`, toast «🔁 Перезапущено»
- если строка не найдена/чужая/удалена — `RequeueOutcome.NotFound`, тихо игнорируется

Если файл физически ещё выполняется старым процессом в момент rerun — этот процесс осиротевает; его финальный `UpdateStatus` может перезаписать заново queued/processing строку тем же `CommandId`. Разруливается на уровне БД по `CommandId`, отдельный kill старого процесса не делается.

## Статусы

`pending` → `processing` → `Done` / `Failed`. `Deleted` — скрыта пользователем/retention. `Cancelled` мигрирован в `Deleted`.

## PostgreSQL channels и locks

| Канал | Назначение |
|---|---|
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

## Представление причин в итоговом уведомлении

Запросы `GetFailedCommandsBySession` и `GetWarnedCommandsBySession` возвращают `FilePath`, `RootPath`, `CommandText` и `ErrorMessage`. Server использует сохранённый `Commands.RootPath` для относительного пути в уведомлении и сокращает путь входного файла в причине перед ограничением её длины. Без корня используется имя файла. Перевод типовых причин применяется только при отображении; исходный `ErrorMessage` в БД и классификация retry не изменяются.
