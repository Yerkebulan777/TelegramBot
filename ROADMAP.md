# Дорожная карта (Roadmap) — TelegramBot

> Актуально на: 11 июня 2026

---

## ✅ v1.0 — Базовая функциональность (реализовано)

### Ядро (Core)
- [x] Модели, DTO, интерфейсы, конфигурация — нулевая зависимость от Telegram SDK
- [x] Архитектура с 4 проектами: `Core → Data → Server`, `Worker`
- [x] DI-регистрация всех сервисов как Singleton
- [x] PostgreSQL persistence через Dapper + Npgsql
- [x] Система доступа: регистрация → запрос → подтверждение администратором
- [x] Soft-delete для всех сущностей
- [x] `.editorconfig` с правилами именования и форматирования

### Server — Telegram Bot
- [x] Long-polling через `TelegramBotHostedService` (BackgroundService)
- [x] Обработка текстовых команд: `/start`, `/help`, `/export`, `/automation`, `/status`
- [x] Chain of Responsibility для callback-хендлеров (7 хендлеров)
- [x] Навигация по файловой системе через inline-клавиатуры
- [x] Выбор проектов/секций/RVT-файлов
- [x] Управление сессиями и командами
- [x] Markdown-экранирование (MarkdownV2 + Markdown)
- [x] Очистка устаревших сообщений при старте
- [x] Команды экспорта: PDF, DWG, NWC, IFC
- [x] Команды автоматизации: BIMDOC, CLASHREP, AUTORES

### Worker — Исполнение команд
- [x] Polling очереди команд раз в 1 минуту
- [x] Пул процессов (глобальный SemaphoreSlim)
- [x] Lease-механизм (TTL)
- [x] Таймаут выполнения процесса
- [x] Трекинг PID (ConcurrentDictionary + БД)
- [x] FOR UPDATE SKIP LOCKED — конкурентная обработка несколькими воркерами
- [x] Graceful shutdown — при остановке Worker принудительно завершает активные процессы (Kill(true)) с ожиданием до 10 секунд
- [x] Повторная проверка очереди после временных ошибок batch-а
- [x] Приоритеты команд (`Priority ASC, CreatedAt ASC, CommandId ASC`)
- [x] Поля `StartedAt`, `CompletedAt`, `ProcessId`, `ErrorMessage`
- [x] Трекинг сообщений бота

### Data — PostgreSQL
- [x] SQL-запросы, разбитые по сущностям (5 partial-файлов)
- [x] Инициализация таблиц при старте
- [x] Сид администраторов
- [x] Индексы для производительности

---

## 🟢 v1.1 — Надёжность и масштабирование (реализовано)

- [x] **Lease с долгим TTL** — при захвате команды Lease = ProcessTimeoutMinutes + 5 мин.
  Команда не вернётся в очередь раньше ProcessTimeout. Фоновая очистка каждые 60 сек возвращает
  команды с истёкшим Lease.
- [x] **Асинхронное чтение stdout/stderr** — `BeginOutputReadLine` / `BeginErrorReadLine`.
  Вывод собирается в `StringBuilder` через событийные хендлеры. Больше нет deadlock при
  заполнении буфера 64KB. Логируется: stdout → Information, stderr → Warning.
  Обрезка >4KB для защиты от раздувания логов.
- [x] **Валидация FilePath** — проверка существования файла, расширения (из `AllowedExtensions`),
  защита от path traversal (`Path.GetFullPath()`).
- [x] **Приоритетные партиции (priority-based)** — `SortedDictionary<int, SemaphoreSlim>`:
  - Critical (Priority 0) → до 5 одновременных процессов
  - High (Priority 1) → до 3
  - Medium (Priority 2) → до 2
  - Low (Priority 3) → до 1
  - Маршрутизация: первый partition threshold `>= Priority`, иначе последний threshold
  - Пороги по возрастанию: thresholds `[0, 1, 2, 3]`
  - Чем меньше Priority, тем выше приоритет (0 = Critical, 3 = Low)
  - Конфигурация через `WorkerOptions.Partitions` + appsettings.json
- [x] **Retry logic** — экспоненциальная задержка (`base * 2^(attempt-1)`): 60s, 120s, 240s, ...
  Лимит попыток: `MaxRetries=5`. Команда возвращается в `pending` с `NextRetryAt`.
- [x] **Telegram-уведомления** — о завершении/ошибках команд через отдельный канал LISTEN/NOTIFY
  (`command_completed`). `CommandNotificationService` слушает PostgreSQL и кладёт события в Channel,
  `NotificationSenderService` отправляет сообщения.
  Уведомления приходят только при завершении всей сессии (сводка: `N ✅, M ❌`),
  с указанием имени проекта и списком файлов с ошибками.
