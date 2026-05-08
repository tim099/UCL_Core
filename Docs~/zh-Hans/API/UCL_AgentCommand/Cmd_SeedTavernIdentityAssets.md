---
title: Cmd_SeedTavernIdentityAssets — 从 identities.json roster 建 UCL_ChatTavernIdentityAsset 壳
description: Agent Command — 读 identities.json 为每笔 identity 建一个对应的 UCL_ChatTavernIdentityAsset .json 壳，预填 m_Tags 一笔对应 kind；其他 rich data 栏位（avatar / role_settings / color / catchphrases）保持空，等使用者编辑。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_User]
tags: [agent-command, tavern, identity, asset, bootstrap]
related:
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_MigrateAssetToTemplate.md | Cmd_MigrateAssetToTemplate | 把 seed 出的 Asset 搬到 Templates~ 当默认范本
  - ucl_core:Docs~/{lang}/UCL_ModuleService/UCL_CoreBootstrap.md | UCL_CoreAssetBootstrap | Templates~ ↔ 项目 .BuiltinModules 双向同步
---

# Cmd_SeedTavernIdentityAssets

依 `identities.json` lightweight roster 为每笔 identity 建立一个对应的 `UCL_ChatTavernIdentityAsset` `.json` 壳。

---

## 1. 概述

### 为什么需要

- `identities.json` 是 **lightweight roster**（id / display_name / kind / created_at / last_seen_at），给 `Cmd_Tavern` 与 Python 用
- `UCL_ChatTavernIdentityAsset` 是 **rich persona** view layer（avatar / role_settings / color / catchphrases / tags）
- 两者独立 — 但 rich data 通常会跟某个 identity 对应
- 首次想为某 identity 加 rich data 时，需要先 seed 出对应 `.json` 壳 → 再用 Editor 编辑

### 完整流程（一次性 bootstrap）

```
1. (前提) identities.json 已有 5 笔 identity（Cmd_Tavern op=join 自然产生）
   ▼
2. Cmd_SeedTavernIdentityAssets 一键建 5 个 UCL_ChatTavernIdentityAsset .json 壳
   ▼ (落在 <project>/Assets/.BuiltinModules/.../UCL_Assets/UCL_ChatTavernIdentityAsset/)
3. Editor 内用 UCL_SelectAssetPage 找 UCL_ChatTavernIdentityAsset → 编 avatar / role_settings / catchphrases
   ▼
4. (可选) Cmd_MigrateAssetToTemplate id=* 把所有 Asset 搬到 Templates~
   ▼
5. 跨项目散播 — 别的项目 pull UCL_Core 后 AutoTemplatePush 自动补齐
```

---

## 2. 参数

| 参数 | 必填 | 默认 | 说明 |
|---|---|---|---|
| `force` | ❌ | `false` | `true` = 覆写已存在的 Asset；`false` = skip |
| `onlyId` | ❌ | `""` | 只 seed 指定 ID（适用补单笔 / 测试）；空 = 全部 roster |

---

## 3. 预填栏位

| 栏位 | 预填内容 | 备注 |
|---|---|---|
| `ID` | identity.id | UCL_Asset 系统的稳定键 |
| `m_Tags` | `[<kind>]` | 对应 roster.kind（"agent" / "human" / "npc" / "system"），让使用者一眼看到分类 |
| `m_AvatarPath` | `""` | 待使用者编辑（Inspector 拖 Sprite path） |
| `m_RoleSettings` | `""` | 待使用者编辑（persona 模板片段） |
| `m_ColorHex` | `""` | 待使用者编辑（#RRGGBB） |
| `m_Catchphrases` | `[]` | 待使用者编辑（LLM persona reminder bullets） |

---

## 4. 路径

```
src = AgentCommands/ChatTavern/identities.json (roster)
dst = <projectRoot>/Assets/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets/UCL_ChatTavernIdentityAsset/<id>.json
```

走 `UCL_Asset.Save()` API，由 `UCL_ModuleService` 解析当前 edit module 路径。

---

## 5. 使用范例

```bash
# 全 roster seed（默认不覆写已存在的）
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run SeedTavernIdentityAssets

# 强制全部覆写
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run SeedTavernIdentityAssets --arg force=true

# 只 seed 单笔（测试 / 补件）
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run SeedTavernIdentityAssets --arg onlyId=claude-da-xiaojie
```

---

## 6. 完成后动作

Console 会印 `created / skipped / failed` 计数 + 下一步建议：
- 开 Editor 用 `UCL_SelectAssetPage` 找 `UCL_ChatTavernIdentityAsset` 编辑
- 编完后跑 `Cmd_MigrateAssetToTemplate id=*` 把它们搬到 Templates~

---

## 7. 相关文件

- [Cmd_MigrateAssetToTemplate.md](Cmd_MigrateAssetToTemplate.md) — 下一步：搬到 Templates~
- [UCL_CoreBootstrap](../../UCL_ModuleService/UCL_CoreBootstrap.md) — Templates~ 系统机制
- [Create_UCL_Asset_Workflow.md](../../Workflows/Create_UCL_Asset_Workflow.md) — UCL_Asset 框架
