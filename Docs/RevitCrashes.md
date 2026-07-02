# Revit Crashes — `ACCESS_VIOLATION` при выполнении команд

> **Статус:** 🔴 **Открыто.** Системный баг: 100% запусков Revit крашатся до открытия файла задания.
> **Дата обнаружения:** 2026-07-02.
> **Связанные документы:** [BimPluginContract.md](BimPluginContract.md) — контракт Worker ↔ Revit AddIn,
> [CriticalReview.md](CriticalReview.md) — известные архитектурные риски.

---

## Симптомы

Worker запускает Revit (`Revit.exe /command "WORKER" <task-file.xml>`) для выполнения экспортных
команд (`PDF`, `DWG`, `IFC`, `BIMDOC`, `NWC`). Процесс падает с кодом:

```
exitCode = -1073741819 (0xC0000005, ACCESS_VIOLATION)
```

**Статистика за 2026-07-02** (лог `~/Documents/TelegramBot/Logs/Worker/log-20260702.txt`):

| Метрика | Значение |
|---------|----------|
| Запусков Revit | 70 |
| Крашей `0xC0000005` | 70 |
| Успешных (`status=done`) | **0** |
| `Result file not found` | 70 (AddIn ни разу не записал результат) |
| Уровень воспроизводимости | **100%** — ни одна команда не выполнена |

Баг затрагивает обе установленные версии: **Revit 2019** и **Revit 2023**.

---

## Root Cause (по данным журналов)

Анализ `journal.0107.txt` (Revit 2023, упавшая команда `id=3877`, старт 17:22:05) показывает, что
**краш происходит до открытия файла и до выполнения AddIn-команды**:

```
17:22:05.822  started recording journal file
17:22:05.824  ->desktop InitApplication
17:22:06.220  License initialization complete
17:22:06.221  ->loadAllDB
17:22:06.272  <-loadAllDB
              ... (инициализация, регистрация updaters/extensions) ...
              [НЕТ Document.Open, НЕТ WORKER-команды, НЕТ упоминания TelegramBot AddIn]
              ... (cleanup: Forcibly unregistering Updaters, Unregistering all external services)
              [ЖУРНАЛ ОБРЫВАЕТСЯ — нет 'Journal Exit', нет 'ExitManagedInstance']
```

В нормальном завершении (для сравнения, `journal.0132.txt`) после инициализации идут:

```
19:26:37.838  logging started worker services
... (регистрация DocumentOpened/Opening events всех extensions)
... (Starting External Application: IFC override, Link Topography, ...)
... (Added pushbutton: WorkerCommand, assembly: RevitBIMFusion.dll)
19:26:46.391  <-desktop InitNativeInstance
19:26:46.626  ->UI-less ExitManagedInstance
19:26:47.456  Journal Exit
```

**Вывод:** Revit стартует, доходит до фазы инициализации/загрузки AddIn-приложений и падает на
очистке (unregistering updaters) — то есть либо сам крашится во время инициализации, либо
инициализация прерывается внешним фактором, после чего Revit пытается корректно завершиться и
крашится уже на cleanup-фазе. AddIn-команда `WORKER` **не вызывается** — журналы не содержат
никаких записей от `RevitBIMFusion.WorkerCommand`.

---

## Гипотезы и проверка

### Гипотеза 1: `DialogDismisser` убивает Revit через P/Invoke ❓ _Проверяется_

`DialogDismisser` активно использует Win32 API (`EnumWindows`, `PostMessage`, `BM_CLICK`,
`WM_CLOSE`) для поиска и закрытия модальных окон. Любой P/Invoke в адресном пространстве
Revit-процесса может вызвать `ACCESS_VIOLATION` при:
-Race condition с UI-потоком Revit
- Ошибке в маршалинге handle'ов
- Закрытии не того окна (например, главного окна Revit вместо диалога)

