# Дорожная карта (Roadmap) — TelegramBot

> Актуально на: июнь 2026

---

## ✅ v1.0 — Базовая функциональность (реализовано)

### Ядро (Core)
- [x] Модели, DTO, интерфейсы, конфигурация — нулевая зависимость от Telegram SDK
- [x] Архитектура с 4 проектами: `Core → Data → Server`, `Worker` как отдельный процесс
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

## 🟡 v1.2 — Метрики, лимиты, операционные улучшения (в планах)

### Мониторинг и наблюдаемость
- [ ] **Интеграция Prometheus/Grafana** — метрики: количество активных команд, время выполнения,
  количество ошибок по типам, размер очереди. Exporter в `CommandExecutionService` и
  `CommandNotificationService`.
- [ ] **Статистика выполнения** — среднее время выполнения, процент успеха/ошибок по типам команд,
  по пользователям.
- [ ] **Health checks** — эндпоинт `/health` для Worker (liveness + readiness). Необходимо для
  Kubernetes/orchestration.

### Безопасность и контроль
- [ ] **Лимиты очереди (per-user)** — максимальное количество pending-команд на пользователя.
  Защита от аномальной нагрузки и разрастания таблицы `Commands`.
- [ ] **Лимиты очереди (per-session)** — максимальное количество команд в одной сессии.
- [ ] **Rate limiting** — ограничение на количество команд от одного пользователя в единицу
  времени (sliding window или token bucket).

### Операционные улучшения
- [ ] **Graceful shutdown** — таймаут на каждый отдельный цикл ожидания активных процессов.
  Если процесс не реагирует на `Kill(true)`, цикл не должен блокироваться бесконечно.
- [ ] **Runbook** — документация для operational team: типичные инциденты, диагностика,
  восстановление.

### Функциональность
- [ ] **Планировщик задач** — отложенный запуск команд по расписанию (CRON-подобный синтаксис).
- [ ] **Web-дашборд** — минимальный веб-интерфейс для мониторинга очереди и статуса
  (опционально, как отдельный проект).

---

## ⚪ v2.0+ — Долгосрочные планы

### Масштабирование и архитектура
- [ ] **Горизонтальное масштабирование Worker** — несколько воркеров в кластере с
  автоматическим распределением нагрузки. Сейчас FOR UPDATE SKIP LOCKED уже поддерживает
  конкуренцию, но требуется тестирование и настройка.
- [ ] **Health checks endpoint** — стандартные liveness/readiness probes для Kubernetes (DOC-009).
- [ ] **Config reload** — горячая перезагрузка конфигурации без перезапуска сервиса.

### Расширение функционала
- [ ] **Приоритеты пользователей** — VIP-очередь: команды от приоритетных пользователей имеют
  повышенный Priority и выделенные слоты в партициях.
- [ ] **Планировщик с CRON** — регулярные задачи (ежедневный экспорт, отчёты).
- [ ] **Уведомления в несколько каналов** — Telegram + email + webhook.
- [ ] **История и аудит** — полная история действий пользователей, изменений статусов.
- [ ] **Интеграция с CI/CD** — триггеры команд из внешних систем (GitLab CI, GitHub Actions).
- [ ] **API Gateway** — REST API для внешних интеграций.

### Улучшение Developer Experience
- [ ] **Автоматические тесты** — unit-тесты для ключевых сервисов (DataService,
  CallbackDispatcher, CommandExecutionService).
- [ ] **Qodana в CI** — статический анализ кода в GitHub Actions.
- [ ] **Performance profiling** — бенчмарки для узких мест, профилирование памяти.
- [ ] **Docker Compose** — полная среда разработки: PostgreSQL + Seq + Server + Worker.

### Документация
- [ ] **Sequence diagrams** — PlantUML диаграммы для всех ключевых flow.
- [ ] **API Reference** — документация по callback-префиксам и командам.
- [ ] **Contributing guide** — инструкция для контрибьюторов.

---

## 🔄 Текущий спринт (активные изменения)

На основе последних изменений в git:

| Изменение | Статус | Описание |
|-----------|--------|----------|
| Рефакторинг FileSystemBrowser | ✅ Готово | Удалён PathMap/TryResolvePath, пути передаются напрямую |
| Primary constructors | ✅ Готово | Миграция сервисов на C# 12 |
| CommandNotificationService | ✅ Готово | Fire-and-forget исправлен на корректную асинхронную обработку |
| TelegramBotHostedService | ✅ Готово | Рефакторинг на primary constructor |
| UserSession.Reset() | ✅ Готово | Порядок сброса полей унифицирован |
| **Отмена команд через NOTIFY `command_cancel`** | ✅ Готово | Полноценный механизм отмены команд: кнопка «⛔ Отменить» в /status → Server меняет статус на `Cancelled` → NOTIFY command_cancel → Worker отменяет CTS + убивает процесс |
| **Улучшенный статус сессий** | ✅ Готово | Детальная разбивка (Pending, Processing, Done, Failed, Cancelled), прогресс-бар, иконки статусов, информативные списки сессий |

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
