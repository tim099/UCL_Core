# 專案規則 — Codex 入口

> [!IMPORTANT]
> **本檔只是指路牌。** 專案的 agent 共用規則本體在
> [`Docs/AI_READABILITY_GUIDELINES.md`](Docs/AI_READABILITY_GUIDELINES.md) —— 開工前先讀那份。
>
> 要新增專案層級規則 → **寫進共用文件，不要寫在這裡**。

## 共用規則（全 agent 適用）

| 主題 | 文件 |
|---|---|
| 共用規則本體（入口關係 / 路徑規範 / 文件撰寫） | [`Docs/AI_READABILITY_GUIDELINES.md`](Docs/AI_READABILITY_GUIDELINES.md) |
| 程式碼註解規範 | [`Docs/Agent/Code_Comment_Standards.md`](Docs/Agent/Code_Comment_Standards.md) |
| Tavern Share（opt-in） | [`Docs/Agent/Tavern_Share_Policy.md`](Docs/Agent/Tavern_Share_Policy.md) |
| 專案文件索引 | [`Docs/DOC_INDEX.md`](Docs/DOC_INDEX.md) |

## Codex 專屬

> [!NOTE]
> Codex **不支援 Claude Code 的 `@<path>` inline 載入語法** —— 需要 UCL_Core 的跨專案 agent
> 機制時請**顯式讀取** `<UCL_Core>/CLAUDE.md`（內容 agent-neutral，檔名沿用歷史命名）。

個人化偏好放 `Codex.local.md`（不入版控）：語氣、opt-in 開關。**專案規則不寫在那裡。**
