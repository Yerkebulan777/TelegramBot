# Контракт BIM-плагинов

> ⚠️ **CANONICAL CONTRACT (эталон)** находится в:
>
> ```text
> C:\Users\y.zhumabayev\Yandex.Disk\Repository\RevitBIMFusion\Docs\BimPluginContract.md
> ```
>
> Этот документ — **worker-side отражение** эталонного контракта, актуальное описание **нашей реализации**
> (TaskDirectory, плейсхолдеры, поведение Worker'а). Формат JSON, схемы, правила обработки `.rvt` живут в
> эталоне.

## Соответствие эталону

**Реализация TelegramBot.Worker полностью соответствует эталонному контракту.** Любые изменения в коде,
затрагивающие обмен `Worker ↔ BIM-плагин`, **обязаны** сохранять это соответствие:

| Наша реализация (TelegramBot) | Эталон (RevitBIMFusion) |
|------------------------------|-------------------------|
| `TelegramBot.Core/Models/TaskFile.cs` | `WorkerBridge/Core/Contracts.cs` (DTO) |
| `TelegramBot.Core/Models/ResultFile.cs` | `WorkerBridge/Core/Contracts.cs` (DTO) |
| `TelegramBot.Worker/Services/CommandPreparer.cs` | `WorkerBridge/Core/JsonIo.cs` (IO utilities) |
| `TelegramBot.Worker/Services/ProcessRunner.cs` | `WorkerBridge/Commands/WorkerCommandHandler.cs` (executor) |
| `TelegramBot.Core/Config/WorkerOptions.cs` + `appsettings.json` | `RevitBIMFusion/Infrastructure/Worker/BimTaskExecutor.cs` |

Если эталон изменился — **сначала** обновляется эталон + плагин (RevitBIMFusion), **потом** синхронизируется
наша реализация (этот документ + код) в одном релизе.

## Что отдаёт Worker (TaskFile)

Worker пишет `task_{CommandId}_{AttemptToken}.json` в **TaskDirectory** (см. ниже). Формат файла — см.
[TaskFile.schema.json](TaskFile.schema.json).

| Поле | Тип | Описание |
|------|-----|----------|
| `commandId` | `int` | ID команды из таблицы `Commands` |
| `commandText` | `string` | Код команды: `PDF`, `DWG`, `IFC`, `BIMDOC`, `NWC`, `CLASHREP`, `AUTORES` |
| `filePath` | `string` | Абсолютный путь к исходному файлу (.rvt, .rfa, .nwc, …). AddIn открывает файл **сам** через `OpenOptions { Audit = true, DetachAndPreserveWorksets }` |
| `resultFilePath` | `string` | Абсолютный путь в **TaskDirectory**, куда исполнитель обязан записать `ResultFile` |
| `options` | `JsonElement?` | Closed whitelist: поддерживается только `continueOnError` (bool) для PDF/DWG. Неизвестные ключи вне контракта |

`AttemptToken` — GUID без дефисов, новый для каждой retry-попытки. Worker подставляет его в имя файла и в
`resultFilePath`.

## Что Worker ожидает получить (ResultFile)

Исполнитель пишет `result_{CommandId}_{AttemptToken}.json` в `resultFilePath` из TaskFile. Формат файла — см.
[ResultFile.schema.json](ResultFile.schema.json).

| Поле | Тип | Описание |
|------|-----|----------|
| `status` | enum: `done` \| `failed` \| `cancelled` | Обязательное. Сериализуется camelCase через `JsonStringEnumConverter` |
| `errorMessage` | `string?` | Краткое сообщение об ошибке при `failed`/`cancelled` |
| `errorDetails` | `string?` | Полный stack trace / диагностика при неожиданных исключениях |
| `outputFiles` | `string?` | Путь к выходному файлу при `done`. Несмотря на множественное число в имени — **одна строка**, не массив (соответствует канону). Для PDF/NWC — путь к единственному файлу. Для DWG (один `.dwg` на лист) — путь к папке экспорта, а не список листов. Поле опускается из JSON при `null` (WhenWritingNull) |

**Трактовка статусов в Worker'е:**

| Status | Действие |
|--------|----------|
| `done` | `Done` в БД, уведомление в Telegram, retry counter не растёт |
| `failed` | `ErrorClassifier` → permanent (Failed) или transient (retry с экспоненциальной задержкой) |
| `cancelled` | Трактовать как `permanent failure` без retry. Плагин сам сказал «отмена» — повторять бессмысленно |

Если result-файл отсутствует, битый JSON или неизвестный `status` → файл переименовывается в `.bad`, попытка
считается ошибочной (через `ErrorClassifier`). Если файла нет вообще — fallback по exit code (`0` = `Done`,
иначе → `ErrorClassifier`).

## CLI Arguments (плейсхолдеры ArgumentsTemplate)

Worker запускает исполнителя по шаблону `ArgumentsTemplate` из `Worker:Commands:{CommandText}` в
`appsettings.json`.

**КРИТИЧЕСКОЕ ПРАВИЛО** (см. эталон §CLI Arguments): путь к исходному `.rvt` **НЕ передаётся** в CLI args —
он передаётся **только** в `TaskFile.filePath`. Это гарантирует, что AddIn откроет файл сам с правильными
`OpenOptions`.

```text
Revit.exe /command "WORKER" "{TaskFilePath}"
```

| Позиция | Аргумент | Пример | Обязателен |
|:-------:|----------|--------|:----------:|
| 0 | Исполняемый файл | `Revit.exe` или полный путь | да |
| 1 | `/command` | Ключ команды Revit API | да |
| 2 | dispatcher | `"WORKER"` | да |
| 3 | `taskFilePath` | `"C:\\…\\task_42_abc123.json"` | да |

**Строгое правило (эталон §CLI Arguments):** Revit.exe запускается ровно с 4 аргументами.
Дополнительные флаги (например, `/nosplash`) между dispatcher и путём к task-файлу **запрещены** —
`TaskFilePathResolver.Resolve()` валидирует строгий positional layout и не сканирует argv дальше `args[3]`.
Все текущие шаблоны Worker'а (см. ниже) соответствуют этому правилу.

`args[2]` — fixed dispatcher (`WorkerOptions.RevitDispatcherCommand = "WORKER"`), а не тип экспорта.
Реальная команда (`PDF`, `DWG`, `NWC`, ...) живёт только в `TaskFile.commandText` и читается исполнителем
из JSON. Если Worker передаст в `args[2]` реальный commandText вроде `"PDF"`, AddIn не будет угадывать
путь к task-файлу: strict resolver уйдёт в debug fallback (`OpenFileDialog`) или вернёт `Cancelled`
в headless-режиме без `ResultFile`.

**Плейсхолдеры** в `ArgumentsTemplate`:

| Плейсхолдер | Описание | Используется в шаблонах |
|-------------|----------|--------------------------|
| `{CommandText}` | Код команды | Console/wrapper-команды, но **не** Revit AddIn dispatcher |
| `{TaskFilePath}` | Полный путь к `task_{CommandId}_{AttemptToken}.json` | Все |
| `{ResultFilePath}` | Полный путь к `result_*.json` | Только если плагин берёт из CLI (опционально) |
| `{CommandId}` | ID команды | Только если плагину нужен (опционально) |
| `{FilePath}` | ⚠️ **НЕ ИСПОЛЬЗУЕТСЯ** | Передаётся только в `TaskFile.filePath` |

Текущие шаблоны в `appsettings.json` Worker'а (полное соответствие эталону):

```json
"PDF":     "/command \"WORKER\" \"{TaskFilePath}\""
"DWG":     "/command \"WORKER\" \"{TaskFilePath}\""
"IFC":     "/command \"WORKER\" \"{TaskFilePath}\""
"BIMDOC":  "/command \"WORKER\" \"{TaskFilePath}\""
"NWC":     "/command \"WORKER\" \"{TaskFilePath}\""
"CLASHREP":"/command \"{CommandText}\" \"{TaskFilePath}\""
"AUTORES": "ai_agent.py --command \"{CommandText}\" --task \"{TaskFilePath}\""
```

## Расположение файлов (TaskDirectory)

Worker и BIM-исполнители обмениваются JSON через **выделенную папку TaskDirectory** — это **часть контракта**
(см. эталон §TaskFile Location), а не деталь реализации. Ни одна из сторон **не вправе** подменять этот путь
на собственный `Path.GetTempPath()`: `%TEMP%` у Worker'а (Windows-сервис) и у AddIn'а (интерактивная сессия
Revit) — это разные директории (`C:\Windows\System32\config\systemprofile\AppData\Local\Temp\` против
`C:\Users\<user>\AppData\Local\Temp\`), и стороны просто не найдут файлы друг друга.

**Дефолтный путь:**

```text
%USERPROFILE%\Documents\TelegramBot\TaskDirectory\
```

Это намеренно на **одном уровне с `Logs\`** в каталоге `Documents\TelegramBot\` — рядом с логами Worker'а,
чтобы админу было легко ориентироваться:

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

**Важно:** при override путь **обязан быть одинаковым** на обеих сторонах контракта. Worker публикует
конкретный путь в `TaskFile.resultFilePath` для каждой команды, и AddIn обязан использовать его as-is.
Worker создаёт директорию автоматически на старте; если создать не удалось — Worker **падает** с понятной
ошибкой.

**Почему именно `TaskDirectory`, а не `Path.GetTempPath()`** (полная версия — в эталоне §TaskFile Location):

1. **Cross-account mismatch.** `%TEMP%` у Worker'а и у AddIn'а — разные директории. Каждая сторона пишет в
   свой `TempPath` → вторая сторона не видит файл.
2. **Service-profile isolation.** Под `LocalSystem` `TempPath` недоступен интерактивной сессии без элевации.
3. **Volatility.** Disk Cleanup, сторонние cleaner'ы, перезагрузка могут удалить файлы из `%TEMP%` посреди
   длительного Revit-экспорта (до 3 часов).

**Атомарность записи** (эталон §Atomic result publication):

- `CommandPreparer.CreateTaskFile` пишет `{path}.tmp` → `File.Move(.tmp, path, overwrite: true)`. Это
  эквивалентно канонической последовательности `File.Delete(target) → File.Move(.tmp, target)`.
- Плагин (BIM executor — `ResultFileWriter.Write` в `WorkerBridge/Core/JsonIo.cs`) делает то же самое для
  `resultFilePath`.
- `ResultFile.WriteSafe` (в плагине) гарантирует запись result-файла для **каждого** исхода (success,
  expected error, unknown command, exception) через `try/finally`.
- Worker удаляет `result_*` сразу после успешного парсинга; `task_*` и `result_*.bad` — в `finally` через
  `CommandPreparer.CleanupTempFiles`.

## JSON-схемы (для справки)

Копии эталонных схем лежат рядом с этим документом:

| Документ | Файл |
|----------|------|
| TaskFile schema | [TaskFile.schema.json](TaskFile.schema.json) |
| ResultFile schema | [ResultFile.schema.json](ResultFile.schema.json) |

Эти схемы — **single source of truth** для формата JSON. Если в коде появится расхождение со схемой — это
баг, который должен быть исправлен, а не задокументирован.

> **TODO (не реализовано):** добавить опциональную strict-валидацию выходного task-файла по схеме на старте
> Worker'а (через `JsonSchema.Net` или аналог), чтобы ловить drift в dev/test.

## Поддерживаемые команды (соответствие эталону)

Согласно эталону `…\RevitBIMFusion\Docs\BimPluginContract.md` §Supported Commands:

| Команда | Назначение | Статус в эталоне | Конфигурация Worker'а |
|---------|-----------|------------------|------------------------|
| `PDF` | Export sheets to PDF | supported | `Worker:Commands:PDF` (Revit.exe) |
| `DWG` | Export to AutoCAD DWG | supported | `Worker:Commands:DWG` (Revit.exe) |
| `NWC` | Export to Navisworks NWC | supported | `Worker:Commands:NWC` (Revit.exe + AddIn) |
| `IFC` | Export to IFC | planned (NotImplemented) | `Worker:Commands:IFC` (Revit.exe) |
| `BIMDOC` | BIM documentation | planned | `Worker:Commands:BIMDOC` (Revit.exe) |
| `CLASHREP` | Clash report | planned | `Worker:Commands:CLASHREP` (FileConvert.exe) |
| `AUTORES` | Automatic clash resolution | planned | `Worker:Commands:AUTORES` (python, `WorkingDirectory: "."`) |

Плагин возвращает `NotImplemented: <command>. …` в `errorMessage` для planned-команд → `ErrorClassifier`
классифицирует как permanent failure без retry.

## Revit AddIn — правила открытия файла

Любой автоматический flow, который открывает `task.filePath`, **обязан** использовать Revit `OpenOptions` с
`Audit = true` и `DetachFromCentralOption.DetachAndPreserveWorksets` (для workshared-моделей). Это защищает
исходный `.rvt` от случайной модификации во время headless worker execution. Реализация — на стороне
AddIn'а, Worker не может это проверить.

`TaskFilePathResolver.Resolve()` (в плагине) валидирует строгий positional layout:
`args[1] == "/command"`, `args[2] == "WORKER"`, `args[3] == "<task>.json"`. Resolver не сканирует argv
дальше `args[3]` и не угадывает путь.

## Автогенерация result-файла

`ResultFileWriter.Write` в плагине (эталон §How the Add-In Writes ResultFile):

1. Создаёт директорию `resultFilePath` при необходимости.
2. Сериализует `ResultFile` в `{resultFilePath}.tmp`.
3. Переименовывает `.tmp` → `resultFilePath` (атомарно).

`ResultFile` пишется **для каждого** исхода: success, expected error, unknown command, invalid JSON,
unexpected executor exception. `WorkerCommandHandler.Execute` использует `try/finally` +
`ResultFileWriter.WriteSafe` для гарантии.

**Исключения**, когда result не пишется в primary path (эталон §Cases Where ResultFile Is Not Written to
the Primary Path):

1. Task-file selection cancelled (`OpenFileDialog` → Cancel или headless host) — manual debug only.
2. `task.ResultFilePath` пуст — AddIn ничего не пишет и не invent'ит cwd/temp fallback.
3. `TaskFileParser.Parse` упал до получения валидного `resultFilePath` — AddIn возвращает `Cancelled`, но
   `ResultFile` не пишет.
4. Primary-path write failed — fallback `resultFilePath + ".error.json"`.
5. AddIn не стартовал (Revit crash) — вне контракта.

Worker **не различает** эти случаи: если файла по `resultFilePath` нет — fallback по exit code.

## Дополнительные правила (эталон §Paths)

Все пути в контракте — **абсолютные Windows paths** с буквой диска. Windows .NET I/O API корректно
обрабатывают оба стиля разделителей (`\` и `/`).

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
