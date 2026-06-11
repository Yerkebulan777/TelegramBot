# Взаимодействие Worker с CAD-плагинами (Revit / Navisworks)

> **⚠️ Статус: план/дизайн-документ (не реализовано)**
> Текущая реализация Worker не использует JSON-файловый обмен или Revit Idling-плагины.
> Вместо этого Worker запускает внешние BIM-приложения напрямую через `ProcessStartInfo`
> с аргументами из `WorkerOptions.Commands[].ArgumentsTemplate`. Подробнее:
> - Актуальный флоу выполнения команд: [ExecutionAlgorithm.md](ExecutionAlgorithm.md) → «Выполнение внешнего процесса»
> - BIM-резолвинг (определение версии Revit, поиск Revit.exe): см. `CommandPreparer.ResolveExecutablePathAsync()`
>
> Этот документ описывает **целевую архитектуру** для будущей интеграции с CAD-плагинами.

---

## Целевая схема взаимодействия (план)

Для стабильной передачи задач из фонового сервиса (Worker) во внешние CAD-приложения предлагается
**асинхронный файловый обмен через «Горячую папку» (File-based Hot Folder Queue) + Idling/Automation события**.

Этот подход решает проблему однопоточности CAD API, изолирует сбои и не требует сетевой синхронизации (RPC/gRPC).

```
 [БД Postgres] <─── [Worker (C#)] ───────► [Директория обмена] 
                        │                      (C:\BimExchange\Tasks\)
                        │ (Запуск Revit.exe            │
                        │  с аргументами)              │ (Запись task_123.json)
                        ▼                              ▼
                [Revit / Navisworks] ◄─────────────────┘
                        │ (Подписка на Idling / Automation)
                        │ (Выполнение API-команд в UI-потоке)
                        ▼
                [Файл-ответ] ──────────────────────────► [Worker (C#)]
         (C:\BimExchange\Results\result_123.json)          │ (Контроль таймаута /
                                                           │  считывание ответа)
                                                           ▼
                                                    [БД Postgres]
                                                     (Статус: Done/Failed)
```

---

## 🛠️ Шаги реализации

### 1. Формирование задачи (Worker)
Worker генерирует JSON-файл задачи в `C:\BimExchange\Tasks\task_{CommandId}.json`:
```json
{
  "CommandId": 123,
  "CommandCode": "PDF",
  "ModelPath": "C:\\BimExchange\\Models\\Project_A.rvt",
  "OutputPath": "C:\\BimExchange\\Output\\",
  "Parameters": { "ViewName": "3D_Export" }
}
```

### 2. Безопасный запуск CAD (Worker)
Worker запускает процесс через `Process.Start`, передавая путь к модели и файлу задачи в аргументах:
```cmd
Revit.exe "C:\BimExchange\Models\Project_A.rvt" /task:"C:\BimExchange\Tasks\task_123.json"
```

### 3. Выполнение внутри Revit (Плагин C#)
Плагин (`IExternalApplication`) перехватывает аргументы и выполняет код строго в событии `Idling` (основной поток Revit API):
```csharp
public class App : IExternalApplication
{
    private string _taskPath;

    public Result OnStartup(UIControlledApplication app)
    {
        _taskPath = ParseTaskPath(System.Environment.GetCommandLineArgs());
        if (!string.IsNullOrEmpty(_taskPath))
        {
            app.Idling += OnRevitIdling; // Подписка на безопасный UI-контекст
        }
        return Result.Succeeded;
    }

    private void OnRevitIdling(object sender, IdlingEventArgs e)
    {
        var uiApp = sender as UIApplication;
        uiApp.Idling -= OnRevitIdling; // Однократная обработка

        try
        {
            ExecuteBimTask(uiApp, _taskPath); // Чтение задачи -> Выполнение -> Запись result_123.json
        }
        catch (Exception ex)
        {
            WriteErrorResult(_taskPath, ex.Message);
        }
        finally
        {
            uiApp.Application.Quit(); // Высвобождение лицензии и процесса
        }
    }
}
```

### 4. Выполнение внутри Navisworks (Плагин/Automation C#)
Используется **Navisworks Automation API** (`NavisworksApplication`) из фоновой C# консольной утилиты:
```csharp
var navisworksApp = new NavisworksApplication();
try
{
    navisworksApp.OpenFile(task.ModelPath);
    ExecuteNavisworksExport(navisworksApp, task); // Выполнение экспорта/поиска коллизий через COM
    WriteSuccessResult(taskPath);
}
catch (Exception ex)
{
    WriteErrorResult(taskPath, ex.Message);
}
finally
{
    navisworksApp.Dispose(); // Убивает процесс Roamer.exe
}
```

