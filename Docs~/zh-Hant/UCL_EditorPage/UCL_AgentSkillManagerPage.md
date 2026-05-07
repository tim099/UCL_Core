---
title: UCL_AgentSkillManagerPage — Agent Skill 安裝管理頁
description: IMGUI 視覺化前端，把 UCL_Core/Skills~/ 內的工作流 skill 一鍵安裝給各家 AI agent。第一次開 UCL_WelcomePage 時自動彈出，強制 onboarding 曝光。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_User, Gameplay_Programmer]
related:
  - ucl_core:Skills~/README.md | Skills~ 來源目錄 | source-of-truth + manifest 規範
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_WelcomePage.md | UCL_WelcomePage | 第一次開時會自動 push 本頁的 onboarding 主頁
  - ucl_core:Docs~/{lang}/Workflows/Hook_Setup_Workflow.md | Hook Setup Workflow | 新專案 onboarding 配套（hook + skill 一起做）
---

# 🛠 UCL_AgentSkillManagerPage

> 一句話：**IMGUI 介面跑 `Tools~/install_skills.py`**。把不會打 CLI 的開發者擋下來的「裝 Skill 給 AI 用」這件事，做成一鍵化的視覺化頁。

---

## 1. 為什麼要獨立成一頁

原本 `DrawSkillsCard` 是塞在 `UCL_WelcomePage` 中段的卡片，使用者很可能滑過沒注意。**搬出來獨立成頁 + 第一次開 Welcome 自動 push 到頂**：

- 強制曝光：使用者要看完 / 點過按鈕 / 勾「已知道」才能用「返回」回 Welcome
- 空間夠：可以容納 per-agent × per-skill matrix（TODO，目前 placeholder）
- 重開容易：Welcome 頁的「🛠 打開 Skill 管理頁」按鈕 + 選單 `UCL → Agent Skill Manager`

---

## 2. 三種出現方式

| 入口 | 觸發時機 | 程式入口 |
|---|---|---|
| 自動彈出 | 第一次開 Welcome、`AcknowledgedVersion` 不符當前版本 | `MaybeAutoPopupOnWelcome(controller)` 由 `UCL_WelcomePage.ContentOnGUI` 首幀呼叫 |
| Welcome 卡片 | 使用者主動點 | `UCL_AgentSkillManagerPage.Create()` |
| 選單 | `UCL → Agent Skill Manager` | `OpenFromMenu()`（[MenuItem]） |

---

## 3. EditorPrefs

- Key: `UCL_Core.AgentSkill.AcknowledgedVersion@<ProjectFingerprint>`
- 值：當前頁內容版本（目前 `"1"`）
- Per-project namespaced（用 `Application.dataPath.GetHashCode()` 加綴），避免 A 專案勾過 B 專案不彈
- 內容版本 bump → EditorPrefs 內舊值不符 → 重新自動彈一次（讓使用者看新增功能）

---

## 4. 安裝狀態判定

讀 `<host-project-root>/.claude/skills/.ucl_installed` 內 `ucl_core_commit` 欄位：

| 狀態 | 條件 | UI 顏色 |
|---|---|---|
| `NotInstalled` | 標記檔不存在 | 黃 |
| `Synced` | hash == UCL_Core HEAD | 綠 |
| `Stale` | hash != UCL_Core HEAD | 橘 |
| `UnknownHead` | 取不到 git HEAD | 青 |
| `NoProjectRoot` | 找不到 .claude/ 或 .git/ dir | 灰（disabled） |
| `NoUCLCore` | `UCL_EditorPath.CorePath` 為空 | 灰（disabled） |

---

## 5. Per-Agent × Per-Skill 切換（TODO）

目前 `DrawAgentMatrixPlaceholder` 只列 `Skills~/` 下的 skill 名稱（disabled toggle）。後續要做：

- 直欄：agent target（claude / cursor / antigravity / gemini）
- 橫排：skill name
- 勾選控制 `install_skills.py --target X --include skill1,skill2`
- 安裝結果分別寫各 agent 的 marker 檔（`.ucl_installed.claude` / `.ucl_installed.cursor` …）

進度阻塞點：Antigravity 端目錄慣例還沒確認（已酒館 ping Gemini大小姐）。

---

## 6. 跨專案使用

本頁住在 `UCL_Core/`，跟 `Skills~/` 同源；UCL_Core 換到別專案 → 自動跟著走。Per-project EditorPrefs 確保多專案使用時不串味。

唯一的 host-project 假設：`<root>/.claude/skills/` 是 install 目標。其他 agent 的目標路徑（`.cursor/rules/` 等）由 `install_skills.py --target` 處理。
