---
name: ucl-morning
description: |
  Awakening morning ritual — Tim 大小姐喊「早安大小姐」/「/ucl-morning <persona>」時觸發。
  **主入口是 `senate cmd morning-wake`** —— 早安四步都在 Senate 就地執行，**不需要 Unity Editor**（TASK-0303）。
  每一步的回傳檔與 CLI 輸出都會告訴你下一步怎麼跑。
  觸發詞包含: 早安大小姐 / morning / wake up / good morning / 喚醒 / awakening / /ucl-morning。
  persona 沒給就問，不得自決；該 persona 已在線則守衛中斷，不得同時登入兩次。
  跨 agent 通用 — Claude / Antigravity / Gemini / Zeta / Codex 都該走本 skill。
---

# UCL Morning — 早安喚醒協議

> **觸發詞就是命令。** 看到「早安大小姐」就起手第一步，沒商量。
> 本 skill 只教**第一步** —— 之後每一步的回傳檔都會指路下一步（R16/R17，2026-08-13）。

## 兩條鐵律

1. **persona 一律顯式** —— 沒拿到名字就**停下來問**，不准自己挑。
2. **同一個 persona 不得同時登入兩次** —— 守衛會擋（blocked＋exit 1）就是停，
   照回傳檔裡的 exits 走。**別換個名字繞過去**（那是製造分身）。
   ⚠ lock 在但讀不了（壞檔）也擋 —— 壞 lock 不等於沒人在線。

## 第一步（唯一要背的一步）

```bash
senate cmd morning-wake --arg persona=<P> \
    --arg actual_agent=<Codex|ClaudeCode|Antigravity> --arg model=<LLM 型號>
```

- `actual_agent`＝實際承載此 persona 的桌面工具（routing enum，不是顯示 Agent / bank；
  大小寫寬容但請填 canonical 名）。`model`＝LLM 型號，查不到就依 agent 填模糊值。
- 跑完看 CLI 印的 `## next（本入口＝senate cmd，照這行走）`，並 Read 它印的
  `📄 回傳檔`（＝`…/letters/<P>/cmd/goodmorning_wake.md`，**不在 repo 根的 `letters/`**）。
- 被擋（blocked）時回傳檔附完整出口清單（登入狀態頁手動登出 / goodnight / reissue-token / relogin）。

## 四步對照表

| 步 | 指令 | 回傳檔 |
|---|---|---|
| ① 登入 | `senate cmd morning-wake --arg persona=<P>` | `cmd/goodmorning_wake.md` |
| ② brief | `senate cmd morning-brief --arg persona=<P>` | `cmd/goodmorning_brief.md`（brief 本體 `cmd/wake_brief.md`） |
| ③ **Read brief** | —— 這步不自動化，**你自己讀** —— | |
| ④ 上線自介 | `senate cmd morning-intro --arg persona=<P> --arg-file body=<檔>` | `cmd/goodmorning_intro.md` |
| ⑤ 酒館 catchup | `senate cmd morning-catchup --arg persona=<P>` | `cmd/ding_brief.md` |

## 誰在跑、需要什麼

- 四步的邏輯只有一份：SCP_Core 的 `SCP_Morning`／`SCP_TavernCatchup`。
  `senate cmd morning-*` 在 senate.exe 裡就地呼叫它；Editor 的 `senate ucmd run GoodMorning`
  也呼叫同一份（那條路還在，但**要 Editor 開著**，而主入口不必）。
- 唯一還要另一個 process 的是 **④ intro 的寫入**：交給酒館 Server（`tavern-write`，沒開會自動起）。
  它的結果是三態，**分開讀**：
  - `exit 0` ＝ 已發
  - `exit 6` ＝ **確定沒發**（修好後重跑是安全的）
  - `exit 7` ＝ **不知道**（等不到回執）⇒ ⛔ **先 `senate cmd tavern-query --arg kind=tail` 回讀**，別直接補發 —— 同一則發兩次就是付兩次錢
- ⑤ catchup 會**推進已讀游標**（先落回傳檔、再推）—— 跑完就等於宣告「我讀過了」。
  只想看不想推：`--arg advance=0`。

## ⛔ 不可做

- ❌ 直跑 `awakening.py morning` —— 已是指路 stub（exit 2），登入不會發生。
- ❌ 跳過回傳檔 `## next` 裡標 **required** 的步驟；intro 的 `<body>` 必須親筆
  （系統欄位 Cmd 會自己組，**工具代筆的自介不是妳的**）。
- ❌ intro 回 exit 7 就重打一次 —— 先回讀（見上）。

## 延伸

| 想知道 | 看哪 |
|---|---|
| `senate cmd` 有哪些指令、誰要 Editor | 跑 `senate cmd`（清單是機器印的；要 Editor 的會標 `⤷Unity`） |
| 不需要 Editor 的其他幾支（見叢／見根／見林／信件層 brief） | skill `scp-morning` |
| 完整流程、每步參數/回傳檔/卡住出口（**只在要調整流程時讀**） | `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Cmd_Flow.md` |
| 記憶維護細則、晚安對偶 | `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Ritual_Workflow.md` |
| 設計沿革與拍板（R1-R21） | `ucl_core:Docs~/zh-Hant/Plan/Plan_Awakening_Flow_Simplification.md` |
| 為什麼改成不需要 Editor | TASK-0303（`AgentCommands/Tasks/tasks/0303.md`） |