- [x] **Координация очистки Lease** — `pg_try_advisory_lock(1234567)` перед каждой очисткой.
  Только один воркер выполняет очистку, остальные пропускают цикл.
- [x] **Primary constructors** — миграция сервисов на C# 12 (TelegramBotHostedService,
  CallbackDispatcher, CommandExecutionService, CommandNotificationService и др.)
- [x] **Рефакторинг навигации** — удалён `PathMap`/`TryResolvePath`, передача путей напрямую
  в callback-данных вместо токенов. Упрощение `FileSystemBrowser`, `FileNavigationHandler`,
  `FileSelectionHandler`.

---

## ✅ v1.2 — Функциональность, безопасность, операционные улучшения (реализовано)

### Реализовано
- [x] **BimLib (встроен в Worker)** — библиотека для определения версии Revit, резолвинга Revit.exe и мониторинга процессов
  - [x] **Определение версии Revit по .rvt-файлу**: чтение OLE-потока BasicFileInfo через OpenMcdf, поиск строки `Format: YYYY`
  - [x] **Автоматический выбор Revit.exe**: поиск пути через реестр Windows (`HKLM\SOFTWARE\Autodesk\Revit\{version}`) с fallback на WOW6432Node
  - [x] **Мониторинг здоровья процесса**: проверка отклика, автозакрытие диалогов Revit
- [x] **Поддержка Navisworks**: поиск Navisworks.exe/FileConvert.exe через реестр Windows, мониторинг процессов (Roamer, FileConvert)
- [x] **Graceful shutdown** — при остановке Worker принудительно завершает все активные процессы
  через `Kill(entireProcessTree: true)` и ждёт до 10 секунд на завершение. Осиротевших процессов не остаётся.
- [x] **Расширенное логирование Revit-специфичных ошибок** — отдельный файл BimLib.log
  (`~/Documents/TelegramBot/Logs/Worker/BimLib/log-.txt`), фильтрация через BimLibLogFilter
  по SourceContext "TelegramBot.BimLib.*"
- [x] **Rate limiting** — ограничение на количество команд от одного пользователя в единицу
  времени (sliding window per-user).
- [x] **ProjectName в БД** — колонка `ProjectName TEXT` в таблице `Sessions`. Имя проекта
  отображается в `/status` и в уведомлениях о завершении.
- [x] **Correlation ID (correlation_id) в сессиях и уведомлениях** — сквозной идентификатор (GUID)
  для отслеживания полного жизненного цикла запроса от Server к PostgreSQL-очереди и Worker-у,
  и до отправки уведомления пользователю.
- [x] **Безопасные WinAPI-обёртки** — использование безопасных типов данных и P/Invoke сигнатур
  в `BimLib/Native/` для предотвращения утечек дескрипторов и переполнения буферов.
- [x] **Список ошибочных файлов в уведомлении** — при наличии ошибок уведомление содержит
  список файлов с ошибками: `\n\nОшибки:\n- file.rvt`.
- [x] **Timing stats в уведомлениях** — уведомление о завершении содержит длительность сессии,
  рассчитанную по `MIN(StartedAt)` / `MAX(CompletedAt)` из таблицы `Commands`.
- [x] **Worker LISTEN/NOTIFY new_tasks** — Worker подписан на канал `new_tasks` и мгновенно
  реагирует на новые задачи. Fallback polling срабатывает раз в 5 минут при потере соединения.
- [x] **Уведомления через Channel** — `CommandNotificationService` получает NOTIFY и ставит задачу
  в `Channel<NotificationItem>` (256 capacity); `NotificationSenderService` читает канал и отправляет
  сообщения в Telegram.
- [x] **Graceful shutdown Worker** — при остановке Worker принудительно завершает все активные
  процессы (`Kill(entireProcessTree: true)`) и ждёт до 10 секунд на завершение. Если процесс не умер
  за 10 секунд — логируется PID. Осиротевших Revit/Navisworks на сервере не остаётся.
- [x] **Оптимизация: in-memory счётчик сессий** — удалён per-command `GetSessionProgressAsync`,
  заменён на `ConcurrentDictionary.AddOrUpdate`. Счётчик используется как batch-local оптимизация,
  а финальность сессии подтверждается БД через отсутствие `pending`/`processing`.
- [x] **Исправлен retry/counter bug** — retry теперь завершает текущий claim и декрементит
  `_sessionRemaining`; уведомление не теряется после повторных попыток.
