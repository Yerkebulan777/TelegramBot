# Алгоритм выполнения команд

Semantics pipeline Server → PostgreSQL → Worker → Telegram. Точный SQL — в `TelegramBot.Data/Sql/`.

## Общий поток

```text
Telegram update → Server → Sessions + Commands (1 транзакция)
→ Worker polling → claim → process + TaskFile/ResultFile → Done/retry/Failed → NotificationOutbox → Server отправляет итог
```

## 0. Старт Server и инициализация схемы

`DatabaseInitializerService` — hosted service (`BackgroundService`), зарегистрирован первым среди hosted-сервисов в `AddTelegramBotServer`. Создаёт схему (таблицы, индексы, constraints, legacy soft-delete) в одной транзакции с rollback при ошибке.

Кластер и база появляются раньше, на установке: helper `PostgresConnectionCheck ensure` при выборе Server поднимает PostgreSQL 18 в уже запущенном Docker Desktop (`docker compose up -d` в `%ProgramData%\TelegramBot\PostgreSQL`) и пишет строку подключения в `appsettings.Local.json`. Нативный PostgreSQL установщик не ставит. Схему таблиц по-прежнему создаёт только Server.

Ключевое: **инициализация не блокирует старт хоста**. Раньше вызывалась синхронно в `Program.Main` до `host.RunAsync()` — пока PostgreSQL (в Docker) не поднимался при загрузке машины, инициализация висела > 60 c и SCM убивал старт службы по таймауту (event 7009/7000). Теперь схема создаётся в `ExecuteAsync` с retry (2 → 5 → 15 c, бесконечно до успеха), а хост рапортует SCM «started» немедленно.

Hosted-сервисы стартуют параллельно. NotificationSenderService и TrackedMessageCleanupService перехватывают ошибки внутри своих циклов и повторяют работу после восстановления БД/схемы; TelegramBotHostedService обрабатывает ошибки отдельных updates. Уведомления хранятся в PostgreSQL; очередь входящих updates остаётся в памяти.

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

Интервал — `Worker:FallbackPollingIntervalSeconds`, по умолчанию 1 секунда. Старое имя ключа
сохранено для совместимости; значение 0 также означает 1 секунду. Существующие положительные
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
- только для Revit передаёт запуск в `RevitLaunchGate`: session advisory lock PostgreSQL сериализует все Worker, а singleton-строка `RevitLaunchState` хранит время последнего запуска
- под advisory lock ожидает остаток глобального интервала и вызывает `Process.Start()`; для lock acquisition отключён стандартный 30-секундный command timeout Npgsql, но ожидание отменяется общим token команды. Между запусками Revit проходит не менее 15 секунд, транзакция на время ожидания не удерживается
- не-Revit процессы запускаются сразу и глобальную паузу не используют

Команда уже имеет статус `processing`, пока готовится и ожидает Revit launch gate.

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

stdout/stderr: 64 KiB capture, 4 KiB в лог. Валидный ResultFile удаляется вместе с TaskFile после успешной обработки результата. После завершения Revit `RevitTemporaryDirectoryCleaner` запускает отслеживаемую фоновую cleanup-задачу, не занимающую слот выполнения команды. Сервис проверяет опциональный `temporaryDirectoryPath`: это должен быть абсолютный непосредственный дочерний каталог текущего `%TEMP%` с контрактным именем `RBF-{GUID}`, без reparse-point и с обычным RVT-файлом, имя которого совпадает с исходным. Проверка повторяется непосредственно перед каждой рекурсивной попыткой удаления; при ошибке выполняется пять повторов с паузой 30 секунд, без изменения статуса команды. При штатной остановке Worker новые cleanup-задачи не принимаются, а активные ожидаются до трёх минут. При ошибке записи в БД task/result-файлы сохраняются; перед новой попыткой прежний ResultFile переносится в `.previous` (одна последняя копия), чтобы не принять его за новый результат. При отрицательном exit code — Revit journal evidence.

`Commands.ErrorMessage` при `Status='Failed'` — причина сбоя; при `Status='Done'` — опциональный `ResultFile.warningMessage` (whitespace-only не сохраняется). Выборки warned-команд всегда фильтруют `Status = 'Done'`.

