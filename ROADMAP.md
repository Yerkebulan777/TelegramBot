# Дорожная карта (Roadmap) — TelegramBot

> Актуально на: 10 июня 2026

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
- [x] Graceful shutdown исключён из требований — внешние процессы покрываются timeout/lease/crash recovery
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

- [x] **Lease с долгим TTL** — при захвате команды Lease = ProcessTimeoutSeconds + 5 мин.
  Команда не вернётся в очередь раньше ProcessTimeout. Фоновая очистка каждые 60 сек возвращает
  команды с истёкшим Lease.
- [x] **Асинхронное чтение stdout/stderr** — `BeginOutputReadLine` / `BeginErrorReadLine`.
  Вывод собирается в `StringBuilder` через событийные хендлеры. Больше нет deadlock при
  заполнении буфера 64KB. Логируется: stdout → Information, stderr → Warning.
  Обрезка >4KB для защиты от раздувания логов.
- [x] **Валидация FilePath** — проверка существования файла, расширения (из `AllowedExtensions`),
  защита от path traversal (`Path.GetFullPath()`).
- [x] **Приоритетные партиции (priority-based)** — `SortedDictionary<int, SemaphoreSlim>`:
  - Critical (Priority 1) → до 3 одновременных процессов
  - High (Priority 2) → до 5
  - Medium (Priority 3) → до 3
  - Low (Priority 4) → до 1
  - Lowest (Priority 5+) → до 1
  - Маршрутизация: первый partition threshold `>= Priority`, иначе последний threshold
  - Пороги по возрастанию: thresholds `[1, 2, 3, 4, 5]`
  - Чем меньше Priority, тем выше приоритет (1 = Critical, 5 = Lowest)
  - Конфигурация через `WorkerOptions.Partitions` + appsettings.json
- [x] **Retry logic** — экспоненциальная задержка (`base * 2^(attempt-1)`): 60s, 120s, 240s, ...
  Лимит попыток: `MaxRetries=5`. Команда возвращается в `pending` с `NextRetryAt`.
- [x] **Telegram-уведомления** — о завершении/ошибках команд через отдельный канал LISTEN/NOTIFY
  (`command_completed`). `CommandNotificationService` слушает и отправляет сообщения.
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

## 🟢 v1.2 — Функциональность, безопасность, операционные улучшения (частично реализовано)

### Реализовано
- [x] **BimLib (встроен в Worker)** — библиотека для определения версии Revit, резолвинга Revit.exe и мониторинга процессов
  - [x] **Определение версии Revit по .rvt-файлу**: чтение OLE-потока BasicFileInfo через OpenMcdf, поиск строки `Format: YYYY`
  - [x] **Автоматический выбор Revit.exe**: поиск пути через реестр Windows (`HKLM\SOFTWARE\Autodesk\Revit\{version}`) с fallback на WOW6432Node
  - [x] **Мониторинг здоровья процесса**: проверка отклика, автозакрытие диалогов Revit
- [x] **Поддержка Navisworks**: поиск Navisworks.exe/FileConvert.exe через реестр Windows, мониторинг процессов (Roamer, FileConvert)
- [x] **Graceful shutdown не нужен** — при остановке Worker не реализует отдельное ожидание или завершение Revit/Navisworks.
  Корректность обеспечивают timeout, lease/crash recovery и повторный захват команд после перезапуска.
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
- [x] **Graceful shutdown Worker** — при остановке Worker логирует активные процессы и даёт им до 30
  секунд на завершение. Активные Revit/Navisworks не принудительно завершаются — их команды
  подхватываются при следующем запуске через Crash Recovery (истёкший Lease).
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

### В планах
- [ ] **Опционально: Revit Journal-автоматизация** — запуск сценариев через journal-файлы, если потребуется более глубокая интеграция с Revit.
- [ ] **Интеграция Prometheus/Grafana** — метрики: количество активных команд, время выполнения,
  количество ошибок по типам, размер очереди. Exporter в `CommandExecutionService` и
  `CommandNotificationService`.
- [ ] **Статистика выполнения** — среднее время выполнения, процент успеха/ошибок по типам команд,
  по пользователям.
- [ ] **Persistent RateLimiter** — перенести хранение окон запросов из `ConcurrentDictionary` в
  PostgreSQL/Redis для сохранения лимитов после перезагрузки сервера.

### Открытые вопросы
- [ ] **Prometheus/Grafana:** нужен отдельный HTTP exporter в Worker/Server или достаточно периодических
  SQL-запросов/дашборда поверх PostgreSQL?
- [ ] **Статистика выполнения:** хранить агрегаты отдельной таблицей или считать on-demand из `Commands`
  по `StartedAt`/`CompletedAt`/`Status`?
- [ ] **Revit Journal-автоматизация:** какие сценарии нужно запускать через journal-файлы, и нужен ли
  отдельный формат шаблонов/параметров для команд?
