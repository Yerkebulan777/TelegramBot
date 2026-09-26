# Алгоритм выполнения команд

Server → PostgreSQL → Worker → Telegram. SQL — `TelegramBot.Data/Sql/`.

```text
Telegram → Session + Commands (1 tx)
→ Worker poll → claim → TaskFile/ResultFile → Done/retry/Failed
→ NotificationOutbox → Server отправляет итог
```

## 0. Старт Server

`DatabaseInitializerService` создаёт схему в фоне с retry (не блокирует старт процесса) и выставляет `SchemaReadyGate`. Кластер/БД на установке — `PostgresConnectionCheck ensure` + Docker Desktop. Трей и health check стартуют сразу; polling Telegram, outbox, cleanup и очередь Worker ждут схему. `SoftDeleteLegacyCancelled` выполняется до `CHECK` на `Commands.Status`.

Иконка в трее (`ServerTrayHostedService`): процесс жив. Раз в 15 с `ServerHealthCheckService` проверяет PostgreSQL (`SELECT 1`) и Telegram (`getMe`). Серый — ещё проверка, зелёный — оба ок, жёлтый — одно недоступно, красный — оба. `SetMyCommands` при старте тоже retry, чтобы недоступный Telegram не ронял хост.

## 1. Создание задания

`FileSystemBrowser`: при выборе всех файлов раздела каждая подпапка `01_RVT` сканируется один раз. Собранные списки используются для проверки наличия файлов и общей дедупликации в рамках текущего вызова; между вызовами не кешируются.

`SlashCommandService`: сканирование `01_RVT`, лимиты, дедуп. В одной транзакции — user-level `pg_advisory_xact_lock(UserQueue, hashtext(UserId))`, пересчёт дневного лимита, затем `Sessions` + Cartesian product команд×файлов.

`ON CONFLICT (CommandText, FilePath) WHERE Status IN ('pending','processing') DO NOTHING` — глобально по активным парам. Все пропущены → rollback. Частичный skip → `FilesAmount` только по добавленным. Для пропусков показывается снимок прежней команды (до 8 деталей).

Priority (меньше = раньше): PDF/DWG (1) → NWC (2) → IFC/RESAVE (3) → DATA (4) → остальное (5).  
`Partition = "file:" + md5(lower(FilePath))` — одна команда на partition.

Корень: `RuntimeSettings.root_path` (+ admin user id). Первый успешный путь закрепляет admin. Ошибка чтения admin → отказ смены корня (fail-closed). Буква диска/`UNC` → `UncPathResolver`; в Telegram UNC не показывается. Снимок пишется в `Commands.RootPath`. `RootPathSetup` кладёт заявку на 30 мин; активный путь меняет только подтверждение admin в `/help`.

## 2. Claim

Цикл: lease cleanup (по `CleanupIntervalSeconds`) → drain → пауза `FallbackPollingIntervalSeconds` (default 1; 0 = 1).

`availableSlots = MaxConcurrentCommands - running`. Claim: `pending` с наступившим `NextRetryAt`, без partition в `processing`, `FOR UPDATE SKIP LOCKED` + partition advisory lock. Сортировка: Priority, CreatedAt, CommandId.

Аварийная lease: `ProcessTimeoutMinutes + 5` (185 мин). Crash mid-export не снимает неистёкший lease. Штатный shutdown Worker после kill дерева возвращает claimed `processing` в `pending` (`NextRetryAt` ≈ now+15 с, `RetryCount` не растёт, тот же lease-fencing).

## 3. Подготовка и запуск

`CommandPreparer`: конфиг, валидация FilePath по снимку RootPath, resolve Revit/Navisworks/AutoCAD.  
`ProcessRunner`: TaskFile + XSD; Revit — `/language RUS` + `REVITBIMFUSION_TASK_FILE`. `MERGEDWG` — `.scr` + status JSON (чтение с FileShare и retry как ResultFile). Старт Revit и AutoCAD — через `ProcessLaunchGate` (`CommandTraits.GetLaunchGate`; ≥ 15 с между глобальными `Process.Start()` одного продукта, состояние — `ProcessLaunchState`). Прочие команды — сразу. Статус уже `processing` во время ожидания gate.

## 4. Результат

| Условие | Итог |
|---|---|
| `status=done` | `Done` |
| `done` + `warningMessage` | `Done`; warning → `ErrorMessage` |
| plugin `failed` | permanent `Failed`; исключение: Revit `InternalException` + `OpenAndActivateDocument` при RetryCount=0 → один retry через 10 с |
| `cancelled` | `Failed`, без retry |
| invalid XML / schema | permanent `Failed` |
| ResultFile sharing/IO после retry чтения | retry policy |
| Revit без ResultFile | retry policy |
| `MERGEDWG` status JSON `success=true` | `Done` |
| `MERGEDWG` status JSON `success=false` / битый JSON | permanent `Failed` |
| `MERGEDWG` status отсутствует / sharing после retry | retry|permanent по exit |
| wrapper без ResultFile, exit 0 / ≠0 | `Done` / retry|permanent |
| timeout | kill, `Failed` без retry |

stdout/stderr: 64 KiB capture. После `WaitForExitAsync` sync `WaitForExit` ограничен 10 с (drain redirected pipes); таймаут → kill дерева. Valid ResultFile удаляется с TaskFile. После Revit — фоновый `RevitTemporaryDirectoryCleaner` (`RBF-{GUID}` под `%TEMP%`). При ошибке записи БД файлы сохраняются; Worker отпускает lease в `pending` (`NextRetryAt` ≈ now+15 с). Следующий claim, если ResultFile валиден, закрывает команду без нового Process.Start; иначе ResultFile → `.previous` и повторный запуск.