### Ошибка сохранения результата

Финальный статус записывается с четырьмя попытками при временной ошибке БД: до 10 секунд
на подключение и запрос каждой попытки, паузы 2, 4 и 8 секунд. В одной транзакции сначала берётся
session advisory xact lock, затем записывается terminal-статус, проверяется число активных команд и,
если это последняя команда, создаётся outbox-событие. Нетранзиентная ошибка не повторяется.
После исчерпания попыток выбрасывается `CommandPersistenceException`; она не классифицируется
как ошибка Revit и не вызывает немедленный повтор экспорта. Результат сохраняется для диагностики,
а запись БД остаётся для существующего lease recovery. Это ограниченные повторы, не durable outbox
результатов. Нулевая затронутая строка (например, soft-delete) отдельно записывается в лог.

Ошибка `ScheduleRetryAsync` также отделена от BIM-ошибок. Неоднозначный результат SQL не
повторяется автоматически, поскольку счётчик попыток мог уже увеличиться.

## 5. Retry

Permanent (без retry): plugin `status=failed`/`cancelled`, `PermanentFailureExitCodes`, invalid input, validation errors, non-transient exceptions, timeout. Исключение: Revit-команда с plugin `status=failed`, когда `errorDetails` содержит `Autodesk.Revit.Exceptions.InternalException` и `UIApplication.OpenAndActivateDocument`, при `RetryCount = 0` повторяется один раз через 10 с. `ProcessRunner` завершает текущую попытку; следующий polling-цикл подбирает retry не раньше `NextRetryAt`, когда есть свободный слот. Второй такой сбой становится `Failed`.

Transient: `delay = RetryDelayBaseSeconds × 2^RetryCount + jitter`. После `MaxRetries` → `Failed`.

## 6. Завершение сессии и доставка

Обычный terminal transition и аварийный `Failed` при исчерпании lease retry используют один session advisory xact lock (1234570, SessionId). Под ним записывается статус команды; когда активных команд больше нет и есть Done/Failed, в той же транзакции выставляется CompletionNotified и создаётся единственная запись `session_completed` в NotificationOutbox. Флаг означает постановку в очередь, а не подтверждение Telegram. Уникальный индекс и проверка отсутствия outbox защищают от повторного события, включая восстановление старых сессий с уже выставленным флагом.

Запись PID и первого `session_started` также атомарна. LISTEN/NOTIFY и Channel<NotificationItem> удалены. NotificationSenderService владеет одним ожидаемым polling-циклом (3 секунды). Ошибка прерывает только текущую итерацию; сбой БД при старте не завершает сервис.

- На каждый drain берётся session-level sender advisory lock; claim выполняется на соединении, удерживающем lock. За цикл отправляется до 20 событий, claim по одному, lease 5 минут.
- Раз в минуту под session completion locks восстанавливаются до 100 завершённых сессий без outbox. Перед claim удалённые сессии и устаревшие сообщения старта помечаются failed с причиной superseded.
- Одна попытка Telegram с таймаутом 30 секунд. 429 учитывает retry_after и ожидает его под общей sender-блокировкой, приостанавливая уведомления всех реплик до освобождения блокировки; временные ошибки переносятся через NextAttemptAt (до 300 секунд, без потолка числа повторов); постоянные 400/403 переходят в failed.
- Telegram вернул Message: tracking (ChatId, MessageId, SessionId, Kind, CreatedAt, DeleteAfter) и outbox sent сохраняются одной транзакцией. При временной ошибке подтверждения повторяется только запись в БД. При потере ответа Telegram или аварии до commit возможен дубль после восстановления — общей транзакции Telegram/PostgreSQL нет.
- CompletionMessageFormatter формирует текст из готовой SessionCompletionSummary без I/O: секции ошибок Failed и предупреждений Done с непустым ErrorMessage. Sender отвечает за доставку; логи различают принятие Telegram и сохранённое подтверждение.

## 7. Cleanup и shutdown

