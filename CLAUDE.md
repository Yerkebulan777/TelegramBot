# TelegramBot — AI quick reference

Полные правила находятся в [AGENTS.md](AGENTS.md). При конфликте следовать `AGENTS.md` и фактическому коду.

## Проверка

```powershell
dotnet build TelegramBot.slnx
```

Тесты намеренно отключены: не добавлять test projects и не запускать `dotnet test`.

## Ключевые invariants

- 4 проекта на `net10.0`: Core ← Data; Server и Worker зависят от Core + Data.
- Windows-only; BimLib использует Registry и Win32.
- PostgreSQL 18, Dapper/Npgsql, parameterized SQL.
- Только soft-delete через `Status = 'Deleted'`.
- Все DI services singleton; не создавать интерфейс для одной реализации.
- Callback polymorphism остаётся через `ICallbackHandler`.
- Async suffix обязателен; без `async void`, sync-over-async и `ConfigureAwait(false)`.
- Worker ограничивает параллельность tracked tasks и SQL partition scheduling.
- `ProcessStarter._launchGate` сериализует только `Process.Start()`, без stagger delay.
- Revit получает TaskFile только через process-scoped `REVITBIMFUSION_TASK_FILE`; CLI arguments пусты.
- Canonical BIM contract/XSD: `C:\Users\y.zhumabayev\Repository\RevitBIMFusion\Docs`.
- После изменения pipeline/schema/config/DI синхронизировать соответствующий operational doc.

## GitNexus

Репозиторий индексируется как `TelegramBot`.

1. При stale index выполнить `node .gitnexus/run.cjs analyze`.
2. До правки symbol выполнить upstream `impact`.
3. До HIGH/CRITICAL правки предупредить пользователя.
4. После изменений выполнить `detect_changes(scope: "all")`.
5. Затем собрать весь solution.