- [x] **Дневной лимит файлов на пользователя** — `RateLimit:MaxFilesPerUserPerDay` ограничивает
  количество файлов, которые пользователь может поставить в очередь за 24 часа (`0` отключает лимит).
- [x] **Автоочистка старых сессий** — Worker мягко удаляет неактивные сессии старше
  `Worker:CompletedSessionRetentionDays`, если в них нет `pending`/`processing` команд (`0` отключает).
- [x] **Confirmation dialogs для удаления** — кнопки удаления сессии/команды сначала показывают
  подтверждение через `CONFIRMDELETESESSION:` / `CONFIRMDELETECOMMAND:`.
- [x] **Удалён мёртвый код** — `GetCommandStatusAsync` (interface + implementation + SQL),
  `GetFailedFilesBySession` (не использовался — inline SQL вместо константы).

### Не планируется
- **Отдельные `/healthz`/`/readyz` aliases** — не нужны: Server и Worker уже используют единый набор `/health/live`, `/health/ready`, `/health`.

---

## ✅ v1.3 — Упрощение алгоритма и кодовой базы (реализовано)

Цель версии — уменьшить количество состояний, переходов и вспомогательных сущностей, чтобы
исполнение команд было проще читать, сопровождать и отлаживать.

### Упрощение алгоритма выполнения
- [x] **Исправить критические ошибки** — устранены утечки ресурсов и баги:
  - Process.Dispose() не вызывался при завершении команды (утечка OS-дескрипторов)
  - PartitionPoolManager.Initialize() не диспозил старые SemaphoreSlim при повторной инициализации
  - DialogDismisser._dismissAttempts — утечка ConcurrentDictionary при MaxDismissAttempts == 0
  - RateLimiter._requests — unbounded рост словаря (entries никогда не удалялись)
  - NotificationSenderService/Channel — deadlock на shutdown при полном буфере (Writer блокировался навсегда без читателя)
- [x] **Единая модель жизненного цикла команды** — явно описать допустимые переходы статусов
  (`pending → processing → done/failed/deleted`) и убрать дублирующие проверки там, где статус
  уже гарантируется SQL-запросом или Worker-пайплайном.
- [x] **Свести retry, lease и timeout к одному понятному сценарию** — документировать порядок:
  claim → execute → retry/fail → release/complete, затем привести код к этой схеме без
  параллельных "почти одинаковых" веток.
- [x] **Упростить уведомления о завершении сессии** — Worker только подтверждает финальность
  сессии через БД и отправляет `NOTIFY command_completed` с минимальным payload
  `SessionId|CorrelationId`; Server формирует Telegram-сводку через единый сценарный метод
  `SessionDataService.GetSessionCompletionSummaryAsync()`.
- [x] **Пересмотреть in-memory счётчик сессий** — оставить его только как оптимизацию; корректность
  завершения должна подтверждаться БД, особенно для больших сессий и нескольких Worker-процессов.
- [x] **Синхронизировать модель очереди с кодом** — Worker не использует `new_command LISTEN/NOTIFY`;
  основной контур — polling раз в минуту, `command_completed` остаётся каналом уведомлений Server-а.

### Упрощение кодовой базы
- [x] **Разделить `CommandExecutionService` на небольшие компоненты** — отдельно claim/lease,
  execution, retry/fail handling, notification trigger. Без новых интерфейсов, если у компонента
  нет нескольких реализаций.
- [x] **Свести SQL-операции к сценарным методам** — SQL для completion-уведомления вынесен из
  `NotificationSenderService` в `SessionDataService.GetSessionCompletionSummaryAsync()`;
  прикладные сервисы вызывают бизнес-действия (`ClaimPendingCommandsAsync`,
  `ScheduleRetryAsync`, `NotifySessionCompletedAsync`, `GetSessionCompletionSummaryAsync`),
  а не размазывают SQL по Server/Worker.
- [x] **Удалить оставшиеся мёртвые и исторические ветки** — проверены TODO/stale-комментарии,
  удалены `GetCommandStatusAsync`, `SqliteDataService.cs` из `.editorconfig`,
  исправлена документация, описывавшая уже удалённый код.
- [x] **Упростить callback-хендлеры статуса и удаления** — выделить общие операции разбора id,
  формирования сообщений и обновления клавиатур, если повторяется один и тот же 5+ строковый
  шаблон.
- [x] **Синхронизировать документацию с реальным алгоритмом** — обновлены
  `Docs/ExecutionAlgorithm.md`, `Docs/CommandExecutionAlgorithm.md`, `README.md`, `AGENTS.md`, `ROADMAP.md`.
  Исправлены: LISTEN/NOTIFY new_tasks, graceful shutdown, Channel-уведомления, SQL-запросы (Progress/Result/NextRetryAt),
  устранены ссылки на `PostgresDataService`.

