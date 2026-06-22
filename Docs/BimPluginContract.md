# Контракт BIM-плагинов

> ⚠️ **CANONICAL CONTRACT (эталон)** находится в:
>
> ```text
> C:\Users\y.zhumabayev\Yandex.Disk\Repository\RevitBIMFusion\Docs\BimPluginContract.md
> ```
>
> Этот документ — **worker-side отражение** эталонного контракта, актуальное описание **нашей реализации** (TaskDirectory, плейсхолдеры, поведение Worker'а). Формат JSON, схемы, правила обработки `.rvt` живут в эталоне.

## Соответствие эталону

**Реализация TelegramBot.Worker полностью соответствует эталонному контракту.** Любые изменения в коде, затрагивающие обмен `Worker ↔ BIM-плагин`, **обязаны** сохранять это соответствие:

- `TelegramBot.Core/Models/TaskFile.cs` ↔ `…\RevitBIMFusion\Docs\TaskFile.schema.json`
- `TelegramBot.Core/Models/ResultFile.cs` ↔ `…\RevitBIMFusion\Docs\ResultFile.schema.json`
- `TelegramBot.Worker/Services/CommandPreparer.cs` (`CreateTaskFile`, `CreateProcessStartInfo`)
- `TelegramBot.Worker/Services/ProcessRunner.cs` (`TryReadResultFile`, `HandleFailureAsync`)
- `TelegramBot.Core/Config/WorkerOptions.cs` (`Commands` map) + `TelegramBot.Worker/appsettings.json` (секция `Worker:Commands`)

Если эталон изменился — **сначала** обновляется эталон + плагин (RevitBIMFusion), **потом** синхронизируется наша реализация (этот документ + код) в одном релизе.

## Что отдаёт Worker (TaskFile)

Worker пишет `task_{CommandId}_{AttemptToken}.json` в **TaskDirectory** (см. ниже). Формат файла — см. [TaskFile.schema.json](TaskFile.schema.json).

| Поле | Тип | Описание |
|------|-----|----------|
| `commandId` | `int` | ID команды из таблицы `Commands` |
| `commandText` | `string` | Код команды: `PDF`, `DWG`, `IFC`, `BIMDOC`, `NWC`, `CLASHREP`, `AUTORES` |
| `filePath` | `string` | Абсолютный путь к исходному файлу (.rvt, .rfa, .nwc, …). AddIn открывает файл **сам** через `OpenOptions { Audit = true, DetachAndPreserveWorksets }` |
| `resultFilePath` | `string` | Абсолютный путь в **TaskDirectory**, куда исполнитель обязан записать `ResultFile` |
| `options` | `JsonElement?` | Closed whitelist: поддерживается только `continueOnError` (bool) для PDF/DWG. Неизвестные ключи вне контракта |

`AttemptToken` — GUID без дефисов, новый для каждой retry-попытки. Worker подставляет его в имя файла и в `resultFilePath`.

## Что Worker ожидает получить (ResultFile)

Исполнитель пишет `result_{CommandId}_{AttemptToken}.json` в `resultFilePath` из TaskFile. Формат файла — см. [ResultFile.schema.json](ResultFile.schema.json).

| Поле | Тип | Описание |
|------|-----|----------|
| `status` | enum: `done` \| `failed` \| `cancelled` | Обязательное. Сериализуется camelCase через `JsonStringEnumConverter` |
| `errorMessage` | `string?` | Краткое сообщение об ошибке при `failed`/`cancelled` |
| `errorDetails` | `string?` | Полный stack trace / диагностика при неожиданных исключениях |
| `outputFiles` | `string[]?` | Список созданных файлов при `done`. Может быть пустым или `null` |

**Трактовка статусов в Worker'е:**

| Status | Действие |
|--------|----------|
| `done` | `Done` в БД, уведомление в Telegram, retry counter не растёт |
| `failed` | `ErrorClassifier` → permanent (Failed) или transient (retry с экспоненциальной задержкой) |
| `cancelled` | Трактовать как `permanent failure` без retry. Плагин сам сказал «отмена» — повторять бессмысленно |

Если result-файл отсутствует, битый JSON или неизвестный `status` → файл переименовывается в `.bad`, попытка считается ошибочной (через `ErrorClassifier`). Если файла нет вообще — fallback по exit code (`0` = `Done`, иначе → `ErrorClassifier`).

## CLI Arguments (плейсхолдеры ArgumentsTemplate)

Worker запускает исполнителя по шаблону `ArgumentsTemplate` из `Worker:Commands:{CommandText}` в `appsettings.json`.

**КРИТИЧЕСКОЕ ПРАВИЛО** (см. эталон §CLI Arguments): путь к исходному `.rvt` **НЕ передаётся** в CLI args — он передаётся **только** в `TaskFile.filePath`. Это гарантирует, что AddIn откроет файл сам с правильными `OpenOptions`.

```text
Revit.exe /command "{CommandText}" "{TaskFilePath}"
```

| Позиция | Аргумент | Пример | Обязателен |
|:-------:|----------|--------|:----------:|
| 0 | Исполняемый файл | `Revit.exe` или полный путь | да |
| 1 | `/command` | Ключ команды Revit API | да |
| 2 | `commandText` | `"PDF"` | да |
| 3 | `taskFilePath` | `"C:\\…\\task_42_abc123.json"` | да |

**Плейсхолдеры** в `ArgumentsTemplate`:

| Плейсхолдер | Описание | Используется в шаблонах |
|-------------|----------|--------------------------|
| `{CommandText}` | Код команды | Все |
| `{TaskFilePath}` | Полный путь к `task_{CommandId}_{AttemptToken}.json` | Все |
| `{ResultFilePath}` | Полный путь к `result_*.json` | Только если плагин берёт из CLI (опционально) |
| `{CommandId}` | ID команды | Только если плагину нужен (опционально) |
| `{FilePath}` | ⚠️ **НЕ ИСПОЛЬЗУЕТСЯ** | Передаётся только в `TaskFile.filePath` |

Текущие шаблоны в `appsettings.json` Worker'а (полное соответствие эталону):

```json
"PDF":     "/command \"{CommandText}\" \"{TaskFilePath}\""
"DWG":     "/command \"{CommandText}\" \"{TaskFilePath}\""
"IFC":     "/command \"{CommandText}\" \"{TaskFilePath}\""
"BIMDOC":  "/command \"{CommandText}\" \"{TaskFilePath}\""
"NWC":     "/command \"{CommandText}\" \"{TaskFilePath}\""
"CLASHREP":"/command \"{CommandText}\" \"{TaskFilePath}\""
"AUTORES": "ai_agent.py --command \"{CommandText}\" --task \"{TaskFilePath}\""
```

## Расположение файлов (TaskDirectory)

Worker и BIM-исполнители обмениваются JSON через **выделенную папку TaskDirectory** — **не** через `Path.GetTempPath()`. Это нужно, чтобы:

- Windows/system cleaners не удалили task/result-файлы посреди длительной команды (Revit-экспорт может идти до 3 часов).
- Админ мог открыть папку вручную, проверить активные попытки и просмотреть историю для отладки.

**Путь по умолчанию:**

```text
%USERPROFILE%\Documents\TelegramBot\TaskDirectory\
```

Это намеренно на **одном уровне с `Logs\`** в каталоге `Documents\TelegramBot\` — рядом с логами Worker'а, чтобы админу было легко ориентироваться:

```text
%USERPROFILE%\Documents\TelegramBot\
├── Logs\
│   └── Worker\              ← Serilog-логи Worker'а
└── TaskDirectory\           ← task_*.json + result_*.json (живут минуты-часы)
```

**Override** через конфигурацию `FileSystem:TaskDirectory` в `TelegramBot.Worker/appsettings.json`:

```json
{
  "FileSystem": {
    "TaskDirectory": "D:\\TelegramBot\\Worker\\TaskDirectory"
  }
}
```

Worker создаёт директорию автоматически на старте; если создать не удалось — Worker **падает** с понятной ошибкой.

**Атомарность записи** (эталон §Atomic result publication):

- `CommandPreparer.CreateTaskFile` пишет `{path}.tmp` → `File.Move(.tmp, path, overwrite: true)`.
- Плагин (BIM executor) должен делать то же самое для `resultFilePath`.
- Worker удаляет `result_*` сразу после успешного парсинга; `task_*` и `result_*.bad` — в `finally` через `CommandPreparer.CleanupTempFiles`.

## JSON-схемы (для справки)

Копии эталонных схем лежат рядом с этим документом:

| Документ | Файл |
|----------|------|
| TaskFile schema | [TaskFile.schema.json](TaskFile.schema.json) |
| ResultFile schema | [ResultFile.schema.json](ResultFile.schema.json) |

Эти схемы — **single source of truth** для формата JSON. Если в коде появится расхождение со схемой — это баг, который должен быть исправлен, а не задокументирован.

> **TODO (не реализовано):** добавить опциональную strict-валидацию выходного task-файла по схеме на старте Worker'а (через `JsonSchema.Net` или аналог), чтобы ловить drift в dev/test.

## Поддерживаемые команды (соответствие эталону)

Согласно эталону `…\RevitBIMFusion\Docs\BimPluginContract.md` §Supported Commands:

| Команда | Назначение | Статус в эталоне | Конфигурация Worker'а |
|---------|-----------|------------------|------------------------|
| `PDF` | Export sheets to PDF | supported | `Worker:Commands:PDF` (Revit.exe) |
| `DWG` | Export to AutoCAD DWG | supported | `Worker:Commands:DWG` (Revit.exe) |
| `NWC` | Export to Navisworks NWC | supported | `Worker:Commands:NWC` (FileConvert.exe) |
| `IFC` | Export to IFC | planned (NotImplemented) | `Worker:Commands:IFC` (Revit.exe) |
| `BIMDOC` | BIM documentation | planned | `Worker:Commands:BIMDOC` (Revit.exe) |
| `CLASHREP` | Clash report | planned | `Worker:Commands:CLASHREP` (FileConvert.exe) |
| `AUTORES` | Automatic clash resolution | planned | `Worker:Commands:AUTORES` (python) |

Плагин возвращает `NotImplemented: <command>. …` в `errorMessage` для planned-команд → `ErrorClassifier` классифицирует как permanent failure без retry.

## Revit AddIn — правила открытия файла

Любой автоматический flow, который открывает `task.filePath`, **обязан** использовать Revit `OpenOptions` с `Audit = true` и `DetachFromCentralOption.DetachAndPreserveWorksets` (для workshared-моделей). Это защищает исходный `.rvt` от случайной модификации во время headless worker execution. Реализация — на стороне AddIn'а, Worker не может это проверить.

`WorkerCommandHandler.ResolveTaskFilePath` (в плагине) берёт **первый существующий** аргумент с расширением `.json` из `Environment.GetCommandLineArgs()`. Это делает flow толерантным к порядку аргументов.

## Автогенерация result-файла

`ResultFileWriter.Write` в плагине (эталон §How the Add-In Writes ResultFile):

1. Создаёт директорию `resultFilePath` при необходимости.
2. Сериализует `ResultFile` в `{resultFilePath}.tmp`.
3. Переименовывает `.tmp` → `resultFilePath` (атомарно).

`ResultFile` пишется **для каждого** исхода: success, expected error, unknown command, invalid JSON, unexpected executor exception. `WorkerCommandHandler.Execute` использует `try/finally` + `ResultFileWriter.WriteSafe` для гарантии.

**Исключения**, когда result не пишется в primary path (эталон §Cases Where ResultFile Is Not Written to the Primary Path):

1. Task-file selection cancelled (`OpenFileDialog` → Cancel) — manual debug only.
2. `task.ResultFilePath` пуст — fallback `result_error.json` в CWD.
3. Primary-path write failed — fallback `resultFilePath + ".error.json"`.
4. AddIn не стартовал (Revit crash) — вне контракта.

Worker **не различает** эти случаи: если файла по `resultFilePath` нет — fallback по exit code.

## Дополнительные правила (эталон §Paths)

Все пути в контракте — **абсолютные Windows paths** с буквой диска. Windows .NET I/O API корректно обрабатывают оба стиля разделителей (`\` и `/`).

**НЕ поддерживаются:**

| Стиль | Пример | Причина |
|-------|--------|---------|
| Git Bash | `/c/Projects/file.rvt` | Не Windows path |
| WSL | `/mnt/c/Projects/file.rvt` | Linux path |
| Relative | `../Projects/file.rvt` | Working directory неизвестен |
| Root-relative | `\Projects\file.rvt` | Нет буквы диска |

UNC paths (`\\server\share\file.rvt`) поддерживаются через стандартные .NET I/O API.

## Как Worker трактует результат

После завершения процесса:

| Условие | Статус команды |
|---------|----------------|
| Есть result JSON и `status = "done"` | `Done` |
| Есть result JSON и `status = "failed"` | `Failed` или retry через `ErrorClassifier`; `errorMessage` берётся из файла, `errorDetails` логируется |
| Есть result JSON и `status = "cancelled"` | `Failed` permanent (без retry) |
| Result JSON битый или `status` неизвестен | файл переименовывается в `.bad`, дальше `Failed` или retry |
| Result JSON отсутствует, exit code `0` | `Done` |
| Result JSON отсутствует, exit code не `0` | `Failed` или retry через `ErrorClassifier` |
| Таймаут | процесс убивается, команда `Failed` |
