# Repository Simplification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply every approved repository simplification without changing Telegram, database, BIM execution, or dialog-dismissal behavior.

**Architecture:** Remove unused indirection at existing ownership boundaries: Server owns host startup and Telegram output, `ProcessRunner` owns process completion, and synchronous code exposes synchronous APIs. Preserve validation, retries, durable notifications, shutdown, and the complete `DialogDismisser` path.

**Tech Stack:** .NET 10, C#, Telegram.Bot, PostgreSQL/Npgsql/Dapper, Microsoft.Extensions.Hosting, Serilog

---

Implementation commits are intentionally omitted: the worktree already contains user changes in files this plan must edit. Stage or commit only when the user explicitly requests it.

### Task 1: Simplify Server hosting and database startup

**Files:**
- Modify: `TelegramBot.Server/TelegramBot.Server.csproj`
- Modify: `TelegramBot.Server/Program.cs`
- Delete: `TelegramBot.Data/DatabaseInitializer.cs`
- Modify: `TelegramBot.Data/TelegramBot.Data.csproj`
- Modify: `TelegramBot.Core/TelegramBot.Core.csproj`

- [ ] **Step 1: Use the generic Worker host**

Change the Server SDK and Serilog package:

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">
...
<PackageReference Include="Serilog.Extensions.Hosting" Version="10.0.0" />
```

Remove `Serilog.AspNetCore`. Remove the unused `Microsoft.Extensions.Hosting.Abstractions` reference from Core.

- [ ] **Step 2: Move host-specific initialization into Server**

After `Build()`, resolve `DatabaseInitializerService`, then seed configured admins directly:

```csharp
await host.Services.GetRequiredService<DatabaseInitializerService>().InitializeDatabaseAsync();

var adminIds = host.Services.GetRequiredService<IOptions<BotOptions>>().Value.AdminUserIds;
if (adminIds.Length > 0)
{
    await host.Services.GetRequiredService<UserDataService>().UpsertUsersBatchAsync(
        adminIds, (int)UserRole.Admin, (int)UserAccessStatus.Approved);
}
```

Delete `DatabaseInitializer.cs`, then remove `Microsoft.Extensions.Configuration.Binder` and `Microsoft.Extensions.Hosting.Abstractions` from Data.

- [ ] **Step 3: Compile the hosting boundary**

Run:

```powershell
dotnet build TelegramBot.Server/TelegramBot.Server.csproj
```

Expected: exit code `0`.

### Task 2: Remove Server-only service indirection

**Files:**
- Delete: `TelegramBot.Server/Interfaces/ITelegramOutputService.cs`
- Delete: `TelegramBot.Server/Services/Application/DataServices.cs`
- Modify: `TelegramBot.Server/Services/Infrastructure/Telegram/TelegramOutputService.cs`
- Modify: `TelegramBot.Server/Extensions/DependencyInjectionExtensions.cs`
- Modify: all Server consumers of `ITelegramOutputService` and `DataServices`

- [ ] **Step 1: Inject the concrete output service**

Remove `: ITelegramOutputService`, replace constructor parameters with `TelegramOutputService`, and register:

```csharp
services.AddSingleton<TelegramOutputService>();
```

- [ ] **Step 2: Inject data services directly**

Replace `DataServices` at its two consumers:

```csharp
public sealed class SessionsListRenderer(
    SessionDataService sessionDataService,
    KeyboardBuilder keyboardBuilder,
    TelegramOutputService outputService,
    ILogger<SessionsListRenderer> logger)
```

`SlashCommandService` receives `SessionDataService`, `CommandDataService`, and `MessageTrackingDataService` directly. Replace `dataServices.Sessions`, `.Commands`, and `.MessageTracking` with those variables.

- [ ] **Step 3: Compile the DI graph**

Run:

```powershell
dotnet build TelegramBot.Server/TelegramBot.Server.csproj
```

Expected: exit code `0`.

### Task 3: Make synchronous Server APIs synchronous

**Files:**
- Modify: `TelegramBot.Server/Services/Infrastructure/Telegram/KeyboardBuilder.cs`
- Modify: `TelegramBot.Server/Services/Infrastructure/FileSystem/FileSystemBrowser.cs`
- Modify: `TelegramBot.Server/Services/Infrastructure/Telegram/TelegramUpdateMapper.cs`
- Modify: Server call sites and `HandlerHelpers.cs`

- [ ] **Step 1: Return keyboard values directly**

Use direct signatures:

```csharp
public InlineKeyboardMarkup GetSelectionKeyboard(long userId, UserSession session)
public InlineKeyboardMarkup GetCommandKeyboard(CommandGroup group, UserSession session)
public ReplyKeyboardMarkup GetCommandActionsReplyKeyboard()
public ReplyKeyboardMarkup GetFileActionsReplyKeyboard()
public InlineKeyboardMarkup GetSessionsListKeyboard(...)
public InlineKeyboardMarkup GetSessionStatusKeyboard(...)
public InlineKeyboardMarkup GetSessionCommandsKeyboard(...)
```

Change `FileSystemBrowser.GetSectionsViewAsync` to `GetSectionsView` returning `InlineKeyboardMarkup`. Change helper factories from `Func<Task<ReplyKeyboardMarkup>>` to `Func<ReplyKeyboardMarkup>`.

- [ ] **Step 2: Map Telegram updates directly**

Replace `MapAsync` with:

```csharp
public object? Map(Update update)
{
    if (update.Message?.Text != null && update.Message.From != null)
    {
        return MapMessage(update.Message);
    }

