# Revit Open Retry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Повторять один раз только Revit-задачу, чей AddIn не смог открыть модель в `UIApplication.OpenAndActivateDocument`.

**Architecture:** `ResultAnalyzer` распознаёт подтверждённый стек открытия и передаёт флаг в существующий `CommandResult`. `ProcessRunner` обрабатывает этот флаг как единственное исключение из правила «plugin failed permanent», планируя повтор через уже существующий `ScheduleRetryAsync` через 10 секунд. Остальные plugin failure остаются финальными.

**Tech Stack:** C# 12, .NET 10, PostgreSQL/Dapper, `Microsoft.Extensions.Logging`.

## Global Constraints

- Не менять TaskFile/ResultFile XML, XSD или `RevitBIMFusion`.
- Условие: Revit-команда, plugin `status=failed`, и `errorDetails` одновременно содержат `Autodesk.Revit.Exceptions.InternalException` и `UIApplication.OpenAndActivateDocument` без учёта регистра.
- Целевой retry выполняется только когда `RetryCount == 0`; задержка ровно 10 секунд; второй совпадающий сбой — финальный `Failed`.
- Не добавлять тесты: это прямое правило `TelegramBot/AGENTS.md`; проверить полной сборкой `dotnet build TelegramBot.slnx`.
- До изменения каждого символа выполнить GitNexus `impact`; перед коммитом выполнить `detect_changes`.

---

### Task 1: Передать признак повторяемого сбоя из ResultFile

**Files:**
- Modify: `TelegramBot.Worker/Services/ResultAnalyzer.cs:138-184, 222-273`

**Interfaces:**
- Consumes: `CommandPreparer.IsRevitCommand(string)`, `ResultFile.Status`, `ResultFile.ErrorDetails`.
- Produces: `CommandResult.IsRetryableRevitOpenFailure` для `ProcessRunner`.

- [ ] **Step 1: Выполнить pre-change impact для `AnalyzePluginResult` и `CommandResult.Failure`**

Run: `mcp__gitnexus__impact` для `AnalyzePluginResult` и `CommandResult.Failure`, направление `upstream`, репозиторий `TelegramBot`.

Expected: зафиксирован список вызывающих и риск до изменения классификации результата.

- [ ] **Step 2: Добавить узкую проверку сбоя открытия модели**

Добавить приватный метод в `ResultAnalyzer`:

```csharp
private static bool IsRetryableRevitOpenFailure(PendingCommand cmd, ResultFile result)
{
    return CommandPreparer.IsRevitCommand(cmd.CommandText)
        && result.ErrorDetails?.Contains("Autodesk.Revit.Exceptions.InternalException", StringComparison.OrdinalIgnoreCase) == true
        && result.ErrorDetails.Contains("UIApplication.OpenAndActivateDocument", StringComparison.OrdinalIgnoreCase);
}
```

В ветке `status=failed` сохранить результат проверки в named local и передать его в `CommandResult.Failure`:

```csharp
bool isRetryableRevitOpenFailure = IsRetryableRevitOpenFailure(cmd, result);
return CommandResult.Failure(
    result.ErrorMessage ?? "Plugin reported failure",
    exitCode: null,
    isPluginOrigin: true,
    isRetryableRevitOpenFailure: isRetryableRevitOpenFailure);
```

- [ ] **Step 3: Расширить `CommandResult` без изменения прочих сценариев**

Добавить readonly-свойство и параметр фабрики:

```csharp
public bool IsRetryableRevitOpenFailure { get; }

public static CommandResult Failure(
    string errorMessage,
    int? exitCode,
    bool isPluginOrigin = false,
    bool isRetryableRevitOpenFailure = false)
{
    return new(false, true, false, errorMessage, exitCode, isPluginOrigin,
        isRetryableRevitOpenFailure);
}
```

Обновить приватный конструктор и остальные фабрики так, чтобы `Success` и `Cancelled` всегда передавали `false`.

- [ ] **Step 4: Проверить компиляцию проекта Worker**

Run: `dotnet build TelegramBot.Worker/TelegramBot.Worker.csproj`

Expected: `Build succeeded` без ошибок.

### Task 2: Запланировать единственный retry в ProcessRunner

**Files:**
- Modify: `TelegramBot.Worker/Services/ProcessRunner.cs:153-157, 193-238`

**Interfaces:**
- Consumes: `CommandResult.IsPluginOrigin`, `CommandResult.IsRetryableRevitOpenFailure`, `PendingCommand.RetryCount`, `CommandDataService.ScheduleRetryAsync`.
- Produces: новый запуск той же строки Commands через 10 секунд или финальный `Failed`.

