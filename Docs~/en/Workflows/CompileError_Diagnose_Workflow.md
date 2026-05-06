---
title: Unity Compile Error Diagnosis Workflow
description: Use UCL_CompileErrorTracker's .compile_status.json + check_compile.py to read compile errors even when the Cmd system itself can't load due to compile errors (chicken-and-egg). Includes dedupe / log fallback / session boundary detection / 4-step SOP / 8 common error types / real-world case study.
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [compile error, CompileError, CS0103, CS0117, CS1503, CS0246, asmdef, debug, troubleshooting]
tags: [compile, debug, agent_commands, workflow]
---

# 🔧 Unity Compile Error Diagnosis Workflow

> [!IMPORTANT]
> **Problem**: When .cs edits cause Unity compile failure, the Cmd system itself can't load (assembly fails → Registry empty → no Cmds runnable). The "exact moment you most need to query errors" is when you can't.
>
> **Core tool is the standalone Python script** [`check_compile.py`](../../../Tools~/AgentCommands/check_compile.py), which is **independent of the Cmd system** and can print deduped error lists in any state.

---

## 0. TL;DR — Agent Cheat Sheet

```bash
# Default (works in healthy & broken states)
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only

# If `.compile_status.json` not found → fallback to Editor.log
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only --fallback-log

# Wait until next compile finishes (after edits)
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --watch --watch-timeout 60
```

| Exit | Meaning |
|---|---|
| `0` | Compile success, 0 errors |
| `2` | Has errors |
| `3` | `.compile_status.json` not found (Tracker not loaded) |

> [!WARNING]
> **Mental model**: fallback path (`--fallback-log`) reads Editor.log tail, which **accumulates messages from many compile attempts**. Even after dedupe, stale errors may mix with fresh ones. Always prefer `.compile_status.json` (Tracker writes it, contains **only the latest** compile result).

---

## 1. Two Data Sources

| # | Source | Path | Available when | Freshness |
|---|---|---|:-:|---|
| ⭐ A | `.compile_status.json` | `<gitRoot>/AgentCommands/.compile_status.json` | Tracker has loaded successfully | **single compile result** — always latest |
| B | Editor.log fallback | OS-default location | always (Unity always writes) | accumulates retries — **stale**, narrowed by session boundary detection |

**Prefer A**: [`UCL_CompileErrorTracker`](../../../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/UCL_CompileErrorTracker.cs) subscribes to `CompilationPipeline.assemblyCompilationFinished` and writes JSON; fixed schema, no noise, only latest compile.

**Fallback B**: Editor.log accumulates everything. Two steps to mostly disambiguate:
1. **Session boundary detection**: find the last "real compile" `Asset Pipeline Refresh ... Total: N seconds` marker (followed within 5 lines by `CompileScripts:` sub-line); window between previous same-class marker and this one.
2. **Dedupe** by (type, file, line, message): Unity prints same error multiple times.

Even after both steps, stale errors can sneak in — fallback is "last-resort", not the main path.

### 1.1 Editor.log paths

| OS | Path |
|---|---|
| Windows | `%LOCALAPPDATA%\Unity\Editor\Editor.log` |
| macOS | `~/Library/Logs/Unity/Editor.log` |
| Linux | `~/.config/unity3d/Editor.log` |

---

## 2. 4-Step SOP

### Step 1 — Run check_compile.py for summary

Outputs to read:
- `**Errors: N**` — error count
- `Distinct after dedupe: M` — **focus on this number**
- `⚠ Source: Editor.log fallback` — warning that data may be stale

### Step 2 — Stale vs Fresh: cross-verify with file content

> [!WARNING]
> **Common fallback trap**: tool reports 5 errors but only 1 is real; the other 4 were already fixed. **Always cross-check with the actual file content** before fixing:

**Fresh error markers**:
- Code at the reported line matches the error description
- The mentioned type/member is genuinely missing in the expected namespace/asmdef

