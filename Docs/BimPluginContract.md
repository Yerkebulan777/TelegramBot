# Контракт BIM-плагинов

> CANONICAL CONTRACT (эталон):
>
> ```text
> C:\Users\y.zhumabayev\Yandex.Disk\Repository\RevitBIMFusion\Docs\BimPluginContract.md
> ```
>
> Этот документ — worker-side отражение эталонного XML-контракта для `TelegramBot.Worker`.

## Соответствие эталону

TelegramBot.Worker обменивается с BIM-исполнителем через XML-файлы в `TaskDirectory`.

| Наша реализация (TelegramBot) | Эталон (RevitBIMFusion) |
|------------------------------|-------------------------|
| `TelegramBot.Core/Models/TaskFile.cs` | `WorkerBridge/Core/Contracts.cs` |
| `TelegramBot.Core/Models/ResultFile.cs` | `WorkerBridge/Core/Contracts.cs` |
| `TelegramBot.Worker/Services/CommandPreparer.cs` | `WorkerBridge/Core/XmlIo.cs` |
| `TelegramBot.Worker/Services/ProcessRunner.cs` | `WorkerBridge/Commands/WorkerCommandHandler.cs` |
| `Docs/TaskFile.schema.xsd` | `Docs/TaskFile.schema.xsd` |
| `Docs/ResultFile.schema.xsd` | `Docs/ResultFile.schema.xsd` |

Если эталон меняется, синхронно обновляются плагин, схемы, этот документ и код Worker.

## TaskFile

Worker создаёт `task_{CommandId}_{AttemptToken}.xml` в `TaskDirectory` до запуска процесса.
Формат описан в [TaskFile.schema.xsd](TaskFile.schema.xsd).

```xml
<?xml version="1.0" encoding="utf-8"?>
<taskFile>
  <commandId>42</commandId>
  <commandText>PDF</commandText>
  <filePath>C:/Projects/building.rvt</filePath>
  <resultFilePath>C:/Tasks/result_42_abc123.xml</resultFilePath>
  <options>
    <continueOnError>false</continueOnError>
  </options>
</taskFile>
```

| Поле | Тип | Обязательное | Описание |
|------|-----|:------------:|----------|
| `commandId` | `int` | да для Worker | ID команды из таблицы `Commands` |
| `commandText` | `string` | да | `PDF`, `DWG`, `NWC`, `IFC`, `BIMDOC`, `CLASHREP`, `AUTORES` |
| `filePath` | `string` | да | Абсолютный путь к исходному файлу. Revit AddIn открывает `.rvt` сам через `Audit=true` и `DetachAndPreserveWorksets` |
| `resultFilePath` | `string` | да | Абсолютный путь, куда исполнитель обязан записать `ResultFile` |
| `options` | XML element | нет | Closed whitelist параметров |

`options` сейчас поддерживает только `continueOnError` (`bool`) для PDF/DWG. `openFolder`, `addBookmarks`,
`paperFormat`, `orientation`, `dpi` не входят в контракт.

## ResultFile

Исполнитель атомарно пишет `result_{CommandId}_{AttemptToken}.xml` по пути из `taskFile/resultFilePath`.
Формат описан в [ResultFile.schema.xsd](ResultFile.schema.xsd).

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
| `status` | enum | `done`, `failed`, `cancelled` |
| `errorMessage` | `string?` | Короткое сообщение для пользователя. Бот показывает как есть |
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
| 3 | абсолютный путь к `task_*.xml` |

`args[2]` не является типом экспорта. Реальная команда читается только из `taskFile/commandText`.
Путь к `.rvt` не передаётся в CLI args и живёт только в `taskFile/filePath`.

Плейсхолдеры `ArgumentsTemplate`:

| Плейсхолдер | Описание |
|-------------|----------|
| `{CommandText}` | Код команды для console/wrapper-команд; не dispatcher Revit AddIn |
| `{TaskFilePath}` | Полный путь к `task_{CommandId}_{AttemptToken}.xml` |
| `{ResultFilePath}` | Полный путь к `result_{CommandId}_{AttemptToken}.xml` |
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

Эталон RevitBIMFusion использует тот же принцип и допускает override через конфигурацию. Конкретный
result-path всегда передаётся исполнителю в `taskFile/resultFilePath`; исполнитель обязан использовать его
verbatim.

Причины не использовать `%TEMP%`: разные аккаунты Worker/Revit имеют разные temp-директории, service-profile
изоляция ломает доступ из интерактивной сессии, cleaner'ы могут удалить файл во время длительного экспорта.

## Atomic Write And Cleanup

Worker пишет task-файл как `{path}.tmp` → rename в целевой `task_*.xml`. Исполнитель делает то же самое для
`result_*.xml`.

Worker после завершения попытки очищает `task_*.xml` и `result_*.xml`. Если result-файл невалиден, Worker
переименовывает его в `.bad` для диагностики.

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
