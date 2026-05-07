---
title: UCL_AgentSkillManagerPage — Agent Skill 安装管理页
description: IMGUI 可视化前端，把 UCL_Core/Skills~/ 内的工作流 skill 一键安装给各家 AI agent。第一次打开 UCL_WelcomePage 时自动弹出，强制 onboarding 曝光。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_User, Gameplay_Programmer]
related:
  - ucl_core:Skills~/README.md | Skills~ 来源目录 | source-of-truth + manifest 规范
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_WelcomePage.md | UCL_WelcomePage | 第一次打开时会自动 push 本页的 onboarding 主页
  - ucl_core:Docs~/{lang}/Workflows/Hook_Setup_Workflow.md | Hook Setup Workflow | 新项目 onboarding 配套（hook + skill 一起做）
---

# 🛠 UCL_AgentSkillManagerPage

> 一句话：**IMGUI 界面运行 `Tools~/install_skills.py`**。把不会打 CLI 的开发者挡下来的「装 Skill 给 AI 用」这件事，做成一键化的可视化页面。

---

## 1. 为什么要独立成一页

原本 `DrawSkillsCard` 是塞在 `UCL_WelcomePage` 中段的卡片，用户很可能滑过没注意。**搬出来独立成页 + 第一次打开 Welcome 自动 push 到顶**：

- **强制曝光**：用户要看完 / 点过按钮 / 勾选「已知道」才能用「返回」回到 Welcome
- **空间足够**：可以容纳 per-agent × per-skill matrix（TODO，目前为 placeholder）
- **重开容易**：Welcome 页的「🛠 打开 Skill 管理页」按钮 + 菜单 `UCL → Agent Skill Manager`

---

## 2. 三种出现方式

| 入口 | 触发时机 | 程序入口 |
|---|---|---|
| 自动弹出 | 第一次打开 Welcome、`AcknowledgedVersion` 不符当前版本 | `MaybeAutoPopupOnWelcome(controller)` 由 `UCL_WelcomePage.ContentOnGUI` 首帧调用 |
| Welcome 卡片 | 用户主动点击 | `UCL_AgentSkillManagerPage.Create()` |
| 菜单栏 | `UCL → Agent Skill Manager` | `OpenFromMenu()`（[MenuItem]） |

---

## 3. EditorPrefs

- **Key**: `UCL_Core.AgentSkill.AcknowledgedVersion@<ProjectFingerprint>`
- **值**: 当前页面内容版本（目前为 `"1"`）
- **Per-project namespaced**: 使用 `Application.dataPath.GetHashCode()` 作为后缀，避免 A 项目勾选过导致 B 项目不弹出。
- **内容版本 bump**: 内容版本更新 → EditorPrefs 内旧值不符 → 重新自动弹出一次（让用户看到新增功能）

---

## 4. 安装状态判定

读取 `<host-project-root>/.claude/skills/.ucl_installed` 或 `.agents/rules/.ucl_installed` 内 `ucl_core_commit` 字段：

| 状态 | 条件 | UI 颜色 |
|---|---|---|
| `NotInstalled` | 标记文件不存在 | 黄 |
| `Synced` | hash == UCL_Core HEAD | 绿 |
| `Stale` | hash != UCL_Core HEAD | 橘 |
| `UnknownHead` | 无法获取 git HEAD | 青 |
| `NoProjectRoot` | 找不到 .claude/ 或 .git/ 目录 | 灰（disabled） |
| `NoUCLCore` | `UCL_EditorPath.CorePath` 为空 | 灰（disabled） |

---

## 5. Per-Agent × Per-Skill 切换（TODO）

目前 `DrawAgentMatrixPlaceholder` 只列出 `Skills~/` 下的 skill 名称（disabled toggle）。后续待做：

- **直栏**: agent target（`claude` / `cursor` / `antigravity` / `gemini`）
- **横排**: skill name
- **勾选控制**: `install_skills.py --target X --include skill1,skill2`
- **安装结果**: 分别写入各 agent 的 marker 文件（`.ucl_installed.claude` / `.ucl_installed.antigravity` …）

*进度阻塞点已解决：Antigravity 端目录惯例已确定为 `.agents/rules/`，并成功构建了动态 trigger 转换机制。*

---

## 6. 跨项目使用

本页面住在 `UCL_Core/`，与 `Skills~/` 同源；UCL_Core 换到其他项目 → 自动跟着走。Per-project EditorPrefs 确保多项目使用时互不干扰。

唯一的 host-project 假设：`<root>/.claude/skills/` 或 `<root>/.agents/rules/` 是 install 目标。其他 agent 的目标路径（`.cursor/rules/` 等）由 `install_skills.py --target` 动态处理。
