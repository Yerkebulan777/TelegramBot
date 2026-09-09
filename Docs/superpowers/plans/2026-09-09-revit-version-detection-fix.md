# Revit Version Detection Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reliably resolve the installed Revit executable for valid RVT files whose `BasicFileInfo` text starts at either UTF-16 byte alignment, and never pass a bare `Revit.exe` fallback to `Process.Start`.

**Architecture:** `RevitVersionDetector` will read `BasicFileInfo` once and inspect both possible UTF-16 LE alignments directly, deleting the brittle CRLF-fragment parser. `CommandPreparer` will make executable resolution fail closed: Revit commands continue only when BimLib returns an absolute installed executable path.

**Tech Stack:** C# 12+, .NET 10, OpenMcdf 3.1.4, Microsoft.Extensions.Logging, GitNexus.

## Global Constraints

- Do not add permanent tests; use the temporary `.tmp/revit-detect-harness` diagnostic project and delete it before completion.
- Run GitNexus upstream impact analysis before editing every existing method or class.
- Warn before proceeding if GitNexus reports HIGH or CRITICAL risk.
- Keep the diff limited to `RevitVersionDetector`, `CommandPreparer`, and documentation.
- Do not change registry lookup, scheduling, retry classification, XML contracts, or publishing.
- Run the complete `dotnet build TelegramBot.slnx` after changes.

---

### Task 1: Replace CRLF fragment parsing with alignment-safe UTF-16 parsing

**Files:**
- Modify: `TelegramBot.Worker/BimLib/Services/RevitVersionDetector.cs:130-188`
- Diagnostic: `.tmp/revit-detect-harness/Program.cs`

**Interfaces:**
- Consumes: OpenMcdf `RootStorage.OpenRead(string)` and the `BasicFileInfo` stream.
- Produces: unchanged public method `RevitDetectedVersion? DetectVersion(string filePath, CancellationToken ct = default)`.

- [ ] **Step 1: Record symbol impact before editing**

Run GitNexus `impact` with `direction: "upstream"` for `GetRevitVersionText` and `GetBasicFileInfoText` in `TelegramBot.Worker/BimLib/Services/RevitVersionDetector.cs`.

Expected: callers are contained within `RevitVersionDetector`; stop and warn if risk is HIGH or CRITICAL.

- [ ] **Step 2: Re-run the red-capable production-file harness**

Run:

```powershell
dotnet run --project .tmp/revit-detect-harness/revit-detect-harness.csproj -- $env:REVIT_FIXTURE_S66
dotnet run --project .tmp/revit-detect-harness/revit-detect-harness.csproj -- $env:REVIT_FIXTURE_S72
```

Expected before the fix: S66 exits 1 with `RED: DetectVersion returned null`; S72 exits 0 with `GREEN: Revit 2019` and an absolute executable path.

- [ ] **Step 3: Implement direct two-alignment parsing**

Replace `GetRevitVersionText` and delete `GetBasicFileInfoText`:

```csharp
private static string? GetRevitVersionText(string filePath)
{
    using var root = RootStorage.OpenRead(filePath);
    using var stream = root.OpenStream("BasicFileInfo");

    var streamData = new byte[stream.Length];
    stream.ReadExactly(streamData);

    for (var offset = 0; offset < 2 && offset < streamData.Length; offset++)
    {
        var infoText = Encoding.Unicode.GetString(streamData, offset, streamData.Length - offset);
        using var reader = new StringReader(infoText);

        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (!line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var digits = line.Where(char.IsDigit).ToArray();
            if (digits.Length > 0)
            {
                return new string(digits);
            }
        }
    }

    return null;
}
```

- [ ] **Step 4: Verify the detector is green for both alignments**

Run the two commands from Step 2 again.

Expected after the fix: both commands exit 0 with `GREEN: Revit 2019` and the same absolute executable path.

- [ ] **Step 5: Commit the detector fix**

Before committing, run GitNexus `detect_changes(scope: "all")`. Then:

```powershell
git add TelegramBot.Worker/BimLib/Services/RevitVersionDetector.cs
git commit -m "fix: detect Revit version across UTF-16 alignments"
```

### Task 2: Remove the unsafe Revit executable fallback

