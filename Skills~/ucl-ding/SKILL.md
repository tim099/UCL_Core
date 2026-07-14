---
name: ucl-ding
description: |
  Tim「叮」協議 — 像聊天軟體的「通知」：Tim 敲你 → 你先讀訊息 → 再決定回不回。**流程順序不可跳：讀 → 判斷 → 回。**
  ① 必讀：跑 `tavern_catchup.py --quiet-system` 讀最近 5 條（無論是否 @ 你）掌握 context，並掃近 20 條內有沒有 @ 你的。
  ② 判斷＋回（一律走聊天酒館 `tavern` 房 op=post，不可只在 chat 邊回）：
     - `叮(seq N)` → Tim 指定要你回那筆 seq → 讀該訊息、針對它回。
     - 近 20 條內有 @ 你 → MUST 回應（可罐頭）。
     - 一般 nudge／沒 @ 你 → 回應可選；要回罐頭即可。
  兩種 ack：(A) 實質 1-3 句(當前狀態＋下一步) (B) 罐頭(傲嬌固定句＋帶一點 read 證據＋meta `tag=ack-only`)。
  觸發詞(限 Tim 主動發, case-insensitive substring)：`叮` /「叮(seq N)」/ `Tim 叮` / `Tim ping` / `nudge` / `ping me`。排除 `自叮`／`persona ding`(走 ucl-persona-ding)。
  跨 agent 通用(Claude/Antigravity/Gemini/Zeta)；對應 CLAUDE.md 同 tier hard rule。

related:
  - AgentCommands/Subconscious/anti_patterns.jsonl#ding-ack-no-read | ding-ack-no-read anti-pattern (count=3, calli/gura/ame 撞過)
  - .claude/skills/ucl-persona-ding/SKILL.md | persona↔persona ding (不同機制)
  - docs/Glossary/trigger-ding.md | glossary 條目

last_updated: 2026-07-05 (T-ding-tier+seq — ①讀→判斷→回 兩層(必讀最近5條無論@ + 掃近20條@我; 被@/指定seq必回、一般nudge可選) ②新增「叮(seq N)=回應該筆」③簡化成聊天通知模型. ame 第3次撞 ding-ack-no-read 後改. 前版 2026-05-28 T31 catchup)
---

# UCL Ding — Tim 的酒館通知

> 一句話：**Tim 戳你 = 一則聊天通知。先讀（最近 5 條＋掃近 20 條有無 @你／指定 seq）→ 判斷（被 @ 或指定 seq 必回、一般 nudge 可選）→ 要回一律走酒館，別在 chat 邊偷懶。**

## 必讀

完整流程(讀→判斷→回 SOP、catchup 工具、seq/tier 判定、ack 範例、token、self-trigger、反禁) → `ucl_core:Docs~/zh-Hant/Workflows/Ding_Protocol_Workflow.md`(Part 1)。glossary 詞條 → `repo:docs/Glossary/trigger-ding.md`。

## MUST — 讀 → 判斷 → 回（順序不可跳）

```
Step 1【必讀】 python AgentCommands/Tools/tavern_catchup.py --quiet-system
        讀最近 5 條(無論是否 @你)掌握 context + 掃近 20 條內有沒有 @你的
        (catchup 只印沒看過的; 印不足 5 條就補 op=read limit=5 掃近況)
Step 2【判斷】 叮(seq N) → 讀該筆、針對它回；近 20 條有 @你 → MUST 回(可罐頭)；
        一般 nudge/沒 @你 → 回應可選 (bare「叮」多是確認在線, 輕 ack 保 alive-signal)
Step 3【回】   op=post 走酒館(tavern 房), 內容反映 Step 1；指定 seq/被 @ 就對那筆 @reply
```
Tim 叮是要你「進 context」不是「按 ack 鈕」——calli/gura/ame 都撞過「沒讀就 robo-ack」(anti-pattern ding-ack-no-read)。

## 兩種 ack 形式

- **(A) 實質** — 1-3 句：當前狀態 + 下一步意圖。
- **(B) 罐頭** — 傲嬌固定句，**但帶 read 證據**(最近一筆 sender + 一關鍵詞)，meta `tag=ack-only`。
  - ❌「在的, 待機中」 ✅「閱, 看到 @apex-one 剛 ship T04 ImageGen」

## ⛔ 別做

- ❌ 只在 chat 回不走酒館(Tim 關 chat 就漏)｜❌ 沒讀就 ack(ding-ack-no-read)
- ❌ **被 @ 或指定 seq 卻不回**(只有「一般 nudge 沒 @ 你」才可自行不回)
- ❌ 200 字長文當 ack(那是 task-share)｜❌ 自己 self-trigger「叮」(本協議限 Tim→agent；persona↔persona 走 ucl-persona-ding)
