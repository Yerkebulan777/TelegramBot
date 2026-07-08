# Revit `ACCESS_VIOLATION`: расследование

> **Статус:** основной defect исправлен 2026-07-03. Incident history, не описание текущего pipeline. Актуальный контракт: [BimPluginContract.md](https://github.com/Yerkebulan777/RevitBIMFusion/blob/master/Docs/BimPluginContract.md).

## Симптом

Worker запускал Revit через `Revit.exe /command "WORKER" "<task.xml>"`. Все 70 запусков завершались `exitCode = -1073741819 (0xC0000005, ACCESS_VIOLATION)`. ResultFile отсутствовал.

## Корневая причина

Revit не поддерживает `/command` для `IExternalCommand`. Команда трактовалась как открытие файла `WORKER`. Журнал подтверждает: `Jrn.Data "File Name", "IDOK", "WORKER"` вместо `Execute external command`.

Вторичный defect: ошибки в `RevitBIMFusion.Application.OnStartup` могли ронять Revit. Исправлен отдельно.

## Исправление

Текущий handoff: Worker создаёт TaskFile с XSD-валидацией → `Revit.exe` без CLI-аргументов → TaskFile path через process-scoped `REVITBIMFUSION_TASK_FILE` → `OnStartup` подписывает one-shot `Idling` → handler выполняет команду и пишет ResultFile.

## Проверенные гипотезы

| Гипотеза | Итог |
|---|---|
| `DialogDismisser` вызывает crash | не подтвердилось |
| CEF/devtools collision | не объясняет 100% failure |
| XML-контракт | не подтвердилось |
| AddIn startup exception | вторичный defect, исправлен |
| Неподдерживаемый `/command` handoff | **основная причина** |

## Диагностика повторного crash

1. Найти `CommandId`, `CorrelationId`, `ProcessId`, exit code в Worker log.
2. Сопоставить с Revit journal (`%LOCALAPPDATA%\Autodesk\Revit\...\Journals\`).
3. Определить границу по наличию: startup log → Idling handoff → API/export → ResultFile.
4. Проверить сборку AddIn для нужного Revit year.
5. При подозрении на CEF collision — снизить `MaxConcurrentCommands` до 1.

## Связанный код

| Файл | Роль |
|---|---|
| `Worker/Services/CommandPreparer.cs` | TaskFile, XSD, environment |
| `Worker/Services/ProcessStarter.cs` | `Process.Start()` |
| `Worker/Services/ProcessRunner.cs` | timeout, result handling |
| `Worker/Services/ResultAnalyzer.cs` | ResultFile/exit code |
| `Worker/BimLib/Monitor/DialogDismisser.cs` | Win32 dialog dismissal |
