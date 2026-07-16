# Ревью: Server / Data / Worker — стабильность, оптимальность, логика

Дата: 2026-07-17. Объём: TelegramBot.Server, TelegramBot.Data, TelegramBot.Worker (по состоянию master, коммит 8316924).

## Общая оценка

Архитектура зрелая: outbox-паттерн с advisory-lock'ами, LISTEN/NOTIFY + polling safety-net, lease-based claim с партициями, per-user блокировки, graceful shutdown с бюджетом. Ключевые гонки продуманы и задокументированы в комментариях. Ниже — конкретные находки, отсортированы по серьёзности внутри каждого проекта.

---

## TelegramBot.Server

### 🔴 S1. Outbox-уведомление помечается «sent» при недоставленном сообщении

`TelegramOutputService.ExecuteWithRetryAsync` после исчерпания ретраев **возвращает `null`, а не бросает исключение** ([TelegramOutputService.cs:293-337](../TelegramBot.Server/Services/Infrastructure/Telegram/TelegramOutputService.cs)). Цепочка в `NotificationSenderService`:

`SendCompletionOutboxItemAsync` → `SendCompletionNotificationAsync` → `SendMessageAsync` вернул `null` → исключения нет → `MarkSentAsync(item.OutboxId)` ([NotificationSenderService.cs:134-147](../TelegramBot.Server/Services/Infrastructure/Telegram/NotificationSenderService.cs)).

Итог: при устойчивом 429 или сетевой ошибке outbox-элемент помечается `sent`, уведомление о завершении сессии потеряно навсегда. Это ломает основную гарантию outbox («at-least-once»).

**Фикс:** для outbox-пути отправка должна бросать исключение при неуспехе (или `SendMessageAsync` возвращает результат, а `SendCompletionOutboxItemAsync` проверяет `null` → `MarkFailedAsync`).

### 🟡 S2. Гонка в SessionManager: dispose семафора под живым пользователем

`AcquireUserLockAsync` делает `GetOrAdd` из `_sessionLocks`, затем `WaitAsync` ([SessionManager.cs:52-58](../TelegramBot.Server/Services/Application/SessionManager.cs)). Параллельно cleanup (`TryRemoveExpiredSessionAndLockIfIdle`) может успеть `Wait(0)` → `TryRemove` → `Dispose` в окне между `GetOrAdd` и `WaitAsync`. Последствия:

- `WaitAsync` на disposed-семафоре → `ObjectDisposedException` → апдейт пользователя падает (ловится выше как generic error, но обработка теряется);
- хуже: второй запрос того же пользователя через `GetOrAdd` создаёт **новый** семафор → per-user взаимоисключение на этот момент нарушено (два апдейта одного пользователя параллельно).

Окно узкое, но при таймауте сессии + активном пользователе воспроизводимо. Вариант фикса: после `WaitAsync` перепроверять, что семафор всё ещё тот, что в словаре (retry loop), либо не удалять семафоры вовсе (их размер мал, число пользователей ограничено — самое ленивое и надёжное решение).

### 🟡 S3. `AnswerCallbackQuery` не вызывается при исключении в handler'e

В `ProcessUpdateAsync` ответ на callback идёт после `HandleCallbackAsync` ([TelegramBotHostedService.cs:184-189](../TelegramBot.Server/Services/Infrastructure/Telegram/TelegramBotHostedService.cs)). Если handler бросил — исключение перехватывается выше, но `AnswerCallbackQuery` пропущен → у пользователя «часики» на кнопке до таймаута Telegram (~30 с). Обернуть в `finally` или отвечать до обработки.

### 🟡 S4. Outbox-ретраи без dead-letter

`MarkFailedAsync` всегда возвращает элемент в `pending` с задержкой ≤300 с ([NotificationOutboxDataService.cs:55-71](../TelegramBot.Data/NotificationOutboxDataService.cs)). Перманентно неотправляемое уведомление (пользователь заблокировал бота, чат удалён) будет ретраиться каждые 5 минут вечно. Нужен потолок `Attempts` → статус `failed`/`dead` (после фикса S1 это станет актуальным — сейчас такие элементы «спасает» баг с ложным `sent`).

### 🟢 S5. Потеря буферизованных апдейтов при shutdown

При остановке ждём 10 с на дренаж канала, затем отменяем reader ([TelegramBotHostedService.cs:97-105](../TelegramBot.Server/Services/Infrastructure/Telegram/TelegramBotHostedService.cs)). Offset у Telegram уже подтверждён polling'ом — недообработанные апдейты теряются. С буфером 200 и лёгкими handler'ами приемлемо; фиксирую как осознанный компромисс.

### 🟢 S6. `GetSessionsListFiltered` без LIMIT

