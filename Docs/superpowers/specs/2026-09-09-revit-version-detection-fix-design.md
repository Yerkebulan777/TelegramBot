# Revit version detection fix

## Problem

`RevitVersionDetector` extracts a UTF-16 text fragment from the RVT `BasicFileInfo` stream by taking the bytes between the first two CRLF markers. Some valid RVT files contain an earlier unrelated CRLF. For those files, the selected fragment has no `Format:` line, version detection returns `null`, and `CommandPreparer` silently falls back to the configured short name `Revit.exe`. The Worker service does not have Revit's installation directory in `PATH`, so `Process.Start` fails with Win32 error 2.

The production file `ST_05_10_S66_KJ.rvt` reproduces this path. Its `Format:` UTF-16 byte sequence starts at an odd byte offset after an earlier CRLF-delimited fragment. A neighboring file, `ST_05_10_S72_KJ.rvt`, is detected successfully by the current implementation.

## Design

### Decode the complete metadata stream

Remove CRLF-based fragment selection. Read `BasicFileInfo` once and decode the complete byte array twice with UTF-16 LE: once from offset 0 and once from offset 1. Search each decoded view line-by-line for `Format:` using the existing case-insensitive comparison and digit extraction.

There are only two possible UTF-16 alignments. Trying both directly expresses the file-format invariant and eliminates the marker ordering assumption without adding a new abstraction or fallback heuristic. `BasicFileInfo` is small, so the second decode has negligible cost.

### Fail closed when version detection is unavailable

For Revit commands, executable resolution must not fall back to a short executable name. `ResolveRevitPath` will always return an explicit resolution result:

- detected version and installed executable: return the absolute executable path;
- detected version without an installed executable: retain the existing “Revit <year> is not installed” failure;
- version cannot be detected: return a clear failure explaining that the RVT version could not be determined;
- unexpected detector exception: log it and return the same safe failure.

This keeps the executable invariant explicit: a Revit command reaches `Process.Start` only with the absolute path returned by `RevitPathResolver`.

## Scope

The change is limited to `RevitVersionDetector` and `CommandPreparer`. It does not change registry lookup, command scheduling, retry classification, task/result XML, or installer publishing.

Framework assembly load failures observed after a .NET runtime update are operationally separate. The running Worker must be restarted after rebuilding; converting the application to a self-contained deployment is intentionally out of scope.

## Verification

Repository policy forbids adding tests. A temporary harness will therefore provide the regression loop and will be removed before completion:

1. Before the fix, `ST_05_10_S66_KJ.rvt` returns `null` while `ST_05_10_S72_KJ.rvt` resolves to Revit 2019.
2. After the fix, both files resolve to the absolute Revit 2019 executable.
3. Run the complete `dotnet build TelegramBot.slnx` build.
4. Run GitNexus `detect_changes` and confirm that only the expected symbols and execution flows are affected.
5. Confirm the temporary harness and any diagnostic instrumentation are absent from the final worktree.
