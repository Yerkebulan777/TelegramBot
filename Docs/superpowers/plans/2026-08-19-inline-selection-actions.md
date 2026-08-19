# Inline Selection Actions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace reply keyboards with inline callbacks only for command selection and `.rvt` file confirmation, opening `01_PROJECT` immediately when a project is clicked.

**Architecture:** The callback dispatcher remains the entry point. `KeyboardBuilder` and `FileSystemBrowser` render actions in their existing inline messages; `FileSelectionHandler` handles file confirmation and cancellation, delegating the existing queue-submission workflow to `SlashCommandService`.

**Tech Stack:** C# 12, .NET 10, Telegram.Bot, Microsoft.Extensions.DependencyInjection.

## Global Constraints

- Do not add a test project; repository policy requires `dotnet build TelegramBot.slnx` and manual Telegram verification.
- Keep DI services singleton and use existing `CallbackHandlerBase` dispatch.
- Preserve `SelectionFlow.OpenFolder` navigation from a project directly into `01_PROJECT`.
- Use `CallbackPrefixes` constants for every new callback value.
- Keep the diff focused; no unrelated refactoring.

---

### Task 1: Define callbacks and render all actions inline

**Files:**

- Modify: `TelegramBot.Core/Constants/CallbackPrefixes.cs`
- Modify: `TelegramBot.Core/Constants/ButtonTexts.cs`
- Modify: `TelegramBot.Server/Services/Infrastructure/Telegram/KeyboardBuilder.cs`
- Modify: `TelegramBot.Server/Services/Infrastructure/FileSystem/FileSystemBrowser.cs`

**Interfaces:**

- Produces `CallbackPrefixes.ConfirmFileSelection = "CONFIRMFILESEL:"` and `CallbackPrefixes.CancelFileSelection = "CANCELFILESEL:"`.
- Produces a command keyboard with `✅ Применить` / `❌ Отмена`, and a `.rvt` file keyboard with `✅ Подтвердить` / `❌ Отмена`; project and section keyboards remain navigation-only.

- [ ] **Step 1: Record the current manual failure**

Run `/export`, select a command, and observe `✅ Применить` / `❌ Отмена` as a reply keyboard; then enter file selection and observe the separate `Действия:` reply message.

- [ ] **Step 2: Add file-action callback constants**

Add next to `OpenFolder`:

```csharp
public const string ConfirmFileSelection = "CONFIRMFILESEL:";
public const string CancelFileSelection = "CANCELFILESEL:";
```

- [ ] **Step 3: Put command actions in the command-selection message**

Remove reply-keyboard construction from `KeyboardBuilder` and append this row in `BuildSelectableCommandsKeyboard`:

```csharp
buttons.Add([
    InlineKeyboardButton.WithCallbackData(ButtonTexts.Apply, CallbackPrefixes.ApplyCommands),
    InlineKeyboardButton.WithCallbackData(ButtonTexts.Cancel, CallbackPrefixes.CancelCommandSelection)
]);
```

Keep only `Apply` and `Cancel` in `ButtonTexts`; remove `Confirm`.

- [ ] **Step 4: Put file actions in every navigation keyboard**

In `FileSystemBrowser`, add this helper and call it only from `BuildFilesKeyboard`. Project and section builders must contain navigation buttons only. Preserve the existing section-level `⬅️ Назад` callback.

```csharp
private static void AddFileSelectionActions(
    List<List<InlineKeyboardButton>> buttons)
{
    buttons.Add([
        InlineKeyboardButton.WithCallbackData(
            "✅ Подтвердить", CallbackPrefixes.ConfirmFileSelection),
        InlineKeyboardButton.WithCallbackData(
            ButtonTexts.Cancel, CallbackPrefixes.CancelFileSelection)
    ]);
}
```

- [ ] **Step 5: Build the rendering change**

Run: `dotnet build TelegramBot.slnx`

Expected: successful build; callbacks are not yet handled.

### Task 2: Handle file confirmation and cancellation via callbacks

**Files:**

- Modify: `TelegramBot.Server/Services/Application/Handlers/FileSelectionHandler.cs`
- Modify: `TelegramBot.Server/Services/Application/Handlers/FileNavigationHandler.cs`
- Modify: `TelegramBot.Server/Services/Application/SlashCommandService.cs`

**Interfaces:**

- Consumes the two file-action prefixes from Task 1.
- Produces `internal Task ConfirmFileSelectionAsync(long userId, string username, UserSession session, CancellationToken cancellationToken)` and `internal Task CancelSelectionAsync(long userId, UserSession session, string? message = null)` on `SlashCommandService`.

- [ ] **Step 1: Record the unhandled-callback behavior**

Before adding handler support, press either new file action and observe the dispatcher ignoring it. This distinguishes the missing handler from the later submission logic.

- [ ] **Step 2: Extend `FileSelectionHandler`**

Replace its `FileActionsKeyboardService` constructor dependency with `SlashCommandService`. Add the two prefixes to `SupportedPrefixes` and dispatch them:

```csharp
CallbackPrefixes.ConfirmFileSelection => HandleConfirmAsync(context, cancellationToken),
CallbackPrefixes.CancelFileSelection => HandleCancelAsync(context),
```

`HandleConfirmAsync` answers the callback, then invokes `slashCommandService.ConfirmFileSelectionAsync(...)`. `HandleCancelAsync` answers the callback, then invokes `slashCommandService.CancelSelectionAsync(...)`.

