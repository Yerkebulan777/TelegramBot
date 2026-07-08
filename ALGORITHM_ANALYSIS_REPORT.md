# Проверка рекомендаций из алгоритмического отчета

Проверено по текущему коду репозитория на 2026-07-08. Цель этого файла - отделить реальные рекомендации от устаревших, ошибочных и неподтвержденных оптимизаций.

## Итог

| N | Компонент | Вердикт | Что делать |
|---|---|---|---|
| 1 | SessionManager | Частично корректно | Не применять предложенный код. Реальный follow-up - точечная очистка stale locks при lazy-expiry. |
| 2 | KeyboardBuilder | Не подтверждено | Удалить рекомендацию про кэш; максимум локальный подсчет при замерах. |
| 3 | FileSystemBrowser | Не подтверждено | Удалить LRU/read-only рекомендацию. |
| 4 | RevitFileDeduplicator | Некорректно | Удалить рекомендацию: она меняет семантику. |
| 5 | CommandExecutionService shutdown | Некорректно | Удалить Channel-рекомендацию. |
| 6 | DialogDismisser | Частично корректно | Рассматривать только если `DialogDismisser:Enabled=true`. |
| 7 | ExportFolderCleanupService | Сейчас неактуально | Удалить из runtime-рекомендаций: сервис не вызывается. |
| 8 | SlashCommandService submit | Некорректно | Удалить рекомендацию о перестановке проверок и кэше путей. |
| 9 | SessionsListRenderer | Избыточно | Удалить рекомендацию про кэш сессий. |
| 10 | ProcessRunner stdout/stderr | Устарело/некорректно | Удалить рекомендацию: bounded capture уже есть. |

Из исходного отчета удалены неподтвержденные проценты ускорения и общие архитектурные рекомендации (`ObjectPool`, `Channels`, telemetry, source generators), потому что они не привязаны к замерам или активному hot path.

---

## 1. SessionManager

Файл: `TelegramBot.Server/Services/Application/SessionManager.cs`

### Проверка

1. Очистка действительно делает snapshot через `_sessions.Keys.ToList()` раз в 30 минут.
2. Блокировка на каждую истекшую сессию нужна: без нее можно удалить/Dispose `SemaphoreSlim`, пока пользовательский handler еще работает с этой сессией.
3. Предложение "просто TryRemove lock и Dispose" небезопасно.
4. Реальная проблема другая: `GetOrCreateSession` при lazy-expiry удаляет запись из `_sessions`, но не удаляет соответствующий `_sessionLocks`. После этого фоновая очистка уже не увидит ключ в `_sessions.Keys`, и lock может остаться в словаре.

### Вердикт

Не применять исходную рекомендацию.

### Корректная рекомендация

Исправлять точечно: при lazy-expiry удалять session и освобождать lock тем же безопасным способом, что и cleanup, то есть только если lock удалось захватить без ожидания. Не вводить `CancellationTokenSource` на каждую сессию.

---

## 2. KeyboardBuilder

Файл: `TelegramBot.Server/Services/Infrastructure/Telegram/KeyboardBuilder.cs`

### Проверка

1. LINQ в `GetSessionCommandsKeyboard` есть: `Distinct`, `OrderBy`, `Where(...).ToList()`.
2. Эти данные приходят из команд одной сессии, а не из глобального списка.
3. Число уникальных типов команд ограничено `CommandCatalog.All` (сейчас 8), поэтому `Count` внутри цикла имеет малую верхнюю границу.
4. Предложенный кэш по hash списка усложняет код и рискует stale UI после удаления команд или смены статусов.
5. Рекомендация "использовать Array вместо List для value types" не относится к текущему коду: здесь строки и модели, не value types.

### Вердикт

Не применять. Пункт удален из рекомендаций к внедрению.

### Что можно сделать только при замерах

Если появятся сессии с тысячами строк команд и это будет видно в профиле, заменить подсчет `sessionCommands.Count(...)` внутри цикла на локальный `Dictionary<string, int>`. Кэш между запросами не нужен.

---

## 3. FileSystemBrowser

Файл: `TelegramBot.Server/Services/Infrastructure/FileSystem/FileSystemBrowser.cs`

### Проверка

1. `GetOrCache` возвращает тот же экземпляр `List<string>`.
2. Текущие callers список не мутируют: клавиатуры его перечисляют, `Select all` копирует элементы в `UserSession` через `AddSelectedFiles`.
3. Race condition из отчета не подтверждается текущим кодом.
4. TTL 5 секунд выглядит намеренным для файловой системы, где содержимое может меняться извне.
5. LRU eviction с сортировкой кэша на каждом miss усложняет код без данных о росте памяти.

### Вердикт

Не применять. Пункт удален из рекомендаций к внедрению.

### Что можно сделать только при фактическом росте памяти

Самый дешевый вариант - периодически удалять expired entries при cache miss. LRU и разные TTL не нужны без замеров.

---

## 4. RevitFileDeduplicator

Файл: `TelegramBot.Server/Helpers/RevitFileDeduplicator.cs`

### Проверка

1. `(string Path, string Name)` - value tuple; сам по себе это не heap allocation на каждый файл.
2. Regex уже `GeneratedRegex`, а код использует `EnumerateMatches`, что избегает создания `Match` объектов.
3. Текущий алгоритм учитывает длину имени и пересечение чисел внутри группы.
4. Предложенный алгоритм меняет поведение: общий `acceptedNumbers` по prefix может пометить дублями файлы, которые текущая логика различает по допуску длины имени.

### Вердикт

