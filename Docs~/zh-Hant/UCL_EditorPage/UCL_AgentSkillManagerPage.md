---
title: UCL_AgentSkillManagerPage — Agent Skill 安裝管理頁
description: IMGUI 視覺化前端，把 UCL_Core/Skills~/ 內的工作流 skill 一鍵安裝給各家 AI agent。第一次開 UCL_WelcomePage 時自動彈出，強制 onboarding 曝光。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-07-14
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
| 自動彈出 | 初次（無 hash 快照）無條件彈；之後只有 `Skills~` 源 hash 快照有 diff（skill 新增/變更/移除）才彈，彈窗當下即覆寫快照。勾過「永不自動彈」則一律不彈 | `MaybeAutoPopupOnWelcome(controller)` 由 `UCL_WelcomePage.ContentOnGUI` 首幀呼叫 |
| Welcome 卡片 | 使用者主動點 | `UCL_AgentSkillManagerPage.Create()` |
| 選單 | `UCL → Agent Skill Manager` | `OpenFromMenu()`（[MenuItem]） |

---

## 3. EditorPrefs

三個 key 都以 `@<ProjectFingerprint>`（`Application.dataPath.GetHashCode()`）加綴 — per-project namespaced，避免 A 專案勾過 B 專案不彈。指紋必須是穩定值，任何隨開發活動變動的值（如 git commit）都不配當快照基準。

| Key | 值 | 用途 |
|---|---|---|
| `UCL_Core.AgentSkill.AcknowledgedVersion@<fp>` | `"1"` | 「永不自動彈」opt-out 旗標（footer toggle 寫入；skill 更新也不彈） |
| `UCL_Core.AgentSkill.SkillHashes@<fp>` | `"skill-a=1a2b3c...;skill-b=..."` | 全部 skill 的 source hash 快照（單一 key、名稱排序；彈窗判定基準，彈窗當下即覆寫） |
| `UCL_Core.AgentSkill.LastChanges@<fp>` | `"2026-07-14 17:20 \| ~ucl-commit, +ucl-xxx"` | 上次自動彈窗的變動清單（`+`新增 `~`變更 `-`移除；footer 顯示，秒關彈窗事後可查） |

Hash 規格：per-skill 對目錄下所有檔案（`.` 開頭隱藏檔除外）依相對路徑 Ordinal 排序，逐檔餵「相對路徑 + `\0` + 內文」進 MD5 取前 12 hex；內文走 `ReadAllText`（吃 BOM）+ `\r\n` 與孤立 `\r` 均摺成 `\n`（防 autocrlf 假變動）。EditorPrefs 為 per-machine — 新 clone / 換機器會首彈一次（新環境重新曝光，屬預期行為非 bug）。

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

## 5. Per-Target 安裝（已上線）

`DrawOneClickInstall` 對 `AllTargets` 列出每個 target 一行，各自一顆「安裝 / 同步 / 重裝」按鈕。目前已支援：

| Target | CLI flag | 安裝目錄 | Marker 路徑 |
|---|---|---|---|
| Claude Code | `--target claude` | `<root>/.claude/skills/` | `.claude/skills/.ucl_installed` |
| Antigravity | `--target antigravity` | `<root>/.agents/rules/` | `.agents/rules/.ucl_installed` |

頁底另一顆「🚀 一鍵安裝全部 target」會 sequential 跑所有 target — UI 在期間 disabled，每個 target 結束就釋放自己的 install lock。

## 6. Per-Agent × Per-Skill 切換（TODO）

目前 `DrawAgentMatrixPlaceholder` 只列 `Skills~/` 下的 skill 名稱（disabled toggle）。後續要做：

- 直欄：agent target（已上線：claude / antigravity；規劃中：cursor / gemini）
- 橫排：skill name
- 勾選控制 `install_skills.py --target X --include skill1,skill2`
- 安裝結果讀對應 dst 的 `.ucl_installed`（各 target 分開寫，本頁已實作分別讀）

---

## 7. 跨專案使用

本頁住在 `UCL_Core/`，跟 `Skills~/` 同源；UCL_Core 換到別專案 → 自動跟著走。Per-project EditorPrefs 確保多專案使用時不串味。

每個 target 的安裝目錄假設見 §5 表格；未來新 target 由 `install_skills.py --target` 與本頁 `AgentTarget` enum 同步擴充即可。
