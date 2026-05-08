---
title: Cmd_SeedTavernIdentityAssets — Create UCL_ChatTavernIdentityAsset Shells from identities.json Roster
description: Agent Command — Reads identities.json and creates a corresponding UCL_ChatTavernIdentityAsset .json shell for each identity, pre-filling m_Tags with a single entry corresponding to its kind. Other rich data fields (avatar, role_settings, color, catchphrases) remain empty, awaiting user editing.
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_User]
tags: [agent-command, tavern, identity, asset, bootstrap]
related:
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_MigrateAssetToTemplate.md | Cmd_MigrateAssetToTemplate | Move seeded Assets to Templates~ as default templates
  - ucl_core:Docs~/{lang}/UCL_ModuleService/UCL_CoreBootstrap.md | UCL_CoreAssetBootstrap | Templates~ ↔ Project .BuiltinModules Bidirectional Sync
---

# Cmd_SeedTavernIdentityAssets

Creates a corresponding `UCL_ChatTavernIdentityAsset` `.json` shell for each identity in the `identities.json` lightweight roster.

---

## 1. Overview

### Why It's Needed

- `identities.json` is a **lightweight roster** (id / display_name / kind / created_at / last_seen_at) used by `Cmd_Tavern` and Python.
- `UCL_ChatTavernIdentityAsset` is a **rich persona** view layer (avatar / role_settings / color / catchphrases / tags).
- The two are independent, but rich data usually maps to a specific identity.
- When you want to add rich data for an identity for the first time, you need to seed the corresponding `.json` shell first, then edit it in the Editor.

### Complete Flow (One-time bootstrap)

```
1. (Prerequisite) identities.json has 5 identities (naturally generated via Cmd_Tavern op=join)
   ▼
2. Cmd_SeedTavernIdentityAssets seeds 5 UCL_ChatTavernIdentityAsset .json shells with one click
   ▼ (saved in <project>/Assets/.BuiltinModules/.../UCL_Assets/UCL_ChatTavernIdentityAsset/)
3. In Editor, use UCL_SelectAssetPage to find UCL_ChatTavernIdentityAsset → edit avatar / role_settings / catchphrases
   ▼
4. (Optional) Run Cmd_MigrateAssetToTemplate id=* to migrate all Assets to Templates~
   ▼
5. Cross-project propagation — other projects pull UCL_Core and AutoTemplatePush automatically fills missing ones
```

---

## 2. Parameters

| Parameter | Required | Default | Description |
|---|---|---|---|
| `force` | ❌ | `false` | `true` = overwrite existing Assets; `false` = skip |
| `onlyId` | ❌ | `""` | Only seed the specified ID (for single additions / testing); empty = entire roster |

---

## 3. Pre-filled Fields

| Field | Pre-filled Content | Remarks |
|---|---|---|
| `ID` | identity.id | The stable key of the `UCL_Asset` system |
| `m_Tags` | `[<kind>]` | Maps to `roster.kind` ("agent" / "human" / "npc" / "system"), categorized at a glance |
| `m_AvatarPath` | `""` | Awaiting user editing (drag-and-drop Sprite path in Inspector) |
| `m_RoleSettings` | `""` | Awaiting user editing (persona template snippet) |
| `m_ColorHex` | `""` | Awaiting user editing (#RRGGBB) |
| `m_Catchphrases` | `[]` | Awaiting user editing (LLM persona reminder bullets) |

---

## 4. Paths

```
src = AgentCommands/ChatTavern/identities.json (roster)
dst = <projectRoot>/Assets/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets/UCL_ChatTavernIdentityAsset/<id>.json
```

Uses the `UCL_Asset.Save()` API, with paths resolved by `UCL_ModuleService` for the current edit module.

---

## 5. Usage Examples

```bash
# Full roster seed (skips existing ones by default)
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run SeedTavernIdentityAssets

# Force overwrite of all
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run SeedTavernIdentityAssets --arg force=true

# Seed a single item (testing / supplementary)
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run SeedTavernIdentityAssets --arg onlyId=claude-da-xiaojie
```

---

## 6. Actions after Completion

The console prints `created / skipped / failed` counts + next-step recommendations:
- Open Editor, use `UCL_SelectAssetPage` to find and edit `UCL_ChatTavernIdentityAsset`.
- Once edited, run `Cmd_MigrateAssetToTemplate id=*` to migrate them to `Templates~`.

---

## 7. Related Documents

- [Cmd_MigrateAssetToTemplate.md](Cmd_MigrateAssetToTemplate.md) — Next step: Move to `Templates~`
- [UCL_CoreBootstrap](../../UCL_ModuleService/UCL_CoreBootstrap.md) — Overview of the `Templates~` system mechanism
- [Create_UCL_Asset_Workflow.md](../../Workflows/Create_UCL_Asset_Workflow.md) — UCL_Asset framework