### 5. Обратная связь и Обработка Ошибок (Двухконтурная система)
Для надежного информирования пользователя о сбоях внутри CAD-приложений (зависание, нехватка памяти, ошибки API) реализована **двухконтурная обратная связь**:

#### Контур 1: Внутренние ошибки (Обработка исключений в плагине)
Если процесс CAD работает штатно, но API выдает ошибку (например, вид для экспорта не найден):
1. Код выполнения плагина полностью оборачивается в `try-catch`.
2. При перехвате ошибки плагин записывает файл ответа со статусом `Failed` и описанием проблемы:
```json
{
  "CommandId": 123,
  "Status": "Failed",
  "ErrorMessage": "Autodesk.Revit.Exceptions.InvalidOperationException: View '3D_Export' not found.",
  "ExportedFiles": []
}
```
3. После записи плагин завершает работу (`Quit()`). Worker считывает `ErrorMessage` и обновляет Postgres.

#### Контур 2: Внешние ошибки (Системный мониторинг в Worker)
Если плагин физически не может записать файл результата (Revit завис, упал по Out of Memory, или процесс был принудительно убит по таймауту):
1. Worker контролирует состояние процесса через `await process.WaitForExitAsync(ct)`.
2. Если процесс завершился по таймауту, Worker убивает его (`process.Kill(true)`) и отмечает команду в БД как `Failed` (см. `ProcessRunner.HandleTimeoutAsync()`).
3. Если процесс упал с системной ошибкой (Exit Code != 0), Worker записывает ошибку через `HandleFailureAsync()` и планирует retry при `RetryCount < MaxRetries`.
4. Если процесс закрылся успешно, но файл `result_{id}.json` так и не появился, записывается ошибка: *"Процесс завершился, но файл ответа не был найден"*.

#### Дополнительный контур: Телеметрия через логи
Worker асинхронно читает потоки вывода CAD-процесса (`stdout`/`stderr` через `BeginOutputReadLine`/`BeginErrorReadLine`). Все текстовые ошибки и предупреждения автоматически перенаправляются в специализированный файл логов `BimLib.log`, сохраняя полную историю для BIM-координаторов.

---

## 💎 Главные преимущества
1. **Sandboxing (Изоляция):** Падение Revit/Navisworks или Out of Memory не ломает Worker.
2. **Простота отладки:** Любой JSON-файл задачи можно подкинуть вручную без запуска Telegram-бота.
3. **Безопасность потоков:** На 100% исключает ошибки параллельного доступа к Revit/Navisworks API.

---

## 📋 Текущий статус vs Целевая архитектура

| Компонент | Текущая реализация | Целевая (этот документ) |
|-----------|-------------------|-------------------------|
| **Worker → CAD** | `ProcessRunner.StartProcessAsync()` — прямой запуск с аргументами из `ArgumentsTemplate` | Генерация JSON-задачи в `C:\BimExchange\Tasks\` |
| **Revit резолвинг** | `CommandPreparer.ResolveRevitPathAsync()` — BimLib определяет версию по .rvt и находит Revit.exe в реестре | Аналогично, но с дополнительным аргументом `/task:...` |
| **CAD → Worker (результат)** | `WaitForExitAsync` + парсинг stdout/stderr | Файл-ответ `result_{id}.json` через Hot Folder |
| **Ошибки API внутри CAD** | Только exit code + stderr (внешний мониторинг) | Двухконтурная система: try-catch в плагине + `result.json` |
| **Retry** | `HandleFailureAsync()` — экспоненциальный backoff до `MaxRetries` | Аналогично |
| **Timeout** | `CancellationTokenSource.CancelAfter(ProcessTimeoutMinutes)` → `Kill(true)` | Аналогично |
| **Revit Idling-плагин** | ❌ Не реализован | `IExternalApplication` с подпиской на Idling |
| **Navisworks Automation** | ❌ Не реализован | `NavisworksApplication` из консольной утилиты |
| **JSON-транспорт** | ❌ Не реализован | `task_{id}.json` / `result_{id}.json` |

**Связанные документы:** [ExecutionAlgorithm.md](ExecutionAlgorithm.md) — текущий флоу выполнения команд