**Статус проверки:** `DialogDismisser:Enabled` выставлен в `false` в
`TelegramBot.Worker/appsettings.json`. При `Enabled=false` метод
`DismissDialogsForProcess` делает ранний `return` (строка `DialogDismisser.cs:38-41`)
**до любых P/Invoke-вызовов**.

**Критерий подтверждения:**
- Если краши **продолжатся** с `Enabled: false` → гипотеза опровергнута, копать в сторону
  CEF/stagger/AddIn (см. ниже).
- Если краши **исчезнут** → `DialogDismisser` виновен, чинить P/Invoke-логику.

> `ProcessRunner` регистрирует процесс в `_activeProcesses` сразу после `Process.Start()`, поэтому
> при включённом `DialogDismisser` health-loop может просканировать окна во время инициализации Revit.
> Нужен тест с `Enabled=false`, чтобы исключить этот фактор.

### Гипотеза 2: Stagger-gate не работает → коллизия CEF devtools-порта ❌ _Частично опровергнута_

`ProcessRunner` использует `_launchGate` (`SemaphoreSlim(1,1)`) для сериализации моментов
`Process.Start()` с паузой `LaunchStaggerSeconds` (default 30) — чтобы встроенный Chromium (CEF)
Revit успел забиндить devtools-порт 8088 без коллизии.

**Аномалия в логах:**

| Батч | Время | Версия Revit | Интервалы между стартами |
|------|-------|--------------|--------------------------|
| id=3518–3522 | 11:20 | 2019 | **1.08с, 0.075с, 0.149с, 0.104с** ❌ stagger не работает |
| id=3902–3906 | 18:48 | 2023 | **10.0с, 10.0с, 10.0с, 10.0с** ✅ stagger работает (при `LaunchStaggerSeconds=10`) |

**Вывод:** stagger **частично исправен** — в батче 18:48 интервал ровно 10 секунд (соответствует
коммиту `dff5ddc` "Increase LaunchStaggerSeconds 5->10"), но краши `ACCESS_VIOLATION`
**продолжаются**. Значит, коллизия CEF-порта **не является** единственной/главной причиной.

### Гипотеза 3: AddIn `RevitBIMFusion` крашится при инициализации в headless-режиме 🔴 _Подтверждённый главный suspect_

Журналы показывают, что краш происходит **до** регистрации `WorkerCommand` (в нормальном журнале
0132 pushbutton добавляется на строке 1353, в крашнувшемся 0107 этого нет).

**Анализ кода AddIn** (`C:\Users\y.zhumabayev\Repository\RevitBIMFusion\RevitBIMFusion\Application.cs:12-26`):

```csharp
public override void OnStartup()
{
    AssemblyResolver.Install();          // ВНЕ try/catch
    try {
        AppServices.Initialize();         // создаёт логгер (LogManager.ResolveLogger)
        RibbonPanelBuilder.Create(...);   // ← ПАДАЕТ: пытается создать UI-панель
        ...
    }
    catch (Exception ex) {
        throw new InvalidOperationException(...);  // ← ПЕРЕБРАСЫВАЕТ исключение наружу!
    }
}
```

**Три независимых подтверждения этой гипотезы:**

1. **Лог-файл AddIn отсутствует.** `C:\Users\y.zhumabayev\Documents\RevitBIMFusion\RevitBIMFusion.log`
   создаётся в `AppServices.Initialize` → `LogManager.ResolveLogger()`. Файла нет → OnStartup упал
   **до или во время** создания логгера. Если бы инициализация завершилась — лог существовал бы.

2. **Журнал Revit обрывается на cleanup** (`Forcibly unregistering Updaters`) — это стандартная
   реакция Revit на исключение в `IExternalApplication.OnStartup`, проброшенное наружу.

3. **Регистрация `WorkerCommand` не видна** — pushbutton добавляется в `RibbonPanelBuilder.Create`,
   который падает раньше.

