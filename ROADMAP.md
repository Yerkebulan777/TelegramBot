# Дорожная карта (Roadmap) — TelegramBot

> Актуально на: июнь 2026

---

## ✅ v1.0 — Базовая функциональность (реализовано)

### Ядро (Core)
- [x] Модели, DTO, интерфейсы, конфигурация — нулевая зависимость от Telegram SDK
- [x] Архитектура с 5 проектами: `Core → Data → Server`, `Worker` + `BimLib` как отдельные проекты
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
- [x] LISTEN/NOTIFY для мгновенного пробуждения
- [x] Пул процессов (глобальный SemaphoreSlim)
- [x] Lease-механизм (TTL)
- [x] Таймаут выполнения процесса
- [x] Трекинг PID (ConcurrentDictionary + БД)
- [x] FOR UPDATE SKIP LOCKED — конкурентная обработка несколькими воркерами
- [x] Graceful shutdown (30 сек на завершение)
- [x] Fallback poll (5 минут — safety net для потерянных NOTIFY)
- [x] Reconnect loop (5 сек задержка)
- [x] Приоритеты команд (`Priority DESC`)
- [x] Поля `StartedAt`, `CompletedAt`, `ProcessId`, `ErrorMessage`
- [x] Персистентность отслеживаемых сообщений в БД

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
  - High (Priority >= 80) → до 5 одновременных процессов
  - Medium (Priority >= 40) → до 3
  - Low (Priority < 40) → до 1
  - Маршрутизация: `_partitionPools.Keys.Reverse().First(t => cmd.Priority >= t)`
  - Конфигурация через `WorkerOptions.Partitions` + appsettings.json
- [x] **Retry logic** — экспоненциальная задержка (`base * 2^(attempt-1)`): 60s, 120s, 240s, ...
  Лимит попыток: `MaxRetries=5`. Команда возвращается в `pending` с `NextRetryAt`.
- [x] **Telegram-уведомления** — о завершении/ошибках команд через отдельный канал LISTEN/NOTIFY
  (`command_completed`). `CommandNotificationService` слушает и отправляет сообщения.
- [x] **Координация очистки Lease** — `pg_try_advisory_lock(1234567)` перед каждой очисткой.
  Только один воркер выполняет очистку, остальные пропускают цикл.
- [x] **Primary constructors** — миграция сервисов на C# 12 (TelegramBotHostedService,
  CallbackDispatcher, CommandExecutionService, CommandNotificationService и др.)
- [x] **Рефакторинг навигации** — удалён `PathMap`/`TryResolvePath`, передача путей напрямую
  в callback-данных вместо токенов. Упрощение `FileSystemBrowser`, `FileNavigationHandler`,
  `FileSelectionHandler`.

---

## 🟡 v1.2 — Функциональность, безопасность, операционные улучшения (в планах)

### Функциональность
- [x] **Новый проект TelegramBot.BimLib** — библиотека для определения версии Revit, резолвинга Revit.exe и мониторинга процессов
  - [x] **Определение версии Revit по .rvt-файлу**: чтение OLE-потока BasicFileInfo через OpenMcdf, поиск строки `Format: YYYY`
  - [x] **Автоматический выбор Revit.exe**: поиск пути через реестр Windows (`HKLM\SOFTWARE\Autodesk\Revit\{version}`) с fallback на WOW6432Node
  - [x] **Мониторинг здоровья процесса**: проверка отклика, автозакрытие диалогов Revit
- [x] **Поддержка Navisworks**: поиск Navisworks.exe/FileConvert.exe через реестр Windows, мониторинг процессов (Roamer, FileConvert)
- [x] **Graceful shutdown** — Kill только для зависших процессов (process.Responding),
  здоровые процессы продолжают работать. Таймаут 5с на WaitForExitAsync после Kill.
  При остановке Worker не трогает отвечающие процессы (Revit, Navisworks).
- [x] **Расширенное логирование Revit-специфичных ошибок** — отдельный файл BimLib.log
  (`~/Documents/TelegramBot/Logs/Worker/BimLib/log-.txt`), фильтрация через BimLibLogFilter
  по SourceContext "TelegramBot.BimLib.*"
- [ ] Опционально: поддержка Revit Journal-автоматизации

