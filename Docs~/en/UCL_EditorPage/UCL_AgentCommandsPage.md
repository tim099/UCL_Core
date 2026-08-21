---
title: UCL_AgentCommandsPage
description: Editor page for queuing, viewing, and triggering agent commands persisted in AgentCommands/queue.json.
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AgentCommandsPage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-08-21
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# UCL_AgentCommandsPage

## 1. Overview

`UCL_AgentCommandsPage` is the editor-side UI for the **Agent Commands** system — a lightweight pipeline that lets an AI agent enqueue editor-side actions into a JSON file, which a human user (or the agent itself, indirectly) then runs from the Unity Editor on demand.

The page lives at:

- **Code**: `Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AgentCommandsPage.cs`
- **Menu entry (UCL Core)**: `Tools/UCL/Agent Commands/...` (provided by [`UCL_AgentCommandRunner`](#5-related-classes))
- **Project entry (RCG / Emblem of Valor)**: `EditorMenu → Agent Commands` button

It is a thin IMGUI shell over four collaborating types:

| Type | Role |
|---|---|
| `UCL_AgentCommand` | Data model for one queued command (Id / Type / Mode / Args / status) |
| `UCL_AgentCommandQueue` | Read/write `<repoRoot>/AgentCommands/queue.json` |
| `UCL_AgentCommandHandlerBase` | Abstract base for all command handlers — auto-discovered by reflection |
| `UCL_AgentCommandRegistry` | Holds the discovered handlers, indexed by `CommandType` (case-insensitive) |
| `UCL_AgentCommandRunner` | Async runner — awaits `UCL_ModuleService.WaitUntilInitialized` then dispatches |

## 2. Page Layout

```
┌─ TopBar ────────────────────────────────────────────────────────────┐
│ [Back] [Close] | UCL_AgentCommandsPage [Copy] [Refresh] [Run] [...] │
├─ Queue path / Stats ────────────────────────────────────────────────┤
│ Queue: <repo>/AgentCommands/queue.json                              │
│ Total: 3 | Pending: 1 | Done: 1 | Repeatable: 1                     │
├─ Commands (queue.json contents) ────────────────────────────────────┤
│ ● [Pending] ExportEquipmentNotes (OneShot)            [Remove]      │
│ ● [Done]    Ping                  (Repeatable)        [Remove]      │
├─ Available Commands (auto-listed from Registry) ────────────────────┤
│ ExportEquipmentNotes  [查看說明] [+ OneShot] [+ Repeatable]         │
│   匯出全部 Equipment 的 Note / Description 為 Markdown              │
│   ▶ Args Schema                                                     │
│ Ping  [查看說明] [+ OneShot] [+ Repeatable]                         │
│   Sanity check — prints args["msg"] to Console                      │
│   ▶ Args Schema                                                     │
├─ Add Command (manual fallback) ─────────────────────────────────────┤
│ Type: [grid of registered types]                                    │
│ Schema: msg=任意字串（選填，預設 "pong"）                            │
│ Mode: ( ) OneShot  ( ) Repeatable                                   │
│ Description: [...]   Args: [k=v;k=v]                                │
│ [Add 'Ping' (OneShot)]                                              │
└─────────────────────────────────────────────────────────────────────┘
```

## 3. Top Bar Actions

| Button | Behavior |
|---|---|
| `Refresh` | Reload `queue.json` from disk into the in-memory cache |
| `Run Pending Commands` | Call `UCL_AgentCommandRunner.Menu_RunPending()` (async); auto-refresh ~1.5s later |
| `Open Folder` | Open the `AgentCommands/` folder directly in OS file explorer |

## 3b. Section Folding (Tim, 2026-08-21)

Every major section (queue status / add command / failed records / templates / history / tips) folds
independently. Fold toggles always go through `UCL_GUILayout.Toggle` (▼/►); the state lives in the
page instance's `m_FoldDic`.

| Section | Default |
|---|---|
| 📋 Queue status (queue path / watcher bar / command list) | expanded |
| ➕ Add command (picker + form) | expanded |
| ❌ Failed records | **expanded when there are failures**, collapsed otherwise |
| Templates / History / 💡 Tips | collapsed |

> [!NOTE]
> The **queue selector is deliberately not foldable** — it decides which queue every other section is
> talking about, so hiding it strips the subject from every reading below.
>
> Fold headers **keep showing their summary while collapsed** (queue stats, failure count, template /
> history counts). A fold that hides the summary too forces people to expand just to learn whether
> anything needs attention — which defeats the point.

## 3c. ❌ Failed-command panel (re-runnable, Tim 2026-08-21)

Since 2026-08-07 a failed OneShot is **dequeued immediately** (so the queue never blocks and side
effects are not replayed), which means failures are invisible in the queue list. This panel lists
`<DataRoot>/_cmd_failed/<cmdId>.json` — **every** failed Cmd, not one particular kind.

| File | Content | Retention |
|---|---|---|
| `_cmd_results/<id>.json` | machine-readable verdict (**no Args**) | purged after 3 days |
| `_cmd_errors/<id>.md` | human-readable stack trace + Args | permanent |
| `_cmd_failed/<id>.json` ★ | **structured and re-runnable**: Type / Mode / Args / error / queueId / retry trail | until re-run or deleted |

★ Written by `UCL_AgentCommandFailedStore` from the Runner's failure branch. Why a third file: neither
of the first two can drive a re-run — one has no Args, the other is a **human-readable view** (writing
a parser against a human view creates a second source of truth that breaks silently when the format
changes).

| Button | Behaviour |
|---|---|
| `Re-run` | Enqueues a new cmd (new id) into **the queue it originally ran on** (the record's `QueueId`) and runs it immediately; the original record is kept and its `RetryCount` increments |
| `Load into form` | Fills Type / Mode / Args back into the Add-command form — the path for failures that need **editing before re-running** |
| `Delete` / `Clear all records` | Records only; no queue is touched (clear-all asks for confirmation) |

> [!CAUTION]
> **Re-running executes the command again and replays its side effects** — tavern announcements get
> re-posted (the same SHA paid twice), transfers repeat. Hence buttons only, and **no automatic retry**:
> an `ensure_idle` timeout means "never dispatched" (safe to retry), but any failure **after dispatch**
> may already have taken effect — and the two look identical on screen.

> [!IMPORTANT]
> **Re-run is blocked while that queue is running.** The Runner loads the queue into an in-memory list
> and writes the whole list back when it finishes, so any load→add→save in between is overwritten
> (lost update).
> 🩸 Measured during first acceptance: calling re-run from inside a Cmd executing on that same queue
> marked the record as retried and logged a new cmd id, while queue.json came back empty and no verdict
> or error report existed — **the re-run vanished with zero error messages**.
> Current behaviour: check `IsRunningForAgent` first, then **read back and verify the new id landed**;
> if it did not, the record is not marked as retried.

> [!NOTE]
> This store only exists from 2026-08-21 ⇒ **older failures have no structured record and cannot be
> re-run**. The header shows that count separately (derived from `_cmd_errors/`), so "cannot re-run"
> is never drawn as "nothing failed".

## 3d. Example args auto-filled on selection (Tim 2026-08-21)

Switching the command in the Add-command picker **auto-fills the handler's `ExampleArgs`** into the
Args field (clearing it when the handler declares none). Once the command changes, the args left in
the field belong to a different command — keeping them is worse than clearing, because they look like
a valid set. The explicit "fill example" button remains for going back to the sample.

> [!NOTE]
> Applying a template / history entry / failed record into the form is **not** overwritten by the
> example: those paths sync the auto-fill's change-detection baseline (otherwise the next frame would
> overwrite them, which looks like "Apply did nothing").

## 4. How to Add a New Command Type

The command system is **convention-over-configuration**: just write a class that derives from `UCL_AgentCommandHandlerBase`. The registry auto-discovers it via reflection on next domain reload.

```csharp
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands;
using UnityEngine;

namespace YourGame.AgentCommands
{
    public class Cmd_HelloWorld : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "HelloWorld";
        public override string ShortDescription => "Prints a greeting to the Console.";
        public override string ArgsSchema => "name=anyone you'd like to greet";
        public override string HelpURL => "ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_AgentCommandsPage.md";

        public override async UniTask ExecuteAsync(
            Dictionary<string, string> args, CancellationToken token)
        {
            string name = args != null && args.TryGetValue("name", out var n) ? n : "world";
            Debug.Log($"[AgentCmd] Hello, {name}!");
            await UniTask.CompletedTask;
        }
    }
}
#endif
```

Once Unity recompiles, `HelloWorld` shows up automatically in the **Available Commands** list with its `ShortDescription`, foldable `ArgsSchema`, and `[查看說明]` button (if `HelpURL` is set).

> [!IMPORTANT]
> `CommandType` is matched **case-insensitively** but must be **unique** across the entire AppDomain. Duplicate types log an error and the later registration wins.

## 5. Related Classes

| Class | File | Notes |
|---|---|---|
| `UCL_AgentCommand` | `EditorCore/UCL_AgentCommands/UCL_AgentCommand.cs` | Data model + `UCL_AgentCommandMode` enum (OneShot / Repeatable) |
| `UCL_AgentCommandQueue` | `EditorCore/UCL_AgentCommands/UCL_AgentCommandQueue.cs` | Hand-rolled JSON I/O (Unity `JsonUtility` cannot handle `Dictionary`) |
| `UCL_AgentCommandHandlerBase` | `EditorCore/UCL_AgentCommands/UCL_AgentCommandHandlerBase.cs` | Override `CommandType` + `ExecuteAsync`; everything else is optional |
| `UCL_AgentCommandRegistry` | `EditorCore/UCL_AgentCommands/UCL_AgentCommandRegistry.cs` | Static ctor scans `typeof(UCL_AgentCommandHandlerBase).GetAllSubclass()` |
| `UCL_AgentCommandRunner` | `EditorCore/UCL_AgentCommands/UCL_AgentCommandRunner.cs` | `await UCL_ModuleService.WaitUntilInitialized(token)` before dispatching |

## 6. queue.json Schema

```json
{
  "Commands": [
    {
      "Id": "20260504-120000-helloworld",
      "Type": "HelloWorld",
      "Mode": "OneShot",
      "RunCount": 0,
      "Args": { "name": "Tim" },
      "CreatedAt": "2026-05-04T12:00:00.0000000Z",
      "LastRunAt": null,
      "LastRunResult": null,
      "LastRunError": null,
      "Description": "Optional human-readable note from the agent"
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `Id` | Unique identifier; convention is `yyyyMMdd-HHmmss-<typelower>` |
| `Type` | Must match a registered handler's `CommandType` (case-insensitive) |
| `Mode` | `"OneShot"` (auto-removed from queue after success) or `"Repeatable"` (always re-run) |
| `RunCount` | Number of successful executions; incremented by the runner. OneShot commands are removed before this count grows beyond 1, so it is mainly informative for Repeatable. |
| `Args` | Free-form `string→string` map passed to `ExecuteAsync` |
| `LastRun*` | Updated by the runner; `Result` is `"Success"` / `"Failed"` |

## 7. Initialization Contract

The runner calls `UCL_ModuleService.WaitUntilInitialized(token)` before dispatching any handler. This API both **triggers** lazy initialization (by accessing `UCL_ModuleService.Ins`) and **waits** until it completes — handlers can therefore safely assume the module system is ready and `UCL_Asset.Util.GetData()` returns non-null.

> [!NOTE]
> If a handler needs project-specific prewarm (e.g. `RCG_IconSprite.InitSpriteAsset`), the handler itself must `await` it. The framework runner stays free of project-layer dependencies.

## 8. Related Documents

- [`UCL_CommonEditorPage`](./UCL_CommonEditorPage.md) — direct base class
- [`UCL_ModuleService_API`](../UCL_ModuleService/UCL_ModuleService_API.md) — explains `WaitUntilInitialized`
- `Workflows/HelpURL_Workflow.md` (this repo) — `ucl_core:` / `eov_docs:` URL scheme

## 9. Pitfalls

> [!CAUTION]
> **Do not write `Register(...)` calls** like the legacy RCG version did. The new registry is purely reflection-driven — manual `Register` does not exist on `UCL_AgentCommandRegistry`.

> [!IMPORTANT]
> Editor-only. The whole system is wrapped in `#if UNITY_EDITOR` and must not be referenced from runtime code paths.

## ★ NEW: Lock-file Watcher (auto-trigger)

Since 2026-05-05, `UCL_AgentCommandWatcher` (`[InitializeOnLoad]`) polls `<repoRoot>/AgentCommands/pending.trigger` once per second; when present, it does an atomic `File.Move` to `pending.trigger.running` and invokes the Runner. The page now shows a Watcher status row (Auto-Watcher toggle / Idle/Pending/Running indicator / Last trigger time / Simulate Trigger button).

The "Export Cmd Catalog" stand-alone button is removed — add an `ExportCommandCatalog` cmd via the Add Command form instead (same code path, same output).

Python wrapper: `<UCL_CORE>/Tools~/AgentCommands/run_cmd.py` (writes the trigger; `ensure_idle()` blocks if a previous batch hasn't finished).

For the full design (state machine, ensure_idle, failure modes), see the project workflow doc: `docs/Workflows/AgentCommands_Workflow.md` §8a.0.
