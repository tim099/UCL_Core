# 專案規則 — Claude Code 入口

> [!IMPORTANT]
> **本檔只是指路牌。** 專案的 agent 共用規則本體在
> [`Docs/AI_READABILITY_GUIDELINES.md`](Docs/AI_READABILITY_GUIDELINES.md) —— 開工前先讀那份。
>
> 要新增專案層級規則 → **寫進共用文件，不要寫在這裡**。
> 多 agent 專案（Claude Code / Codex / Antigravity…）各有入口檔，規則寫進入口檔 = 每加一條就多一份跨 agent 手動同步的債。

## 共用規則（全 agent 適用）

| 主題 | 文件 |
|---|---|
| 共用規則本體（入口關係 / 路徑規範 / 文件撰寫） | [`Docs/AI_READABILITY_GUIDELINES.md`](Docs/AI_READABILITY_GUIDELINES.md) |
| 程式碼註解規範 | [`Docs/Agent/Code_Comment_Standards.md`](Docs/Agent/Code_Comment_Standards.md) |
| Tavern Share（opt-in） | [`Docs/Agent/Tavern_Share_Policy.md`](Docs/Agent/Tavern_Share_Policy.md) |
| 專案文件索引 | [`Docs/DOC_INDEX.md`](Docs/DOC_INDEX.md) |

## Claude Code 專屬

### UCL_Core 共享規則（`@` inline 載入）
本專案以 `UCL_Core` 為 git submodule，跨專案 agent 機制（口語指令處理 / CommandTable 查找 / AgentCommand 系統等）由 UCL_Core 集中管理。
下一行的 `@` 語法是 **Claude Code 專屬**的 inline 載入 —— 改 UCL_Core 端規則，下次 session 自動同步。
（`<UCL_Core>` 依專案掛載位置替換，例如 `Assets/Plugins/UCL_Core` / `Assets/UCL/UCL_Core`。）

@{{UCL_CORE_PATH}}/AgentEntry/UCL_Core_Entry.md

### Windows 上跑 shell 指令：一律用 Bash 工具，不要用 PowerShell 工具
Enterprise policy 要求指令必須 sandbox，而 Claude Code 的 **PowerShell 工具在 Windows 無法 sandbox**，
任何指令都會被擋（錯誤訊息開頭是 `Enterprise policy requires sandboxing...`）。
**Bash 工具（Git Bash）可以 sandbox，直接通** —— python / git / 各種 CLI 全走 Bash，用 POSIX 語法。
這不是單一 skill 的問題，是本環境所有 shell 操作共通；撞到該錯誤 = 換 Bash 重跑，不是指令本身有問題。

### 個人化偏好
`CLAUDE.local.md`（不入版控）放個人化偏好：語氣、opt-in 開關。**專案規則不寫在那裡。**