- [ ] **Step 3: Remove reply-action refreshes**

Remove `FileActionsKeyboardService` from `FileSelectionHandler` and `FileNavigationHandler`. Delete their `RefreshAsync` and `HideAsync` calls; `ReRenderSelectionAsync` is the only update path because it now includes the action row.

- [ ] **Step 4: Reuse submission logic without reply markup**

Change `ConfirmFileSelectionAsync` and `CancelSelectionAsync` from private to internal. In confirmation remove the pre-submit `RemoveReplyKeyboardAsync` call, remove the obsolete `Advanced`-branch action refresh, and replace the final queued result `RemoveReplyKeyboardAsync(userId, queuedMessage)` with tracked `SendMessageAsync(userId, queuedMessage)`. Simplify `SendWarningAndCleanupAsync` to send a tracked plain warning and retain its cleanup call. Leave `SelectionFlow.Confirm` unchanged: direct project clicks already call `OpenFolder`, while its project branch remains compatible with old state.

- [ ] **Step 5: Build and manually exercise callbacks**

Run: `dotnet build TelegramBot.slnx`

Expected: successful build and both file-action callbacks route to the established confirmation/cancellation workflow.

### Task 3: Remove obsolete reply-keyboard lifecycle code

**Files:**

- Delete: `TelegramBot.Server/Services/Application/FileActionsKeyboardService.cs`
- Modify: `TelegramBot.Server/Services/Application/Handlers/CommandSelectionHandler.cs`
- Modify: `TelegramBot.Server/Services/Application/SlashCommandService.cs`
- Modify: `TelegramBot.Server/Extensions/DependencyInjectionExtensions.cs`
- Modify: `TelegramBot.Server/Services/Infrastructure/Telegram/TelegramOutputService.cs`

**Interfaces:**

- Removes `FileActionsKeyboardService`, reply action builders, and `TelegramOutputService.SendMessageWithReplyKeyboardAsync`.
- Retains the now-unused `UserSession.LastActionsMessageId` to avoid a critical-risk change to the shared session model.
- Preserves general inline keyboard and callback APIs.

- [ ] **Step 1: Verify the removal scope**

Run: `rg -n "FileActionsKeyboardService|LastActionsMessageId|Get.*ActionsReplyKeyboard|SendMessageWithReplyKeyboardAsync" TelegramBot.Server TelegramBot.Core`

Expected: references are limited to the selection flow described here.

- [ ] **Step 2: Remove the service and DI references**

Delete the service file, its DI registration, and all constructor dependencies. Remove `LastActionsMessageId` from `SlashCommandService.CleanupCurrentViewAsync`; leave the model property and its reset assignment unchanged.

- [ ] **Step 3: Remove text-based command actions**

Delete `IsCommandSelectionAction`, `HandleCommandSelectionActionsAsync`, and the now-unreachable private `ApplyCommandSelectionAsync`. Change `StartCommandSelectionAsync` to send only its tracked inline command-selection message:

```csharp
var commandSelectionMessage = await messageTrackingService.TrackAsync(
    outputService.SendMessageWithKeyboardAsync(userId, "Выберите команду:", commandKeyboard), session);
session.CommandSelectionMessageId = commandSelectionMessage?.Id;
```

Remove unused reply-markup imports and `SendMessageWithReplyKeyboardAsync`. A `ReplyKeyboardRemove` is allowed only if still needed to clear keyboards from earlier bot versions.

- [ ] **Step 4: Verify compilation and absence of selection reply UI**

Run: `rg -n "ReplyKeyboardMarkup|KeyboardButton|SendMessageWithReplyKeyboardAsync|FileActionsKeyboardService|LastActionsMessageId|ButtonTexts\.Confirm" TelegramBot.Server TelegramBot.Core`

Expected: no production references remain, except a reachable `ReplyKeyboardRemove` for backward cleanup.

Run: `dotnet build TelegramBot.slnx`

Expected: build succeeds without warnings or errors.

- [ ] **Step 5: Run manual Telegram acceptance**

1. `/export` → toggle commands → inline `✅ Применить` opens project buttons in the same message.
2. Click a project → immediately see its `01_PROJECT` sections; no reply keyboard appears.
3. Click `⬅️ Назад` in `01_PROJECT` → return to project list.
4. Choose a section, toggle files, use `Выбрать все`, and return with `⬅️ Назад`; selected indicators remain correct.
5. Press inline `✅ Подтвердить` with no files → warning, no queued command.
6. Select a file and press inline `✅ Подтвердить` → normal queued-job message.
7. At command selection and `.rvt` file selection press inline `❌ Отмена` → selection resets and no job is created.

- [ ] **Step 6: Inspect scope and commit**

Run GitNexus `detect_changes` against `master`; confirm only selection callbacks, keyboards, DI cleanup, and selection navigation are affected. If risk is HIGH or CRITICAL, stop and report it.

Run: `git add TelegramBot.Core TelegramBot.Server`

Run: `git commit -m "feat: use inline selection actions"`

## Plan Self-Review

- Spec coverage: inline command actions, direct project navigation, `01_PROJECT` back navigation, file confirmation, cancellation, cleanup, and verification map to Tasks 1–3.
- Scope: no database schema, worker, or BIM-contract change is included.
- Test policy: no test project is introduced, per repository instructions.
