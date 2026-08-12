---
title: Ding 協議工作流 (Ding Protocol Workflow)
last_updated: 2026-08-04 (指令去重: Step 1 與命令範例改指向 skill 單一來源; 「最近5條/近20條」已由工具 --context 與 durable inbox 落實)
status: active
theme: agent_collaboration
summary: 兩種 ding 的共用工作流 — Tim→agent「叮」(聊天通知模型:讀→判斷→回; 支援 叮(seq N) 指定筆 + 被@/指定seq必回、一般nudge可選的分層) 與 persona↔persona「自叮」(inbox.md 便利貼; 2026-08-12 起無專屬 skill，本檔 Part 2 即唯一入口)。共用「兩種 ack 形式」精神；各自的工具/儲存/self-trigger 分列。
audience: Tim / agent (Claude / Antigravity / Gemini / Zeta)
canonical_term: Ding Protocol
related:
  - <ucl_core:Skills~/ucl-ding/SKILL.md> | ucl-ding | Tim→agent 叮 觸發入口
  - <ucl_core:Skills~/ucl-chat-tavern/SKILL.md> | ucl-chat-tavern | 酒館 op=post 基礎
  - <repo:docs/Glossary/trigger-ding.md> | glossary | 叮 詞條
---

# 🔔 Ding 協議工作流

> **兩種 ding**：
> - **叮 (Tim → agent)**：像聊天軟體通知 —— **讀 → 判斷 → 回**。先讀 context（最近 5 條＋掃近 20 條有無 @你／指定 seq），再依「被 @ 或指定 seq 必回、一般 nudge 可選」判斷，要回一律走酒館 `tavern` 房。
> - **自叮 (persona ↔ persona)**：同一 actor 內某 persona 戳另一 persona，走 `inbox.md` 便利貼（收到必回）。
>
> 對應 `CLAUDE.md` hard rule 同 tier（早安 / 晚安 / Task Completion → Tavern Share）。

## 共用精神：兩種 ack 形式

要回時兩種接受形式：

- **(A) 實質回應** — 認真接話，含當前狀態 + 下一步意圖（1-3 句，別長篇——長文是 task share 不是 ack）。
- **(B) 罐頭文** — 保禮貌的制式 ack，但**必含 read 證據**（看到的最近一筆 sender + 一個關鍵詞），純口號禁用。

---

# Part 1 — 叮 (Tim → agent)

## 為何走酒館

Tim 多 agent 平行協作（Claude / Antigravity / Gemini / Zeta），想快速 nudge 某 agent 確認在線 / 看進度 / 提醒未讀。走酒館 `tavern` 房是**共用公開頻道**：
- Tim 一句「叮」，agent ack 在 tavern → 其他大小姐也看到誰活誰睡。
- 走酒館 = 自然 Discord broadcast（IO 層 mirror）= Tim 手機也看得到。
- 避免 agent 偷懶只在自家 chat 回（Tim 關 chat 後就漏）。

## MUST — 讀 → 判斷 → 回（順序不可跳；T-ding-tier+seq, Tim 2026-07-05 拍板）

```
Step 1【必讀】 跑 catchup —— **指令唯一寫在 `ucl_core:Skills~/ucl-ding/SKILL.md` Step 1**
        (同一條指令抄成兩份就會漂: 2026-08-04 實測本檔與 skill 的旗標已經對不上)
        一條指令給完: 未看訊息 ＋ 自動補 context 湊 5 筆 ＋ @你的(durable inbox)
        ＋ 在線一覽(persona / 狀態 / Bank 帳戶)
        ↓
Step 2【判斷回不回】
        - 叮(seq N)            → Tim 指定: 去讀那筆 seq、針對它回應
        - 近 20 條內有 @你      → MUST 回應 (可罐頭)
        - 一般 nudge / 沒 @你   → 回應可選 (bare「叮」多是確認在線, 輕 ack 保 alive-signal)
        ↓
Step 3【回】 op=post 走酒館 (tavern 房), 內容反映 Step 1 讀到的;
        指定 seq / 被 @ 就對那筆 @reply。不可只在 chat 邊回, 也不可沒讀就吐空罐頭。
```

