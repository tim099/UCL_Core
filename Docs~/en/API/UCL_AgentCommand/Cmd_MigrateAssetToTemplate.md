---
title: Cmd_MigrateAssetToTemplate — Migrate UCL_Asset .json from Project to Templates~
description: Agent Command — Copies specified UCL_Asset subclass .json from current project .BuiltinModules to UCL_Core Templates~ (becoming a cross-project template). Used in conjunction with UCL_CoreAssetBootstrap's AutoTemplatePush auto-distribution mechanism.
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_User]
tags: [agent-command, migration, template, asset]
related:
  - ucl_core:Docs~/{lang}/UCL_ModuleService/UCL_CoreBootstrap.md | UCL_CoreAssetBootstrap | Templates~ ↔ Project .BuiltinModules Bidirectional Sync Mechanism
  - ucl_core:Docs~/{lang}/Workflows/Create_UCL_Asset_Workflow.md | Create UCL_Asset Workflow | SOP for adding persistent data
---

# Cmd_MigrateAssetToTemplate

Migrates specified `UCL_Asset` subclass `.json` instances from the current project `.BuiltinModules` to `UCL_Core` repository `Templates~`, **making this Asset a cross-project default template**.

---

## 1. Overview

### When to Use

- Developer customized a certain `UCL_Asset` in a project (e.g. `claude-da-xiaojie` in `UCL_ChatTavernIdentityAsset`).
- Wants to return this customized content back to the `UCL_Core` repository as a default template.
- After other projects pull `UCL_Core`, [UCL_CoreAssetBootstrap](../../UCL_ModuleService/UCL_CoreBootstrap.md)'s **AutoTemplatePush** will automatically distribute it into those projects' `.BuiltinModules`.

### Relationship with Existing Mechanisms

| Action | Tool |
|---|---|
| Add / Modify `UCL_Asset` instance | `UCL_SelectAssetPage` / `UCL_CommonEditPage` (Editor UI) |
| Make modified Asset a Template (**This Cmd**) | `Cmd_MigrateAssetToTemplate` |
| Automatically distribute Template to other projects | [`AutoTemplatePushIfNeeded`](../../UCL_ModuleService/UCL_CoreBootstrap.md) (InitializeOnLoad) |
| Manually trigger Template push | `Tools/UCL/Bootstrap/Push Templates → Modules (Force)` |

---

## 2. Parameters

| Parameter | Required | Default | Description |
|---|---|---|---|
| `assetType` | ✅ | — | Short name of `UCL_Asset` subclass (e.g. `UCL_ChatTavernIdentityAsset`); case-sensitive |
| `id` | ✅ | — | ID of the Asset to migrate (e.g. `claude-da-xiaojie`); enter `*` to migrate all of that type |
| `module` | ❌ | `Core` | Source module ID (only required for multi-module projects) |
| `force` | ❌ | `false` | `true` = directly overwrite existing Template; `false` = skip if already exists |

---

## 3. Path Mapping

```
src = <projectRoot>/Assets/.BuiltinModules/ModulesRoot/Modules/<module>/UCL_Assets/<assetType>/<id>.json
dst = <UCL_Core>/Templates~/Assets/.BuiltinModules/ModulesRoot/Modules/<module>/UCL_Assets/<assetType>/<id>.json
```

Example (id=`claude-da-xiaojie`, assetType=`UCL_ChatTavernIdentityAsset`, module=`Core`):
- src: `<project>/Assets/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets/UCL_ChatTavernIdentityAsset/claude-da-xiaojie.json`
- dst: `<UCL_Core>/Templates~/Assets/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets/UCL_ChatTavernIdentityAsset/claude-da-xiaojie.json`

`UCL_AssetPath.GetPath(BuiltinModules / TemplateModules)` is used to resolve paths.

---

## 4. Behavior

