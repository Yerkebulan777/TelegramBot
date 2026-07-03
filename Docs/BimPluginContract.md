# Контракт BIM-плагинов

> CANONICAL CONTRACT (эталон):
>
> ```text
> C:\Users\y.zhumabayev\Repository\RevitBIMFusion\Docs\BimPluginContract.md
> ```
>
> Этот документ — worker-side отражение эталонного XML-контракта для `TelegramBot.Worker`.
>
> **Проверено:** 2026-07-03 — построчное сравнение с эталоном (структурная версия §1–§11), включая
> схему имени файла. Полное совпадение: поля DTO, XML-схема, CLI args, имя task/result-файла.
> Рассинхронизация контракта **исключена** как причина крашей Revit `ACCESS_VIOLATION`
> (см. [RevitCrashes.md](RevitCrashes.md), Гипотеза 6).

## Соответствие эталону

TelegramBot.Worker обменивается с BIM-исполнителем через XML-файлы в `TaskDirectory`.

| Наша реализация (TelegramBot) | Эталон (RevitBIMFusion) |
|------------------------------|-------------------------|
| `TelegramBot.Core/Models/TaskFile.cs` | `WorkerBridge/Core/Contracts.cs` |
| `TelegramBot.Core/Models/ResultFile.cs` | `WorkerBridge/Core/Contracts.cs` |
| `TelegramBot.Worker/Services/CommandPreparer.cs` | `WorkerBridge/Core/XmlIo.cs`, `WorkerBridge/Commands/WorkerCommandHandler.cs` |
| `TelegramBot.Worker/Services/ProcessRunner.cs` | `WorkerBridge/Commands/WorkerCommandHandler.cs` |
| `Docs/TaskFile.schema.xsd` | `Docs/TaskFile.schema.xsd` |
| `Docs/ResultFile.schema.xsd` | `Docs/ResultFile.schema.xsd` |

Если эталон меняется, синхронно обновляются плагин, схемы, этот документ и код Worker.

## TaskFile

Worker создаёт task-файл в `TaskDirectory` до запуска процесса. Формат описан в
[TaskFile.schema.xsd](TaskFile.schema.xsd).

```xml
<?xml version="1.0" encoding="utf-8"?>
<taskFile>
  <commandId>42</commandId>
  <commandText>PDF</commandText>
  <filePath>C:/Projects/building.rvt</filePath>
  <resultFilePath>C:/Tasks/result_building_42.xml</resultFilePath>
  <options>
    <continueOnError>false</continueOnError>
  </options>
</taskFile>
```

| Поле | Тип | Обязательное | Описание |
|------|-----|:------------:|----------|
| `commandId` | `int` | да для Worker | ID команды из таблицы `Commands`. В эталоне `commandId` также служит **correlationId** — то же значение фигурирует в имени result-файла и per-task логе |
| `commandText` | `string` | да | `PDF`, `DWG`, `NWC`, `IFC`, `BIMDOC`, `CLASHREP`, `AUTORES` |
| `filePath` | `string` | да | Абсолютный путь к исходному файлу. Revit AddIn открывает `.rvt` сам через `Audit=true` и `DetachAndPreserveWorksets` |
| `resultFilePath` | `string` | да | Абсолютный путь, куда исполнитель обязан записать `ResultFile`. AddIn не имеет права вычислять путь сам |
| `options` | XML element | нет | Closed whitelist параметров |

`options` сейчас поддерживает только `continueOnError` (`bool`) для PDF/DWG. `openFolder`, `addBookmarks`,
`paperFormat`, `orientation`, `dpi` не входят в контракт. Формат печати/ориентация/DPI/буклеты в эталоне
владеет сам AddIn (auto-detect по размеру листа, bookmarks всегда включены) — не параметры контракта.

> ⚠️ Worker никогда не записывает `<options>` (`CommandPreparer.CreateTaskFile` не устанавливает
> `Options`), AddIn никогда не читает `task.Options` — open-опции (`Audit=true`,
> `DetachAndPreserveWorksets`) захардкожены в `TaskExecutor.CreateWorkerOpenOptions`. Мёртвый код с
> обеих сторон, на обмен не влияет. Поле оставлено для будущих расширений.

## ResultFile

