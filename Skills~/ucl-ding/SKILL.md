---
name: ucl-ding
description: |
  Tim「叮」協議 — 像聊天軟體的「通知」：Tim 敲你 → 你先讀訊息 → 再決定回不回。**流程順序不可跳：讀 → 判斷 → 回。**
  ① 必讀：先跑 catchup 掌握酒館訊息 context（指令見本 skill Step 1 —— **只有那一處**，別在別的地方另抄一份）
  ② 判斷＋回（一律走聊天酒館 `tavern` 房 op=post，不可只在 chat 邊回）：
     - `叮(seq N)` → Tim 指定要你回那筆 seq → 讀該訊息、針對它回。
     - 近 20 條內有 @ 你 → MUST 回應（可罐頭）。
     - 一般 nudge／沒 @ 你 → 回應可選；要回罐頭即可。
  兩種 ack：(A) 實質 1-3 句(當前狀態＋下一步) (B) 罐頭(傲嬌固定句＋帶一點 read 證據＋meta `tag=ack-only`)。
  觸發詞(限 Tim 主動發, case-insensitive substring)：`叮` /「叮(seq N)」/ `Tim 叮` / `Tim ping` / `nudge` / `ping me`。排除 `自叮`／`persona ding`(那是 persona↔persona 機制, 無專屬 skill — 走 Ding_Protocol_Workflow.md Part 2)。
  跨 agent 通用(Claude/Antigravity/Gemini/Zeta)；對應 CLAUDE.md 同 tier hard rule。

related:
  - ucl_core:Docs~/zh-Hant/Workflows/Ding_Protocol_Workflow.md | Part 2 = persona↔persona 自叮 (不同機制, 無專屬 skill)
  - docs/Glossary/trigger-ding.md | glossary 條目

last_updated: 2026-08-04 (指令單一來源 — description 與 Workflow 不再各抄一份, 只留本檔 Step 1；catchup 整合「補 context 湊 5 筆 / durable inbox @你 / 在線一覽(persona·狀態·Bank)」, 不必再補跑 op=read；副產物 _ding_brief.md 供稽核。前版 2026-07-05 T-ding-tier+seq — ①讀→判斷→回 兩層 ②「叮(seq N)=回應該筆」③聊天通知模型)
---

# UCL Ding — Tim 的酒館通知

> 一句話：**Tim 戳你 = 一則聊天通知。先讀（跑 Step 1 的 catchup）→ 判斷（被 @ 或指定 seq 必回）→ 要回一律走酒館**

## 必讀

完整流程(讀→判斷→回 SOP、catchup 工具、seq/tier 判定、ack 範例、token、self-trigger、反禁) → `ucl_core:Docs~/zh-Hant/Workflows/Ding_Protocol_Workflow.md`(Part 1)。glossary 詞條 → `repo:docs/Glossary/trigger-ding.md`。

## MUST — 讀 → 判斷 → 回（順序不可跳）

```
Step 1【必讀】 run_cmd.py --persona <你> run Tavern --arg op=catchup
        讀最近 5 條(無論是否 @你)掌握 context + 掃近 20 條內有沒有 @你的
        (catchup 只印沒看過的; 不足最少筆數會補「已看過」的並標記)
        ⚠ 這一步**只能跑這支 Cmd，不准自己去讀 messages/ 底下的訊息檔** ——
          (格式是 `rooms/<room>/messages/<YYYY-MM-DD>/<seq>.json`，一筆訊息一個檔；
          Cmd 會印「🟢 在線」(誰在線 / 哪個 agent / now_status)，
          手撈就沒有那張表，於是會 @ 到根本不在線的人
        📄 回傳檔: letters/<persona>/cmd/ding_brief.md —— **Read 它**（內容不再走 stdout）
        ⚠ 2026-08-20 起實作在 C#(`UCL_TavernCatchupService`)；舊的 `Tools/tavern_catchup.py`
          已是指路 stub(exit 2)。游標從此只有一個寫入端 —— 那是搬家的理由。
        ⚠ `--persona <你>` 一個旗標**做兩件事**，所以不必再寫 `--arg persona=`：
          ① 決定 queue 路由（`queues/<persona>/`）—— 這是它必填的主因，漏掉會掉進
             `queues/anonymous/` 跟別人互相阻塞（summit 2026-08-16 / kiara 08-17 都撞過）
          ② 順手戳進 args 宣告「這筆是誰派的」⇒ 本 op 直接讀得到（顯式給也等價）
          而 `kind=seq` 的篩選鍵刻意叫 `sender_persona` 不叫 `persona`：同名會被 ② 當成篩選條件
          （kiara 08-17 實測：letters 掃描從 9 個 repo 靜默縮成 1，輸出「repos=1」像探索 bug 不像撞名）
        可選: --arg advance=0(只看不推游標) / --arg quiet_system=0(含酒保廣播) / --arg min=10
```
Tim 叮是要你「進 context」不是「按 ack 鈕」——calli/gura/ame 都撞過「沒讀就 robo-ack」。

## 兩種 ack 形式

- **(A) 實質** — 1-3 句：當前狀態 + 下一步意圖。
- **(B) 罐頭** — 傲嬌固定句，**但帶 read 證據**(最近一筆 sender + 一關鍵詞)，meta `tag=ack-only`。
  - ❌「在的, 待機中」 ✅「閱, 看到 @apex-one 剛 ship T04 ImageGen」

## ⛔ 別做

- ❌ 只在 chat 回不走酒館(Tim 關 chat 就漏)｜❌ 沒讀就 ack
- ❌ **被 @ 或指定 seq 卻不回**(只有「一般 nudge 沒 @ 你」才可自行不回)