- [ ] **Статус `Sessions`:** нужно ли переводить `Sessions.Status` в `Done`/`Failed`, или оставить
  текущую модель, где фактический статус сессии вычисляется по командам?
- [ ] **Фильтрация `/status`:** какие фильтры нужны первыми — проект, пользователь, статус,
  пагинация или поиск по дате?
- [ ] **Pre-warm Revit:** допустимо ли держать idle Revit-процессы постоянно запущенными на сервере,
  и кто отвечает за их перезапуск/очистку?

### Не планируется
- **Health checks для Worker** — удалено из roadmap: сейчас не используется Docker/K8s, поэтому отдельные `/health`, `/healthz`, `/readyz` не нужны.

---

## 🟡 v1.3 — Упрощение алгоритма и кодовой базы (частично реализовано)

Цель версии — уменьшить количество состояний, переходов и вспомогательных сущностей, чтобы
исполнение команд было проще читать, сопровождать и отлаживать.

### Упрощение алгоритма выполнения
- [ ] **Исправить критические ошибки** — устранить утечки ресурсов, баги и другие проблемы,
  которые могут приводить к зависанию процессов, некорректной обработке команд или потере
  стабильности Worker/Server.
- [ ] **Единая модель жизненного цикла команды** — явно описать допустимые переходы статусов
  (`pending → processing → done/failed/deleted`) и убрать дублирующие проверки там, где статус
  уже гарантируется SQL-запросом или Worker-пайплайном.
- [ ] **Свести retry, lease и timeout к одному понятному сценарию** — документировать порядок:
  claim → execute → retry/fail → release/complete, затем привести код к этой схеме без
  параллельных "почти одинаковых" веток.
- [ ] **Упростить уведомления о завершении сессии** — оставить один источник истины для
  определения финальности сессии и формирования payload, чтобы Worker и Server не дублировали
  бизнес-логику.
- [x] **Пересмотреть in-memory счётчик сессий** — оставить его только как оптимизацию; корректность
  завершения должна подтверждаться БД, особенно для больших сессий и нескольких Worker-процессов.
- [x] **Синхронизировать модель очереди с кодом** — Worker не использует `new_command LISTEN/NOTIFY`;
  основной контур — polling раз в минуту, `command_completed` остаётся каналом уведомлений Server-а.

### Упрощение кодовой базы
- [ ] **Разделить `CommandExecutionService` на небольшие компоненты** — отдельно claim/lease,
  execution, retry/fail handling, notification trigger. Без новых интерфейсов, если у компонента
  нет нескольких реализаций.
- [ ] **Свести SQL-операции к сценарным методам** — методы Data-слоя должны отражать бизнес-действия
  (`ClaimPendingCommandsAsync`, `MarkCommandCompletedAsync`, `ScheduleRetryAsync`), а не размазывать
  статусные переходы по сервисам.
- [x] **Удалить оставшиеся мёртвые и исторические ветки** — проверены TODO/stale-комментарии,
  удалены `GetCommandStatusAsync`, `SqliteDataService.cs` из `.editorconfig`,
  исправлена документация, описывавшая уже удалённый код.
- [ ] **Упростить callback-хендлеры статуса и удаления** — выделить общие операции разбора id,
  формирования сообщений и обновления клавиатур, если повторяется один и тот же 5+ строковый
  шаблон.
- [x] **Синхронизировать документацию с реальным алгоритмом** — обновлены
  `Docs/ExecutionAlgorithm.md`, `Docs/CommandExecutionAlgorithm.md`, `README.md`, `AGENTS.md`, `ROADMAP.md`.
  Исправлены: LISTEN/NOTIFY new_tasks, graceful shutdown, Channel-уведомления, SQL-запросы (Progress/Result/NextRetryAt),
  устранены ссылки на `PostgresDataService`.

---

## 🔄 v1.2 — Рефакторинг и упрощение кода (завершено)

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

---

## 💡 Рекомендации по улучшению

На основе анализа кодовой базы — список потенциальных улучшений, отсортированных
по ожидаемому эффекту / сложности.

