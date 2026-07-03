# Revit Worker Contract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the invalid Revit `/command` launch contract with a process-scoped environment-variable handoff in both contract copies.

**Architecture:** TelegramBot.Worker will eventually pass the absolute TaskFile path in `REVITBIMFUSION_TASK_FILE`; RevitBIMFusion will consume it during startup and execute once from `Idling`. This plan updates only the authoritative contract and its TelegramBot mirror, as requested.

**Tech Stack:** Markdown, XML Schema

---

### Task 1: Update the canonical contract

**Files:**
- Modify: `C:/Users/y.zhumabayev/Repository/RevitBIMFusion/Docs/BimPluginContract.md`
- Modify: `C:/Users/y.zhumabayev/Repository/RevitBIMFusion/Docs/TaskFile.schema.xsd`

- [x] **Step 1: Replace the CLI section**

Document `REVITBIMFUSION_TASK_FILE`, empty Revit command arguments, startup validation, and one-shot `Idling` execution.

- [x] **Step 2: Align options and unsupported-command behavior**

Document `<options/>` as reserved/empty and `Unsupported command:` as a permanent failure.

- [x] **Step 3: Verify canonical consistency**

Run:

```powershell
rg -n '/command "WORKER"|continueOnError|NotImplemented:' C:\Users\y.zhumabayev\Repository\RevitBIMFusion\Docs\BimPluginContract.md
```

Expected: no matches.

### Task 2: Synchronize the TelegramBot mirror

**Files:**
- Modify: `Docs/BimPluginContract.md`
- Modify: `Docs/TaskFile.schema.xsd`
- Modify: `TelegramBot.Worker/Schemas/TaskFile.schema.xsd`

- [x] **Step 1: Copy the contract semantics**

Apply the same launch, options, commands, and failure wording as the canonical contract.

- [x] **Step 2: Synchronize XSD files**

Make both TelegramBot TaskFile schemas semantically identical to the canonical schema.

- [x] **Step 3: Verify drift is gone**

Run:

```powershell
git diff --no-index -- Docs/BimPluginContract.md C:/Users/y.zhumabayev/Repository/RevitBIMFusion/Docs/BimPluginContract.md
git diff --no-index -- Docs/TaskFile.schema.xsd C:/Users/y.zhumabayev/Repository/RevitBIMFusion/Docs/TaskFile.schema.xsd
```

Expected: no semantic differences.