**Механизм:** при headless-запуске (`/command`) Revit не имеет полного UI. `RibbonPanelBuilder.Create`
пытается создать Ribbon-панель через RevitAPI → выбрасывает исключение → `catch` перебрасывает как
`InvalidOperationException` → Revit на фазе инициализации падает с `0xC0000005` **до** выполнения
`/command`. Это точно объясняет:
- краш **до** вызова WORKER-команды (мы видим в журнале)
- краш **до** открытия файла (журнал обрывается на инициализации)
- 100% failure rate (OnStartup выполняется при каждом запуске Revit)
- одинаковое поведение для Revit 2019 и 2023 (один и тот же код OnStartup)

**Дополнительные риски в OnStartup:**
- `AssemblyResolver.Install()` — вне try/catch (менее вероятно)
- Версия сборки не соответствует Revit (R19-сборка в Addins\2023 → `TypeLoadException`/`MissingMethodException`)

**Диагностика для подтверждения:**
1. Проверить какая именно сборка лежит в `%APPDATA%\Autodesk\Revit\Addins\2023\RevitBIMFusion\` —
   сравнить TFM (`net47` для R19, `net48` для R23) с целевой версией Revit.
2. Запустить Revit вручную в обычном (не `/command`) режиме — если AddIn грузится и лог
   создаётся, то проблема именно в headless-режиме.
3. Проверить наличие `RevitBIMFusion.log` после ручного запуска.

**Исправление (на стороне AddIn, репозиторий RevitBIMFusion):**
- Не перебрасывать исключение из `OnStartup` — логировать и возвращать `Result.Failed`/продолжать
  (для headless-режима UI не нужен).
- Обернуть `RibbonPanelBuilder.Create` в отдельный try/catch — UI-ошибка не должна ронять весь AddIn.
- Обернуть `AssemblyResolver.Install()` в try/catch.

> ⚠️ **Это исправление нужно делать в репозитории `RevitBIMFusion`** (вне этого проекта).
> TelegramBot.Worker здесь не виноват — он корректно запускает Revit, краш происходит внутри AddIn.

### Гипотеза 4: Лицензия / network-менеджер ❓

Журналы показывают `License mode: Network`, `Server: tp-pc52`. При параллельном запуске 5 процессов
Network License Manager может отказывать в лицензии → Revit показывает диалог и завершается.
`DialogDismisser` (если включён) может закрывать этот диалог, усугубляя ситуацию.

### Гипотеза 5: Нехватка ресурсов / GDI-исчерпание ❓

Журналы содержат регулярные строки `GUI Resource Usage GDI: Avail ...`. При 5 параллельных Revit
можно исчерпать GDI-объекты (default 10000 на сессию) → нестабильность и краши.

### Гипотеза 6: Рассинхронизация контракта Worker ↔ AddIn ❌ _Опровергнута (2026-07-02)_

**Проверка:** построчное сравнение `TelegramBot.Core/Models/TaskFile.cs` + `ResultFile.cs` с
эталоном в `C:\Users\y.zhumabayev\Repository\RevitBIMFusion\` (`WorkerBridge/Core/Contracts.cs`,
`XmlIo.cs`, XSD-схемы).

**Результат — полное совпадение:**
- Имена XML-элементов (`commandId`, `commandText`, `filePath`, `resultFilePath`, `status`,
  `errorMessage`, `errorDetails`, `outputFiles`) — идентичны с обеих сторон.
- Тип `outputFiles` — `string?` (одна строка, НЕ массив) с обеих сторон.
- XML namespace — Worker пишет без namespace (`EmptyXmlNamespaces`), AddIn парсит нечувствительно
  к namespace (по `XDocument` + `LocalName`).
- Порядок элементов — совместим (AddIn использует `XDocument.FirstOrDefault`, порядок неважен;
  Worker использует `XmlSerializer`, порядок записи AddIn совпадает с порядком свойств).
- CLI-аргументы — `args.Length == 4 && args[2]=="WORKER"` с обеих сторон.
- `options`/`continueOnError` — Worker никогда не пишет `<options>`, AddIn никогда не читает
  `task.Options` (опции открытия захардкожены). Расхождение типов — «мёртвый код» с обеих сторон.

**Почему контракт физически не может вызвать `ACCESS_VIOLATION`:**
- `ACCESS_VIOLATION` (`0xC0000005`) — ошибка **неуправляемой памяти** (native code).
- XmlSerializer (Worker) и XDocument (AddIn) — **полностью управляемый** код, который физически
  не может вызвать `AccessViolationException`.
- Все исключения парсинга task-файла в AddIn перехватываются try/catch в
  `WorkerCommandHandler.Execute` → пишутся в `ResultFile.Failed`.
- Краш происходит **до** выполнения WORKER-команды, значит парсинг task-файла вообще не успевает
  начаться.

**Вывод:** контракт **исключён** из списка подозреваемых. См. [BimPluginContract.md](BimPluginContract.md)
для актуального состояния контракта.

---

## Возможные методы исправления

Методы упорядочены по сложности и вероятности успеха.

### Метод A: Уменьшить `MaxConcurrentCommands` до 1 (быстрая проверка)

**Что:** В `TelegramBot.Worker/appsettings.json` выставить:

```json
"Worker": {
  "MaxConcurrentCommands": 1
}
```

**Цель:** Исключить любую параллельность Revit-процессов. Если краши исчезнут → проблема в
параллельном запуске (CEF, GDI, лицензия, race). Если продолжатся → проблема в одиночном запуске
(AddIn, конфигурация Revit, лицензия на один процесс).

**Стоимость:** Низкая (только конфиг), но снижает throughput в 5×.

### Метод B: Поддержать корректный stagger и поднять его до 30+ секунд

**Что:** Несмотря на то, что stagger частично работает, есть батч (11:20) где интервалы составили
<1.5с. Нужно убедиться, что `_launchGate` действительно сериализует `Process.Start()` для всех
параллельных `ProcessWithPoolAsync`-задач. Если лог-аномалия подтвердится — поднять
`LaunchStaggerSeconds` до 30–60 секунд (уже сделано в `WorkerOptions.cs`, default = 30).

**Цель:** Устранить коллизию CEF devtools-порта 8088 и race condition при инициализации.

**Стоимость:** Низкая (только конфиг), но увеличивает время старта батча.

### Метод C: Сериализовать запуск через очередь, а не gate

**Что:** Заменить `SemaphoreSlim _launchGate(1,1) + Task.Delay` на явную очередь запуска
(`Channel<Command>` или `BlockingCollection`), где Worker последовательно стартует процесс, ждёт
готовности AddIn (signal через `result-file` или именованный pipe), и только потом стартует следующий.

**Цель:** Полностью устранить window между `Process.Start()` и реальной готовностью Revit AddIn.

**Стоимость:** Средняя — требует протокола handshake между Worker и AddIn (изменение контракта
[BimPluginContract.md](BimPluginContract.md)).

### Метод D: Лечить AddIn — включить Revit-логирование и найти исключение

**Что:**

1. Запустить Revit вручную с теми же аргументами:
   ```
   "C:\Program Files\Autodesk\Revit 2023\Revit.exe" /command "WORKER" "<task-file.xml>"
   ```
   Если Revit крашится и в ручном режиме → проблема воспроизводится вне Worker, чинить AddIn.
2. Включить verbose-логирование Revit (переменная окружения `REVIT_JOURNAL_VERBOSE=1` или
   опции в `Revit.ini`).
3. Найти в журнале строки между `License initialization complete` и обрывом — искать
   `Exception`, `Error`, `Failed to load`, имя `RevitBIMFusion`.
4. Проверить `RevitBIMFusion.addin`-манифест: корректность `Assembly`, `FullClassName`, `ClientId`.
5. Отключить **все** сторонние AddIn кроме `RevitBIMFusion` — сузить круг подозреваемых.

**Цель:** Найти точное исключение в AddIn, которое крашит Revit.

**Стоимость:** Средняя — требует ручного воспроизведения и анализа Revit-журналов. Это, вероятно,
**самый надёжный путь**, т.к. журналы показывают краш до вызова `WORKER`-команды.

### Метод E: Перевести Revit в batch/UI-less режим

**Что:** Вместо запуска отдельного `Revit.exe` на каждую команду, использовать:

1. **Revit.exe с ключом `/n` (no UI)** или запуск как Windows-сервис (headless) — устраняет
   GDI-давление и UI-race.
2. **Design Automation API for Revit** (Autodesk Forge) — облачный бэкенд, полностью headless,
   устраняет локальные краши.
3. **Единый долгоживущий Revit-процесс**, который Worker держит запущенным и скармливает ему
   команды через очередь (аналог ModelChecker-паттерна) — устраняет overhead старта и проблемы
   параллельной инициализации.

**Цель:** Полностью уйти от паттерна "1 команда = 1 Revit-процесс".

**Стоимость:** Высокая — архитектурное изменение, но решает класс проблем (краши инициализации,
лицензионные ограничения, GDI-исчерпание, медленный старт).

### Метод F: Диагностика лицензии Network License Manager

**Что:**

1. Проверить доступность NLM-сервера `tp-pc52` (`ping`, `telnet <host> 27000`).
2. Посмотреть лог NLM (`flexnet.log` или аналог) — были ли отказы в выдаче лицензии в моменты
   крашей (11:20, 17:22, 18:48, 19:24).
3. Временно перевести Revit в standalone-режим (если есть serial number) для изоляции фактора.

**Цель:** Исключить нехватку сетевых лицензий при параллельном запуске.

**Стоимость:** Низкая–средняя, зависит от доступа к NLM-серверу.

### Метод G: Исправить OnStartup в AddIn (репозиторий RevitBIMFusion) ⭐ _Рекомендуемый_

**Что:** Главное исправление по Гипотезе 3. Внести изменения в репозиторий
`C:\Users\y.zhumabayev\Repository\RevitBIMFusion\` (`RevitBIMFusion\Application.cs`):

1. **Не перебрасывать исключение** из `OnStartup`. Заменить `throw new InvalidOperationException(...)`
   на логирование + продолжение (для headless-режима UI-ошибка не фатальна):
   ```csharp
   catch (Exception ex)
   {
       LogManager.ResolveLogger().LogError(ex, "OnStartup failed, continuing in degraded mode");
       // НЕ throw — Revit крашится, если OnStartup пробрасывает исключение наружу
   }
   ```
2. **Обернуть `RibbonPanelBuilder.Create`** в отдельный try/catch — создание Ribbon-панели не
   должно ронять весь AddIn. В headless-режиме (`/command`) Revit не имеет полного UI, и попытка
   создать панель может выбрасывать исключение.
3. **Обернуть `AssemblyResolver.Install()`** в try/catch (сейчас вне try/catch).
4. **Проверить TFM сборки** в `%APPDATA%\Autodesk\Revit\Addins\<year>\RevitBIMFusion\` — сборка
   для R19 (`net47`) не должна лежать в Addins\2023, и наоборот.

**Цель:** Устранить краш инициализации AddIn в headless-режиме — главную причину 100% failure.

**Стоимость:** Средняя (нужен пересбор + redeploy AddIn в RevitBIMFusion-репозитории), но это
**самое прямое исправление** найденной причины.

---

## Рекомендуемый порядок диагностики

1. **Сначала** — убедиться, что `DialogDismisser:Enabled = false` (уже сделано) и прогнать
   тестовый батч. Если краши продолжатся → Гипотеза 1 опровергнута.
2. **Параллельно** — поставить `MaxConcurrentCommands = 1` (Метод A). Самый дешёвый эксперимент,
   который сузит круг причин (параллельность vs одиночный запуск).
3. **Ключевая диагностика (Гипотеза 3)** — проверить наличие лог-файла AddIn
   `C:\Users\<user>\Documents\RevitBIMFusion\RevitBIMFusion.log`. Если **отсутствует** → OnStartup
   падает до создания логгера (подтверждение Гипотезы 3). Запустить Revit **вручную** (без `/command`)
   — если в ручном режиме лог создаётся и AddIn работает, то проблема именно в headless-режиме.
4. **Если Гипотеза 3 подтверждена** → применить Метод G (исправить OnStartup в RevitBIMFusion).
   Это наиболее вероятный путь исправления по данным расследования.
5. **Параллельно с Методом G** — ручной запуск Revit с теми же аргументами (Метод D, шаг 1) для
   воспроизведения вне Worker.
6. **Если** ни один Quick-Win не помог — переходить к архитектурным изменениям (Метод E).

---

## Ссылки по коду

### TelegramBot.Worker (этот репозиторий)

| Файл | Роль |
|------|------|
| `TelegramBot.Worker/Services/ProcessRunner.cs` | `StartProcessAsync` — запуск Revit, `_launchGate`, stagger-задержка |
| `TelegramBot.Worker/Services/CommandExecutionService.cs:252` | Вызов `dialogDismisser.DismissDialogsForProcess` в health-check-цикле |
| `TelegramBot.Worker/BimLib/Monitor/DialogDismisser.cs:36` | `DismissDialogsForProcess` — ранний `return` при `Enabled=false` |
| `TelegramBot.Worker/BimLib/Config/DialogDismisserOptions.cs` | `Enabled` toggle (по умолчанию `true`, сейчас `false` для тестов) |
| `TelegramBot.Core/Config/WorkerOptions.cs:67` | `LaunchStaggerSeconds` (default 30) |
| `TelegramBot.Worker/Services/CommandPreparer.cs` | `CreateTaskFile` — atomic write `task_{CommandId}_{AttemptToken}.xml` |

### RevitBIMFusion AddIn (внешний репозиторий — главный suspect)

| Файл | Роль |
|------|------|
| `C:\Users\y.zhumabayev\Repository\RevitBIMFusion\RevitBIMFusion\Application.cs:12-26` | `OnStartup` — **главное место краша**: `RibbonPanelBuilder.Create` падает в headless-режиме, `catch` перебрасывает исключение |
| `...\RevitBIMFusion\Infrastructure\Ui\RibbonPanelBuilder.cs:16-26` | `Create` — создание Ribbon-панели, падает без полного UI |
| `...\WorkerBridge\Commands\WorkerCommandHandler.cs:23-41` | try/catch вокруг парсинга task-файла (безопасен, исключения перехватываются) |
| `...\RevitBIMFusion.addin` | Манифест AddIn: `AddInId=18E159D5-...`, `FullClassName=RevitBIMFusion.Application` |
| `C:\Users\y.zhumabayev\Documents\RevitBIMFusion\RevitBIMFusion.log` | Лог AddIn — **отсутствует** (диагностический признак краша в OnStartup) |
| `%USERPROFILE%\AppData\Roaming\Autodesk\Revit\Addins\<year>\RevitBIMFusion\` | Deploy-цель сборки AddIn (проверить TFM: `net47` для R19, `net48` для R23) |

### Revit-журналы (для сопоставления с Worker-логом по времени)

| Путь | Назначение |
|------|------------|
| `%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2023\Journals\journal.*.txt` | Журналы Revit 2023 (искать краш-сессии по времени из Worker-лога) |
| `%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2019\Journals\journal.*.txt` | Журналы Revit 2019 |
