# Контракт BIM-плагинов

> **Связанные документы:** [ExecutionAlgorithm.md](ExecutionAlgorithm.md) — очередь и статусы команд | [AGENTS.md](../AGENTS.md) — архитектура Worker/BimLib | [README.md](../README.md) — запуск и конфигурация

Этот документ описывает внешний контракт между `TelegramBot.Worker` и исполнителями BIM-команд: Revit AddIn, Navisworks/FileConvert-обёрткой и AI-скриптом.

## Общий принцип

Worker не выполняет экспорт сам. Он:

1. Берёт команду из PostgreSQL.
2. Валидирует файл и расширение.
3. Создаёт `task_{CommandId}_{AttemptToken}.json` во временной папке `Path.GetTempPath()`.
4. Запускает внешний процесс по `Worker:Commands:{CommandText}`.
5. Ждёт завершения процесса.
6. Сначала пытается прочитать `result_{CommandId}_{AttemptToken}.json`.
7. Если result-файла нет, использует fallback по exit code процесса. Если result-файл есть,
   но не читается, содержит битый JSON или неизвестный `status`, попытка считается ошибочной.
8. Удаляет temp-файлы текущей попытки в `finally`.

`AttemptToken` — GUID без дефисов, новый для каждой retry-попытки. Он нужен, чтобы не читать stale-result от прошлой попытки и не конфликтовать при параллельных запусках.

## JSON-обмен

| Файл | Кто создаёт | Кто читает | Назначение |
|------|-------------|------------|------------|
| `task_{CommandId}_{AttemptToken}.json` | Worker | Плагин/обёртка | Задание: команда, исходный файл, путь результата |
| `result_{CommandId}_{AttemptToken}.json` | Плагин/обёртка | Worker | Итог выполнения: успех или ошибка |

### TaskFile

Модель: `TelegramBot.Core.Models.TaskFile`.

```json
{
  "commandId": 42,
  "commandText": "PDF",
  "filePath": "B:\\project.rvt",
  "resultFilePath": "C:\\Users\\svc\\AppData\\Local\\Temp\\result_42_6f1c.json",
  "options": {}
}
```

Поля:

| Поле | Описание |
|------|----------|
| `commandId` | ID команды в таблице `Commands` |
| `commandText` | Код команды: `PDF`, `DWG`, `IFC`, `BIMDOC`, `NWC`, `CLASHREP`, `AUTORES` |
| `filePath` | Полный путь к исходному файлу |
| `resultFilePath` | Полный путь, куда исполнитель должен записать result JSON |
| `options` | Дополнительные параметры; сейчас Worker создаёт пустой/`null`, поле оставлено для расширения |

### ResultFile

Модель: `TelegramBot.Core.Models.ResultFile`.

```json
{
  "status": "done",
  "errorMessage": null,
  "outputFiles": ["B:\\project.pdf"]
}
```

Поля:

| Поле | Описание |
|------|----------|
| `status` | Обязательно: только `"done"` или `"failed"` |
| `errorMessage` | Сообщение об ошибке при `"failed"` |
| `outputFiles` | Список созданных файлов при `"done"` |

Писать result-файл нужно атомарно: сначала `resultFilePath + ".tmp"`, затем rename/move в `resultFilePath`.

## Revit AddIn

Revit — GUI-приложение. Он не является console runner, не пишет полезный stdout/stderr и не понимает `/command` сам по себе. Поэтому для `PDF`, `DWG`, `IFC`, `BIMDOC` на сервере должен быть установлен Revit AddIn.

Worker запускает Revit по шаблону из `TelegramBot.Worker/appsettings.json`:

```text
Revit.exe /command "{CommandText}" "{FilePath}" "{TaskFilePath}"
```

Фактический путь к `Revit.exe` Worker пытается определить через BimLib:

1. `RevitVersionDetector` читает OLE-поток `BasicFileInfo` из `.rvt/.rfa`.
2. Извлекает `Format: YYYY`.
3. `RevitPathResolver` ищет соответствующий Revit в Windows Registry.
4. Если версия определена, но не установлена, команда сразу получает `Failed`.