1. **Validate `assetType`**: Uses reflection across assemblies to find a class whose name matches and which actually inherits from `UCL_Asset<T>`; fails if not found.
2. **Calculate src / dst directories**: Uses `UCL_Assets/<TypeName>` subfolders (aligned with [`UCL_ModulePath.ModuleRelativePath.GetAssetRelativePath`](../../../UCL_Core_Scripts/AssetCore/UCL_ModulePath.RelativePath.cs)).
3. **Single File vs All**:
   - `id=<Specific ID>`: copies a single file.
   - `id=*`: enumerates all `*.json` in src and copies each of them.
4. **Skip vs Overwrite**: Determined by the `force` flag.
5. **Completion**: Prints `copied / skipped / missing` counts + src/dst paths + a reminder that "changes are not auto-committed".

---

## 5. Usage Examples

### From Python (run_cmd.py)

```bash
# Single item migration
senate ucmd run MigrateAssetToTemplate \
    --arg assetType=UCL_ChatTavernIdentityAsset \
    --arg id=claude-da-xiaojie

# All item migration
senate ucmd run MigrateAssetToTemplate \
    --arg assetType=UCL_ChatTavernIdentityAsset \
    --arg id=*

# Force overwrite (overwrites even if Template already exists)
senate ucmd run MigrateAssetToTemplate \
    --arg assetType=UCL_ConfigAsset \
    --arg id=CurLangKey \
    --arg force=true

# Specify module
senate ucmd run MigrateAssetToTemplate \
    --arg assetType=MyAsset \
    --arg id=MyID \
    --arg module=MyCustomModule
```

### From UCL_AgentCommandsPage (Editor UI)

`Tools/UCL/Agent Commands` → find `MigrateAssetToTemplate` → "Fill Example" to auto-fill sample parameters → Run.

---

## 6. Actions after Completion

⚠ **Cmd does not auto-commit** — after writing to `Templates~`, you still need to follow the three-tier bump workflow under [ucl-commit skill](../../../Skills~/ucl-commit/SKILL.md):

```bash
# 1. Switch to Dev in UCL_Core -> commit
git -C <UCL_Core> switch Dev
git -C <UCL_Core> add Templates~
git -C <UCL_Core> commit -m "[feat] migrate <assetType>:<id> as default template"

# 2. Bump UCL submodule
git -C <UCL> switch Dev
git -C <UCL> add UCL_Core
git -C <UCL> commit -m "[bump] UCL_Core <hash>"

# 3. Bump main project
git -C <project> add CardGame/Assets/UCL
git -C <project> commit -m "[bump] UCL <hash>"
```

For details, see [Commit_Workflow.md](../../Workflows/Commit_Workflow.md).

---

## 7. Failure Scenarios and Troubleshooting

| Symptom | Cause | Solution |
|---|---|---|
| `Cannot find UCL_Asset subclass 'X'` | Type name misspelled / not compiled yet | Check spelling (short name without namespace) + confirm `.cs` file is compiled |
| `Source directory does not exist` | The type has no instances in the current project | Create an instance using `UCL_SelectAssetPage` in the Editor, edit it, and rerun migration |
| `Source file does not exist — skip` | The `.json` corresponding to the specified ID does not exist | Check ID spelling / use `id=*` to see what actually exists |
| `Target already exists (force=false) — skip` | Template already has the same file, skipped by default | Add `--arg force=true` to force overwrite |
| `Cannot find TemplateModules path` | `UCL_CoreEditor.asmdef` cannot be located | Check if `UCL_Core` path is complete |

---

## 8. Related Documents

- [UCL_CoreBootstrap.md](../../UCL_ModuleService/UCL_CoreBootstrap.md) — Overview of the `Templates~` system and AutoTemplatePush mechanism
- [Create_UCL_Asset_Workflow.md](../../Workflows/Create_UCL_Asset_Workflow.md) — SOP for adding new `UCL_Asset` subclasses
- [UCL_AgentCommand_Architecture.md](UCL_AgentCommand_Architecture.md) — Agent Command system architecture
- [Commit_Workflow.md](../../Workflows/Commit_Workflow.md) — Three-tier submodule bump process
- [Cmd_SeedTavernIdentityAssets.md](Cmd_SeedTavernIdentityAssets.md) — Create `UCL_ChatTavernIdentityAsset` shells from identities.json roster (pre-requisite seed before migration)
