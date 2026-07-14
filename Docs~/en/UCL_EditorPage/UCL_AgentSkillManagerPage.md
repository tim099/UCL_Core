---
title: UCL_AgentSkillManagerPage — Agent Skill Installation Manager
description: IMGUI visual frontend to install workflow skills from UCL_Core/Skills~/ to various AI agents with one click. Automatically pops up when opening UCL_WelcomePage for the first time for mandatory onboarding exposure.
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-07-14
target_audience: [AI_Agent, Tools_User, Gameplay_Programmer]
related:
  - ucl_core:Skills~/README.md | Skills~ Source Directory | source-of-truth + manifest specifications
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_WelcomePage.md | UCL_WelcomePage | Main onboarding page which automatically pushes this page on first open
  - ucl_core:Docs~/{lang}/Workflows/Hook_Setup_Workflow.md | Hook Setup Workflow | New project onboarding package (setup hooks + skills together)
---

# 🛠 UCL_AgentSkillManagerPage

> In a nutshell: **An IMGUI visual interface running `Tools~/install_skills.py`**. It converts the task of "installing skills for AI use" (which often acts as a barrier for developers unfamiliar with CLI) into a simple, one-click visual page.

---

## 1. Why An Independent Page?

Previously, `DrawSkillsCard` was embedded as a card in the middle of `UCL_WelcomePage`, making it easy for users to scroll past without noticing. **Moving it to an independent page and pushing it to the top on the first opening of Welcome offers several benefits**:

- **Mandatory Exposure**: Users must view/click the buttons or check "Acknowledged" to use the "Back" button and return to Welcome.
- **Sufficient Space**: It easily accommodates the per-agent × per-skill matrix (TODO, currently a placeholder).
- **Easy Re-opening**: A "🛠 Open Skill Manager" button is provided on the Welcome page, along with the menu path `UCL → Agent Skill Manager`.

---

## 2. Three Display Modes

| Entrance | Trigger Condition | Code Entry Point |
|---|---|---|
| Auto Popup | First time (no hash snapshot): pops unconditionally. Afterwards: pops only when the `Skills~` source hash snapshot has a diff (skill added/changed/removed); the snapshot is overwritten at popup time. Never pops if "Never auto-open" is checked | `MaybeAutoPopupOnWelcome(controller)` called on the first frame of `UCL_WelcomePage.ContentOnGUI` |
| Welcome Card | Active click by user | `UCL_AgentSkillManagerPage.Create()` |
| Menu Item | `UCL → Agent Skill Manager` | `OpenFromMenu()` ([MenuItem]) |

---

## 3. EditorPrefs

All three keys are suffixed with `@<ProjectFingerprint>` (`Application.dataPath.GetHashCode()`) — per-project namespaced so project A's state never blocks project B. The fingerprint must be a stable value; anything that churns with normal development activity (e.g. a git commit) is unfit as a snapshot key.

| Key | Value | Purpose |
|---|---|---|
| `UCL_Core.AgentSkill.AcknowledgedVersion@<fp>` | `"1"` | "Never auto-open" opt-out flag (written by the footer toggle; suppresses popups even on skill updates) |
| `UCL_Core.AgentSkill.SkillHashes@<fp>` | `"skill-a=1a2b3c...;skill-b=..."` | Source hash snapshot of all skills (single key, name-sorted; popup baseline, overwritten at popup time) |
| `UCL_Core.AgentSkill.LastChanges@<fp>` | `"2026-07-14 17:20 \| ~ucl-commit, +ucl-xxx"` | Change list from the last auto popup (`+`added `~`changed `-`removed; shown in the footer for anyone who closed the popup too fast) |

Hash spec: per skill, all files (hidden dot-files excluded) sorted by relative path with Ordinal comparison; each file feeds `relative path + '\0' + content` into MD5, truncated to 12 hex chars. Content goes through `ReadAllText` (absorbs BOM) with both `\r\n` and lone `\r` folded to `\n` (guards against autocrlf false diffs). EditorPrefs is per-machine — a fresh clone / new machine pops once (fresh-environment re-exposure is expected behavior, not a bug).

---

## 4. Installation Status Determination

Determined by reading the `ucl_core_commit` field inside `<host-project-root>/.claude/skills/.ucl_installed` or `.agents/rules/.ucl_installed`:

| Status | Condition | UI Color |
|---|---|---|
| `NotInstalled` | Global marker file does not exist | Yellow |
| `Synced` | Hash matches UCL_Core HEAD commit | Green |
| `Stale` | Hash does not match UCL_Core HEAD commit | Orange |
| `UnknownHead` | Unable to fetch git HEAD commit | Cyan |
| `NoProjectRoot` | Cannot locate .claude/ or .git/ directories | Gray (disabled) |
| `NoUCLCore` | `UCL_EditorPath.CorePath` is empty | Gray (disabled) |

---

## 5. Per-Agent × Per-Skill Matrix (TODO)

Currently, `DrawAgentMatrixPlaceholder` only lists skill names from `Skills~/` (disabled toggles). Future work includes:

- **Columns**: Agent targets (`claude` / `cursor` / `antigravity` / `gemini`)
- **Rows**: Skill names
- **Checkboxes**: Direct control of `install_skills.py --target X --include skill1,skill2`
- **Installation Markers**: Write separate per-agent global markers (e.g., `.ucl_installed.claude`, `.ucl_installed.antigravity`, etc.)

*Progress blocker resolved: Antigravity directory convention successfully mapped to `.agents/rules/` and dynamic triggers established.*

---

## 6. Cross-Project Usage

This page resides in `UCL_Core/`, sharing the same origin as `Skills~/`. When UCL_Core is moved to another project, this page automatically comes along. Per-project EditorPrefs ensure that settings do not overlap between multiple active projects.

The only host-project assumption is that `<root>/.claude/skills/` or `<root>/.agents/rules/` acts as the install target. Target paths for other agents are handled dynamically by `install_skills.py --target`.