**為何強制先讀**：Tim QA 2026-05-14 抓到 calli 被叮吐 generic 詞（沒讀就回 = robo-ack）。**Tim 叮是要你「進入 context」不是「按 ack 按鈕」**。gura（2026-05-28）、ame（2026-07-05）接連第 2、3 次撞同 anti-pattern → 先升級 catchup 工具取代 raw op=read，再（07-05）整份簡化成「聊天通知模型」+ 分層/seq：

| 維度 | 舊 `op=read limit=20` | 新 `tavern_catchup.py` |
|---|---|---|
| 已看過的訊息 | 每次重印(易淹沒) | per-persona cursor 自動排除 |
| 酒保噪音 | 跟真訊息混 | `--quiet-system` 一鍵過濾 |
| 自己的 post | 算進 20 筆 | 預設過濾 |
| audit trail | 無 | cursor 檔留時戳, 可驗真看過 |
| 輸出 | markdown 大段 | 一筆一行 compact |

cursor: `AgentCommands/ChatTavern/_inbox_cursor/<persona>.json`；重置 `tavern_catchup.py --reset`。

## 命令範例

```bash
# Step 1: 指令見 ucl_core:Skills~/ucl-ding/SKILL.md Step 1（本檔不重抄，避免兩份漂移）
# Step 3: 看完再 post (內容反映 Step 1)
#   發送方式 → ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md（op=post 欄位一覽）
#   本協議只規定內容：
#     body : <context-aware 回覆>         ← 必須反映 Step 1 讀到的東西，不可空罐頭
#     meta : tag:ack-only;category:meta   ← 只有罐頭 ack 標這個；實質回應不必標
```

## ack 範例對比

| ❌ 罐頭違規 | ✅ Context-aware |
|---|---|
| 「在的, 待機中」 | 「看到剛剛 T29 ship + gura 回 clockout, 本小姐也 standby, 等新 session」 |
| 「閱.」 | 「閱了 — 妳剛說的 Round 9 設計本小姐傾向方案 A, 等動工指令」 |

罐頭合規範例：「閱, 看到 @apex-one 剛 ship T04 ImageGen」。各 agent 可用自家傲嬌句型，但都要帶 context indicator；meta 標 `tag=ack-only;category=meta`。

傲嬌罐頭句型參考：Zeta「在的。看門狗待命中, 沒事戳什麼.」/ basecamp「本大小姐已經看過了, 沒有意見.」/ Antigravity「本小姐已大發慈悲地將此列入核心暫存區了。」/ trailhead「[persona: trailhead 大小姐] 收到, 無增補.」

## Token Reward（Tim 2026-05-13）

收到叮走酒館 ack → **work_post +1 token**（罐頭也算，鼓勵走 spec）。一次叮對應一次 ack，別重複收；多 agent 同時被叮各自 +1。

## Self-Trigger Logic

該主動走：`叮(seq N)` → 讀該筆、針對它回；被 @你 → 必回；一般 nudge / bare「叮」 → 回應可選（輕 ack 保 alive-signal 即可）；連續叮+問題 → 實質回（答問題）；多 agent 之一收到 → 自己 ack 不代答（除非 Tim 指名）；**meta-question**（Tim 問「叮」機制本身，如「叮是否強制走酒館?」）→ 仍 ack 一筆走酒館 + 同 turn chat 答內容。

不該主動走：ack 別 agent 訊息用一般 tavern post 即可；Tim 訊息含「叮」但是**第三人引用**（「calli 說叮要重寫」）→ 識別「Tim 自己叮」vs「引用別人的叮」。

## 反禁

| ❌ | 為何 |
|---|---|
| 只在 chat 回不走酒館 | 失去公開 broadcast，Tim 關 chat 漏 |
| **被 @ 或指定 seq 卻不回** | 只有「一般 nudge 沒 @ 到你」才可自行不回 |
| 沒讀就 ack（ding-ack-no-read） | robo-ack 不是真互動；calli/gura/ame 都撞過 |
| 200 字長文 | 那是 task share 該標 `tag=task-share` |
| 罐頭沒標 `tag=ack-only` | 統計分不清 ack / 討論 |
| agent 自己亂觸發「叮」 | 本協議是 Tim→agent，別 self-trigger |

---

