# Revit `ACCESS_VIOLATION`: расследование 2026-07-02

> Статус: основной startup defect исправлен 2026-07-03. Этот документ — incident history, не описание текущего pipeline. Актуальный контракт: [RevitBIMFusion/Docs/BimPluginContract.md](https://github.com/Yerkebulan777/RevitBIMFusion/blob/master/Docs/BimPluginContract.md).

## Симптом

Worker запускал Revit-команды через:

```text
Revit.exe /command "WORKER" "<task-file.xml>"
```

Наблюдалось:

```text
exitCode = -1073741819 (0xC0000005, ACCESS_VIOLATION)
```

Статистика Worker log за 2026-07-02:

| Метрика | Значение |
|---|---:|
| Запусков Revit | 70 |
| `0xC0000005` | 70 |
| Успешных команд | 0 |
| Отсутствующих ResultFile | 70 |

Проблема воспроизводилась на Revit 2019 и 2023.

## Корневая причина

Revit не предоставляет документированный CLI-механизм вызова `IExternalCommand`. `/command "WORKER"` трактовался как открытие файла с именем `WORKER`, а не как запуск `WorkerCommand`.

Ключевое evidence из Revit journal:

```text
Jrn.Command "Internal", "Открытие существующего проекта, ID_REVIT_FILE_OPEN"
Jrn.Data "File Name", "IDOK", "WORKER"
```

В crash journals не было `Execute external command` для `WorkerCommand`. Поэтому AddIn не читал TaskFile, не открывал RVT и не записывал ResultFile; Worker корректно видел failure и повторял попытку.

Вторичный defect находился в `RevitBIMFusion.Application.OnStartup`: ошибки startup/UI initialization могли пробрасываться наружу и ронять Revit. Он был исправлен отдельно.

## Реализованное исправление

Текущий handoff:

1. Worker атомарно создаёт и XSD-валидирует TaskFile.
2. `Revit.exe` запускается без контрактных CLI arguments.
3. Абсолютный TaskFile path задаётся только в environment дочернего процесса:

   ```text
   REVITBIMFUSION_TASK_FILE=C:\...\task_project_42.xml
   ```

4. `RevitBIMFusion.Application.OnStartup` валидирует путь и подписывает one-shot `Idling`.
5. Handler отписывается до выполнения, читает `<commandText>`, открывает документ и пишет ResultFile.
6. Для Revit отсутствие ResultFile остаётся failure даже при exit code `0`.

Environment принадлежит конкретному `ProcessStartInfo`, поэтому параллельные Revit processes не разделяют task path.

## Проверенные гипотезы

| Гипотеза | Итог |
|---|---|
| `DialogDismisser` вызывает основной crash | не подтвердилось; при `Enabled=false` Win32 path не вызывается |
| CEF/devtools collision | не объясняет одиночный 100% failure |
| Ошибка XML-контракта | не подтвердилась; models/XSD были совместимы, а handler не запускался |
| AddIn startup exception | вторичный подтверждённый defect, исправлен в RevitBIMFusion |
| Неподдерживаемый `/command` handoff | основная подтверждённая причина |

Исторически исследовался stagger между `Process.Start()`. В текущем TelegramBot настройка stagger удалена как мёртвая: `ProcessStarter._launchGate` сериализует только сам вызов `Process.Start()`, без дополнительной паузы. Если реальная эксплуатация покажет CEF collision, задержку нужно вернуть как await внутри gate и подтвердить журналами.

## Ограничения Revit automation

- Локальный Revit всё равно запускается с UI; true headless требует Autodesk Platform Services Design Automation.
- `UIApplication.OpenAndActivateDocument` нельзя вызывать из `Idling`/`ExternalEvent`.
- `Application.OpenDocumentFile` допустим и возвращает `Document`, достаточный для экспорта.
- Долгие batch-сценарии должны считать process crash нормальным failure signal и оставлять retry/timeout на уровне Worker.

Полезные ссылки:

- [The Building Coder: Idling and External Events](https://jeremytammik.github.io/tbc/a/0743_external_event.htm)
- [APS Design Automation for Revit](https://aps.autodesk.com/en/docs/design-automation/v3)
- [RevitBatchProcessor issue #51](https://github.com/bvn-architecture/RevitBatchProcessor/issues/51)

## Диагностика повторного crash

1. Найти `CommandId`, `CorrelationId`, `ProcessId` и exit code в Worker log.
2. Сопоставить время с Revit journal:

   ```text
   %LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit <year>\Journals\journal.*.txt
   ```

3. Проверить последовательность:

   ```text
   Task file created (Debug)
   Process started
   Revit AddIn startup
   RVT open/export
   ResultFile write
   process exit
   ```

4. Если ResultFile отсутствует, определить границу:

   - нет startup log → deployment/manifest/startup;
   - startup есть, нет task execution → environment/Idling handoff;
   - task начался, нет result → Revit API/native/export crash;
   - result invalid → contract drift, Worker переименует файл в `.bad`.

5. Проверить установленную сборку AddIn именно для нужного Revit year.
6. Временно снизить `Worker:MaxConcurrentCommands` до `1`, если crash проявляется только при параллельном запуске.

## Связанный код

| Файл | Роль |
|---|---|
| `TelegramBot.Worker/Services/CommandPreparer.cs` | TaskFile, XSD, environment handoff |
| `TelegramBot.Worker/Services/ProcessStarter.cs` | сериализованный `Process.Start()` |
| `TelegramBot.Worker/Services/ProcessRunner.cs` | timeout, active process, result handling |
| `TelegramBot.Worker/Services/ResultAnalyzer.cs` | ResultFile/exit code/journal evidence |
| `TelegramBot.Worker/BimLib/Monitor/DialogDismisser.cs` | optional Win32 dialog handling |
| `RevitBIMFusion/RevitBIMFusion/Application.cs` | `OnStartup` + one-shot `Idling` |
| `RevitBIMFusion/WorkerBridge/Commands/WorkerCommandHandler.cs` | task execution and ResultFile |