Исполнитель атомарно пишет result-файл по пути из `taskFile/resultFilePath`. Формат описан в
[ResultFile.schema.xsd](ResultFile.schema.xsd).

```xml
<?xml version="1.0" encoding="utf-8"?>
<resultFile>
  <status>done</status>
  <outputFiles>C:/Export/building.pdf</outputFiles>
</resultFile>
```

```xml
<?xml version="1.0" encoding="utf-8"?>
<resultFile>
  <status>failed</status>
  <errorMessage>Document is not saved</errorMessage>
  <errorDetails>System.InvalidOperationException: ...</errorDetails>
</resultFile>
```

| Поле | Тип | Описание |
|------|-----|----------|
| `status` | enum | `done`, `failed`, `cancelled`. `done` означает, что команда выполнена **полностью**; частично выполненный экспорт — всегда `failed` (частичного успеха контракт не допускает) |
| `errorMessage` | `string?` | Короткое сообщение для пользователя. Бот показывает как есть. Формат в эталоне: `<Категория> in <Место>: <причина>` — категория выводится из типа исключения, место — первый non-system stack frame |
| `errorDetails` | `string?` | Полная диагностика/stack trace для поддержки |
| `outputFiles` | `string?` | Одна строка, не массив. Для PDF/NWC — файл, для DWG — папка экспорта |

Null/empty optional elements исполнитель опускает.

## CLI Arguments

Revit запускается строго с четырьмя аргументами:

```text
Revit.exe /command "WORKER" "{TaskFilePath}"
```

| Позиция | Значение |
|:-------:|----------|
| 0 | `Revit.exe` или полный путь |
| 1 | literal `/command` |
| 2 | fixed dispatcher `WORKER` |
| 3 | абсолютный путь к task-файлу (`.xml`) |

`args[2]` не является типом экспорта. Реальная команда читается только из `taskFile/commandText`.
Путь к `.rvt` не передаётся в CLI args и живёт только в `taskFile/filePath`. Лишние флаги
(`/language`, `/nosplash` и т.п.) между dispatcher'ом и путём к task-файлу запрещены — argv строго
позиционный.

Плейсхолдеры `ArgumentsTemplate`:

| Плейсхолдер | Описание |
|-------------|----------|
| `{CommandText}` | Код команды для console/wrapper-команд; не dispatcher Revit AddIn |
| `{TaskFilePath}` | Полный путь к task-файлу |
| `{ResultFilePath}` | Полный путь к result-файлу |
| `{CommandId}` | ID команды |
| `{FilePath}` | Не используется Revit AddIn; путь передаётся в `taskFile/filePath` |

Текущие шаблоны Worker для Revit-команд:

```json
"PDF":    "/command \"WORKER\" \"{TaskFilePath}\""
"DWG":    "/command \"WORKER\" \"{TaskFilePath}\""
"IFC":    "/command \"WORKER\" \"{TaskFilePath}\""
"BIMDOC": "/command \"WORKER\" \"{TaskFilePath}\""
"NWC":    "/command \"WORKER\" \"{TaskFilePath}\""
```

## TaskDirectory

Worker и BIM-исполнитель используют одну и ту же директорию обмена. Ни одна сторона не должна заменять её
на `Path.GetTempPath()`, текущую рабочую директорию или локально вычисленный путь.

Дефолт TelegramBot.Worker:

```text
%USERPROFILE%\Documents\TelegramBot\TaskDirectory\
```

Дефолт эталона (AddIn, RevitBIMFusion):

```text
%USERPROFILE%\Documents\RevitBIMFusion\TaskDirectory\
```

> ⚠️ Дефолты различаются по названию подпапки — не проблема, т.к. путь всегда передаётся исполнителю
> явно через `taskFile/resultFilePath` (абсолютный), а не выводится AddIn'ом из своего дефолта. При
> деплое проверять, что `FileSystem:TaskDirectory` Worker'а и реальная директория AddIn'а совпадают.

Причины не использовать `%TEMP%`: разные аккаунты Worker/Revit имеют разные temp-директории, service-profile
изоляция ломает доступ из интерактивной сессии, cleaner'ы могут удалить файл во время длительного экспорта.

## Atomic Write And Cleanup

