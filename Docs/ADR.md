# Architecture Decision Log

Краткие действующие решения. Детали реализации — код и [ExecutionAlgorithm.md](ExecutionAlgorithm.md).

## ADR-001: Revit handoff через environment variable

**Статус:** действует (2026-07-03)

CLI `/command "WORKER" "<task.xml>"` трактовался Revit как открытие файла → 100% `ACCESS_VIOLATION`.  
Решение: без контрактных CLI (`/language RUS` допустим); путь TaskFile — process-scoped `REVITBIMFUSION_TASK_FILE`. AddIn читает переменную в `OnStartup` и подписывает one-shot `Idling`.

## ADR-004: Soft-delete

**Статус:** действует

Команды/сессии — `Status='Deleted'`. Физический `DELETE` только для `TrackedMessages`. Retention — `SessionCleanupService`.

## ADR-005: Durable notifications (outbox)

**Статус:** действует

`NotificationOutbox` + атомарная запись с финализацией команды. `NotificationSenderService` drain с advisory lock (at-least-once).

## ADR-006: Partition scheduling

**Статус:** действует

`Partition = "file:" + md5(lower(FilePath))`. Одна команда на файл; разные файлы — параллельно.

## ADR-007: Per-user serialization

**Статус:** действует

`SessionManager` — per-user `SemaphoreSlim`. Разные пользователи — `Parallel.ForEachAsync` (max 10).

## ADR-008: Concrete classes over interfaces

**Статус:** действует

DI регистрирует concrete classes. Handlers — через `CallbackHandlerBase`.

## ADR-009: Тесты отключены

**Статус:** действует

Верификация: `dotnet build`, format, GitNexus impact, code review. Test projects не добавлять.

## ADR-010: Generic Host вместо ASP.NET

**Статус:** действует

Server — long-polling Telegram, не HTTP API → `Host.CreateDefaultBuilder`.

## ADR-011: AutoCAD MERGEDWG handoff через .scr + status JSON

**Статус:** действует (2026-09-11)

`MERGEDWG` не использует BIM ResultFile/XSD. Worker пишет AutoCAD script (`NETLOAD` + `MERGEDWG_BATCH` + папка DWG + путь status), запускает `acad.exe /nologo /b`, читает JSON статуса плагина AutoBIMFusion. Папка DWG вычисляется из выбранного RVT как в DrawingExportModule (`{Base}/02_DWG/{relative?}/{RevitFileName}/`).

Вместе с командой обобщён launch gate: `RevitLaunchGate` + однострочная `RevitLaunchState` заменены на `ProcessLaunchGate` и `ProcessLaunchState(Product, LastLaunchAt)` — по строке на продукт, upsert без seed, отдельный advisory lock на продукт. Legacy-таблица удаляется при инициализации схемы (хранила только cooldown).

## ADR-012: Server — задача планировщика, не Windows Service

**Статус:** действует (2026-09-11)

Windows Service логинится отдельным network logon к DC (без cached credentials). На доменном ПК с повреждённым secure channel интерактивный вход ещё работает, а SCM пишет 7038 и оставляет службу Stopped после reboot. То же при GPO, который затирает `SeServiceLogonRight`, и при смене пароля учётки после Setup.

Решение: Server регистрируется так же, как Worker — logon-trigger, `InteractiveToken`, рабочий каталог рядом с exe. Пароль в SCM не хранится. Нужна залогиненная учётка (автологон на выделенном ПК). Leftover-службу старых Setup удаляет.

## ADR-013: Индикатор состояния Server в трее

**Статус:** действует (2026-09-11)

Server работает в интерактивной сессии (ADR-012), без окна. Чтобы было видно, что процесс жив и доступны PostgreSQL и Telegram, `TelegramBot.Server` — `WinExe` с `NotifyIcon`. Цвет: серый / зелёный / жёлтый / красный. Проверки — `SELECT 1` и `getMe` каждые 15 с. Меню: статус, повторная проверка, папка логов. Без пункта «Выход» — задачу останавливает планировщик.
