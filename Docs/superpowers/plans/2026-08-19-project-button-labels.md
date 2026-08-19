# Project Button Labels Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Сделать отображаемые имена проектов в inline-кнопках длиной ровно 50 символов.

**Architecture:** Локальный форматтер в `FileSystemBrowser` будет обрезать имя папки до 50 символов с суффиксом `...` и дополнять его пробелами справа. `BuildProjectKeyboard` применит форматтер только к тексту кнопки; путь, токен и callback data останутся без изменений.

**Tech Stack:** C# 12, .NET 10, Telegram.Bot inline keyboards.

## Global Constraints

- Изменение применяется только к кнопкам проектов на уровне `RootPath`.
- Имя проекта занимает ровно 50 символов; длинное имя заканчивается на `...`.
- Callback data `OPENFOLDER:` + токен пути не меняется и остаётся в лимите Telegram 64 байта.
- Тесты не добавляются; после изменения выполняется `dotnet build TelegramBot.slnx`.

---

### Task 1: Format project button labels

**Files:**
- Modify: `TelegramBot.Server/Services/Infrastructure/FileSystem/FileSystemBrowser.cs:83-96`

**Interfaces:**
- Consumes: `Path.GetFileName(string)` from `BuildProjectKeyboard`.
- Produces: `FormatProjectButtonName(string name): string`, returning a string of exactly 50 UTF-16 code units.

- [ ] **Step 1: Add the project-name length constant and formatter**

Add beside the existing file-browser constants:

```csharp
private const int _projectButtonNameLength = 50;
```

Add a private static method:

```csharp
private static string FormatProjectButtonName(string name)
{
    const string suffix = "...";
    var displayName = name.Length <= _projectButtonNameLength
        ? name
        : name[..(_projectButtonNameLength - suffix.Length)] + suffix;

    return displayName.PadRight(_projectButtonNameLength);
}
```

- [ ] **Step 2: Use the formatter for project button text**

Replace the label construction in `BuildProjectKeyboard`:

```csharp
var label = $"📁 {FormatProjectButtonName(Path.GetFileName(dir))}";
```

Keep `CallbackPrefixes.OpenFolder` and `CreateSelectionToken(dir)` unchanged.

- [ ] **Step 3: Build the full solution**

Run:

```powershell
dotnet build TelegramBot.slnx
```

Expected: build succeeds with zero errors.

- [ ] **Step 4: Inspect the changed symbol scope**

Run:

```powershell
node .gitnexus/run.cjs detect-changes --scope all --repo TelegramBot
```

Expected: only `FileSystemBrowser` project-keyboard formatting is affected; no unrelated execution flows are reported.
