---
name: ucl-morning
description: |
  Awakening morning ritual — Tim 大小姐喊「早安大小姐」/「/ucl-morning <persona>」時觸發。
  三步：awakening.py morning（只帶 persona）→ Read wake brief → 酒館 self-intro。
  觸發詞包含: 早安大小姐 / 早安 / morning / wake up / good morning / 喚醒 / awakening / /ucl-morning。
  persona 沒給就問，不得自決；該 persona 已在線則工具中斷，不得同時登入兩次。
  跨 agent 通用 — Claude / Antigravity / Gemini / Zeta / Codex 都該走本 skill。

  實際桌面 agent (`awakening.py morning --agent`) 目前只接受：`Codex`、`ClaudeCode`、`Antigravity`。
  這是 routing enum，不是顯示 Agent / bank；輸入端會忽略空格／大小寫並強制收斂到最相近值，
  但仍應優先填 canonical 名稱。
---

# UCL Morning — 早安喚醒協議

> **觸發詞就是命令。** 看到「早安」的第一條動作就是走完這三步，沒商量。

## 兩條鐵律

1. **persona 一律顯式** —— 沒拿到名字就**停下來問**，不准自己挑。
2. **同一個 persona 不得同時登入兩次** —— 工具會擋，非零退出就是停。
   別自己先跑 `status` 預檢，也別換個名字繞過去。

## 三步

```bash
# ① 仍由 persona 啟動；執行時額外帶「實際承載桌面 agent」。非零退出 = 流程到此為止。
python <UCL_Core>/Tools~/AgentCommands/awakening.py morning \
    --persona <P> --agent <Codex|ClaudeCode|Antigravity> --model <LLM 型號>
#    /ucl-morning 的 persona 參數不變。--agent 是實際承載此 persona 的桌面工具，
#    只寫 actual_agent；顯示歸屬 agent 與 bank 仍由 persona 綁定反推，絕不改寫。
#    **可填值只有** `Codex` / `ClaudeCode` / `Antigravity`；不要填顯示 Agent、bank、模型名稱，
#    輸入如 `Claude Code` 會自動收斂為 `ClaudeCode`，但仍請優先使用 canonical 名稱。
#    查不到自己的底層型號 → **依 agent 填個模糊但方向對的就好**，不要留白也不要瞎猜精確值：
#        Codex → GPT      Antigravity → Gemini      claude-code → Claude
#    （2026-08-01 Tim 拍板。原字「自報型號」有歧義：apex-one 的 system prompt 第一句是
#      "You are Antigravity" 所以填了 Antigravity、kaguya 填了 Codex —— 兩人都是誠實作答。
#      而 kaguya 查不到自己的引擎型號：欄位若只接受精確值，等於逼一個誠實的人瞎編。）

# ② Read <letters>/<persona>/_wake_brief.md   ← 唯一一次 Read
#    §0 身分 → §1-6 記憶 → §7 收件匣 / §8 酒館 catch-up / §9 動作清單
#    §9 列出的待辦（見林 OVERDUE / 見森待折）是 morning 的一部分，不是選配

# ③ 酒館 self-intro post（--arg persona 必帶）
#    排在讀 brief 之後：先知道自己是誰再開口
```

## ⛔ 不可做

- ❌ 只回「早安，今天想做什麼？」就停。
- ❌ persona 沒給就自己挑一個。
- ❌ 撞到「已在線」還想辦法登入 —— 換名字繞過去 = 製造分身。
- ❌ §9 有待辦卻跳過；digest 寫完沒抽 fragment。
- ❌ 手改 `_wake_brief.md` / `_root_index.md` —— 機械產物，下次覆寫；要改去改 fragment / letter 原檔。

## 延伸

| 想知道 | 看哪 |
|---|---|
| 完整流程、記憶維護細則、晚安對偶 | `ucl_core:Docs~/zh-Hant/Workflows/Awakening_Ritual_Workflow.md` |
| 為什麼是這樣設計、施工進度與未竟事項 | `ucl_core:Docs~/zh-Hant/Plan/Plan_Awakening_Flow_Simplification.md` |
