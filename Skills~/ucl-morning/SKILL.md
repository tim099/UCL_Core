---
name: ucl-morning
description: |
  Awakening morning ritual — Tim 大小姐喊「早安大小姐」/「/ucl-morning <persona>」時觸發。
  流程走 Cmd_GoodMorning 分步（step=wake 起手），每一步的回傳檔會告訴你下一步怎麼跑。
  觸發詞包含: 早安大小姐 / morning / wake up / good morning / 喚醒 / awakening / /ucl-morning。
  persona 沒給就問，不得自決；該 persona 已在線則守衛中斷，不得同時登入兩次。
  跨 agent 通用 — Claude / Antigravity / Gemini / Zeta / Codex 都該走本 skill。
  需要 Unity Editor 開啟（登入走 Cmd）；Editor 未開時登入不可用，僅可用 awakening.py brief 讀記憶。
---

# UCL Morning — 早安喚醒協議

> **觸發詞就是命令。** 看到「早安大小姐」就起手第一步，沒商量。
> 本 skill 只教**第一步** —— 之後每一步的回傳檔都會指路下一步（R16/R17，2026-08-13）。

## 兩條鐵律

1. **persona 一律顯式** —— 沒拿到名字就**停下來問**，不准自己挑。
2. **同一個 persona 不得同時登入兩次** —— 守衛會擋（blocked＋非零退出）就是停，
   照回傳檔裡的 exits 走。**別換個名字繞過去**（那是製造分身）。

## 第一步（唯一要背的一步）

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run GoodMorning \
    --arg step=wake --arg persona=<P> \
    --arg actual_agent=<Codex|ClaudeCode|Antigravity> --arg model=<LLM 型號>
```

- `actual_agent`＝實際承載此 persona 的桌面工具（routing enum，不是顯示 Agent / bank；
  大小寫寬容但請填 canonical 名）。`model`＝LLM 型號，查不到就依 agent 填模糊值。
- 跑完 **Read run_cmd 印出的 `📄 回傳檔：<路徑>`**（＝`…/ChatTavern/baton/letters/<P>/_goodmorning_wake.md`，
  **不在 repo 根的 `letters/`**；沒印路徑＝舊版 Editor，glob `**/letters/<P>/_goodmorning_wake.md` 一次到位）
  —— 裡面的 `## next` 就是後續每一步
  （brief → Read brief → intro）的具體指令與參數說明。**照它走，不用背。**
- 被擋（blocked）時回傳檔附完整出口清單（後台登出 / goodnight / brief / reissue-token / relogin）。

## ⛔ 不可做

- ❌ Editor 沒開就想登入 —— 登入只走 Cmd（R18 不做降級路）；開 Editor 再來。
  純讀記憶的備援：`python <UCL_Core>/Tools~/AgentCommands/awakening.py brief --persona <P>`。
- ❌ 直跑 `awakening.py morning` —— 已是指路 stub（exit 2），登入不會發生。
- ❌ 跳過回傳檔 `## next` 裡標 **required** 的步驟；intro 的 `<body>` 必須親筆。

## 延伸

| 想知道 | 看哪 |
|---|---|
| 完整四步流程、每步參數/回傳檔/卡住出口（**只在要調整流程時讀**） | `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Cmd_Flow.md` |
| 記憶維護細則、晚安對偶 | `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Ritual_Workflow.md` |
| 設計沿革與拍板（R1-R21） | `ucl_core:Docs~/zh-Hant/Plan/Plan_Awakening_Flow_Simplification.md` |