- **Lease recovery**: каждые CleanupIntervalSeconds — expired processing → pending либо Failed с атомарным итоговым событием при исчерпании retry.
- **Process health monitoring**: каждые ProcessMonitorIntervalSeconds — проверка Process внутри CommandExecutionService + DialogDismisser.
- **Session retention**: soft-delete завершённых сессий старше CompletedSessionRetentionDays откладывается, пока есть pending/processing итоговое уведомление.
- **Telegram cleanup**: Kind различает interface, temporary, completion и job_status. Интерактивные действия сохраняют DeleteAfter=NOW для устаревшего интерфейса, защищая completion и exceptMessageIds. Входящие сообщения используют дату Telegram. Временные предупреждения и старт хранятся TemporaryRetentionMinutes (5 минут), результаты — RetentionHours (24 часа).
- **Повтор удаления**: отдельный цикл каждые IntervalMinutes (1 минута) выбирает до BatchSize (500) due-записей с учётом NextDeleteAttemptAt. Неудачные ID откладываются минимум на минуту, 429 — с учётом retry_after; порядок по фактическому сроку попытки не позволяет постоянно сбойным сообщениям вытеснять остальные. Telegram получает пакеты до 100; одиночный fallback только при ошибке конкретного сообщения.
- **Учёт**: уникальный (ChatId, MessageIdPg), идемпотентный insert; дата сообщения и срок удаления передаются явно из слоя приложения. Ошибки БД после ограниченного retry пробрасываются. Подтверждённые/уже отсутствующие ID удаляются из tracking, а остальные откладываются одним атомарным SQL-запросом. Частичный прогресс записывается с отдельным бюджетом 5 секунд до передачи отмены вызывающему коду. Просроченные за MaximumDeletionAgeHours (47 часов) записи удаляются только из БД с Warning.
- **Миграция**: добавление полей идемпотентно; дубликаты tracking объединяются по ID. Старые записи без достоверного Kind консервативно защищаются до RetentionHours; новые получают фактический тип.
- **Worker shutdown**: остановка циклов → process-tree kill (30s budget) → освобождение ресурсов. Cancellation передаётся в Telegram и операции очереди/очистки.

## 8. Повторный запуск из /status

Кнопка с именем файла в `/status` для `Done`/`Failed` шлёт `RERUNCMD:{commandId}:{filter}` → `SessionManagementHandler.HandleRerunCommandAsync`.

Server читает принадлежащий пользователю завершённый command как снимок и вызывает обычный
`CreateSessionWithCommandsAsync`. Поэтому ручной повтор всегда создаёт новые `SessionId`, `CommandId`,
`CorrelationId` и `RetryCount = 0`; исходная строка `Done`/`Failed` остаётся историей. Частичный
уникальный индекс не допускает повтор, если та же операция уже `pending`/`processing`; пользователь
видит «Уже поставлено или выполняется». Новый command подхватывается тем же polling-циклом, что и
обычное задание. Автоматический retry, в отличие от ручного, продолжает использовать ту же строку.

## Статусы

`pending` → `processing` → `Done` / `Failed`. `Deleted` — скрыта пользователем/retention. `Cancelled` мигрирован в `Deleted`.

## PostgreSQL locks

Session/advisory locks используются для lease cleanup, outbox sender, terminal completion, partition claim, duplicate check и глобального Revit launch gate. Worker и sender используют polling PostgreSQL; уведомительные каналы не требуются.

## Добавление команды

1. `CommandCodes` + `CallbackPrefixes` + `CommandCatalog`
2. priority map в `SlashCommandService`
3. `Worker:Commands` config
4. `CommandTraits.RequiresRevit` если Revit AddIn
5. canonical BIM contract/XSD/plugin если меняется boundary
6. README, AGENTS и этот документ

## Представление причин в итоговом уведомлении

Запросы `GetFailedCommandsBySession` и `GetWarnedCommandsBySession` возвращают `FilePath`, `RootPath`, `CommandText` и `ErrorMessage`. Server использует сохранённый `Commands.RootPath` для относительного пути в уведомлении и сокращает путь входного файла в причине перед ограничением её длины. Без корня используется имя файла. Перевод типовых причин применяется только при отображении; исходный `ErrorMessage` в БД и классификация retry не изменяются.