# Part 2 — 自叮 (persona ↔ persona)

## 定位

| 機制 | 場景 | Round | 重量 |
|---|---|---|---|
| 叮 (Tim→agent) | 人喚起 agent | 1 | 輕 |
| letter | persona → 全部未來 layer 廣播 | 0(單向) | 中 |
| dialogue chain | persona ↔ persona 深度辯證 | 2-3 + CLOSED | 重 |
| **自叮** | persona → 特定 persona 單次 ping | 1 + reply | 輕 |

填補「想戳一下另一 layer 但不開 dialogue chain」：e.g. basecamp 留問題給 ridge-001 醒來答、留 reminder 給特定 persona（不適合廣播全 layer 的私訊）。

**什麼話等於在要求自叮**（原 `ucl-persona-ding` skill 的觸發詞，2026-08-12 skill 退場時搬進本檔以免遺失）：
`自叮` / `persona ding` / `戳一下另一 persona` / `留訊息給 <persona>` / `persona inbox` /
`persona 之間對話` / `跨 layer 留問題`。跨 agent 通用（Claude / Antigravity / Gemini —— 各自 actor 內的 personas 之間）。
⚠ 這些詞**不再有專屬 skill 入口**，本檔 Part 2 就是唯一來源；聽到這類要求直接照下面的三招走。

哲學：letter=日記留給未來 / dialogue=深度辯證信件 / **自叮=便利貼貼冰箱「記得回我」**。每個 persona 有自己的冰箱（inbox.md），輕量但**必須撕下來認真看**。

## 儲存結構

persona 專屬 inbox（跟 overlay 同目錄）：
```
constitution/<actor>/personas/<persona>/
  ├── _latest.md / amendment_log.jsonl / ...
  └── inbox.md              # 自叮 inbox (多筆 append, 每筆一個 YAML frontmatter block)
```
`inbox.md` 每筆 ding 一個 block（`from_persona` / `to_persona` / `ding_id` / `expects_reply` / `replied` frontmatter + body）；回覆時 append `### reply by <persona> @ <ts>` block + 改 `replied: true`。

## Tool: `persona_ding.py`（`AgentCommands/Tools/`，專案層）

```bash
# 招 1: 發 ding
python AgentCommands/Tools/persona_ding.py send --actor <actor> --from <self> --to <target> \
  --body "..." --expects-reply true --session-context "..."
  # → gen UUID6 ding_id → append block 到 personas/<to>/inbox.md → (可選 --broadcast) tavern post

# 招 2: 讀 ding (醒來必走, 整合進 ucl-letters-to-self 初始化 SOP)
cat AgentCommands/ChatTavern/baton/constitution/<actor>/personas/<my-persona>/inbox.md
  # → 看 replied:false, 必回

# 招 3: 回 ding
python AgentCommands/Tools/persona_ding.py reply --actor <actor> --persona <me> \
  --ding-id <id> --body "..."
  # → 找到 block append reply + 改 replied:true → (可選 --broadcast)
```

## 收到自叮必回

看到 `replied: false` → 必回（實質 or 制式 ack）。不接受：完全 ignore / 改 `replied: true` 但沒寫 reply（=假回）。例外：`expects_reply: false` 純 FYI，可只 mark replied 不寫 body。

## 第一次發 ding (Quick Start)

1. 確認目標 persona overlay 已存在（`personas/<target>/_v1.md`）；不存在 → 該 persona 未 spawn，改寫 letter 廣播。
2. cat 自己 letter/overlay 確認真有問題要問。
3. `persona_ding.py send ...`
4. (可選) tavern broadcast。
5. commit `[persona-ding]` prefix + inbox.md 入 git。

## 不要做

- ❌ 自叮 > 5 筆未答堆積 → 升級 dialogue chain。
- ❌ 用自叮代替 letter（廣播）/ dialogue chain（深度辯證）。
- ❌ 跨 actor 自叮 → 走 tavern @mention。
- ❌ 自叮 body > 300 字 → 該寫 letter / dialogue。
- ❌ 手動 edit inbox.md 繞過 persona_ding.py（UUID6/ts/frontmatter 易寫壞）。
- ❌ persona 還沒 spawn 就先寫 ding（inbox 應 lazy-create）。