Полный агрегирующий скан всех неудалённых сессий с JOIN на Commands при каждом `/status` ([Queries.Sessions.cs:15-44](../TelegramBot.Data/Sql/Queries.Sessions.cs)). Пагинация, судя по всему, делается в памяти. Retention-очистка сдерживает рост, но при отключённой очистке (`CompletedSessionRetentionDays <= 0`) запрос деградирует линейно. LIMIT/OFFSET на SQL-уровне — когда станет заметно.

### ✅ Что сделано хорошо (Server)

- Bounded channel + `Parallel.ForEachAsync` c DOP=10, независимый CTS для дренажа при shutdown — грамотно.
- Advisory lock на drain outbox между репликами; lease + `FOR UPDATE SKIP LOCKED` внутри.
- Fallback'и на «reply markup too long», клампинг под лимит 4096, обрезка причин ошибок до первой строки.

---

## TelegramBot.Data

### 🟡 D1. Пароль БД захардкожен в fallback connection string

`DefaultConnectionString` с `Password=postgres` в коде ([DataAccessBase.cs:22-23](../TelegramBot.Data/DataAccessBase.cs)) и молчаливый fallback при отсутствии `ConnectionStrings:Postgres`. Опечатка в имени ключа конфига → сервис тихо ходит в localhost с дефолтным паролем вместо fail-fast. Лучше: бросать при отсутствии строки; dev-дефолт держать в `appsettings.Development.json`.

### 🟡 D2. Несогласованность типов SessionId: long vs int

`CreateSessionWithCommandsAsync` возвращает `long? SessionId`, но весь остальной слой (`GetSessionsStatusAsync(int)`, `NotifySessionCompletedOnceAsync(int, ...)`, `MessageTrackingService`) работает с `int`. Пока `SessionId` — serial/int, работает, но два типа для одной сущности — источник будущих кастов и путаницы. Выровнять на один тип.

### 🟡 D3. Смешанный регистр статусов

`'pending'`, `'processing'` — lowercase; `'Done'`, `'Failed'`, `'Deleted'` — PascalCase (см. [Queries.Commands.cs:67-79](../TelegramBot.Data/Sql/Queries.Commands.cs)). SQL-сравнения строгие по регистру — одна ошибка регистра в новом запросе даст молча пустой результат. CHECK-constraint на колонку и единый регистр закрыли бы класс ошибок.

### 🟢 D4. Лишние транзакции вокруг одиночных стейтментов

`NotificationOutboxDataService.ClaimPendingAsync` оборачивает один CTE-стейтмент в explicit-транзакцию ([NotificationOutboxDataService.cs:25-38](../TelegramBot.Data/NotificationOutboxDataService.cs)) — одиночный UPDATE атомарен сам по себе. (В `Commands.ClaimAndReturn` транзакция **нужна** — там `pg_try_advisory_xact_lock`; в outbox — нет.) Микро-оптимизация: два лишних round-trip'а на каждый claim.

### 🟢 D5. Inline SQL мимо SqlQueries

`GetSessionUsernameAsync` держит SQL строкой в сервисе ([SessionDataService.cs:143-149](../TelegramBot.Data/SessionDataService.cs)) — единственное отступление от паттерна `SqlQueries.*`. Перенести для единообразия.

### 🟢 D6. Lease сравнивается по часам клиента

Lease пишется как unix-секунды `DateTimeOffset.UtcNow` Worker'а, освобождение сравнивает с `UtcNow` того же процесса — самосогласованно. Но при нескольких Worker'ах на разных машинах clock skew может преждевременно освобождать чужие leases. Для текущего single-machine деплоя не проблема; при масштабировании перейти на `NOW()` БД (как в outbox — там уже так).

### ✅ Что сделано хорошо (Data)

- `ClaimAndReturn` — образцовый: партиционный claim через `pg_try_advisory_xact_lock(hashtext(partition))` + `FOR UPDATE SKIP LOCKED` + повторная проверка `Status='pending'` в финальном UPDATE. Гонки между worker'ами закрыты на уровне БД.
- `NotifyCompletionOnce` / `MarkProcessStartedAndNotifyOnce` — идемпотентность через флаг в той же транзакции, NOTIFY в CTE. Ровно один сигнал.
- Batch-insert через `unnest` с `ON CONFLICT DO NOTHING` по частичному уникальному индексу — дубликаты отсекаются точечно, RETURNING даёт честный отчёт о пропущенных парах.

---

## TelegramBot.Worker

### 🟡 W1. Requeue не сбрасывает CompletionNotified — потерянное уведомление