Worker пишет task-файл как `{path}.tmp` → rename в целевой `task_*.xml`. Исполнитель делает то же самое для
`result_*.xml`.

**Ответственность за удаление (обновлено в эталоне):**

- **TaskFile** — теперь удаляет **AddIn** после успешной записи `ResultFile`, но только в headless-режиме
  (`TaskFilePathResolver.IsHeadlessLaunch()`; в debug/`OpenFileDialog`-режиме файл сохраняется для анализа).
  Реализовано в `WorkerCommandHandler.TryDeleteTaskFile`. Сбой удаления логируется как warning и не влияет
  на уже записанный `ResultFile`.
- **ResultFile** — по-прежнему удаляет **Worker** после того, как прочитал и обработал результат.

Worker's `CommandPreparer.CleanupTempFiles` по-прежнему best-effort удаляет оба файла (task и result) после
завершения попытки — для task-файла это теперь избыточно (обычно уже удалён AddIn'ом), но не вредно:
`File.Exists` проверяется перед `File.Delete`, повторное удаление не бросает исключение.

Если result-файл невалиден, Worker переименовывает его в `.bad` для диагностики.

Имя файла — `task_{projectName}_{commandId}.xml`, без `attemptToken`. Retry одной команды
перезаписывает файл предыдущей попытки; если та ещё пишет файл в момент старта retry — возможна
коллизия/stale result. Принятый trade-off ради 1:1 совпадения имени с эталоном (AddIn имя файла не
парсит, путь читает из `args[3]`).

## Runtime Validation

Перед atomic rename Worker валидирует tmp-task-файл по XSD (`TaskFileValidator.Validate`, embedded-копия
`Docs/TaskFile.schema.xsd` в `TelegramBot.Worker/Schemas/`). При расхождении C#-модели `TaskFile` и схемы
(tmp не проходит валидацию) Worker **abort'ит** создание task-файла: команда завершается `Failed` на этапе
старта, плагин не получает невалидный файл. Это ловит drift модель↔XSD в рантайме, а не только при сборке.
Result-файл от плагина Worker парсит через `XmlSerializer` и при невалидном XML/`status` переименовывает в
`.bad` (см. «Как Worker трактует ResultFile»).

## Watch Timeout на стороне AddIn

Эталон описывает in-process watch timeout, которым AddIn защищается от зависшего Revit-процесса (модальный
диалог, который некому закрыть, зависший вызов Revit API, network/workset stall):

| Свойство | Значение |
|----------|----------|
| Владелец | AddIn (эталон), не оркестратор |
| Таймаут | 30 минут (hardcoded, `TaskExecutionWatch`) |
| Действие при таймауте | `LogCritical`, затем `Process.GetCurrentProcess().Kill()` |
| Результат для Worker | Процесс завершается **без ResultFile** — тот же contract-valid сигнал сбоя, что и другие случаи отсутствия result-файла; Worker применяет обычную retry-политику |

Это не требует изменений в TelegramBot.Worker — существующая обработка «процесс завершился без
result-файла» уже покрывает этот случай.

## Как Worker трактует ResultFile

| Условие | Статус команды |
|---------|----------------|
| `status=done` | `Done` |
| `status=failed` | `Failed` или retry через `ErrorClassifier` |
| `status=cancelled` | `Failed` без retry |
| Result XML битый или `status` неизвестен | `.bad`, затем `Failed` или retry |
| Result XML отсутствует, exit code `0` | `Done` fallback |
| Result XML отсутствует, exit code не `0` | `Failed` или retry |
| Таймаут | процесс убивается, команда `Failed` |

## Поддерживаемые команды

| Команда | Назначение | Статус в эталоне |
|---------|------------|:----------------:|
| `PDF` | Export sheets to PDF | supported |
| `DWG` | Export to AutoCAD DWG | supported |
| `NWC` | Export to Navisworks NWC | supported |
| `IFC` | Export to IFC | planned |
| `BIMDOC` | BIM documentation | planned |
| `CLASHREP` | Clash report | planned |
| `AUTORES` | Automatic clash resolution | planned |

Unknown/planned команды возвращают `NotImplemented:` в `errorMessage`; Worker классифицирует это как
permanent failure без retry.
