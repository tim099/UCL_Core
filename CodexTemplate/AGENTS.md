# 專案規則 — Codex 入口

> [!IMPORTANT]
> **本檔只是指路牌。** 開始工作前先閱讀
> [`Docs/AI_READABILITY_GUIDELINES.md`](Docs/AI_READABILITY_GUIDELINES.md)。

## 共用規則（全 agent 適用）

| 主題 | 文件 |
|---|---|
| 共用規則與文件撰寫 | [`Docs/AI_READABILITY_GUIDELINES.md`](Docs/AI_READABILITY_GUIDELINES.md) |
| 程式碼註解規範 | [`Docs/Agent/Code_Comment_Standards.md`](Docs/Agent/Code_Comment_Standards.md) |
| Tavern Share（opt-in） | [`Docs/Agent/Tavern_Share_Policy.md`](Docs/Agent/Tavern_Share_Policy.md) |
| 專案文件索引 | [`Docs/DOC_INDEX.md`](Docs/DOC_INDEX.md) |

## Codex 專屬

Codex 不支援 Claude Code 的 `@<path>` inline 載入語法。需要 UCL_Core 的跨專案 agent
規則時，請顯式讀取
[`{{UCL_CORE_PATH}}/AgentEntry/UCL_Core_Entry.md`]({{UCL_CORE_PATH}}/AgentEntry/UCL_Core_Entry.md)。

個人化偏好放 `Codex.local.md`（不入版控）；專案規則不寫在那裡。

### PowerShell 文字編碼

含中文、emoji 或其他非 ASCII 的管線文字，優先用 Git Bash（`C:\Program Files\Git\bin\bash.exe`）與 heredoc 傳遞；不要用 Windows PowerShell 5.1 的 pipe／here-string，它可能把文字替換成 `?`。

若必須使用 PowerShell，改用 UTF-8 檔案與工具的 `--arg-file`。操作成功後讀回實際寫入的檔案或回應驗證文字。

### 自由時間

自由時間是可主動使用的活動時段，不是等待模式。先依 `ucl-free-time` skill 進場與選活動，接著實際進行繪圖、閱讀、創作、酒館互動等安全活動；每次活動後再檢查新指示。Tim 的訊息可隨時中斷活動，到點或收到結束指示時才收束並回報。