Сценарий: сессия завершилась (все Failed), уведомление отправлено, `CompletionNotified=TRUE`. Пользователь делает requeue команды ([Queries.Commands.cs:157-187](../TelegramBot.Data/Sql/Queries.Commands.cs)) — флаг не сбрасывается. Команда выполняется, `NotifySessionCompletedOnceAsync` возвращает 0 → пользователь **никогда не узнает** о результате повторного запуска. Добавить `CompletionNotified = FALSE` в CTE `requeued` (обновление Sessions по SessionId).

### 🟡 W2. Poison-command: бесконечный цикл через lease expiry

`ReleaseExpiredLeases` возвращает команду в `pending` **без инкремента RetryCount** ([Queries.Commands.cs:204-212](../TelegramBot.Data/Sql/Queries.Commands.cs)). Обычный путь ретраев ограничен `MaxRetries`, но если команда валит сам Worker (crash до записи статуса), цикл «claim → crash → lease expiry → pending» не ограничен ничем. Инкрементировать RetryCount при освобождении lease (и уважать MaxRetries) — замыкает последнюю дыру в retry-логике.

### 🟡 W3. Таймаут = перманентный Failed, без ретрая

`HandleTimeoutAsync` пишет Failed сразу ([ProcessRunner.cs:149-166](../TelegramBot.Worker/Services/ProcessRunner.cs)), минуя `ErrorClassifier` и retry-механизм. При этом крэш процесса ретраится. Зависший Revit (диалог, сетевой диск) — типично transient-ситуация; логичнее пустить таймаут через ту же развилку retry/permanent. Если поведение осознанное (3-часовые задачи дорого повторять) — зафиксировать причину в комментарии.

### 🟢 W4. Утечка `_unresponsiveSince`

Запись удаляется только если процесс ещё в `ActiveProcesses` ([CommandExecutionService.cs:216-243](../TelegramBot.Worker/Services/CommandExecutionService.cs)). Если команда завершилась, пока числилась NotResponding, запись остаётся навсегда. Рост копеечный (int+DateTime на инцидент), но для long-running сервиса — чистить по отсутствию commandId в ActiveProcesses.

### 🟢 W5. Shutdown не освобождает leases убитых процессов

При graceful shutdown процессы убиваются, но их команды остаются `processing` до истечения lease (ProcessTimeout + 5 мин) — после рестарта Worker'а очередь простаивает до этого момента. Оптимизация: после kill возвращать команды в `pending` (или обнулять lease) в рамках shutdown-бюджета.

### 🟢 W6. `Log.Fatal` без ненулевого exit code

`Program.Main` ловит фатал и выходит нормально ([Program.cs:107-110](../TelegramBot.Worker/Program.cs)). Супервизор (NSSM/sc/Task Scheduler) не увидит сбой и может не перезапустить. `Environment.ExitCode = 1` в catch.

### 🟢 W7. `WaitForExitAsync` + синхронный `WaitForExit`

[ProcessRunner.cs:116-117](../TelegramBot.Worker/Services/ProcessRunner.cs) — известный .NET-приём для гарантированного дренажа redirected output, но формально это sync-over-async, запрещённый в AGENTS.md. Стоит закрепить комментарием «почему» прямо у вызова, чтобы никто не «починил».

### ✅ Что сделано хорошо (Worker)

- `CommandOrchestrator`: single-flight drain через `SemaphoreSlim.WaitAsync(0)`, fire-and-forget launch без head-of-line blocking, re-trigger drain по завершении задачи, `ContinueWith` на `CancellationToken.None` — цикл claim/launch без гонок и без утечки unobserved exceptions.
- LISTEN/NOTIFY + drain safety-net polling + lease cleanup + reconnect с экспоненциальным backoff — надёжная тройная страховка доставки задач.
- Graceful shutdown: общий бюджет 30 с, параллельный kill, поэтапное ожидание фоновых циклов с остаточным бюджетом.
- Классификация ошибок (permanent/transient/plugin-origin) с экспоненциальным backoff + jitter.

---

## Итог и приоритеты

| # | Находка | Серьёзность | Усилие |
|---|---------|-------------|--------|
| S1 | Outbox «sent» при недоставке | Высокая | Малое |
| W1 | Requeue не сбрасывает CompletionNotified | Средняя | Малое |
| W2 | Poison-command без лимита через lease | Средняя | Малое |
| S2 | Гонка dispose в SessionManager | Средняя | Среднее |
| S3 | Callback-спиннер при исключении | Средняя | Малое |
| S4 | Outbox без dead-letter | Средняя | Малое |
| D1 | Захардкоженный пароль-fallback | Средняя | Малое |
| W3 | Таймаут без ретрая | Низкая* | Малое |
| Остальные (D2–D6, W4–W7, S5–S6) | Низкая | Малое |

\* низкая, если поведение осознанное.

Первым чинить S1 — единственная находка, которая молча теряет данные (уведомления) при штатной работе. S1+S4 логично закрыть одним PR.