Не применять. Пункт удален из рекомендаций к внедрению.

---

## 5. CommandExecutionService shutdown

Файл: `TelegramBot.Worker/Services/CommandExecutionService.cs`

### Проверка

1. `ActiveProcesses.ToList()` нужен как snapshot concurrent collection перед shutdown.
2. `Task.WhenAll` не ждет бесконечно: всем `KillProcessAsync` передается общий `shutdownBudgetCts.Token`.
3. `ProcessKillHelper.KillAsync` уже имеет per-process timeout через `CancelAfter(timeout)`.
4. Channel с producer/consumer для shutdown добавит больше кода, но не решит подтвержденную проблему.

### Вердикт

Не применять. Пункт удален из рекомендаций к внедрению.

---

## 6. DialogDismisser

Файл: `TelegramBot.Worker/BimLib/Monitor/DialogDismisser.cs`

### Проверка

1. В `FindDialogs` действительно есть несколько проходов по top-level окнам: по title patterns, по class `#32770`, потом все окна процесса.
2. Для каждого кандидата стратегия C перечисляет child windows и читает title.
3. `CommandExecutionService` вызывает `DismissDialogsForProcess` из health monitor каждые 30 секунд, но `DialogDismisser:Enabled` в `TelegramBot.Worker/appsettings.json` сейчас `false`, поэтому метод выходит до P/Invoke-сканирования окон.
4. Кэшировать HWND рискованно: окна Revit живут недолго, handle может устареть или переиспользоваться.

### Вердикт

Частично применять только если выставят `DialogDismisser:Enabled=true` и мониторинг покажет заметную цену P/Invoke.

### Корректная рекомендация

При включении заменить несколько top-level enumeration на один проход с теми же фильтрами и сохранить текущие safety checks. Кэш HWND не добавлять.

---

## 7. ExportFolderCleanupService

Файл: `TelegramBot.Worker/BimLib/Services/ExportFolderCleanupService.cs`

### Проверка

1. `CleanupExportDirs` есть, но по текущему коду не вызывается.
2. `ExportFolderCleanupService` не зарегистрирован в `Program.cs`.
3. Поэтому оптимизация `GetFiles`, `GroupBy`, `OrderBy` сейчас не влияет на выполнение Worker.
4. Предложенный `FileGroup` усложняет код и в примере содержит ошибку: `OrderByDescending(...)` вызывается без присваивания результата, значит список не сортируется.

### Вердикт

Не применять. Пункт удален из runtime-рекомендаций.

### Что сделать при подключении сервиса

Перед включением сервиса проверить политику удаления/архивации и только потом заменить `GetFiles("*", AllDirectories)` на `EnumerateFiles` при больших папках. Сейчас это не требуется.

---

## 8. SlashCommandService

Файл: `TelegramBot.Server/Services/Application/SlashCommandService.cs`

### Проверка

1. Текущий порядок сначала валидирует выбранные файлы через `File.Exists`, затем считает дневной лимит и проверяет дубликаты.
2. Это логично: в БД должны попадать только файлы, которые реально существуют на момент submit.
3. Если проверить дубликаты до `File.Exists`, пользователь может получить предупреждение о дубле для файла, которого уже нет на диске.
4. Повторная защита от дублей уже есть в `SessionDataService.CreateSessionWithCommandsAsync` под user-level advisory lock.
5. Кэш metadata путей добавит состояние без явной инвалидации и не подтвержден замерами.

### Вердикт

Не применять. Пункт удален из рекомендаций к внедрению.

---

## 9. SessionsListRenderer

Файл: `TelegramBot.Server/Services/Application/SessionsListRenderer.cs`

### Проверка

1. Renderer делает два DB-запроса параллельно: список и count.
2. `/status` показывает живые статусы и используется после удаления/смены фильтра/пагинации.
3. Кэш на 10 секунд может показать stale данные сразу после soft-delete или завершения команд.
4. Инвалидация, предложенная в отчете, потребует протаскивать события изменений из нескольких мест, что сложнее текущего кода.

### Вердикт

Не применять. Пункт удален из рекомендаций к внедрению.

### Что можно сделать только при замерах

Если два SQL-запроса станут проблемой, лучше объединить list+count в один SQL через `COUNT(*) OVER()` или отдельный CTE, а не добавлять кэш UI.

---

## 10. ProcessRunner stdout/stderr

Файлы:
- `TelegramBot.Worker/Services/ProcessRunner.cs`
- `TelegramBot.Worker/Services/OutputCollector.cs`
- `TelegramBot.Core/Helpers/StringBuilderExtensions.cs`

### Проверка

1. `OutputCollector` уже ограничивает stdout/stderr до 64 KiB через `AppendBounded`.
2. В лог выводится максимум 4 KiB.
3. Поэтому риск OOM от GB stdout/stderr, описанный в отчете, устарел.
4. Синхронный `process.WaitForExit()` после `WaitForExitAsync(ct)` не является очевидно лишним: при async redirected output это распространенный способ дождаться доставки последних output events после выхода процесса.
5. Рекомендация с `PipeReader` переписывает рабочий bounded collector без подтвержденной проблемы.

### Вердикт

Не применять. Пункт удален из рекомендаций к внедрению.

---

## Оставшиеся действия

1. `SessionManager`: сделать минимальный fix lazy-expiry lock cleanup.
2. `DialogDismisser`: если `Enabled=true` включат в конфиге, рассмотреть один top-level проход вместо нескольких, без кэша HWND.

Все остальные пункты исходного отчета сейчас не подтверждены текущим кодом или предлагают более сложный код без измеримой пользы.