---

## 🔄 v1.1a — Рефакторинг и упрощение кода (завершено)

| Изменение | Статус | Описание |
|-----------|--------|----------|
| **DB-трекинг сообщений сохранён** | ✅ Зафиксировано | `TrackedMessages` и методы `IDataService` используются для очистки сообщений; документация обновлена под фактическую DB-backed модель |
| **Удалены лишние интерфейсы** | ✅ Готово | Удалены `IFileSystemBrowser`, `ITelegramUpdateMapper`, `IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker` — прямые зависимости без потери тестируемости |
| **Primary constructors — удалены redundant поля** | ✅ Готово | Из 8 классов удалены ~23 redundant `private readonly` поля, дублирующих параметры primary constructor |
| **CallbackHandlerBase — убрано двойное логирование** | ✅ Готово | `HandleAsync()` больше не ловит исключения — только `CallbackDispatcher`. Устранено двойное логирование каждой ошибки |
| **Unused usings** | ✅ Готово | `dotnet format --diagnostics IDE0005` удалил все неиспользуемые `using` directives по всему проекту |
| **Унификация дубликатов** | ✅ Готово |
|   — `HandlerHelpers.SendActionsReplyKeyboardAsync()` | | Заменяет 3 дублированных метода в `FileNavigationHandler`, `CommandSelectionHandler`, `SlashCommandService` |
|   — `ProcessHealthHelper.CheckHealth()` | | Общая логика для `RevitProcessTracker` и `NavisworksProcessTracker` |
|   — `NpgsqlHelper.CreateOpenConnectionAsync()` | | Перенесён из `Worker.Services` (internal) → `TelegramBot.Data` (public). |
| | | | Используется в `CommandExecutionService` (Worker) и `CommandNotificationService` (Server) |
|   — `TryParseId()` | | Заменяет 5 одинаковых блоков `int.TryParse` в `SessionManagementHandler` |
| **Упрощение DI** | ✅ Готово | `TelegramOutputService` больше не зависит от `IDataService`; убраны 2 лишних параметра из `TelegramBotHostedService`; мёртвый `IDataService` убран из `CommandAppService` |
| **PostgresDataService — `CreateConnectionAsync()`** | ✅ Готово | Выделен helper, заменивший ~15 ручных `new NpgsqlConnection + OpenAsync` |
| **Документация** | ✅ Готово | `AGENTS.md`, `ROADMAP.md` обновлены под все изменения |

