# TelegramBot — AI quick reference

Полные правила: [AGENTS.md](AGENTS.md). При конфликте — код.

## Проверка

```powershell
dotnet build TelegramBot.slnx
```

Тесты не добавлять.

## Ключевые invariants

- 4 проекта net10.0: Core ← Data; Server/Worker зависят от Core+Data
- Windows-only; PostgreSQL 18, Dapper/Npgsql
- Soft-delete (`Status='Deleted'`), кроме `TrackedMessages` (physical DELETE)
- DI services — singleton; один интерфейс — `ICallbackHandler`
- `Async` suffix, без `async void`/sync-over-async/`ConfigureAwait(false)`
- Worker: tracked tasks + SQL partition scheduling
- Revit: TaskFile через `REVITBIMFUSION_TASK_FILE`, CLI args пусты
- `IsRevitCommand()`: PDF, DWG, NWC, DATA, IFC, BIMDOC
- Canonical BIM: `RevitBIMFusion/Docs`

## GitNexus

1. `node .gitnexus/run.cjs analyze` при stale index
2. `impact` → `detect_changes` → build