### Безопасность и контроль
- [x] **Rate limiting** — ограничение на количество команд от одного пользователя в единицу
  времени (sliding window per-user).

### Операционные улучшения

### Мониторинг и наблюдаемость (самая последняя очередь)
- [ ] **Интеграция Prometheus/Grafana** — метрики: количество активных команд, время выполнения,
  количество ошибок по типам, размер очереди. Exporter в `CommandExecutionService` и
  `CommandNotificationService`.
- [ ] **Статистика выполнения** — среднее время выполнения, процент успеха/ошибок по типам команд,
  по пользователям.
- [ ] **Health checks** — эндпоинт `/health` для Worker (liveness + readiness). Необходимо для
  Kubernetes/orchestration.

---

## 🔄 v1.2 — Рефакторинг и упрощение кода (завершено)

| Изменение | Статус | Описание |
|-----------|--------|----------|
| **Удалён DB-трекинг сообщений** | ✅ Готово | Удалена таблица `TrackedMessages`, 7 методов из `IDataService`, SQL-файл. Трекинг сообщений только in-memory через `UserSession._trackedMessageIds` |
| **Удалены лишние интерфейсы** | ✅ Готово | Удалены `IFileSystemBrowser`, `ITelegramUpdateMapper`, `IRevitPathResolver`, `IRevitProcessTracker`, `INavisworksProcessTracker` — прямые зависимости без потери тестируемости |
| **Primary constructors — удалены redundant поля** | ✅ Готово | Из 8 классов удалены ~23 redundant `private readonly` поля, дублирующих параметры primary constructor |
| **CallbackHandlerBase — убрано двойное логирование** | ✅ Готово | `HandleAsync()` больше не ловит исключения — только `CallbackDispatcher`. Устранено двойное логирование каждой ошибки |
| **Unused usings** | ✅ Готово | `dotnet format --diagnostics IDE0005` удалил все неиспользуемые `using` directives по всему проекту |
| **Унификация дубликатов** | ✅ Готово |
|   — `HandlerHelpers.SendActionsReplyKeyboardAsync()` | | Заменяет 3 дублированных метода в `FileNavigationHandler`, `CommandSelectionHandler`, `SlashCommandService` |
|   — `ProcessHealthHelper.CheckHealth()` | | Общая логика для `RevitProcessTracker` и `NavisworksProcessTracker` |
|   — `NpgsqlHelper.CreateOpenConnectionAsync()` | | Перенесён из `Worker.Services` (internal) → `TelegramBot.Data` (public). 
| | | | Используется в 3 сервисах: `HealthCheckServer`, `CommandExecutionService` (Worker) 
| | | | и `CommandNotificationService` (Server) |
|   — `TryParseId()` | | Заменяет 5 одинаковых блоков `int.TryParse` в `SessionManagementHandler` |
| **Упрощение DI** | ✅ Готово | `TelegramOutputService` больше не зависит от `IDataService`; убраны 2 лишних параметра из `TelegramBotHostedService`; мёртвый `IDataService` убран из `CommandAppService` |
| **PostgresDataService — `CreateConnectionAsync()`** | ✅ Готово | Выделен helper, заменивший ~15 ручных `new NpgsqlConnection + OpenAsync` |
| **Документация** | ✅ Готово | `AGENTS.md`, `CLAUDE.md`, `ROADMAP.md` обновлены под все изменения |

---

## 📊 Легенда статусов

| Статус | Значение |
|--------|----------|
| ✅ v1.0 | Реализовано в базовой версии |
| 🟢 v1.1 | Реализовано (улучшения надёжности) |
| 🟡 v1.2 | В планах (ближайшие спринты) |
| ⚪ v2.0+ | Долгосрочные планы |
| 🔄 | В работе / текущий спринт |

---

## 📎 Связанные документы

- [Docs/execution-algorithm.md](Docs/execution-algorithm.md) — полная спецификация алгоритма выполнения команд
- [README.md](README.md) — обзор проекта (основная документация)
- [AGENTS.md](AGENTS.md) — руководство для AI-агентов по работе с кодом (RU)
- [CLAUDE.md](CLAUDE.md) — руководство для Claude Code (EN)
- [Docs/qodana-setup.md](Docs/qodana-setup.md) — настройка статического анализа Qodana
- [README.TOKEN.md](README.TOKEN.md) — настройка токена Telegram-бота