- [x] **Утечка памяти RateLimiter** — entries в `_requests` никогда не удалялись [#20](https://github.com/Yerkebulan777/TelegramBot/issues/20)
- [x] **Утечка SemaphoreSlim в PartitionPoolManager** — `Initialize()` не диспозил старые пулы [#19](https://github.com/Yerkebulan777/TelegramBot/issues/19)
- [x] **Утечка Process.Dispose** — после завершения команды `Process` не диспозился [#17](https://github.com/Yerkebulan777/TelegramBot/issues/17)
- [x] **Утечка _dismissAttempts в DialogDismisser** — unbounded рост при `MaxDismissAttempts == 0` [#16](https://github.com/Yerkebulan777/TelegramBot/issues/16)
- [x] **Deadlock Channel на shutdown** — `Writer.TryComplete()` в `finally` для защиты от вечного ожидания [#18](https://github.com/Yerkebulan777/TelegramBot/issues/18)

---

## ✅ v1.4 — Упрощение и оптимизация (реализовано)

Цель: удалить всё несущественное, устранить дублирование, оптимизировать алгоритм бота.
Каждый функционал должен иметь ровно одну реализацию — самый стабильный и простой метод.

### Удаление мёртвого кода

| # | Задача | Файл | Приоритет | Сложность | Статус |
|---|--------|------|-----------|-----------|--------|
| 1 | **Удалить неиспользуемый метод `GetProtectedMessageIds()`** | `CommandAppService.cs` | 🔴 HIGH | 🟢 Низкая | ✅ Уже удалён при v1.3 |
| 2 | **Удалить дублирующийся default для ConnectionString** | `NotificationSenderService.cs`, `CommandNotificationService.cs`, `CommandExecutionService.cs` | 🔴 HIGH | 🟢 Низкая | ✅ Использован `DataAccessBase.DefaultConnectionString` |
| 3 | **Исправить ссылку на несуществующий `CadIntegrationAlgorithm.md`** | `README.md` | 🟠 MEDIUM | 🟢 Низкая | ✅ Ссылка уже удалена |
| 4 | **Удалить ссылку на несуществующий `QodanaSetup.md`** | `ROADMAP.md` | 🟠 MEDIUM | 🟢 Низкая | ✅ Ссылка удалена |

### Устранение дублирования

| # | Задача | Описание | Приоритет | Сложность |
|---|--------|----------|-----------|-----------|
| 5 | **Унифицировать `MarkdownHelper`** | Два отдельных метода `EscapeMarkdownV2()` и `EscapeMarkdown()` дублируют логику. Объединить в один метод с параметром `ParseMode`, используя общий набор экранируемых символов + расширение для V2 | ✅ DONE | 🟢 Низкая |
| 6 | **Удалить thin wrappers `SendErrorAsync`/`SendNotificationAsync`** | Оба метода — обёртки над `SendMessageAsync` с добавлением префикса. Заменить на прямые вызовы `SendMessageAsync` с форматированием на месте вызова | ✅ DONE | 🟢 Низкая |
| 7 | **Выделить общий `DefaultConnectionString`** | Значение `"Host=localhost;Database=telegram_bot;Username=postgres;Password=postgres"` повторяется в 6+ файлах. Вынести в `DataAccessBase` как публичную константу | 🟠 MEDIUM | 🟢 Низкая | ✅ DONE |
| 8 | **Унифицировать reconnect-циклы** | `CommandNotificationService` и `CommandExecutionService` имеют одинаковый outer retry loop (5 сек delay). Выделить общий helper `PostgresReconnectLoop` | 🟡 LOW | 🟡 Средняя | ✅ DONE |

### Оптимизация избыточных интерфейсов (single-implementation)

Целевой принцип: интерфейс нужен только если есть ≥2 реализации или код требует mocking (в проекте нет тестов — mocking не нужен).

| # | Интерфейс | Реализация | Потребители | Риск удаления |
|---|-----------|------------|-------------|---------------|
| 9 | `ICallbackDispatcher` | `CallbackDispatcher` | 1 (`CommandAppService`) | ✅ DONE |
| 10 | `ICommandAppService` | `CommandAppService` | 1 (`TelegramBotHostedService`) | ✅ DONE |
| 11 | `IDatabaseInitializer` | `DatabaseInitializerService` | 1 (`Program.cs`) | ✅ DONE |
| 12 | `ISlashCommandService` | `SlashCommandService` | 1 (`CommandAppService`) | ✅ DONE |
| 13 | `IKeyboardBuilder` | `KeyboardBuilder` | 4 handler-а | ✅ DONE |
| 14 | `ISessionManager` | `SessionManager` | 2 (`CommandAppService`, `SlashCommandService`) | ✅ DONE |
| 15 | `IAccessValidator` | `AuthorizationMiddleware` | 2 (`CommandAppService`, `SlashCommandService`) | ✅ DONE |
| 16 | `ITelegramOutputService` | `TelegramOutputService` | 7+ потребителей | 🔴 Оставлен |
| 17 | `IUserDataService` | `UserDataService` | 3+ (Server + Worker) | ✅ DONE |
| 18 | `ICommandDataService` | `CommandDataService` | 3+ (Server + Worker) | ✅ DONE |
| 19 | `ISessionDataService` | `SessionDataService` | 4+ (Server + Worker) | ✅ DONE |
| 20 | `IMessageTrackingDataService` | `MessageTrackingDataService` | 2 | ✅ DONE |
| 21 | `INotificationDataService` | `SessionDataService` | 2 (Worker + Server) | ✅ DONE |
| 22 | `IRevitVersionDetector` | `RevitVersionDetector` | 1 (`CommandPreparer`) | ✅ DONE |
| 23 | `INavisworksPathResolver` | `NavisworksPathResolver` | 1 (`CommandPreparer`) | ✅ DONE |

> **Статус:** Все single-implementation интерфейсы удалены (#9-#23), кроме `ITelegramOutputService`. `ICallbackHandler` сохранён — CoR (6 реализаций).

### Оптимизация двойной DI-регистрации для Data-сервисов

| # | Задача | Описание | Приоритет |
|---|--------|----------|-----------|
| 24 | **Упростить регистрацию `SessionDataService`** | Было: двойная регистрация concrete + 2 interface. Стало: одна `AddSingleton<SessionDataService>()` | ✅ DONE |
| 25 | **Упростить регистрацию `CommandDataService`** | Аналогично — заменена на одну concrete регистрацию | ✅ DONE |

### Оптимизация алгоритма Telegram-бота

| # | Оптимизация | Эффект | Приоритет | Сложность |
|---|------------|--------|-----------|-----------|
| 26 | **Batch-обработка обновлений Telegram** | `TelegramBotHostedService` обрабатывает обновления по одному. При пиковой нагрузке — задержки. Заменить на параллельную обработку батча через `Parallel.ForEachAsync` с ограничением concurrency | -50% latency под нагрузкой | 🟠 MEDIUM | 🟡 Средняя | ✅ DONE в v1.6 |
| 27 | **Lazy-очистка сессий `SessionManager`** | `PeriodicTimer` каждые 5 мин проверяет ВСЕ сессии. Заменить на проверку только при доступе к сессии + фоновая очистка раз в 30 мин | Снижение CPU в простое | 🟡 LOW | 🟢 Низкая | ✅ DONE |
| 28 | **Кэширование списка директорий в `FileSystemBrowser`** | При каждой навигации читается вся директория заново. Добавить TTL-кэш (5 сек) для содержимого директорий | Снижение дисковых операций | 🟡 LOW | 🟢 Низкая | ✅ DONE |
| 29 | **Оптимизация Markdown-экранирования** | Цепочка `.Replace().Replace()...` создаёт N промежуточных строк. Заменить на `StringBuilder` или `Regex.Replace` | Снижение аллокаций на 70% | 🟡 LOW | 🟢 Низкая | ✅ DONE |
| 30 | **Рассмотреть переход на Webhook** | Long-polling добавляет ~0.5-2s latency. Webhook устраняет задержку, но требует публичного HTTPS. Оставить long-polling для простоты деплоя | -0.5-2s latency | 🟢 NONE | — |

### Документация

| # | Задача | Описание | Приоритет |
|---|--------|----------|-----------|
| 31 | **Удалить ссылки на несуществующие документы** | `CadIntegrationAlgorithm.md` (в README), `QodanaSetup.md` (в ROADMAP) | 🔴 HIGH | ✅ Завершено |
| 32 | **Сократить `ExecutionAlgorithm.md`** | Убрать дублирование с AGENTS.md, удалить устаревшую информацию, оставить только спецификацию алгоритма и SQL-запросы | 🟠 MEDIUM | ✅ DONE |
| 33 | **Привести `README.md` к минимальному формату** | Удалить разделы, дублирующие AGENTS.md, оставить: обзор, запуск, конфигурация, команды бота | 🟠 MEDIUM | ✅ DONE |

### Проверка после реализации v1.4
- [x] `dotnet build TelegramBot.slnx` — 0 ошибок
- [x] `dotnet format TelegramBot.slnx --verify-no-changes` — 0 нарушений
- [x] Проверка после упрощения notification-flow: `dotnet build TelegramBot.slnx` — 0 ошибок, 0 предупреждений

---

## ✅ v1.5 — Smart retry + Health checks (реализовано)

### Умный retry: классификация ошибок по exit code

Ранее `ProcessRunner.HandleFailureAsync` пытался retry для любых ошибок (кроме таймаута и prepare-ошибок).
Теперь ошибки классифицируются:

| Тип | Поведение | Примеры |
|-----|-----------|--------|
| `InvalidFileError` (permanent) | сразу Failed, без retry | Файл не найден, нет доступа, неверный формат |
| `ProcessCrashError` (transient) | retry с экспоненциальной задержкой (60s, 120s, 240s...) | Процесс упал с общим кодом ошибки |

**Изменения:**
- **`ErrorClassifier.cs`** (новый) — статический классификатор:
  - По тексту ошибки: `"not found"`, `"access denied"`, `"invalid file"`, `"permission denied"` и др.
  - По exit code: настраиваемый набор `PermanentFailureExitCodes` (через `WorkerOptions`)
  - По типу исключения: `FileNotFoundException`, `UnauthorizedAccessException` и др.
- **`ProcessRunner.HandleFailureAsync`** — трёхходовая логика: isPermanent → Failed, else+retries → retry, else → Failed
- **`WorkerOptions.PermanentFailureExitCodes`** — настраиваемые коды permanent-ошибок

### Health check endpoints

Добавлен минимальный HTTP-сервер health checks на основе `TcpListener`:

| Endpoint | Код | Описание |
|----------|-----|----------|
| `GET /health/live` | 200 OK | Liveness — процесс жив |
| `GET /health/ready` | 200 / 503 | Readiness — проверка PostgreSQL доступен |
| `GET /health` | 200 / 503 | Подробный JSON: статус, checks, uptime, версия |

**Изменения:**
- **`HealthCheckHostedService.cs`** (Core/Health) — `BackgroundService` с `TcpListener`, кэширование результатов (10 сек)
- **`HealthCheckOptions.cs`** (Core/Config) — порт, имя сервиса, таймаут DB check
- **`HealthCheckServiceFactory.cs`** (Data) — общая настройка PostgreSQL readiness check для Server и Worker
- **Server** — порт 5000, регистрация в `DependencyInjectionExtensions.cs`
- **Worker** — порт 5001, регистрация в `Program.cs`
- Конфигурация через `appsettings.json` (секция `HealthCheck`)
- Дополнительные checks: Server — `notificationChannel`, Worker — `bimInstallRoot`, `activeProcesses`

### Проверка после реализации v1.5
- [x] `dotnet build TelegramBot.slnx` — 0 ошибок
- [x] Code review — без критических замечаний
- [x] Регрессионная проверка после v1.3 notification/SQL-упрощения: `dotnet build TelegramBot.slnx` — 0 ошибок, 0 предупреждений

---

## ✅ v1.6 — JSON-обмен с плагинами и параллельная обработка Telegram (реализовано)

Цель: завершить интеграцию с CAD-плагинами через JSON-файловый обмен и
оптимизировать обработку обновлений Telegram для снижения latency под нагрузкой.

### JSON-файловый обмен с CAD-плагинами

- [x] **Модель `TaskFile`** (`TelegramBot.Core.Models.TaskFile`) — файл задания для плагина:
  - Поля: `commandId`, `commandText`, `filePath`, `resultFilePath`, `options`
  - Сериализуется в camelCase JSON (`PropertyNamingPolicy = CamelCase`)
  - Создаётся как `task_{CommandId}.json` в `Path.GetTempPath()`
- [x] **`CommandPreparer.CreateTaskFile()`** — статический метод, создаёт task-файл перед запуском процесса
  - Не фатально при ошибке: плагин может получить данные из аргументов командной строки
- [x] **`ProcessRunner.StartProcessAsync()`** — вызывает `CreateTaskFile` перед стартом
- [x] **Плейсхолдеры аргументов**: `{TaskFilePath}`, `{ResultFilePath}`, `{CommandId}`
  - Обновлены defaults в `WorkerOptions.cs` и `appsettings.json`
- [x] **Документация API плагина** — в `AGENTS.md`: формат TaskFile, ResultFile, алгоритм, таблица плейсхолдеров

### Параллельная обработка обновлений Telegram

**Проблема:** `HandleUpdateAsync` обрабатывал обновления последовательно — SDK ждал завершения
каждого обновления, прежде чем получить следующее. При пиковой нагрузке — задержки.

**Решение:** развязка через `Channel<Update>` + `Parallel.ForEachAsync`:
```
SDK polling → HandleUpdateAsync (WriteAsync в канал, мгновенный возврат)
                      ↓
              Channel<Update> (буфер 200, Wait mode)
                      ↓
              Parallel.ForEachAsync (MaxDegreeOfParallelism = 10)
                      ↓
              ProcessUpdateAsync (бизнес-логика с per-user lock)
```

- [x] **`HandleUpdateAsync`** — только `WriteAsync` в канал. Возвращается мгновенно, SDK получает следующее обновление
- [x] **`ProcessUpdatesAsync`** — фоновая задача: `Parallel.ForEachAsync(channel.Reader.ReadAllAsync(), maxParallelism: 10)`
- [x] **Per-user lock сохранён** — `SessionManager.AcquireUserLockAsync()` гарантирует последовательную
  обработку обновлений одного пользователя. Разные пользователи обрабатываются параллельно
- [x] **Graceful shutdown**: `TryComplete()` → drain до 10с → `CancelAsync()`
  - Отдельный `CancellationTokenSource` для reader: writer закрывается до отмены reader-а
  - Оставшиеся в буфере обновления дочитываются и обрабатываются
- [x] **Безопасность**: `catch (Exception)` в `HandleUpdateAsync` — защита от `ChannelClosedException`

### Удаление устаревшего
- [x] **Упоминание `CommandStatuses.cs`** удалено из `AGENTS.md` — файл давно отсутствует в проекте

### Проверка после реализации v1.6
- [x] `dotnet build TelegramBot.slnx` — 0 ошибок, 0 предупреждений
- [x] Code review: исправлен camelCase-баг в сериализации TaskFile, `catch (Exception)`, отдельный CTS для shutdown

---

## ✅ v1.7 — Исправление P1 (Critical) проблем (реализовано)

Цель: устранить критические проблемы, выявленные в [CriticalReview.md](Docs/CriticalReview.md).

### Изменения

| P1 | Проблема | Фикс |
|----|----------|------|
| 1 | **Head-of-line blocking** — `Task.WhenAll` в `ProcessBatchAsync` блокировал listener до завершения всех задач батча. Если одна команда висела 3ч (timeout Revit), остальные 4 не стартовали | Заменён на **drain loop** (`DrainPendingCommandsAsync`): claim → fire-and-forget → сразу claim ещё, пока очередь не опустеет. `_runningTasks` (HashSet + lock) для shutdown tracking |
| 2 | **Race condition в SessionManager** — `GetOrCreateSession()` вызывал `RemoveSession()`, который диспозил `SemaphoreSlim`, удерживаемый текущим потоком через `AcquireUserLockAsync` | `GetOrCreateSession` и `RemoveSession` больше не диспозят семафор из `_sessionLocks`. Фоновая `CleanUpExpiredSessionsAsync` безопасно обрабатывает cleanup |
| 3 | **Data race — мутация shared `CommandConfig` из `IOptions`** — параллельные команды «загрязняли» `ExecutablePath` друг друга | `PrepareAsync()` создаёт **копию** `CommandConfig` с resolved path, вместо мутации shared-объекта |
| 4 | **Temp-file protocol — 5 подпроблем:** предсказуемые имена, stale-файлы между retry, `File.Delete()` до парсинга, неатомарная запись, отсутствие cleanup | Уникальный `attemptToken` (GUID) на попытку; атомарная запись (`.tmp` + rename); parse-before-delete; `.bad` для битого JSON; `CleanupTempFiles` в `finally` |
| 5 | **Graceful drain сломан** — `_processingCts` был linked к `stoppingToken`, из-за чего `ReadAllAsync(ct)` прекращался немедленно при shutdown, теряя буферизованные обновления | `_processingCts` — независимый CTS. Порядок: `TryComplete()` → drain до 10с → `CancelAsync()`. Буферизованные обновления (до 200) не теряются |

### Проверка после реализации v1.7
- [x] `dotnet build TelegramBot.slnx` — 0 ошибок, 0 предупреждений
- [x] Code review — без критических замечаний
- [x] Документация обновлена (AGENTS.md, ROADMAP.md, CriticalReview.md)

---

## 💡 Дополнительные улучшения (не вошедшие в v1.6)

| # | Улучшение | Приоритет | Сложность | Описание |
|---|-----------|-----------|-----------|----------|
| 1 | **Фильтрация в /status** | 🟠 Средний | 🟢 Низкая | Добавлены фильтр-табы (Все/Активные/Завершённые/С ошибками) + пагинация. `CallbackPrefixes.StatusFilter`/`StatusPage`, новые SQL-запросы, фильтр-табы в клавиатуре | ✅ DONE в v1.4 |
| 3 | **Умный retry: transient vs permanent** | 🟡 Низкий | 🟡 Средняя | ✅ DONE в v1.5 |
| 4 | **Health check endpoints** | 🟠 Средний | 🟢 Низкая | ✅ DONE в v1.5 |
| 2 | **Асинхронный сбор файлов (RVT)** | 🟠 Средний | 🟢 Низкая | `SlashCommandService.CollectRvtFiles` — синхронный обход ФС, блокирует цикл Telegram. Исправлено: `Task.Run` для файлового I/O, await в `ConfirmFileSelectionAsync` | ✅ DONE в v1.6 |

### Шкала приоритетов

| Приоритет | Описание |
|-----------|----------|
| 🔥 **Высокий** | Существенно влияет на производительность или UX — рекомендуется к реализации в первую очередь |
| 🟠 **Средний** | Заметное улучшение качества, низкий риск |
| 🟡 **Низкий** | Полировка, удобство поддержки, безопасность |

---

## 📊 Легенда статусов

| Статус | Значение |
|--------|----------|
| ✅ v1.0 | Реализовано в базовой версии |
| 🟢 v1.1 | Реализовано (улучшения надёжности) |
| ✅ v1.2 | Реализовано (BimLib, Navisworks, rate limiting, уведомления, correlation ID) |
| ✅ v1.3 | Реализовано (упрощение алгоритма и кодовой базы) |
| ✅ v1.4 | Реализовано (упрощение, оптимизация, устранение дублирования, фильтрация /status) |
| ✅ v1.5 | Реализовано (smart retry, health check endpoints) |
| ✅ v1.6 | Реализовано (JSON-обмен с плагинами, параллельная обработка Telegram) |
| ✅ v1.7 | Реализовано (исправление P1 критических проблем: drain loop, race в SessionManager, clone CommandConfig, temp-file protocol, graceful drain) |


---

## 📎 Связанные документы

- [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md) — полная спецификация алгоритма выполнения команд
- [README.md](README.md) — обзор проекта (основная документация)
- [AGENTS.md](AGENTS.md) — руководство для AI-агентов по работе с кодом