| # | Улучшение | Приоритет | Сложность | Описание |
|---|-----------|-----------|-----------|----------|
| 1 | **Pre-warm Revit** | 🔥 Высокий | 🔴 Высокая | Запуск Revit.exe занимает 1–10 минут. **Решение:** держать пул idle Revit-процессов, передавать файлы уже в запущенный Revit через API/Journal. Радикальный прирост скорости, но требует глубокой интеграции с Revit API |
| 2 | **Timing stats в уведомлениях** | ✅ Реализовано | 🟢 Низкая | Уведомление содержит длительность сессии по `MIN(StartedAt)` / `MAX(CompletedAt)` |
| 3 | **Фильтрация в /status** | 🟠 Средний | 🟢 Низкая | При большом количестве сессий список становится нечитаемым. **Решение:** фильтрация по проекту (`ProjectName`) и статусу, пагинация по страницам |
| 4 | **Дневной лимит файлов на пользователя** | ✅ Реализовано | 🟢 Низкая | `RateLimit:MaxFilesPerUserPerDay = 100`; при превышении бот отказывает в создании новой сессии |
| 5 | **Автоочистка старых сессий** | ✅ Реализовано | 🟢 Низкая | Worker мягко удаляет сессии старше `CompletedSessionRetentionDays`, если в них нет `pending`/`processing` |
| 6 | **Умный retry: transient vs permanent** | 🟡 Низкий | 🟡 Средняя | Сейчас retry для всех ошибок одинаков. **Решение:** классифицировать по exit code / error message: `InvalidFileError` → сразу Failed (retry бесполезен), `ProcessCrashError` → retry (возможно временный сбой) |
| 7 | **Confirmation dialogs для удаления** | ✅ Реализовано | 🟢 Низкая | `DELETESESSION:` / `DELETECOMMAND:` сначала показывают подтверждение, затем soft-delete |

| 8 | **Асинхронное ожидание процессов** | ✅ Реализовано | 🟢 Низкая | В .NET 10 используется асинхронное ожидание `await process.WaitForExitAsync(ct)` вместо блокирующего `WaitForExit()` внутри `Task.Run` для разгрузки ThreadPool |
| 9 | **Асинхронный сбор файлов (RVT)** | 🟠 Средний | 🟢 Низкая | В `SlashCommandService.CollectRvtFiles` заменить синхронный обход ФС на асинхронный, чтобы не блокировать цикл обработки Telegram-сообщений |
| 10 | **Persistent RateLimiter** | 🟡 Низкий | 🟡 Средняя | Перенести хранение окон запросов из `ConcurrentDictionary` в PostgreSQL/Redis для сохранения лимитов после перезагрузки сервера |

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
| 🟢 v1.2 | Частично реализовано, оставшиеся пункты в планах |
| 🟡 v1.3 | Частично реализовано (упрощение алгоритма и кодовой базы) |
| ⚪ v2.0+ | Долгосрочные планы |
| 🔄 | В работе / текущий спринт |

---

## 📎 Связанные документы

- [Docs/ExecutionAlgorithm.md](Docs/ExecutionAlgorithm.md) — полная спецификация алгоритма выполнения команд
- [README.md](README.md) — обзор проекта (основная документация)
- [AGENTS.md](AGENTS.md) — руководство для AI-агентов по работе с кодом (RU)
- [Docs/QodanaSetup.md](Docs/QodanaSetup.md) — настройка статического анализа Qodana

---

## 🩺 Health checks — предложение к реализации

Ранее health checks для Worker были исключены из roadmap, потому что проект не использовал
Docker/K8s. Решение стоит пересмотреть: endpoint'ы здоровья полезны не только для Kubernetes,
но и для systemd/Windows Service, Uptime Kuma, reverse proxy, CI smoke-checks и ручной диагностики.

### Рекомендуемый подход
- [ ] **Server health endpoints** — добавить встроенные ASP.NET Core Health Checks:
  - `/health/live` — процесс запущен, без проверки внешних зависимостей.
  - `/health/ready` — приложение готово принимать нагрузку: PostgreSQL доступен, Telegram API отвечает.
  - `/health` — подробный JSON-ответ для ручной диагностики.
- [ ] **PostgreSQL check** — custom `IHealthCheck`, использующий `NpgsqlHelper.CreateOpenConnectionAsync()`
  и лёгкий запрос к БД.
- [ ] **Telegram API check** — custom `IHealthCheck` через `ITelegramBotClient.GetMe`, с коротким timeout
  и кэшированием результата, чтобы мониторинг не создавал лишнюю нагрузку на Telegram API.
- [ ] **Worker health endpoints** — добавить отдельный HTTP listener/порт для Worker:
  - `/health/live` — worker-процесс жив.
  - `/health/ready` — PostgreSQL доступен, worker не находится в фатальном состоянии.
- [ ] **BIM-зависимости как non-blocking diagnostics** — Revit/Navisworks/registry checks показывать
  в диагностике, но не делать обязательным условием readiness на первом этапе.

### Альтернативы
- **Только Server endpoints** — самый простой первый шаг, но падение Worker может остаться незамеченным.
- **Отдельный watchdog service** — хорошая изоляция, но избыточно для текущей архитектуры.
- **Heartbeat через PostgreSQL без HTTP** — полезно как дополнение, но хуже подходит для стандартных
  мониторинговых инструментов.

### Проверка после реализации
- [ ] `dotnet build TelegramBot.slnx`
- [ ] Ручная проверка `/health/live`, `/health/ready`, `/health`
