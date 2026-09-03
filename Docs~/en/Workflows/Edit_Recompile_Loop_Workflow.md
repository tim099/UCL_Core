---
title: Edit → Recompile → Fix-Errors Loop Workflow
description: Step-by-step SOP — after an agent / tool maintainer edits .cs files, how to force Unity to recompile, confirm there are no compile errors, and loop fixes until reaching 0 errors. Built on top of Cmd_Recompile + UCL_CompileErrorTracker + run_cmd.py.
source_root: AgentCommands/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [edit recompile loop, script edit loop, compile error fix loop, agent compile loop]
tags: [workflow, agent, compile, recompile, error-fix]
---

# 🔁 Edit → Recompile → Fix-Errors Loop Workflow

> [!IMPORTANT]
> This workflow owns the SOP for "**after editing a .cs, how to confirm it actually got compiled in + that no compile errors slipped in**".
> After editing files, an agent must NOT assume Unity has reloaded — when the Editor lacks focus or has Auto Refresh disabled,
> your code may not even be in the assembly yet, and every subsequent Cmd will run against the stale version.

> Design philosophy: **forced sync point**. `Cmd_Recompile` + the Python `recompile` subcommand together form the "**every .cs change before this point is now reflected**" guarantee boundary.

---

## 0. TL;DR — Understand the Loop in Five Minutes

```
[1] Edit / generate .cs files (Edit / Write)
       ▼
[2] senate ucmd run Recompile     ← triggers Unity recompile + waits for completion
       │
       ├── exit 0  → clean, continue downstream flow
       └── exit 1  → compile error(s) present
              ▼
[3] Read messages from AgentCommands/.compile_status.json
       ▼
[4] For each error, look up file:line and fix the source
       ▼
[5] goto [2] (at most N rounds; ≤ 5 recommended; beyond that means wrong direction — call a human)
```

---

## 1. Prerequisites (check once at the start of each session)

| # | Check | How to verify | What to do if it fails |
|---|---|---|---|
| 1 | Unity Editor is running | Visible in the system tray / window list | Launch Unity, load the project |
| 2 | Auto-Watcher is enabled | Open UCL_AgentCommandsPage and confirm `Auto-Watcher ✔` | Click the checkbox to flip it to ✔ |
| 3 | `run_cmd.py` is callable | `python <path> --help` prints usage | Fix PATH / verify Python install |
| 4 | `.compile_status.json` exists | `AgentCommands/.compile_status.json` | Trigger one compile inside Unity (touch and save any file) |

> [!CAUTION]
> If Auto-Watcher is Idle, every Cmd will sit pending. Without it enabled, the `recompile` subcommand **cannot work**.

---

## 2. Why Force a `recompile`?

After an agent writes `.cs`, Unity does not necessarily compile right away:

| Unity state | Behavior |
|---|---|
| Editor focused + Auto Refresh ON | Detects file change immediately → compiles (the ideal case) |
| Editor in background + Auto Refresh ON | Detection waits until focus returns (invisible from the agent's perspective) |
| Auto Refresh OFF | Will not compile automatically at all; needs manual Ctrl+R |
| Last compile failed | Stuck in error state, new Cmd handlers cannot be loaded |

**Conclusion**: after an agent edits `.cs`, **you must not assume** the change has taken effect. You must run `recompile` to force the sync point and confirm 0 errors via the exit code.

---

## 3. Core Loop (pseudocode)

```python
import subprocess, json
from pathlib import Path

RUN_CMD = "CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py"
STATUS  = Path("AgentCommands/.compile_status.json")

def recompile_and_check() -> tuple[int, list]:
    """returns (errors_count, messages); errors_count==0 → clean"""
    rc = subprocess.run(["python", RUN_CMD, "recompile"], capture_output=False)
    if rc.returncode == 0:
        return 0, []
    if rc.returncode == 1:
        st = json.loads(STATUS.read_text(encoding="utf-8-sig"))
        return st["total_errors"], [m for m in st["messages"] if m["type"] == "Error"]
    raise RuntimeError(f"infra failure: exit code {rc.returncode}")

# Main loop
MAX_ROUNDS = 5
for round_idx in range(MAX_ROUNDS):
    edit_files(...)            # agent edits / generates .cs
    err_count, errors = recompile_and_check()
    if err_count == 0:
        break
    for e in errors:
        print(f"× {e['file']}:{e['line']}  {e['message']}")
        fix_error(e)            # read source + Edit
else:
    raise RuntimeError(f"still {err_count} errors after {MAX_ROUNDS} rounds — STOP, ask human")
```

---

## 4. Detailed Steps

### 4.1 Edit / Generate .cs Files
- Use the Edit / Write tools to modify source
- **Do not** create `.meta` files manually (Unity generates them; see memory `feedback_no_direct_meta.md`)
- Batch multi-file changes in one go and recompile once at the end (avoid one recompile per file)

### 4.2 Trigger recompile
```bash
senate ucmd run Recompile
```

**Exit code reference**:

| exit | Meaning | Action |
|---|---|---|
| 0 | Compile finished, 0 errors | Continue |
| 1 | Compile finished, has errors | Go to §4.3 to fix |
| 2 | Cmd_Recompile was not picked up by Unity (queue not drained) | Re-check prerequisites in §1 — Watcher / Editor state |
| 3 | Failed to parse `.compile_status.json` | File corrupted / encoding issue |
| 4 | mtime did not advance (compile did not run) | Did UCL_CompileErrorTracker fail to hook events? See [CompileError_Diagnose_Workflow](CompileError_Diagnose_Workflow.md) |

### 4.3 Read the Error Messages
- stdout prints the first 5 entries
- Full list: the `messages` array in `AgentCommands/.compile_status.json`, with fields:
  - `type`: `"Error"` / `"Warning"`
  - `file`: relative path (from the Unity project root)
  - `line`: line number
  - `column`: column number
  - `message`: error text (includes the CS code, e.g. `CS0103: ...`)

### 4.4 Fix the Errors
For each error:
1. **Open source**: use the Read tool to view the code context around `file:line` (±10 lines)
2. **Diagnose**: cross-reference common CS errors (see [CompileError_Diagnose_Workflow §Common Errors](CompileError_Diagnose_Workflow.md))
3. **Edit source**: use Edit with the smallest possible scope
4. **Avoid cascading breakage**: when changing `RCG_X`, check whether anything else references it (Grep first)

### 4.5 Back to §4.2
Run recompile again, confirm the error is gone (and that no new error was introduced).

### 4.6 Exit Conditions
- ✅ exit 0 → move to the next workflow (e.g. ExportNotes / testing / commit)
- ❌ Still errors after 5 consecutive rounds → **stop**, hand the error list and the fixes you tried over to a human. Plowing on blindly only makes things worse.

---

## 5. Failure-Mode Reference

| Symptom | Possible Cause | Diagnosis / Fix |
|---|---|---|
| `recompile` exits 2; queue stuck on Recompile cmd | Auto-Watcher not enabled | Open UCL_AgentCommandsPage, confirm `✔ Auto-Watcher` |
| `recompile` exits 4 | UCL_CompileErrorTracker did not write status | Look for `Tracker just loaded, no compile event captured yet` placeholder; touch any file to trigger a compile once |
| Same error stays after editing | Unity did not recompile the target file | Confirm the file is actually saved; run `recompile` again |
| Edited file A but file B reports the error | namespace / asmdef isolation; CS0246 missing using | See [CompileError_Diagnose_Workflow §asmdef](CompileError_Diagnose_Workflow.md) |
| New errors keep showing up | The edit broke a contract upstream | Roll back + replan; may be time to stop and ask a human |
| `recompile` ran but the change did not take effect | The edited file is in an `_Editor` submodule / under an `Editor/` subfolder | Check the corresponding asmdef is dirty + whether the script type is Editor-only |

---

## 6. Relationship with Other Workflows

```
   Create_EditorPage_Workflow          create a new page
   Create_Cmd_Workflow                 create a new Cmd
              │
              ▼ after editing .cs
   ┌──────────────────────────────────────┐
   │  Edit_Recompile_Loop_Workflow (here) │  ← forced sync + error fixing
   └──────────────────────────────────────┘
              │
              ▼ once compile reaches 0 errors
   Downstream: run Cmd_ExportNotes / automated tests / commit

   For compile-error analytical detail: see CompileError_Diagnose_Workflow
```

---

## 7. Usage Examples

### Example A: agent verifies after adding a new Cmd
```bash
# 1. Use Edit / Write to create Cmd_Foo.cs
# 2. Trigger recompile
senate ucmd run Recompile
# → expect exit 0; if exit 1, read compile_status.json, fix, then run again

# 3. Confirm the new Cmd is registered
senate ucmd run ExportCommandCatalog | grep "Foo"

# 4. Run the new Cmd
senate ucmd run Foo --arg x=1
```

### Example B: agent verifies after refactoring an EditorPage
```bash
# 1. Edit RCG_StoryDataEditorPage.cs
# 2. recompile
senate ucmd run Recompile
# 3. Run ExportNotes to verify the output stays aligned
senate ucmd run ExportNotes --arg targets=story
# 4. Eyeball the file / git diff for confirmation
```

---

## 8. Acceptance Checklist

Agent self-check (run once at the end of each round):

- [ ] The most recent `recompile` exited 0
- [ ] `.compile_status.json` has `total_errors == 0`
- [ ] No leftover temporary markers (`__DELETE_ME__` / `_Deprecated`, etc.) in the edited .cs files
- [ ] No `.meta` was hand-created
- [ ] When exiting the loop, round count ≤ 5 (anything more means stuck — do not continue)

---

## 9. Related Documents

- [Create_Cmd_Workflow](Create_Cmd_Workflow.md) — creating a new `Cmd_<Name>.cs`
- [Create_EditorPage_Workflow](Create_EditorPage_Workflow.md) — creating a new `UCL_*Page`
- [CompileError_Diagnose_Workflow](CompileError_Diagnose_Workflow.md) — fine-grained compile-error triage (asmdef / CS0246 / etc.)
- [HelpURL_Workflow](HelpURL_Workflow.md) — `[HelpURL]` prefix resolution
- `run_cmd.py` — Python CLI wrapper (`recompile` / `run` / `submit` / `wait` / `catalog`)
- `Cmd_Recompile` — the Editor-side Agent Command that triggers a recompile
