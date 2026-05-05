---
title: UCL_AgentCommandsPage
description: Editor page for queuing, viewing, and triggering agent commands persisted in AgentCommands/queue.json.
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AgentCommandsPage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-05-04
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
