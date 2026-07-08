# Architecture Decision Log (ADR)

## ADR-001: Revit handoff через environment variable

**Дата:** 2026-07-03 | **Статус:** реализовано

**Контекст:** Worker запускал Revit через `Revit.exe /command "WORKER" "<task.xml>"`. Revit не поддерживает `/command` для `IExternalCommand` — команда трактовалась как открытие файла, вызывая 100% `ACCESS_VIOLATION`.

**Решение:** Заменить CLI-аргументы на process-scoped environment variable `REVITBIMFUSION_TASK_FILE`. Revit запускается без контрактных аргументов, TaskFile path передаётся только через environment.

**Последствия:** (+) Параллельные Revit-процессы не разделяют TaskFile path. (+) Совместимо с документированным Revit API. (-) RevitBIMFusion должен читать переменную в `Application.OnStartup` и подписывать one-shot `Idling`.

---

## ADR-002: Worker simplification — slot semaphore удалён

**Дата:** 2026-07-02 | **Статус:** реализовано

**Контекст:** Worker имел отдельный `SemaphoreSlim` для ограничения параллельных команд, дублирующий логику `_drainGate` + `_runningTasks.Count`.

**Решение:** Удалить command-slot semaphore. `_drainGate` и tracked running-task count уже ограничивают claim до `MaxConcurrentCommands`.

**Последствия:** (+) Меньше concurrency primitives. (-) Нет change — поведение не изменилось.

---

## ADR-003: Worker — async Revit detection → sync

**Дата:** 2026-07-02 | **Статус:** реализовано

**Контекст:** `RevitVersionDetector.DetectVersion` был async, но OpenMcdf не имеет async API — фактически метод выполнялся синхронно с фейковым `Task.Run`.

**Решение:** Сделать `DetectVersion` синхронным.

**Последствия:** (+) Прозрачность: вызов не скрывает реальную работу. (-) Не блокирует — метод вызывается до Process.Start в рамках одной команды.

---

## ADR-004: Soft-delete вместо DELETE

**Дата:** 2026-07 (начало проекта) | **Статус:** действует

**Контекст:** Необходима возможность восстановления данных и аудит удалений.

**Решение:** Все команды и сессии удаляются через `Status = 'Deleted'`. Физический `DELETE` разрешён только для `TrackedMessages` (вспомогательная таблица).

**Последствия:** (+) Возможность отката. (+) Простой аудит. (-) Нужен retention cleanup (`SessionCleanupService`).

---

## ADR-005: Durable notifications через outbox table

**Дата:** 2026-07 (начало проекта) | **Статус:** действует

**Контекст:** Telegram может быть недоступен, Server может перезагрузиться между завершением команды и отправкой уведомления.

**Решение:** `NotificationOutbox` таблица с `session_completed` event. Запись создаётся атомарно в одной транзакции с финализацией команды. `NotificationSenderService` drain-ит outbox (advisory lock для multi-instance).

**Последствия:** (+) Гарантированная доставка (at-least-once). (-) Дополнительная таблица и фоновый poll.

---

## ADR-006: Partition scheduling — один файл за раз

**Дата:** 2026-07 (начало проекта) | **Статус:** действует

**Контекст:** Две команды одного файла не должны выполняться параллельно (конфликт при открытии .rvt).

**Решение:** `Partition = "file:" + md5(lower(FilePath))`. SQL claim исключает partition с `processing`-командой. Одна команда на partition за раз.

**Последствия:** (+) Последовательная обработка файла — безопасно. (+) Разные файлы — параллельно — максимальная пропускная способность.

---

## ADR-007: Per-user serialization через SessionManager

**Дата:** 2026-07 (начало проекта) | **Статус:** действует

**Контекст:** Обновления одного пользователя не должны обрабатываться параллельно (race condition на UserSession).

**Решение:** `SessionManager.AcquireUserLockAsync` — per-user `SemaphoreSlim`. Обновления разных пользователей — параллельно (`Parallel.ForEachAsync max 10`), одного — последовательно.

**Последствия:** (+) Нет гонок на сессии. (+) Масштабируется по числу пользователей.

---

## ADR-008: Concrete classes over interfaces

**Дата:** 2026-07 (начало проекта) | **Статус:** действует

**Контекст:** Большинство сервисов имеют одну реализацию — интерфейсы добавляют косвенность без пользы.

**Решение:** DI регистрирует concrete classes. Единственный интерфейс — `ICallbackHandler` (dispatch polymorphism). Интерфейсы data services удалены за ненадобностью.

**Последствия:** (+) Меньше файлов и косвенности. (-) Сложнее mock для тестов (тесты отключены — не проблема).

---

## ADR-009: Тесты отключены

**Дата:** 2026-07 (начало проекта) | **Статус:** действует

**Контекст:** Проект — Windows-only сервис, интегрированный с PostgreSQL, Telegram API и внешними BIM-процессами. Модульные тесты имеют низкое отношение польза/стоимость.

**Решение:** Не добавлять test projects. Верификация — `dotnet build TelegramBot.slnx`. Compliance через статический анализ (Qodana) и format check (dotnet format).

**Последствия:** (+) Быстрая сборка. (-) Нет safety net для рефакторинга. Компенсируется GitNexus impact analysis и strict code review.

---

## ADR-010: Сервер на Generic Host вместо ASP.NET

**Дата:** 2026-07-03 | **Статус:** реализовано

**Контекст:** Server не обрабатывает HTTP-запросы — только Telegram long-polling. ASP.NET Web SDK добавляет ненужные middleware и зависимости.

**Решение:** Заменить `WebApplication.CreateBuilder` на `Host.CreateDefaultBuilder`. Удалить `Serilog.AspNetCore`.

**Последствия:** (+) Меньше зависимостей, быстрее startup. (-) Нет встроенного health-check endpoint (не нужен — проект не Web API).