**Files:**
- Modify: `TelegramBot.Worker/Services/CommandPreparer.cs:191-233`

**Interfaces:**
- Consumes: `RevitVersionDetector.DetectVersion(string, CancellationToken)`.
- Produces: `ResolveExecutablePath` returns either an absolute Revit path or an explicit error; `PrepareAsync` continues to persist that error and return `null`.

- [ ] **Step 1: Record symbol impact before editing**

Run GitNexus `impact` with `direction: "upstream"` for `ResolveExecutablePath` and `ResolveRevitPath` in `TelegramBot.Worker/Services/CommandPreparer.cs`.

Expected: the impact is limited to command preparation flows; stop and warn if risk is HIGH or CRITICAL.

- [ ] **Step 2: Make Revit resolution explicit and fail closed**

Change the Revit branch in `ResolveExecutablePath` to return `ResolveRevitPath` directly. Make `ResolveRevitPath` return a non-null tuple and handle every state explicitly:

```csharp
private const string RevitVersionDetectionError =
    "Не удалось определить версию Revit для файла. Проверьте целостность файла или обратитесь к администратору.";

private (string? resolvedPath, string? errorMessage) ResolveRevitPath(
    PendingCommand cmd, string commandText, CancellationToken ct)
{
    try
    {
        var version = versionDetector.DetectVersion(cmd.FilePath!, ct);
        if (version?.ExecutablePath != null)
        {
            logger.LogDebug("{Cmd} via BimLib: {Path} (Revit {Year})",
                commandText, version.ExecutablePath, version.Year);
            return (version.ExecutablePath, null);
        }

        if (version != null)
        {
            var message = $"Revit {version.Year} не установлен на сервере. Пожалуйста, установите Revit {version.Year} или обратитесь к администратору.";
            logger.LogWarning("Resolve fail: {Cmd}: {Message}", commandText, message);
            return (null, message);
        }

        logger.LogWarning("Resolve fail: {Cmd}: {Message}", commandText, RevitVersionDetectionError);
        return (null, RevitVersionDetectionError);
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "BimLib detect fail for {Cmd}", commandText);
        return (null, RevitVersionDetectionError);
    }
}
```

Keep the Navisworks and non-BIM branches unchanged.

- [ ] **Step 3: Verify no bare Revit fallback remains**

Run:

```powershell
rg -n "ResolveRevitPath.*\?\?|fallback" TelegramBot.Worker/Services/CommandPreparer.cs
```

Expected: no Revit fallback match; the existing Navisworks fallback may remain outside this scope.

- [ ] **Step 4: Commit the fail-closed resolution**

Before committing, run GitNexus `detect_changes(scope: "all")`. Then:

```powershell
git add TelegramBot.Worker/Services/CommandPreparer.cs
git commit -m "fix: require resolved Revit executable path"
```

### Task 3: Full verification and cleanup

**Files:**
- Delete: `.tmp/revit-detect-harness/`
- Review: all changes since `dadc055`

**Interfaces:**
- Consumes: completed detector and command-preparation changes.
- Produces: a clean, buildable worktree with no diagnostic artifacts.

- [ ] **Step 1: Run the full solution build**

Run:

```powershell
dotnet build TelegramBot.slnx
```

Expected: exit code 0, zero errors.

- [ ] **Step 2: Run final GitNexus scope detection**

Run GitNexus `detect_changes(scope: "compare", base_ref: "dadc055")`.

Expected: only version-detection and command-preparation flows are affected; investigate any unrelated symbol.

- [ ] **Step 3: Apply the thermo-nuclear quality gate**

Review `git diff dadc055..HEAD` for structural regression, ad-hoc branching, thin abstractions, duplicate parsing, and accidental scope expansion.

Expected: the old marker helper is deleted, no new abstraction is introduced, and the executable invariant is explicit at the command-preparation boundary.

- [ ] **Step 4: Remove the temporary harness and verify cleanliness**

Resolve `.tmp/revit-detect-harness` to an absolute path, confirm it remains under the repository `.tmp` directory, and delete exactly that directory. Then run:

```powershell
rg -n "\[DEBUG-" TelegramBot.Worker
git status --short
git diff --check HEAD~2..HEAD
```

Expected: no debug instrumentation, no temporary harness in status, and no whitespace errors.
