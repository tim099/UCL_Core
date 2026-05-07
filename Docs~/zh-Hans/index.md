---
title: UCL_Core 文件索引
description: UCL_Core 框架的多语系文件入口 — 含 Agent Command 系统、UCL_Asset 资产系统、编辑器页面、模块服务等四大主题分类
last_updated: 2026-05-05
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# 📚 UCL_Core 文件索引

> **UCL_Core** 是 UCL 框架的核心模块（编辑器资产系统 + 模块服务 + Agent Command 系统 + 编辑器 UI）。本档是简体中文版文件入口，其他语系见 `Docs~/{en,ja,zh-Hans,zh-Hant}/index.md`。

---

## ⭐ 重点：Agent Command 系统

> **AI agent 与 Unity Editor 的跨 process 指令系统** — agent 写 `queue.json`、Editor 端执行、结果写回。是本框架**最重要的 AI 协作工具**。

### 必读
| 文件 | 说明 |
|---|---|
| 🤖 **[UCL_AgentCommand_Architecture](API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)** ⭐⭐ | **整体架构** — 组件图 / 生命周期 / 自动发现 / 触发方式 / queue.json schema / 扩展点 |
| [UCL_AgentCommand](API/UCL_AgentCommand/UCL_AgentCommand.md) | 单一指令的数据模型 |
| [UCL_AgentCommandsPage](UCL_EditorPage/UCL_AgentCommandsPage.md) | Editor IMGUI 页面（人类友好 UI） |

### 内建 Cmd 的 API 文件
| Cmd Type | API 文件 | 用途 |
|---|---|---|
| `DebugLog` | [Cmd_DebugLog](API/UCL_AgentCommand/Cmd_DebugLog.md) | 连线测试 / 最简范例 |
| **`ResolveAssetReferences`** ⭐ | [Cmd_ResolveAssetReferences](API/UCL_AgentCommand/Cmd_ResolveAssetReferences.md) | **批次解析 UCL_Asset 联动链** — BFS + 反射 + maxDepth + 去重，输出 (AssetType, ID, JSON 路径) 清单给 AI agent |
| **`ExportCommandCatalog`** ⭐ | [Cmd_ExportCommandCatalog](API/UCL_AgentCommand/Cmd_ExportCommandCatalog.md) | **导出当前所有已注册 Handler 为 Markdown 目录** — 与 Page 按钮共用渲染逻辑 |
| **`FindAssetUsages`** ⭐ | [Cmd_FindAssetUsages](API/UCL_AgentCommand/Cmd_FindAssetUsages.md) | **反向查询被引用位置** — 给定目标 Asset（例 RCG_CustomStatusData/Stun），扫描所有 UCL_Asset 子类找出所有引用点，附 dotted field path |

### 触发方式（4 种）
1. Editor UI（`UCL_AgentCommandsPage`）按钮
2. `Tools/UCL/Agent Commands/Run Pending` Editor 菜单
3. 直接编辑 `AgentCommands/queue.json` + 上面任一触发
4. **Python CLI 包装器** — `Tools~/AgentCommands/run_cmd.py`（推荐给 Agent）
5. **Unity Batchmode**（CI / 全自动）