    return update.CallbackQuery != null ? MapCallback(update.CallbackQuery) : null;
}
```

Remove corresponding `await` operations at call sites.

- [ ] **Step 3: Compile Server**

Run:

```powershell
dotnet build TelegramBot.Server/TelegramBot.Server.csproj
```

Expected: exit code `0`.

### Task 4: Simplify callback dispatch

**Files:**
- Modify: `TelegramBot.Core/Interfaces/ICallbackHandler.cs`
- Modify: `TelegramBot.Server/Services/Application/CallbackDispatcher.cs`
- Modify: `TelegramBot.Server/Services/Application/Handlers/*.cs`

- [ ] **Step 1: Remove ignored boolean results**

Use:

```csharp
Task HandleAsync(CallbackContext context, CancellationToken cancellationToken = default);
```

Handler switch defaults return `Task.CompletedTask`; successful and rejected branches return normally.

- [ ] **Step 2: Keep one exact dispatcher path**

Build the prefix map with `ToDictionary`, then dispatch with one awaited call. Retain cancellation propagation and exception logging, but remove stopwatch and handled/not-handled branches:

```csharp
public async Task DispatchAsync(CallbackContext context, CancellationToken cancellationToken = default)
{
    if (!_handlerMap.TryGetValue(context.ParsedCallback.Prefix, out var handler))
    {
        logger.LogDebug("Callback ignored: prefix={Prefix}", context.ParsedCallback.Prefix);
        return;
    }

    try
    {
        await handler.HandleAsync(context, cancellationToken);
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error in handler {HandlerName}", handler.GetType().Name);
    }
}
```

- [ ] **Step 3: Compile Server**

Run:

```powershell
dotnet build TelegramBot.Server/TelegramBot.Server.csproj
```

Expected: exit code `0`.

### Task 5: Simplify slash-command and session state

**Files:**
- Modify: `TelegramBot.Server/Services/Application/SlashCommandService.cs`
- Modify: `TelegramBot.Core/Models/UserSession.cs`
- Modify: `TelegramBot.Server/Services/Application/SessionManager.cs`
- Modify: `TelegramBot.Server/Middleware/AuthorizationMiddleware.cs`
- Modify: `TelegramBot.Core/Services/RateLimiter.cs`

- [ ] **Step 1: Replace the strategy/result pipeline with direct flow**

`HandleUserCommandAsync` performs normalization, access refresh for `/start`, access rejection, chat cleanup for slash commands, `/start`, reply-button action handling, and finally `HandleSlashCommandAsync`. Delete `CommandStrategy`, `CommandExecutionStatus`, `UserCommandContext`, `CommandExecutionResult`, `FormatResponseMessageAsync`, and `LogCommandExecutionAsync`.

Use direct access rejection:

```csharp
if (command != "/start" && !access.IsActive)
{
    logger.LogWarning("Command rejected: command={Command}, user={Username} ({UserId}), reason=access_denied",
        command, username, userId);
    await SendSafeResponseAsync(chatId, "У вас нет доступа. Введите /start для запроса доступа.", session);
    return;
}
```

- [ ] **Step 2: Remove redundant `UserSession` locking**

Keep private `List<string>` and `HashSet<string>` collections, but remove `_commandLock`, `_selectionLock`, and snapshot allocations. Each method operates directly because the hosted service holds the per-user lock.

- [ ] **Step 3: Remove dead state and excessive concurrency**

Remove `AccessValidationResult.IsBanned` and calculate `IsActive` directly. Remove the never-injected optional logger and its logging-only catch blocks from `SessionManager`. Replace the rate limiter's locked `ConcurrentQueue<DateTime>` with `Queue<DateTime>` using `Count`, `Peek`, `Dequeue`, and `Enqueue`.

- [ ] **Step 4: Compile Core and Server**

Run:

```powershell
dotnet build TelegramBot.Server/TelegramBot.Server.csproj
```

Expected: exit code `0`.

### Task 6: Simplify Worker orchestration

**Files:**
- Modify: `TelegramBot.Worker/Services/CommandExecutionService.cs`
- Modify: `TelegramBot.Worker/Services/ProcessRunner.cs`
- Delete: `TelegramBot.Worker/Services/SessionCompletionTracker.cs`
- Modify: `TelegramBot.Worker/Program.cs`

- [ ] **Step 1: Remove the duplicate command-slot semaphore**

Delete `_commandSlots`, its initialization/disposal, and `ProcessWithPoolAsync`. Start tracked work through one method that only catches and logs execution failures:

```csharp
private async Task ProcessCommandAsync(PendingCommand cmd, CancellationToken ct)
{
    try
    {
        await processRunner.RunAsync(cmd, ct);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogError(ex, "Error executing command {CommandId}, correlationId={CorrelationId}",
            cmd.CommandId, cmd.CorrelationId);
    }
}
```

The existing `_drainGate`, available-slot calculation, and tracked task count remain the only concurrency bound.

- [ ] **Step 2: Move completion checking to its owner**

Inject `SessionDataService` into `ProcessRunner`. Add the existing completion body as a private `NotifySessionCompletionAsync(PendingCommand cmd)` method and replace all `sessionCompletionTracker.OnCommandCompletedAsync(cmd)` calls. Delete the service and registration.

- [ ] **Step 3: Preserve dialog dismissal**

Do not change `DialogDismisser`, its options, native helpers, DI registration, `CheckProcessesHealth`, or `ProcessMonitorIntervalSeconds`.

- [ ] **Step 4: Compile Worker**

Run:

```powershell
dotnet build TelegramBot.Worker/TelegramBot.Worker.csproj
```

Expected: exit code `0`.

### Task 7: Simplify BIM lookup and Worker configuration

**Files:**
- Modify: `TelegramBot.Worker/BimLib/Services/NavisworksPathResolver.cs`
- Modify: `TelegramBot.Worker/BimLib/Services/RevitVersionDetector.cs`
- Modify: `TelegramBot.Worker/Services/CommandPreparer.cs`
- Modify: `TelegramBot.Worker/BimLib/Config/BimIntegrationOptions.cs`
- Modify: `TelegramBot.Core/Config/WorkerOptions.cs`
- Modify: `TelegramBot.Worker/appsettings.json`
- Modify: `TelegramBot.Worker/Program.cs`

- [ ] **Step 1: Resolve one Navisworks executable**

Replace the public version-list/path trio with:

```csharp
public string? ResolveLatestExecutable()
{
    for (var year = _options.MaxSupportedVersion; year >= _options.MinSupportedVersion; year--)
    {
        var installDir = GetNavisworksDirectory(year);
        if (installDir == null)
        {
            continue;
        }

        foreach (var relativePath in new[] { "FileConvert.exe", @"FileConvert\FileConvert.exe", "Roamer.exe", "Navisworks.exe" })
        {
            var path = Path.Combine(installDir, relativePath);
            if (File.Exists(path))
            {
                return path;
            }
        }
    }

    return null;
}
```

Update `CommandPreparer` to call it once.

- [ ] **Step 2: Make Revit detection synchronous**

Rename `DetectVersionAsync` to `DetectVersion`, return `RevitDetectedVersion?`, preserve cancellation checks, parsing, cache, and error handling, and remove every `Task.FromResult`.

- [ ] **Step 3: Remove dead and duplicated configuration**

Delete `RevitInstallRoot` from options and JSON. Initialize `WorkerOptions.Commands` as:

```csharp
public Dictionary<string, CommandConfig> Commands { get; set; } =
    new(StringComparer.OrdinalIgnoreCase);
```

Remove unused Worker registrations for `UserDataService` and `MessageTrackingDataService`.

- [ ] **Step 4: Compile Worker**

Run:

```powershell
dotnet build TelegramBot.Worker/TelegramBot.Worker.csproj
```

Expected: exit code `0`.

### Task 8: Remove completed plans, align documentation, and verify

**Files:**
- Delete: `Docs/superpowers/plans/2026-07-03-revit-worker-contract.md`
- Delete: `Docs/superpowers/plans/2026-07-03-revit-worker-startup-implementation.md`
- Delete after execution: `Docs/superpowers/plans/2026-07-03-repository-simplification-implementation.md`
- Modify where references are stale: `AGENTS.md`, `README.md`, `Docs/ExecutionAlgorithm.md`, `CLAUDE.md`

- [ ] **Step 1: Remove stale references**

Search:

```powershell
rg -n "ITelegramOutputService|DataServices|SessionCompletionTracker|RevitInstallRoot|DetectVersionAsync|GetInstalledVersions|ResolveFileConvertPath|Serilog.AspNetCore" `
  AGENTS.md CLAUDE.md README.md Docs TelegramBot.* -g "*.md" -g "*.cs" -g "*.csproj" -g "*.json"
```

Expected after edits: no references to removed symbols. References inside this active implementation plan are allowed until its final deletion.

- [ ] **Step 2: Confirm dialog code remains**

Run:

```powershell
rg -n "DialogDismisser|CheckProcessesHealth|ProcessMonitorIntervalSeconds" TelegramBot.Worker
```

Expected: existing configuration, registration, monitor loop, and implementation remain.

- [ ] **Step 3: Run repository verification**

Run:

```powershell
dotnet build TelegramBot.slnx
```

Expected: exit code `0`, zero build errors.

- [ ] **Step 4: Inspect final scope**

Run:

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors; all pre-existing user changes remain present and no unrelated file is reverted.
