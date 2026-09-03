---
title: Workflow — Creating a New Agent Command Handler
description: Step-by-step SOP to add a `Cmd_<Name>.cs` handler that can be triggered via queue.json. Covers naming, file placement, metadata fields, ExecuteAsync conventions, in-Editor verification, and common pitfalls.
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# 🛠️ Workflow — Creating a New Agent Command Handler

> [!IMPORTANT]
> This workflow is scoped to **writing one `Cmd_<Name>.cs` subclass**. For how the system itself works (queue / trigger / watcher / runner), see [UCL_AgentCommand_Architecture](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md).
>
> Design philosophy: **convention-over-configuration**. Subclass `UCL_AgentCommandHandlerBase` and place the file correctly — `UCL_AgentCommandRegistry` will discover it via reflection on the next domain reload. **Do not touch the registry.**

---

## 0. TL;DR — Add a Cmd in 5 Minutes

```
[1] Decide CommandType (PascalCase, globally unique in AppDomain)
       ▼
[2] Choose the right location (§2 decision tree) — UCL_Core vs downstream module
       ▼
[3] Create Cmd_<Name>.cs : UCL_AgentCommandHandlerBase
       ▼
[4] Override the four metadata: CommandType / ShortDescription / ArgsSchema / HelpURL
       ▼
[5] Implement ExecuteAsync(args, token)
       ▼
[6] Wait for Unity domain reload → open UCL_AgentCommandsPage
       → New cmd appears in Available Commands → Add + Run Pending → check Console
```

---

## 1. Pre-Decisions

| # | Question | Impact |
|---|------|------|
| 1 | **CommandType naming** | PascalCase, verb-led, AppDomain-unique; collisions LogError + last-write-wins |
| 2 | **OneShot or Repeatable** | Decided by the agent in queue.json, not by the handler; hint via ShortDescription |
| 3 | **What args** | Only `Dictionary<string,string>`; pack complex objects into JSON strings |
| 4 | **Owning module** | Inside UCL_Core vs downstream module (see §2) |

### 1.1 Naming

| Item | Example | Rule |
|---|---|---|
| C# class | `Cmd_DebugLog` | `Cmd_` prefix + PascalCase |
| File name | `Cmd_DebugLog.cs` | Match the class name exactly |
| `CommandType` value | `"DebugLog"` | No `Cmd_` prefix; case-insensitive in queue.json |
| Namespace (UCL_Core) | `UCL.Core.EditorLib.AgentCommands` | Framework-level commands |
| Namespace (downstream) | `<YourModule>.AgentCommands` | e.g. `RCG.AgentCommands` |

> [!CAUTION]
> **Do not** put `Editor` as a middle namespace segment (e.g. `MyMod.Editor.AgentCommands`). C# resolves `Editor` from inside out and may collide with `UnityEditor.Editor`, producing CS0118.

---

## 2. File-Placement Decision Tree

```
This Cmd is…
├── Generic (file I/O, UCL_Asset ops, catalog export — no downstream types)
│       → Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Cmd_<Name>.cs
│         namespace UCL.Core.EditorLib.AgentCommands
│
└── Downstream-specific (calls RCG_*Data / project EditorPage / mutates game assets)
        → That module's AgentCommands folder
          namespace <YourModule>.AgentCommands
```

### 2.1 Why split?

- **UCL_Core is a submodule** intended to be reusable across any UCL-based Unity project; referencing downstream types breaks portability.
- **Downstream modules** can freely `using` their own namespaces and call project-specific Editor APIs.
- Rule of thumb: **does this Cmd make sense in a pure-UCL project without the downstream module?** Yes → UCL_Core; No → downstream.

> [!TIP]
> When unsure → start in the downstream module. Promoting later is easy (move file + change namespace). **Demoting** is harder (more references to update).

---

## 3. Standard Template (Copy-and-Modify)

