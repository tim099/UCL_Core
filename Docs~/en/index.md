---
title: UCL_Core Document Index
description: Multilingual document entry for the UCL_Core framework — containing Agent Command system, UCL_Asset asset system, editor pages, and module services.
last_updated: 2026-05-05
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# 📚 UCL_Core Document Index

> **UCL_Core** is the core module of the UCL framework (Editor Asset System + Module Services + Agent Command System + Editor UI). This file is the English version document entry. For other languages, see `Docs~/{en,ja,zh-Hans,zh-Hant}/index.md`.

---

## ⭐ Key Feature: Agent Command System

> **AI agent to Unity Editor cross-process command system** — agents write to `queue.json`, the Editor executes, and results are written back. This is the **most important AI collaboration tool** in this framework.

### Required Reading
| Document | Description |
|---|---|
| 🤖 **[UCL_AgentCommand_Architecture](API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)** ⭐⭐ | **Overall Architecture** — Component diagram / Lifecycle / Auto-discovery / Trigger methods / queue.json schema / Extensibility |
| [UCL_AgentCommand](API/UCL_AgentCommand/UCL_AgentCommand.md) | Data model for a single command |
| [UCL_AgentCommandsPage](UCL_EditorPage/UCL_AgentCommandsPage.md) | Editor IMGUI page (human-friendly UI) |

### Built-in Cmd API Documents
| Cmd Type | API Document | Purpose |
|---|---|---|
| `DebugLog` | [Cmd_DebugLog](API/UCL_AgentCommand/Cmd_DebugLog.md) | Sanity testing / simplest example |
| **`ResolveAssetReferences`** ⭐ | [Cmd_ResolveAssetReferences](API/UCL_AgentCommand/Cmd_ResolveAssetReferences.md) | **Batch resolve UCL_Asset chain** — BFS + reflection + maxDepth + deduplication, outputs a list of (AssetType, ID, JSON path) to the AI agent |
| **`ExportCommandCatalog`** ⭐ | [Cmd_ExportCommandCatalog](API/UCL_AgentCommand/Cmd_ExportCommandCatalog.md) | **Export all currently registered handlers as a Markdown catalog** — shares rendering logic with the page button |
| **`FindAssetUsages`** ⭐ | [Cmd_FindAssetUsages](API/UCL_AgentCommand/Cmd_FindAssetUsages.md) | **Reverse lookup of asset references** — given a target asset (e.g. RCG_CustomStatusData/Stun), scan every UCL_Asset subclass for usage points, with dotted field paths |

### Trigger Methods (4 ways)
1. Editor UI (`UCL_AgentCommandsPage`) buttons
2. `Tools/UCL/Agent Commands/Run Pending` Editor menu
3. Directly edit `AgentCommands/queue.json` + invoke any above triggers
4. **Python CLI wrapper** — `Tools~/AgentCommands/run_cmd.py` (recommended for agents)
5. **Unity Batchmode** (CI / fully automated)

Complete comparison and examples can be found in [UCL_AgentCommand_Architecture §7](API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md#7-觸發方式對照).

---

## UCL_Asset Asset System

| Document | Description |
|---|---|
| [UCL_Asset API](API/UCL_Asset/) | Asset serialization, Asset Entry, Common Editable interface |

---

## Architecture

| Document | Description |
|---|---|
| [Architecture/Polymorphism_In_UCL](Architecture/Polymorphism_In_UCL.md) ⭐ | **Polymorphism architecture** — roles and interactions of `[SerializeReference]` × `UCLI_TypeListable` × `UCL_PolymorphicHelper` × `UCL_TypeReflectCache` across GUI editing and JSON serialization paths; recommended pattern for new polymorphic fields, UnityJsonSerializableObject symmetric exception, why cache ctor must not call services |

---

## Editor Pages (UCL_EditorPage)

| Document | Description |
|---|---|
| [UCL_AgentCommandsPage](UCL_EditorPage/UCL_AgentCommandsPage.md) ⭐ | Agent Command main page (Queue management / Add / Run Pending / Export Catalog) |
| [UCL_CommonEditorPage](UCL_EditorPage/UCL_CommonEditorPage.md) | Common base for Editor pages |
| [UCL_ModuleEditPage](UCL_EditorPage/UCL_ModuleEditPage.md) | Module edit page |
| [UCL_ModuleServiceEditPage](UCL_EditorPage/UCL_ModuleServiceEditPage.md) | Module service edit page |
| [UCL_ModulePlayListPage](UCL_EditorPage/UCL_ModulePlayListPage.md) | Module play list page |
| [UCL_SelectAssetPage](UCL_EditorPage/UCL_SelectAssetPage.md) | Asset selector page |

---

## UCL_ModuleService Module Service

| Document | Description |
|---|---|
| [UCL_ModuleSystem_Architecture](UCL_ModuleService/UCL_ModuleSystem_Architecture.md) | Module system overall architecture |
| [UCL_ModuleService_API](UCL_ModuleService/UCL_ModuleService_API.md) | Service API |
| [UCL_Module_API](UCL_ModuleService/UCL_Module_API.md) | Single module API |
| [UCL_ModulePath_API](UCL_ModuleService/UCL_ModulePath_API.md) | Path calculation API |

---

## Workflows

| Document | Description |
|---|---|
| [HelpURL_Workflow](Workflows/HelpURL_Workflow.md) | Prefix mechanisms such as `ucl_core:` / `eov_docs:` |
| [Hardcoded_Localize](Workflows/Hardcoded_Localize.md) | Handling hard-coded localization strings |
| 🛠️ [Create_Cmd_Workflow](Workflows/Create_Cmd_Workflow.md) | **SOP for creating a new `Cmd_<Name>.cs` subclass** — naming / file-placement decision tree (UCL_Core vs downstream module) / standard template (CommandType / ShortDescription / ArgsSchema / HelpURL) / ExecuteAsync conventions / in-Editor verification / 8 common pitfalls / **§9 auto-classification scheme for doc placement** (`source_root` frontmatter + `Cmd_ValidateDocPlacement`) |

---

## Naming Conventions Quick Reference

| Pattern | Purpose |
|---|---|
| `Cmd_<TypeName>` | Agent Command Handler subclass (e.g. `Cmd_ResolveAssetReferences`) |
| `UCL_<Module>` | UCL framework class |
| `UCL_<Page>Page` | Editor IMGUI page |
| `<NS>.EditorLib.AgentCommands` | Agent Command namespace |

---

## Cross-Repo Resources

- Project-level workflow (including complete Agent Command workflow and troubleshooting): [`docs/Workflows/AgentCommands_Workflow.md`](../../../../../../docs/Workflows/AgentCommands_Workflow.md)
- Python CLI wrapper: [`Tools~/AgentCommands/run_cmd.py`](../../Tools~/AgentCommands/run_cmd.py)
- queue.json location: `AgentCommands/queue.json` (Project root directory)

---

## Other Languages

- 🇬🇧 English (This file)
- 🇯🇵 [日本語](../ja/index.md)
- 🇨🇳 [简体中文](../zh-Hans/index.md)
- 🇹🇼 [繁體中文](../zh-Hant/index.md)
