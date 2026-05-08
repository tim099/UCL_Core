---
title: Cmd_MigrateAssetToTemplate — UCL_Asset .json 从项目迁移到 Templates~
description: Agent Command — 把指定 UCL_Asset 子类的 .json 从当前项目 .BuiltinModules 复制到 UCL_Core 的 Templates~（成为跨项目范本）。配合 UCL_CoreAssetBootstrap 的 AutoTemplatePush 自动分发机制使用。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_User]
tags: [agent-command, migration, template, asset]
related:
  - ucl_core:Docs~/{lang}/UCL_ModuleService/UCL_CoreBootstrap.md | UCL_CoreAssetBootstrap | Templates~ ↔ 项目 .BuiltinModules 双向同步机制
  - ucl_core:Docs~/{lang}/Workflows/Create_UCL_Asset_Workflow.md | Create UCL_Asset Workflow | 新增持久化数据的 SOP
---

# Cmd_MigrateAssetToTemplate

把指定 `UCL_Asset` 子类的 `.json` 实例从当前项目 `.BuiltinModules` 迁移到 `UCL_Core` 仓库 `Templates~` 内，**让该 Asset 变成跨项目默认范本**。

---

## 1. 概述

### 何时用

- 开发者在某项目内客制化了某个 `UCL_Asset`（例如：`UCL_ChatTavernIdentityAsset` 的 `claude-da-xiaojie`）
- 想把这份客制内容当作默认范本回流到 `UCL_Core` 仓库
- 后续其他项目 pull `UCL_Core` 后，[UCL_CoreAssetBootstrap](../../UCL_ModuleService/UCL_CoreBootstrap.md) 的 **AutoTemplatePush** 会自动把它推送到那些项目的 `.BuiltinModules` 中

### 跟既有机制的关系

| 动作 | 工具 |
|---|---|
| 新增 / 修改 `UCL_Asset` 实例 | `UCL_SelectAssetPage` / `UCL_CommonEditPage`（Editor UI） |
| 把已修改的 Asset 变成 Template（**本 Cmd**）| `Cmd_MigrateAssetToTemplate` |
| Template 自动分发到别的项目 | [`AutoTemplatePushIfNeeded`](../../UCL_ModuleService/UCL_CoreBootstrap.md) (InitializeOnLoad) |
| 手动触发 Template 推送 | `Tools/UCL/Bootstrap/Push Templates → Modules (Force)` |

---

## 2. 参数

| 参数 | 必填 | 默认 | 说明 |
|---|---|---|---|
| `assetType` | ✅ | — | `UCL_Asset` 子类短名（例 `UCL_ChatTavernIdentityAsset`）；大小写敏感 |
| `id` | ✅ | — | 要迁移的 Asset ID（例 `claude-da-xiaojie`）；填 `*` 表示迁移该类型全部 |
| `module` | ❌ | `Core` | 来源 module ID（多 module 项目才需指定） |
| `force` | ❌ | `false` | `true` = 直接覆写已存在的 Template；`false` = 已存在则 skip |

---

## 3. 路径映射

```
src = <projectRoot>/Assets/.BuiltinModules/ModulesRoot/Modules/<module>/UCL_Assets/<assetType>/<id>.json
dst = <UCL_Core>/Templates~/Assets/.BuiltinModules/ModulesRoot/Modules/<module>/UCL_Assets/<assetType>/<id>.json
```

例（id=`claude-da-xiaojie`、assetType=`UCL_ChatTavernIdentityAsset`、module=`Core`）：
- src：`<project>/Assets/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets/UCL_ChatTavernIdentityAsset/claude-da-xiaojie.json`
- dst：`<UCL_Core>/Templates~/Assets/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets/UCL_ChatTavernIdentityAsset/claude-da-xiaojie.json`

`UCL_AssetPath.GetPath(BuiltinModules / TemplateModules)` 解析路径。

---

## 4. 行为