**Stale error markers**:
- Reported line has been rewritten (line numbers don't match)
- The mentioned type is now correctly imported/defined

### Step 3 — Find the real root (don't be fooled by cascades)

| Symptom | Usual root cause |
|---|---|
| `CS0103: name 'X' does not exist` plus a wave of X-related errors | X itself failed to compile → fix X's actual error |
| Cross-assembly type not found | asmdef references missing / wrong direction |
| Many `CS1503: cannot convert from ...` lambdas | upstream syntax error broke type inference chain |
| `CS0117: type does not contain definition for X` | the other type failed to compile — not a real missing member |
| 5+ errors after a single edit, but manual check shows 1 | fallback-mode stale noise — wait until Tracker is active |

**Triage hint**: dedupe shows several errors clustered in 1-3 adjacent lines → likely syntax error (missing `;` or `}`); spread across files → namespace/type issue.

### Step 4 — After each fix, **force a recompile** + iterate

Editing alone doesn't always trigger recompile (asset cache):

1. **Focus Unity window** (triggers `AssetDatabase.Refresh`) — most reliable
2. **Add a comment to a .cs** to bump mtime
3. **`Tools/UCL/Agent Commands/Run Pending`** menu — also shows Console messages

> [!IMPORTANT]
> **Agent must explicitly tell the user to focus Unity** after a batch of .cs edits and wait for the spinner. Otherwise old assembly is still loaded → all subsequent diagnostics waste time.

Loop Step 1~4 until `**Errors: 0**`. Once compiled, `.compile_status.json` appears — fallback no longer needed.

---

## 3. 8 Common Error Types

| # | Code | Example | Typical fix |
|---|---|---|---|
| 1 | **CS0103** | `name 'X' does not exist` | add `using` / fully qualify / fix X's own compile error first |
| 2 | **CS0117** | `type 'A' does not contain 'B'` | check spelling / `internal` only visible in same asm / cross-asm needs `public` |
| 3 | **CS0234** | `namespace 'X' does not contain 'Y'` | missing asmdef reference / package not installed |
| 4 | **CS1503** | `cannot convert from '(string, lambda)' to '(string label, Action onClick)'` | tuple lambda inferred as `Func<T>` not `Action` — **safest fix is to flatten the tuple into separate parameters** |
| 5 | **CS0246** | `type or namespace 'X' could not be found` | missing `using` / cross-asmdef / package not installed |
| 6 | **CS0029 / CS0266** | `cannot implicitly convert 'A' to 'B'` | explicit cast or fix type |
| 7 | **CS1061** | `does not contain 'X' and no extension method` | other type not yet compiled / missing using |
| 8 | **CS0535** | `does not implement interface member 'X'` | interface added a method, child not synced |

---

## 4. Asmdef Cross-Boundary Issues (most common non-syntax error)

UCL_Core has at least two asmdefs:

```
UCL_Core_Scripts/UCL_Core.asmdef          ← Editor + Runtime
Editor/UCL_CoreEditor.asmdef              ← Editor-only, references UCL_Core
```

**One-way dependency**: `UCL_CoreEditor → UCL_Core`.
- ✅ `UCL_CoreEditor` sees `UCL_Core` types
- ❌ `UCL_Core` cannot see `UCL_CoreEditor` types (e.g., `UCL_MenuWindow`)

**Symptom**:
```
CS0103: The name 'UCL_MenuWindow' does not exist in the current context
```

**Fixes (preferred order)**:
1. **Move the type into UCL_Core asm**: relocate from `UCL_Core/Editor/` to `UCL_Core/UCL_Core_Scripts/...` + wrap in `#if UNITY_EDITOR`
2. **Use `EditorApplication.ExecuteMenuItem("...")`** to indirectly invoke (e.g., a `[MenuItem("UCL/Menu")]`-registered method)
3. **Reflection** `Type.GetType("...")` + `MethodInfo.Invoke()` (last resort)

---

## 5. Namespace Pitfalls (even within same asm)

> [!IMPORTANT]
> **Same assembly ≠ automatic visibility across sibling namespaces**. C# name resolution walks outward from the current namespace but **doesn't cross sibling branches**.

Example: writing a page class in `UCL.Core.EditorLib.Page` calling a class in `UCL.Core.Page` (NOT `UCL.Core.EditorLib.Page`):

```csharp
using UCL.Core.Page;
// or fully qualify
UCL.Core.Page.UCL_ModuleServiceEditPage.Create();
```

**Triage hint**: if CS0103 fires on a type that "should be visible in same asm", verify the other type's actual `namespace` declaration.

---

## 6. Tracker's Own Chicken-and-Egg

> [!IMPORTANT]
> Tracker is `[InitializeOnLoad]` — its static ctor **only runs on domain reload**, which **only happens after a successful compile**.
>
> - Editor starts → compile → success → reload → Tracker subscribes → future compiles tracked
> - Editor starts → compile → **fails** → no reload → Tracker doesn't subscribe → JSON not written

**First-launch with errors scenario**: `.compile_status.json` will never appear. Must use `--fallback-log`. After the first successful compile, Tracker activates and you're back on the happy path.

The Tracker ctor writes a "placeholder" JSON for first-load debugging, but the ctor itself requires `UCL_Core` asm to compile.

---

## 7. Case Study (real session)

**Setup**: Wrote 5 new .cs files; first compile failed.

| Round | check_compile.py reported | Actual fresh errors | Fix |
|:-:|---|---|---|
| 1 | 4 distinct (lines 65, 73, 149, 151) | All 4 fresh | (a) Tracker cross-asmdef → moved into UCL_Core asm<br/>(b) `UCL_MenuWindow` cross-asmdef → switched to `EditorApplication.ExecuteMenuItem`<br/>(c) Tuple lambda CS1503 → flattened parameters |
| 2 | 4 distinct (same 4) | **0 fresh** — all stale, Editor not yet recompiled | Asked user to focus Unity |
| 3 | 5 distinct (line 152 added) | **1 fresh** (line 152) | `UCL_ModuleServiceEditPage` in `UCL.Core.Page` ≠ current `UCL.Core.EditorLib.Page` → added `using UCL.Core.Page;` |
| 4 | 0 errors | — | ✅ Clean, Tracker activated, `.compile_status.json` appears |

**Key takeaways**:
- **Don't assume N reported errors = N to fix**. Fallback mode shows stale noise. Always cross-verify with file content.
- **C# tuple-element lambda inference is fragile**. Flatten signatures rather than struggle with tuples.
- **Same asm doesn't mean same namespace visible**. Verify actual namespace declaration on CS0103.

---

## 8. Tool Maintainer Notes

### 8.1 Windows stdout encoding

`check_compile.py` outputs emoji (`🔧`/`❌`/`⚠`/`✅`); Windows default stdout uses cp950/cp1252 → throws `UnicodeEncodeError`. Header:

```python
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")
```

When adding similar tools, replicate this (or stick to ASCII).

### 8.2 Editor.log session boundary algorithm

Fallback algorithm:
1. Read tail (~1MB) — find all `Asset Pipeline Refresh ... Total: N seconds` lines
2. **Filter**: only keep markers with `CompileScripts:` within the next 5 lines (skips pure asset refreshes)
3. Window = `[2nd-last filtered marker, last filtered marker]`
4. If only one filtered marker → window = "last 200 lines before that marker"

**Known limit**: 3+ consecutive failed compiles squeezed into 200-line lookback can mix older stale errors with the latest. Once compile succeeds, Tracker takes over and the issue disappears.

---

## 9. Related

- [`UCL_CompileErrorTracker.cs`](../../../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/UCL_CompileErrorTracker.cs)
- [`Cmd_GetCompileErrors.cs`](../../../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_GetCompileErrors.cs)
- [`check_compile.py`](../../../Tools~/AgentCommands/check_compile.py) — main path
- [Workflows/Create_Cmd_Workflow](Create_Cmd_Workflow.md)
- [API/UCL_AgentCommand/UCL_AgentCommand_Architecture](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)

---

## Other languages

- 🇬🇧 English (this file)
- 🇯🇵 [日本語](../../ja/Workflows/CompileError_Diagnose_Workflow.md)
- 🇨🇳 [简体中文](../../zh-Hans/Workflows/CompileError_Diagnose_Workflow.md)
- 🇹🇼 [繁體中文](../../zh-Hant/Workflows/CompileError_Diagnose_Workflow.md)