Revit AddIn должен:

1. Быть установлен для нужных версий Revit на сервере.
2. При старте прочитать путь к task-файлу из аргументов командной строки.
3. Открыть `taskFilePath`, распарсить `TaskFile`.
4. Выполнить `commandText` для `filePath`.
5. Записать `ResultFile` в `resultFilePath`.
6. Завершить Revit-процесс с exit code `0`, если result-файл успешно записан.

Если AddIn не установлен, Revit обычно просто откроется как GUI и будет ждать. Worker завершит команду по таймауту `Worker:ProcessTimeoutMinutes` и убьёт дерево процесса.

`DialogDismisser` только закрывает известные модальные окна Revit/Navisworks. Он не заменяет AddIn и не запускает экспорт.

## Navisworks/FileConvert

Для `NWC` и `CLASHREP` Worker использует `FileConvert.exe`, если он найден. Если `FileConvert.exe` не найден, fallback — `Roamer.exe`/`Navisworks.exe`.

Путь определяется через `NavisworksPathResolver`:

1. Читает установленные версии из Windows Registry.
2. Берёт последнюю найденную версию.
3. Ищет `FileConvert.exe` в корне установки или в подпапке `FileConvert`.
4. Если не найдено, пробует `Roamer.exe` или `Navisworks.exe`.

Шаблон запуска сейчас такой же:

```text
FileConvert.exe /command "{CommandText}" "{FilePath}" "{TaskFilePath}"
```

Важно: стандартный Autodesk `FileConvert.exe` не обязан понимать этот кастомный `/command` и task JSON. Если используется чистый FileConvert без собственной обёртки/плагина, результат будет определяться только exit code. Для полноценного контракта `TaskFile + ResultFile` нужен один из вариантов:

| Вариант | Что делает |
|---------|------------|
| Собственная CLI-обёртка | Принимает `TaskFilePath`, вызывает Navisworks/FileConvert, пишет `ResultFile` |
| Navisworks automation/plugin | Читает task-файл, выполняет `NWC`/`CLASHREP`, пишет `ResultFile` |
| Только стандартный FileConvert | Worker использует fallback: exit code `0` = `Done`, иначе retry/`Failed` |

## AUTORES

`AUTORES` запускает Python:

```text
python ai_agent.py --command "{CommandText}" --file "{FilePath}" --task "{TaskFilePath}" --result "{ResultFilePath}"
```

Скрипт должен читать task/result аргументы и писать `ResultFile`. Если result-файла нет, Worker снова использует fallback по exit code.

## Как Worker трактует результат

После завершения процесса:

| Условие | Статус команды |
|---------|----------------|
| Есть result JSON и `status = "done"` | `Done` |
| Есть result JSON и `status = "failed"` | `Failed` или retry через `ErrorClassifier`, `errorMessage` берётся из файла |
| Result JSON битый или `status` неизвестен | файл переименовывается в `.bad`, дальше `Failed` или retry через `ErrorClassifier` |
| Result JSON отсутствует, exit code `0` | `Done` |
| Result JSON отсутствует, exit code не `0` | `Failed` или retry через `ErrorClassifier` |
| Таймаут | процесс убивается, команда `Failed` |

Plugin-reported `"failed"` проходит через тот же `HandleFailureAsync()`, что и ненулевой exit code.
Permanent ошибки сразу становятся `Failed`, transient ошибки планируются на retry.

## Плейсхолдеры ArgumentsTemplate

| Плейсхолдер | Описание |
|-------------|----------|
| `{CommandText}` | Код команды |
| `{FilePath}` | Полный путь к исходному файлу |
| `{CommandId}` | ID команды |
| `{TaskFilePath}` | Полный путь к `task_{CommandId}_{AttemptToken}.json` |
| `{ResultFilePath}` | Полный путь к `result_{CommandId}_{AttemptToken}.json` |

Исполнитель должен считать `TaskFile` основным источником данных. Аргументы командной строки нужны только для bootstrap и совместимости.