完整对照与范例见 [UCL_AgentCommand_Architecture §7](API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md#7-觸發方式對照)。

---

## UCL_Asset 资产系统

| 文件 | 说明 |
|---|---|
| [UCL_Asset API](API/UCL_Asset/) | 资产序列化、Asset Entry、Common Editable 接口 |

---

## UCL_GUILayout / UCL_GUIStyle（IMGUI 组件 + 样式层）

| 文件 | 说明 |
|---|---|
| 🎨 **[UCL_GUILayout_Overview](API/UCL_GUILayout/UCL_GUILayout_Overview.md)** ⭐ | **8 档 partial class 的整体导览** — 设计分层、文件职责、API 速查（按用途分组）、跨文件共通模式（三段式重载 / `[SerializeReference]` 多态自动侦测 / 反射缓存）、三个少见但高价值 helper（`IntFieldAuto` / `PopupSearchCache` / `DrawCopyPaste`）|
| 🎨 [UCL_GUIStyle_Overview](API/UCL_GUIStyle/UCL_GUIStyle_Overview.md) | **IMGUI 样式中央** — `BoxStyle` / `ButtonStyle` / `LabelStyle` / `TextField/Area`、DPI 全域 `Scale`、EditorWindow / Runtime 双 cache、`LabelStyle` 反指守则（不可给互动控件） |

---

## Architecture

| 文件 | 说明 |
|---|---|
| [Architecture/Polymorphism_In_UCL](Architecture/Polymorphism_In_UCL.md) ⭐ | **多型支援整体架构** — `[SerializeReference]` × `UCLI_TypeListable` × `UCL_PolymorphicHelper` × `UCL_TypeReflectCache` 四者在 GUI 编辑与 JSON 序列化两条路径的角色与互动；新增多型字段的标准写法、UnityJsonSerializableObject 双边例外、为何 cache ctor 不能碰 service |

---

## 编辑器页面（UCL_EditorPage）

| 文件 | 说明 |
|---|---|
| [UCL_AgentCommandsPage](UCL_EditorPage/UCL_AgentCommandsPage.md) ⭐ | Agent Command 主页面（队列管理 / 新增 / Run Pending / Export Catalog）|
| [UCL_CommonEditorPage](UCL_EditorPage/UCL_CommonEditorPage.md) | 编辑器页面共通基底 |
| [UCL_ModuleEditPage](UCL_EditorPage/UCL_ModuleEditPage.md) | 模块编辑页面 |
| [UCL_ModuleServiceEditPage](UCL_EditorPage/UCL_ModuleServiceEditPage.md) | 模块服务编辑页面 |
| [UCL_ModulePlayListPage](UCL_EditorPage/UCL_ModulePlayListPage.md) | 模块播放列表 |
| [UCL_SelectAssetPage](UCL_EditorPage/UCL_SelectAssetPage.md) | 资产选择器 |

---

## UCL_ModuleService 模块服务

| 文件 | 说明 |
|---|---|
| [UCL_ModuleSystem_Architecture](UCL_ModuleService/UCL_ModuleSystem_Architecture.md) | 模块系统整体架构 |
| [UCL_ModuleService_API](UCL_ModuleService/UCL_ModuleService_API.md) | 服务 API |
| [UCL_Module_API](UCL_ModuleService/UCL_Module_API.md) | 单一模块 API |
| [UCL_ModulePath_API](UCL_ModuleService/UCL_ModulePath_API.md) | 路径计算 API |

---

## Workflows

| 文件 | 说明 |
|---|---|
| [HelpURL_Workflow](Workflows/HelpURL_Workflow.md) | `ucl_core:` / `eov_docs:` 等 prefix 机制 |
| [Hardcoded_Localize](Workflows/Hardcoded_Localize.md) | 硬写本地化字符串的处理 |
| 🛠️ [Create_Cmd_Workflow](Workflows/Create_Cmd_Workflow.md) | **创建新的 `Cmd_<Name>.cs` 子类 SOP** — 命名 / 文件位置决策树（UCL_Core vs 下游模块） / 标准范本（CommandType / ShortDescription / ArgsSchema / HelpURL） / ExecuteAsync 守则 / Editor 验收 / 8 大常见地雷 / **§9 文件放置自动判断方案**（`source_root` frontmatter + `Cmd_ValidateDocPlacement`）|
| 🛠️ [Create_EditorPage_Workflow](Workflows/Create_EditorPage_Workflow.md) ⭐ | **创建新的 `UCL_CommonEditorPage` 子类 SOP** — 继承关系 / 必/选 override / TopBarButtons 客制 / 入口点挂接（父页 / Welcome 卡片 / 菜单） / UI 组件选用对照（链接 UCL_GUILayout 与 UCL_GUIStyle 文件） / HelpURL `{lang}` 占位 / 8 大常见地雷 / 验收清单 |

---

## 命名规则速查

| 样式 | 用途 |
|---|---|
| `Cmd_<TypeName>` | Agent Command Handler 子类（如 `Cmd_ResolveAssetReferences`）|
| `UCL_<Module>` | UCL 框架类别 |
| `UCL_<Page>Page` | Editor IMGUI 页面 |
| `<NS>.EditorLib.AgentCommands` | Agent Command 命名空间 |

---

## 跨 repo 资源

- 项目层工作流（含完整 Agent Command 工作流、踩雷纪录）：[`docs/Workflows/AgentCommands_Workflow.md`](../../../../../../docs/Workflows/AgentCommands_Workflow.md)
- Python CLI 包装器：[`Tools~/AgentCommands/run_cmd.py`](../../Tools~/AgentCommands/run_cmd.py)
- queue.json 位置：`AgentCommands/queue.json`（项目根目录）

---

## 其他语系

- 🇬🇧 [English](../en/index.md)
- 🇯🇵 [日本語](../ja/index.md)
- 🇨🇳 简体中文（本档）
- 🇹🇼 [繁體中文](../zh-Hant/index.md)
