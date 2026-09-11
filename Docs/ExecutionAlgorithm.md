# Алгоритм выполнения команд

Server → PostgreSQL → Worker → Telegram. SQL — `TelegramBot.Data/Sql/`.

```text
Telegram → Session + Commands (1 tx)
→ Worker poll → claim → TaskFile/ResultFile → Done/retry/Failed
→ NotificationOutbox → Server отправляет итог
```

## 0. Старт Server

`DatabaseInitializerService` создаёт схему в фоне с retry (не блокирует старт процесса). Кластер/БД на установке — `PostgresConnectionCheck ensure` + Docker Desktop. Hosted-сервисы стартуют параллельно; outbox и cleanup переживают временную недоступность БД.

## 1. Создание задания

`SlashCommandService`: сканирование `01_RVT`, лимиты, дедуп. В одной транзакции — `Sessions` + Cartesian product команд×файлов.

`ON CONFLICT (CommandText, FilePath) WHERE Status IN ('pending','processing') DO NOTHING` — глобально по активным парам. Все пропущены → rollback. Частичный skip → `FilesAmount` только по добавленным. Для пропусков показывается снимок прежней команды (до 8 деталей).

Priority (меньше = раньше): PDF/DWG (1) → NWC (2) → IFC/RESAVE (3) → DATA (4) → остальное (5).  
`Partition = "file:" + md5(lower(FilePath))` — одна команда на partition.

Корень: `RuntimeSettings.root_path` (+ admin user id). Первый успешный путь закрепляет admin. Буква диска/`UNC` → `UncPathResolver`; в Telegram UNC не показывается. Снимок пишется в `Commands.RootPath`. `RootPathSetup` кладёт заявку на 30 мин; активный путь меняет только подтверждение admin в `/help`.

## 2. Claim

Цикл: lease cleanup (по `CleanupIntervalSeconds`) → drain → пауза `FallbackPollingIntervalSeconds` (default 1; 0 = 1).

`availableSlots = MaxConcurrentCommands - running`. Claim: `pending` с наступившим `NextRetryAt`, без partition в `processing`, `FOR UPDATE SKIP LOCKED` + partition advisory lock. Сортировка: Priority, CreatedAt, CommandId.

Аварийная lease: `ProcessTimeoutMinutes + 5` (185 мин). Перезапуск Worker не снимает неистёкшие lease (нет exactly-once гарантии при аварии mid-export).

## 3. Подготовка и запуск

`CommandPreparer`: конфиг, валидация FilePath по снимку RootPath, resolve Revit/Navisworks/AutoCAD.  
`ProcessStarter`: TaskFile + XSD; Revit — `/language RUS` + `REVITBIMFUSION_TASK_FILE`. `MERGEDWG` — `.scr` + status JSON. Старт Revit и AutoCAD — через `ProcessLaunchGate` (`CommandTraits.GetLaunchGate`; ≥ 15 с между глобальными `Process.Start()` одного продукта, состояние — `ProcessLaunchState`). Прочие команды — сразу. Статус уже `processing` во время ожидания gate.

## 4. Результат

| Условие | Итог |
|---|---|
| `status=done` | `Done` |
| `done` + `warningMessage` | `Done`; warning → `ErrorMessage` |
| plugin `failed` | permanent `Failed`; исключение: Revit `InternalException` + `OpenAndActivateDocument` при RetryCount=0 → один retry через 10 с |
| `cancelled` | `Failed`, без retry |
| invalid XML / Revit без ResultFile | retry policy |
| `MERGEDWG` status JSON `success=true` | `Done` |
| `MERGEDWG` status JSON `success=false` / отсутствует | permanent `Failed` / retry|permanent по exit |
| wrapper без ResultFile, exit 0 / ≠0 | `Done` / retry|permanent |
| timeout | kill, `Failed` без retry |

stdout/stderr: 64 KiB capture. Valid ResultFile удаляется с TaskFile. После Revit — фоновый `RevitTemporaryDirectoryCleaner` (`RBF-{GUID}` под `%TEMP%`). При ошибке записи БД файлы сохраняются; перед retry ResultFile → `.previous`.

Финальный статус: до 4 попыток записи (паузы 2/4/8 с) в одной tx с session lock + outbox при последней команде. Исчерпание → `CommandPersistenceException` (не BIM-retry).

## 5. Retry

Permanent: plugin failed/cancelled (кроме open-retry выше), permanent exit codes, invalid input, timeout.  
Transient: `RetryDelayBaseSeconds × 2^RetryCount + jitter`; после `MaxRetries` → `Failed`. Следующий poll подбирает по `NextRetryAt`.

## 6. Завершение и доставка

Terminal transition и lease-Failed — один session advisory lock: статус → при отсутствии активных команд + Done/Failed → `CompletionNotified` + одна запись `session_completed`.

`NotificationSenderService`: poll 3 с, sender advisory lock, до 20 событий/цикл, lease 5 мин. Раз в минуту — recover до 100 сессий без outbox. Telegram timeout 30 с; 429 → `retry_after` под общей блокировкой; transient → `NextAttemptAt` (до 300 с); 400/403 → failed. Ack + tracking — одна tx (at-least-once; возможен дубль при аварии между Telegram и commit).

`CompletionMessageFormatter` — чистый текст: Failed и Done-с-warning; относительный путь от `Commands.RootPath` (иначе имя файла); перевод типовых причин только при отображении.

## 7. Cleanup и shutdown

- Lease recovery → pending или Failed + outbox  
- Process monitor + DialogDismisser  
- Soft-delete сессий старше `CompletedSessionRetentionDays`, пока нет pending outbox  
- Telegram cleanup: Kind (interface/temporary/completion/job_status); interactive ставит `DeleteAfter=NOW` для устаревшего UI, защищая completion; temporary — 5 мин, results — 24 ч; цикл каждую минуту, batch 500, пакеты Telegram до 100; после `MaximumDeletionAgeHours` (47) — удаление только tracking-записи  
- Worker shutdown: stop loops → process-tree kill (30 с) → release  

## 8. Rerun из /status

`RERUNCMD` → новый Session/Command/CorrelationId, `RetryCount=0`. Авто-retry продолжает ту же строку.

## Статусы и locks

`pending` → `processing` → `Done`/`Failed`. `Deleted` — скрытие/retention.  
Advisory locks: lease cleanup, outbox, completion, partition claim, launch gate (Revit и AutoCAD — разные id). Polling; LISTEN/NOTIFY не используется.

## Добавление команды

1. `CommandCodes` + `CallbackPrefixes` + `CommandCatalog`  
2. `CommandTraits` (priority / RequiresRevit)  
3. `Worker:Commands` config  
4. BIM contract/XSD/plugin при смене boundary  
5. README / AGENTS / этот документ  