- [ ] **Step 1: Выполнить pre-change impact для `ProcessRunner.HandleFailureAsync`**

Run: `mcp__gitnexus__impact` для `HandleFailureAsync`, направление `upstream`, репозиторий `TelegramBot`.

Expected: зафиксирован риск изменения финального состояния команд и связанные execution flow.

- [ ] **Step 2: Передать признак из `WaitAndHandleResultAsync` в `HandleFailureAsync`**

Изменить вызов:

```csharp
await HandleFailureAsync(
    cmd,
    commandResult.ErrorMessage!,
    sw,
    commandResult.ExitCode,
    isPluginOrigin: commandResult.IsPluginOrigin,
    isRetryableRevitOpenFailure: commandResult.IsRetryableRevitOpenFailure);
```

Добавить параметр `bool isRetryableRevitOpenFailure = false` к `HandleFailureAsync`.

- [ ] **Step 3: Обработать целевой retry до общего plugin permanent пути**

Добавить константу `private const int RevitOpenRetryDelaySeconds = 10;` рядом с другими константами `ProcessRunner`.

В начале `HandleFailureAsync`, после `sw.Stop()` и до `if (isPluginOrigin)`, вставить:

```csharp
if (isRetryableRevitOpenFailure)
{
    if (cmd.RetryCount == 0)
    {
        DateTime nextRetryAt = DateTime.UtcNow.AddSeconds(RevitOpenRetryDelaySeconds);
        int newRetryCount = await commandDataService.ScheduleRetryAsync(cmd.CommandId, nextRetryAt, errorMessage);
        logger.LogWarning(
            "Revit open retry scheduled: cmd={Cmd}, id={Id}, corr={CorrelationId}, attempt={Attempt}, retryAt={Next:O}, ms={ElapsedMs}, err={Msg}",
            cmd.CommandText, cmd.CommandId, cmd.CorrelationId, newRetryCount, nextRetryAt, sw.ElapsedMilliseconds, errorMessage);
        await NotifySessionCompletionAsync(cmd);
        return;
    }

    const string retryFailureSuffix = " Автоматическая повторная попытка открытия модели также завершилась неудачей.";
    string finalErrorMessage = errorMessage + retryFailureSuffix;
    logger.LogError(
        "Revit open retry exhausted: cmd={Cmd}, id={Id}, corr={CorrelationId}, attempt={Attempt}, ms={ElapsedMs}, err={Msg}",
        cmd.CommandText, cmd.CommandId, cmd.CorrelationId, cmd.RetryCount + 1, sw.ElapsedMilliseconds, finalErrorMessage);
    _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed, errorMessage: finalErrorMessage);
    await NotifySessionCompletionAsync(cmd);
    return;
}
```

- [ ] **Step 4: Проверить полную сборку решения**

Run: `dotnet build TelegramBot.slnx`

Expected: `Build succeeded` без ошибок.

### Task 3: Синхронизировать документацию и проверить scope

**Files:**
- Modify: `Docs/ExecutionAlgorithm.md:74-94`

**Interfaces:**
- Consumes: финальную retry-семантику `ProcessRunner`.
- Produces: единственный источник правил Worker для операторов и разработчиков.

- [ ] **Step 1: Уточнить таблицу результата и retry-политику**

Заменить строку про plugin `status=failed` на правило: permanent, кроме Revit `InternalException` с `OpenAndActivateDocument` в `errorDetails`, который повторяется один раз через 10 секунд.

В разделе `Retry` сохранить список permanent failures и добавить то же узкое исключение с условием `RetryCount == 0`; явно указать, что второй совпадающий сбой становится `Failed`.

- [ ] **Step 2: Выполнить проверку документации и сборки**

Run: `git diff --check; dotnet build TelegramBot.slnx`

Expected: нет пробельных ошибок; решение собирается без ошибок.

- [ ] **Step 3: Выполнить post-change impact review и создать коммит**

Run: `mcp__gitnexus__detect_changes` с `scope: all`, `repo: TelegramBot`.

Expected: затронуты только результат-анализ, retry-ветка Worker и документация; нет неожиданных execution flow.

Run:

```powershell
git add -- TelegramBot.Worker/Services/ResultAnalyzer.cs TelegramBot.Worker/Services/ProcessRunner.cs Docs/ExecutionAlgorithm.md
git commit -m "fix: retry Revit document open failure once"
```

Expected: отдельный коммит без изменения XML-контракта.