```csharp
// Handler: <CommandType> — <one-line description>
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Block responsibility: <what this Cmd does, when it runs, who triggers it>
    /// Physical meaning: <assets modified / files written / game-state effects>
    /// Numeric impact: <"None" if no numeric mutation; otherwise specify scope>
    /// </summary>
    public class Cmd_Example : UCL_AgentCommandHandlerBase
    {
        // CommandType — matched against queue.json `"Type"`; AppDomain-unique.
        public override string CommandType => "Example";

        // ShortDescription — shown in the searchable dropdown of UCL_AgentCommandsPage
        // and as a one-line entry in commands_catalog.md. Be specific so agents can self-learn.
        public override string ShortDescription => "Example Cmd — prints args[\"msg\"] to Console";

        // ArgsSchema — `key=description`, one per line. Use "(no args)" if none.
        public override string ArgsSchema =>
            "msg=string to print (optional, default \"hello\")";

        // HelpURL — uses ucl_core: prefix inside UCL_Core; downstream modules register their own.
        // The {lang} placeholder is auto-replaced by UCL_LocalizeService.CurLang.
        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md";

        public override async UniTask ExecuteAsync(
            Dictionary<string, string> args, CancellationToken token)
        {
            // Block: parse + validate args
            // Physical: convert string-dict into typed params, throw on missing/invalid
            // Numeric: none (read-only)
            string msg = args != null && args.TryGetValue("msg", out var m) ? m : "hello";

            // Block: actual logic
            // Physical: print one log line to Console
            // Numeric: none
            Debug.Log($"[AgentCmd] {CommandType} → {msg}");

            await UniTask.CompletedTask;
        }
    }
}
#endif
```

### 3.1 Required Fields

| Field | Type | Required | Notes |
|---|---|---|---|
| `CommandType` | `string` | ✅ | Matches queue.json `"Type"`; PascalCase; no `Cmd_` prefix |
| `ShortDescription` | `string` | ⚠ Strongly recommended | One-line summary in UI + catalog |
| `ArgsSchema` | `string` | ⚠ Strongly recommended | `key=description` format; `"(no args)"` if none |
| `HelpURL` | `string` | Optional | `ucl_core:` or downstream-registered prefix; if absent, the "Help" button is hidden |
| `ExecuteAsync` | `UniTask` | ✅ | Real entry point; end with `await UniTask.CompletedTask` if there are no awaits |

---

## 4. ExecuteAsync Conventions

### 4.1 Runner-Provided Guarantees

Before dispatching any handler, `UCL_AgentCommandRunner` has already:

1. ✅ `await UCL_ModuleService.WaitUntilInitialized(token)` — module system ready
2. ✅ Running on Unity main thread; `AssetDatabase` / `EditorUtility` are safe
3. ✅ `UCL_Asset<T>.Util.GetData(...)` works (unless the ID truly doesn't exist)

> [!IMPORTANT]
> **Do not re-await `UCL_ModuleService.WaitUntilInitialized` inside the handler** — it's redundant and obscures the init contract. If you need module-specific warm-up (e.g. `SomeModule.PreloadAssets()`), `await` it locally; do **not** push it back into the framework runner.

### 4.2 Argument Parsing

| Case | Pattern |
|---|---|
| Required string | `args.TryGetValue("k", out var v)` → `throw new ArgumentException(...)` if missing |
| Optional with default | `args.TryGetValue("k", out var v) ? v : "default"` |
| bool | `args.TryGetValue("k", out var v) && bool.TryParse(v, out var b) && b` |
| int | `args.TryGetValue("k", out var v) && int.TryParse(v, out var n)` |
| List | `args["k"].Split(',')` |
| Complex object | Pack JSON into the value, then `JsonUtility.FromJson<T>(args["k"])` |

### 4.3 Error Handling

- **Bad args** → `throw new ArgumentException($"[{CommandType}] ...")`; runner writes it to `LastRunError`
- **Missing asset** → `throw new InvalidOperationException(...)`; the agent reads it and self-corrects
- **Unrecoverable** → same; **never** swallow with a catch — errors are the agent's feedback channel
- **Cancellation** → `token.ThrowIfCancellationRequested()` at loop tops

### 4.4 Output Path Convention

> [!IMPORTANT]
> **Path separation rules:**
> - `queue.json` lives at **git-root** `AgentCommands/queue.json`
> - Cmd outputs land at **Unity-project-root** `AgentCommands/<output>.md` (NOT inside `Assets/`!)
> - The Cmd-side `outputPath` is relative to the Unity project root.

```csharp
string outputPath = args.TryGetValue("outputPath", out var p)
    ? p
    : "AgentCommands/default_report.md";
string fullPath = System.IO.Path.Combine(
    UnityEngine.Application.dataPath, "..", outputPath);
System.IO.File.WriteAllText(fullPath, content);
Debug.Log($"[AgentCmd] {CommandType} → wrote {fullPath}");
```

---

## 5. Verification SOP

### 5.1 In-Editor (mandatory)

1. **Save and wait for Unity to recompile** — no red lines in Console
2. **Open the page** — `Tools/UCL/Agent Commands/Open Page` or your project entry → [`UCL_AgentCommandsPage`](../UCL_EditorPage/UCL_AgentCommandsPage.md)
3. **Find the new Cmd** in Available Commands as `<CommandType> — <ShortDescription>`
4. **Expand Args Schema** — verify it shows your text
5. **Click "View Help"** — verify it jumps to your `HelpURL`
6. **Add a test entry** — pick your Cmd, OneShot, fill Args as `key=value;key=value`
7. **▶ Run Pending Commands** — confirm the expected `[AgentCmd]` log
8. **OneShot check**: success → entry disappears from queue; failure → entry stays with `LastRunError`

### 5.2 Catalog Auto-Export (optional)

```
Add Command → ExportCommandCatalog → OneShot → Run Pending
→ Open AgentCommands/commands_catalog.md
→ Verify the new Cmd is listed with ShortDescription / ArgsSchema
```

### 5.3 Python Wrapper (agent angle)

```bash
senate ucmd run <CommandType> \
    --arg key=value --timeout 60
```

OneShot success → wrapper prints `✓ Cmd disappeared from queue → Success`.

---

## 6. Common Pitfalls

| # | Pitfall | Symptom | Fix |
|---|---|---|---|
| 1 | **Missing `#if UNITY_EDITOR`** | Build fails: `UCL_AgentCommandHandlerBase` not found | Wrap the whole file in `#if UNITY_EDITOR ... #endif` |
| 2 | **CommandType collision** | Console: `[UCL_AgentCommandRegistry] duplicate CommandType ...` | Rename; case-insensitive — even casing-only differences collide |
| 3 | **`Editor` segment in namespace** | CS0118: 'Editor' is a namespace but is used like a type | Use a namespace without `Editor` as a middle segment |
| 4 | **No `await` in `ExecuteAsync`** | Warning CS1998 | Append `await UniTask.CompletedTask;` |
| 5 | **Swallowed exceptions** | Runner sees success; agent thinks it worked | `throw` — runner writes to `LastRunError` |
| 6 | **Run before Unity finished compiling** | Old handler runs | Wait for the compile spinner; ensure no red Console |
| 7 | **Output written under `Assets/`** | File gets imported by Unity Asset Database | Output to `<UnityProjectRoot>/AgentCommands/` |
| 8 | **UCL_Core Cmd references downstream types** | Breaks submodule portability / fails to compile | Generalize, or move the Cmd back to the downstream module (§2) |

---

## 7. Wrapping Existing EditorPage Logic (Recommended Pattern)

> [!TIP]
> Most Cmds shouldn't reinvent the wheel — if a button in an EditorPage already does the job, **just call its static method**.

```csharp
public override async UniTask ExecuteAsync(
    Dictionary<string, string> args, CancellationToken token)
{
    Debug.Log($"[AgentCmd] {CommandType} — invoking SomeEditorPage.DoExport()");
    SomeEditorPage.DoExport();
    await UniTask.CompletedTask;
}
```

Benefits:
- Humans (button) and agents (Cmd) take the **same code path** — consistent behavior
- Fix bugs in one place (EditorPage)
- The Cmd becomes a clean RPC wrapper layer for the agent

---

## 8. Minimal Skeleton

```csharp
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands  // change to your module's namespace if downstream
{
    /// <summary>
    /// <one-line responsibility>
    /// </summary>
    public class Cmd_<Name> : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "<Name>";
        public override string ShortDescription => "<one-line UI summary>";
        public override string ArgsSchema => "(no args)";
        public override string HelpURL => "ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md";

        public override async UniTask ExecuteAsync(
            Dictionary<string, string> args, CancellationToken token)
        {
            // TODO: implement
            Debug.Log($"[AgentCmd] {CommandType} executed.");
            await UniTask.CompletedTask;
        }
    }
}
#endif
```

---

## 9. Auto-Classification Scheme for Doc Placement (Future)

> This workflow lives under `Assets/UCL/UCL_Core/Docs~/` rather than the project-level `docs/Workflows/` because it describes **only UCL_Core framework extensions** — it depends on no downstream type. To let future docs decide their location **automatically**, we propose:

### 9.1 Decision rule — by the source location of the subject

| If the doc describes code that is… | The doc lives in… |
|---|---|
| Entirely under `Assets/UCL/UCL_Core/` | `Assets/UCL/UCL_Core/Docs~/{lang}/...` (multi-language) |
| Entirely under a downstream module (e.g. `Assets/Scripts/RCG_Scripts/`) | `docs/...` (project-level, single-language is OK) |
| Crosses both — "how downstream X uses UCL_Core's Y" | `docs/Workflows/` (caller-side; UCL_Core docs stay framework-only) |

### 9.2 Required frontmatter

Every doc gets:

```yaml
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/
namespace: UCL.Core.EditorLib.AgentCommands
```

`source_root` is the path prefix of the code described. An auto-classifier just:

1. Reads `source_root`
2. If `startsWith("Assets/UCL/UCL_Core/")` → should live under `Assets/UCL/UCL_Core/Docs~/{lang}/`
3. If `startsWith("Assets/Scripts/")` → should live under `docs/`
4. Mismatch → warn + suggest move

### 9.3 Suggested implementation: `Cmd_ValidateDocPlacement`

| Step | Action |
|---|---|
| 1 | Scan `Assets/UCL/UCL_Core/Docs~/**/*.md` + `docs/**/*.md`; read frontmatter |
| 2 | Compute the expected location from `source_root` (§9.1) |
| 3 | Diff vs actual path; record violations |
| 4 | Extra: grep UCL_Core docs for downstream namespaces (e.g. `RCG_`) — fail if present |
| 5 | Emit `AgentCommands/doc_placement_report.md` listing suggested moves |

Bonuses of doing this as a Cmd:
- Same shape as `Cmd_ValidateAssetFormat` (validate → report)
- Runs under CI batchmode (`Tools/UCL/Agent Commands/Run Pending`)
- The report is markdown — agents read it directly and act on the suggestions

### 9.4 Pre-write checklist

Before writing a new doc:

- [ ] Where is the .cs file I'm describing? (→ `source_root`)
- [ ] Does that .cs reference any downstream type? (→ affects placement)
- [ ] Is frontmatter complete: `source_root` / `namespace` / `last_updated` / `target_audience`?
- [ ] Multi-language: 4 copies (UCL_Core) or 1 copy (project-level)?

---

## 10. Related

- 🤖 [UCL_AgentCommand_Architecture](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md) — system overview
- 📖 [UCL_AgentCommand](../API/UCL_AgentCommand/UCL_AgentCommand.md) — data model
- 🪟 [UCL_AgentCommandsPage](../UCL_EditorPage/UCL_AgentCommandsPage.md) — Editor IMGUI page
- 🔗 [HelpURL_Workflow](HelpURL_Workflow.md) — `ucl_core:` / `eov_docs:` prefix mechanism
- 📁 UCL_Core handler folder: `Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/`
- 📖 Project-level supplementary workflow: [`docs/Workflows/AgentCommands_Workflow.md`](../../../../../../docs/Workflows/AgentCommands_Workflow.md) — covers downstream (RCG) Cmd examples and integration

---

## Other Languages

- 🇬🇧 English (this document)
- 🇯🇵 [日本語](../../ja/Workflows/Create_Cmd_Workflow.md)
- 🇨🇳 [简体中文](../../zh-Hans/Workflows/Create_Cmd_Workflow.md)
- 🇹🇼 [繁體中文](../../zh-Hant/Workflows/Create_Cmd_Workflow.md)
