# Plan: Плагин-Failed → сразу Failed (без retry)

## Контекст бага
Плагин пишет валидный `result.xml` со `status=Failed` (напр. "No PDF printer installed"), но Worker
гоняет команду в retry (до 6 запусков Revit). Пока команда в `status='pending'`, сессия считается
незавершённой → уведомление в Telegram не приходит.

## Корень (подтверждено кодом)
- `ResultAnalyzer.cs:161` — ветка plugin `Failed` возвращает `CommandResult.Failure(msg, exitCode: null, ...)`.
  Флага «происхождение = плагин» нет.
- `ProcessRunner.cs:143` → `HandleFailureAsync(:170)` → `ErrorClassifier.IsPermanentFailure(:174)`
  с `exitCode:null, ex:null`. Только текстовый паттерн-матчинг → текст ошибки плагина не матчится →
  `isPermanent=false` → retry (`:187`).

## Принцип (универсальный, без хардкода текстов ошибок)
Валидный `result.xml` со `status=Failed` = **permanent failure** при любой причине (плагин осознанно
записал ошибку — повторять бессмысленно). Retry остаётся только для крашей процесса без result-файла.

## Blast radius (подтверждено)
- `CommandResult` (nested в `ResultAnalyzer.cs:206`) создаётся **только** в `ResultAnalyzer.cs`
  (Failure: `:99`, `:127`, `:161`; Success/Cancelled — не затрагиваются поведением).
- Используется **только** в `ProcessRunner.cs` (`:129`, `:131`, `:136`, `:141`, `:143`).
- `HandleFailureAsync` — ровно 2 call-site: `:79` (исключение при старте процесса, без result-файла)
  и `:143` (после чтения result-файла).

## Изменения

### 1. `TelegramBot.Worker/Services/ResultAnalyzer.cs` — `CommandResult` (:206)
- Добавить свойство: `public bool IsPluginOrigin { get; private set; }`
- Расширить приватный конструктор `(:215)` параметром `bool isPluginOrigin` и присвоить его.
- `Success(...)` (`:225`) → передать `isPluginOrigin: false`.
- `Failure(...)` (`:230`) → добавить параметр `bool isPluginOrigin = false` (default false) и пробросить.
- `Cancelled(...)` (`:235`) → передать `isPluginOrigin: false`.

### 2. `TelegramBot.Worker/Services/ResultAnalyzer.cs` — `AnalyzePluginResult` / `DetermineResult`
- Ветка `Failed` (`:161`): `CommandResult.Failure(result.ErrorMessage ?? "Plugin reported failure", null, sw.ElapsedMilliseconds, isPluginOrigin: true)`.
- `:99` и `:127` — без изменений (default `false`). Cancelled (`:148`) — без изменений (только `IsCancelled`-путь, до `HandleFailureAsync` не доходит).

### 3. `TelegramBot.Worker/Services/ProcessRunner.cs` — `HandleFailureAsync` (:170)
- Сигнатура: добавить `bool isPluginOrigin = false` (после `Exception? ex = null`).
- В начале тела (после `sw.Stop();` `:172`) — ранний возврат для plugin-origin:
  ```csharp
  if (isPluginOrigin)
  {
      // Валидный result.xml status=Failed — плагин осознанно записал ошибку: permanent, retry бессмысленен.
      logger.LogInformation("Plugin permanent fail (no retry): cmd={Cmd}, id={Id}, corr={CorrelationId}, ms={ElapsedMs}, err={Msg}",
          cmd.CommandText, cmd.CommandId, cmd.CorrelationId, sw.ElapsedMilliseconds, errorMessage);
      _ = await commandDataService.UpdateCommandStatusAsync(cmd.CommandId, Statuses.Failed, errorMessage: errorMessage);
      await NotifySessionCompletionAsync(cmd);
      return;
  }
  ```
  Затем — существующая логика (`ErrorClassifier` + retry/Failed) без изменений.

### 4. `TelegramBot.Worker/Services/ProcessRunner.cs` — call-site `:143`
```csharp
await HandleFailureAsync(cmd, commandResult.ErrorMessage!, sw, commandResult.ExitCode,
    isPluginOrigin: commandResult.IsPluginOrigin);
```
Call-site `:79` (исключение при старте) — **без изменений** (`isPluginOrigin` по умолчанию `false` → краш без result-файла остаётся retry-кандидатом).

## НЕ трогать
- `ErrorClassifier.cs` и его паттерны (никаких хардкод-текстов ошибок плагина).
- Контракт result-файла (`ResultFile`/`ResultStatus`), схему БД, логику retry/`MaxRetries`/`ScheduleRetryAsync`.
- Ветку `Cancelled` и notification-логику `NotifySessionCompletionAsync`.
- Репозиторий RevitBIMFusion.

## Конвенции (AGENTS.md)
- Structured-шаблоны **без интерполяции** (`{Id}`, `{CorrelationId}`, `{Cmd}`, `{Msg}`, `{ElapsedMs}`).
- IDs: `CommandId`/`CorrelationId`. C#12+ nullable, `async`-suffix.
- Минимальный diff, без speculative abstractions. `ErrorClassifier` не трогать.

## Валидация
- `dotnet build TelegramBot.slnx` → 0 errors, 0 warnings.
- Поведение: плагин-Failed с **любой** причиной → один запуск, сразу `Failed`, уведомление приходит.
- Crash без result-файла → retry сохранён (call-site `:79` и fallback `DetermineResult` `:127` не задают `IsPluginOrigin`).