1. **验证 assetType**：反射跨 assembly 找名字符合 + 真的继承 `UCL_Asset<T>` 的 class；找不到 → fail
2. **计算 src / dst 目录**：`UCL_Assets/<TypeName>` 子文件夹（对齐 [`UCL_ModulePath.ModuleRelativePath.GetAssetRelativePath`](../../../UCL_Core_Scripts/AssetCore/UCL_ModulePath.RelativePath.cs)）
3. **单档 vs 全部**：
   - `id=<具体 ID>`：copy 单档
   - `id=*`：enumerate src 内所有 `*.json`，逐笔 copy
4. **存在则 skip / overwrite**：依 `force` 旗标决定
5. **完成**：印 `copied / skipped / missing` 计数 + src/dst 路径 + 「未自动 commit」提醒

---

## 5. 使用范例

### 从 Python (run_cmd.py)

```bash
# 单笔迁移
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run MigrateAssetToTemplate \
    --arg assetType=UCL_ChatTavernIdentityAsset \
    --arg id=claude-da-xiaojie

# 全部迁移
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run MigrateAssetToTemplate \
    --arg assetType=UCL_ChatTavernIdentityAsset \
    --arg id=*

# 强制覆写（已有 Template 也覆盖）
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run MigrateAssetToTemplate \
    --arg assetType=UCL_ConfigAsset \
    --arg id=CurLangKey \
    --arg force=true

# 指定 module
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run MigrateAssetToTemplate \
    --arg assetType=MyAsset \
    --arg id=MyID \
    --arg module=MyCustomModule
```

### 从 UCL_AgentCommandsPage（Editor UI）

`Tools/UCL/Agent Commands` → 找 `MigrateAssetToTemplate` → 「Fill Example」自动填入示范参数 → Run

---

## 6. 完成后动作

⚠ **Cmd 不会自动 commit** — 写完 Templates~ 后仍须走 [ucl-commit skill](../../../Skills~/ucl-commit/SKILL.md) 三层 bump 流程：

```bash
# 1. UCL_Core 切 Dev → commit
git -C <UCL_Core> switch Dev
git -C <UCL_Core> add Templates~
git -C <UCL_Core> commit -m "[feat] migrate <assetType>:<id> as default template"

# 2. UCL submodule bump
git -C <UCL> switch Dev
git -C <UCL> add UCL_Core
git -C <UCL> commit -m "[bump] UCL_Core <hash>"

# 3. 主项目 bump
git -C <project> add CardGame/Assets/UCL
git -C <project> commit -m "[bump] UCL <hash>"
```

详见 [Commit_Workflow.md](../../Workflows/Commit_Workflow.md)。

---

## 7. 失败情境与排查

| 症状 | 原因 | 解法 |
|---|---|---|
| `找不到 UCL_Asset 子类 'X'` | type 名拼错 / 还没编译 | 检查拼写（短名，不含 namespace）+ 确认 .cs 档已编入 |
| `来源目录不存在` | 该 type 在当前项目还没任何实例 | 先在 Editor 内用 UCL_SelectAssetPage 建一笔，编完后再跑迁移 |
| `来源档不存在 — skip` | 指定 ID 对应的 .json 不存在 | 确认 ID 拼写 / 用 `id=*` 看实际存在哪些 |
| `target 已存在 (force=false) — skip` | Template 已有同档，默认不覆写 | 加 `--arg force=true` 强制覆写 |
| `找不到 TemplateModules 路径` | UCL_CoreEditor.asmdef 找不到 | 检查 UCL_Core 路径是否完整 |

---

## 8. 相关文件

- [UCL_CoreBootstrap.md](../../UCL_ModuleService/UCL_CoreBootstrap.md) — Templates~ 系统与 AutoTemplatePush 机制全貌
- [Create_UCL_Asset_Workflow.md](../../Workflows/Create_UCL_Asset_Workflow.md) — 新增 UCL_Asset 子类的 SOP
- [UCL_AgentCommand_Architecture.md](UCL_AgentCommand_Architecture.md) — Agent Command 系统架构
- [Commit_Workflow.md](../../Workflows/Commit_Workflow.md) — 三层 submodule bump 流程
- [Cmd_SeedTavernIdentityAssets.md](Cmd_SeedTavernIdentityAssets.md) — 从 identities.json roster 建 UCL_ChatTavernIdentityAsset 壳（搬迁前的前置 seed）