Финальный статус: до 4 попыток записи (паузы 2/4/8 с) в одной tx с session lock + outbox при последней команде. Исчерпание → `CommandPersistenceException` (не BIM-retry).

## 5. Retry

Permanent: plugin failed/cancelled (кроме open-retry выше), schema/invalid XML, битый MERGEDWG JSON, permanent exit codes, missing/invalid file or path, unsupported command, timeout. ACL/UNC/`UnauthorizedAccessException` / access denied — transient, как retry policy.  
Transient: `RetryDelayBaseSeconds × 2^RetryCount + jitter`; после `MaxRetries` → `Failed`. Следующий poll подбирает по `NextRetryAt`.  
`ScheduleRetry` и terminal `UpdateStatus` — только `Status='processing'` и `Lease` claim'а; иначе no-op.

## 6. Завершение и доставка

Terminal transition и lease-Failed — один session advisory lock: статус → при отсутствии активных команд + Done/Failed → `CompletionNotified` + одна запись `session_completed`.

`NotificationSenderService`: poll 3 с, sender advisory lock, до 20 событий/цикл, lease 5 мин. Раз в минуту — recover до 100 сессий без outbox и requeue `session_completed` со статусом `failed` (не чаще чем раз в 15 мин). Telegram timeout 30 с; 429 → `retry_after` под общей блокировкой; transient → `NextAttemptAt` (до 300 с); 400/403 → failed. Ack + tracking — одна tx (at-least-once). Окно дубля: процесс упал после accept Telegram и до `MarkSent` — повтор той же записи outbox; идемпотентный ключ в текст не вставляем.

`CompletionMessageFormatter` — чистый текст для любого итога: код команды, проект, имена файлов, затем `выполнено без ошибок` / `есть ошибки` / `есть предупреждения`. При сбое — `Ошибка:` и причина по файлу; warning плагина — `Предупреждение:`. Перевод типовых причин только при отображении; UNC в причине сжимается до имени файла.

## 7. Cleanup и shutdown

- Lease recovery → pending или Failed + outbox  
- Process monitor + DialogDismisser (kill только через ProcessRunner; заголовок «Revit» не матчится как произвольный диалог)  
- Soft-delete сессий старше `CompletedSessionRetentionDays`, пока есть `session_completed` не в `sent` (requeue failed outbox ждёт 15 мин и требует `s.Status != 'Deleted'`)
- История `/status`: 15 последних команд на пользователя. Более старые `Done`/`Failed` помечаются `Deleted` в той же транзакции, что и загрузка списка, и тем же циклом cleanup; пустая после этого сессия тоже `Deleted`, её tracked-сообщения получают `DeleteAfter=NOW`. `pending`/`processing` и сессия с `session_completed` не в `sent` не трогаются  
- Telegram cleanup: Kind (interface/temporary/completion/job_status); interactive ставит `DeleteAfter=NOW` для устаревшего UI, кроме `completion`; итог сессии (успех и ошибка) пишется как `temporary` и снимается следующим сообщением или колбэком, иначе 24 ч; прочий `temporary` — иначе 5 мин; остальной UI — 24 ч; цикл каждую минуту, batch 500, пакеты Telegram до 100; после `MaximumDeletionAgeHours` (47) — удаление только tracking-записи. Soft-delete сессии (вручную и retention) в той же транзакции ставит её tracked-сообщения, включая completion, на `DeleteAfter=NOW` и сразу зовёт Telegram; строка трекинга снимается только после подтверждения. Уже удалённые сессии и доставленные итоги со старым Kind=`completion` догоняются при инициализации схемы  
- Soft-delete команд и сессий не трогает `processing`; `/status` list/count/details/delete только с `UserId`  
- Worker shutdown: stop loops → process-tree kill (30 с) → release claimed leases в `pending` (`NextRetryAt` +15 с)  

## 8. Rerun из /status

Список `/status` загружается одним запросом с фильтром и `UserId` после обрезки истории до 15 команд. Постраничной навигации списка сессий нет. Кнопка удаления (сессия, команда, тип) пишет soft-delete в БД и только после этого обновляет сообщение.

`RERUNCMD` → новый Session/Command/CorrelationId, `RetryCount=0`. Авто-retry продолжает ту же строку. Список и действия `/status` — только сессии вызывающего `UserId`.

## Статусы и locks

`pending` → `processing` → `Done`/`Failed`. `Deleted` — скрытие/retention. Soft-delete `processing` запрещён.  
Advisory locks (ключи в `AdvisoryLockIds`): 1-arg — lease cleanup `1234567`, outbox `1234569`, AutoCAD launch `1234570`, Revit launch `1234571`; 2-arg — partition claim `(1234568, hashtext)`, completion `(1234570, SessionId)` (та же цифра, что AutoCAD, другая арность), user queue `(1234572, hashtext(UserId))`. Polling; LISTEN/NOTIFY не используется.

## Добавление команды

1. `CommandCodes` + `CallbackPrefixes` + `CommandCatalog`  
2. `CommandTraits` (priority / RequiresRevit)  
3. `Worker:Commands` config  
4. BIM contract/XSD/plugin при смене boundary  
5. README / AGENTS / этот документ  
