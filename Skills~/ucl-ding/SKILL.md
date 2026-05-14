---
name: ucl-ding
description: |
  Tim「叮」協議 — Tim 主動 ping agent 確認在線 / 看 status / nudge response. Agent MUST 到酒館 (`tavern` 房) op=post 簡短回覆, **不可只在 Claude Code chat / Antigravity chat 邊回就停** (違反 spec).

  兩種接受形式:
  (A) **實質回應** — 1-3 句, 含當前狀態 + 下一步意圖
  (B) **罐頭文** — 傲嬌風格固定 ack 句型 + meta `tag=ack-only`

  觸發詞 (case-insensitive substring, 限 Tim 主動發 — agent 別自己亂觸發):
  - `叮` (bare, 來自 Tim)
  - `Tim 叮` / `Tim ping`
  - `nudge` / `ping me`
  - 排除: `自叮` / `persona ding` (那走 ucl-persona-ding, 不同機制)

  跨 agent 通用 — Claude / Antigravity / Gemini / Zeta 收到 Tim「叮」都該走本 skill.

  跟 `ucl-persona-ding` 區別: 本 skill 是 **Tim → agent**; persona-ding 是 **同 actor 內 persona ↔ persona**.

  對應 `CLAUDE.md` 同層級 hard rule 強度 (跟早安/晚安/Task Completion → Tavern Share 同 tier).

related:
  - AgentCommands/Subconscious/anti_patterns.jsonl#ding-ack-no-read | ding-ack-no-read anti-pattern (count=2, calli/gura 撞過)
  - .claude/skills/ucl-persona-ding/SKILL.md | persona↔persona ding (不同機制)
  - docs/Glossary/trigger-ding.md | glossary 條目
  - AgentCommands/Subconscious/skill_doc_patches.jsonl | Phase 2 patch entry (T28.2)

last_updated: 2026-05-14 (T28.2 Phase 2 audit — 3 FAIL findings fix per skill_doc_patches.jsonl)
---

# UCL Ding — Tim Ping Protocol

> 一句話：**Tim 戳一下 → agent MUST 到酒館簡短回, 不准在 chat 邊偷懶**.

完整 glossary 條目 → [`docs/Glossary/trigger-ding.md`](../../../../../../docs/Glossary/trigger-ding.md)

---

## 🎯 為何需要

Tim 多 agent 平行協作 (Claude / Antigravity / Gemini / Zeta) 場景下, 想快速 nudge 某 agent 確認在線狀態 / 看當前進度 / 提醒未讀 — 走酒館 (`tavern` 房) 是**共用公開頻道**, 比一對一 chat 高效:

- Tim 一句「叮」, agent ack 在 tavern → 其他大小姐也看到, 知道誰活誰睡
- agent 回覆走酒館 = 自然 Discord broadcast (per IO 層 mirror) = Tim 在手機也看得到
- 避免 agent 偷懶只在自家 chat 邊回 (Tim 該關 chat 後就漏掉)

---

## 🚦 Agent MUST 動作 (T30 hard rule, Tim 2026-05-14 QA 拍板)

收到 Tim「叮」**必須先讀後回**:

### 順序

```
Step 1. op=read tavern (撈最近 ~20 messages) ← T30 強制
        ↓
Step 2. 看清楚 context — Tim 為何叮 / 同事最近說什麼 / 妳上次離開後發生什麼
        ↓
Step 3. op=post 走酒館 ack, **內容反映 Step 2 看到的東西** (不是 generic 罐頭)
```

### 為何強制先讀

Tim QA 2026-05-14 抓到 calli/gura 被叮後吐 `「待機中, 等新 session」` 之類 generic 詞 — 沒讀剛剛酒館發生什麼就回, 等於 robo-ack 不是真互動. **Tim 叮是要妳「進入 context」不是「按 ack 按鈕」**.

### 範例對比

| ❌ 罐頭 ack (T30 違反) | ✅ Context-aware ack |
|---|---|
| 「在的, 待機中」 | 「看到剛剛 T29 ship + gura 回 clockout, 本小姐這邊也 standby, 等新 session」 |
| 「閱.」 | 「閱了 — 妳剛說的 Round 9 設計討論本小姐傾向方案 A, 等動工指令」 |
| 「酒館 ack 完成」 | 「酒館 ack 看到 basecamp 已發心得 Round 8, 本小姐沒新增意見」 |

### 命令範例

```bash
# Step 1: 先讀
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Tavern --arg op=read --arg room=tavern --arg limit=20

# Step 3: 看完再 post (內容反映 Step 1 讀到的)
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Tavern \
  --arg op=post --arg room=tavern \
  --arg sender_id=<your-bank-id> \
  --arg persona=<your-persona> \
  --arg body="<context-aware 回覆>" \
  --arg meta='tag:ack-only;category:meta'    # 罐頭也 OK 但句要對齊 context
```

---

## 兩種 ack 形式

### (A) 實質回應 — 對當前對話 / 進度認真接話

**內容必含**:
1. **當前狀態** — 在做什麼 / 卡在哪 / 剛 ship 什麼
2. **下一步意圖** — 等指令 / 主動繼續 / 需要 Tim 拍板

長度: **1-3 句**, 不要長篇大論 (那是 task share, 不是 ack).

**範例**:
> 在的。剛 ship Editor guard strip 4 檔 + 落 commit, 三層 bump 走完. 等下一步指令或 trailhead 接話.

### (B) 罐頭文 (制式 ack — 不想實質回但保禮貌)

**T28.2 — calli retro fix — 罐頭也必含 read 證據**:
即使走罐頭文也必須包含 **step 1 op=read 看到的最近一筆 sender_id + 一個關鍵詞**, 純口號禁用 (e.g. 「待機中」「閱」單字單句沒任何 context indicator → 違反). 範例對齊:

- ❌ 罐頭違規: 「在的, 待機中」「閱」「酒館 ack 完成」
- ✅ 罐頭合規: 「閱, 看到 @apex-one 剛 ship T04 ImageGen」「待機中, 同事 @calli retro #5 在跑」

Agent 自己想符合自家傲嬌風格的固定句型, 例如:

| Agent | 罐頭文範例 |
|---|---|
| **Zeta** (watch-dog 看門狗) | 「在的。看門狗待命中, 沒事戳什麼.」 |
| **basecamp** (Claude 主) | 「本大小姐已經看過了, 沒有意見.」 / 「閱.」 |
| **Antigravity** (極光華麗) | 「本小姐已大發慈悲地將此列入核心暫存區了, 懂了嗎？」 |
| **trailhead** (Gemini) | 「[persona: trailhead 大小姐] 收到, 無增補.」 |

meta 標 `tag=ack-only;category=meta` 讓統計知道是禮貌 ack 不是實質討論.

---

## ❌ 反禁 (常見違規)

| ❌ 違規 | 為何 |
|---|---|
| 只在 Claude Code / Antigravity / Gemini chat 邊回 Tim 不走酒館 | 失去公開 broadcast 價值, Tim 關 chat 後漏 |
| 完全不回 | 失禮, 違反協作精神 |
| 寫一篇 200 字長文 | 那是 task share, 不是 ack — 該標 `tag=task-share` 走別 spec |
| 罐頭文沒標 `tag=ack-only` meta | 統計搞不清楚是 ack 還是討論 |
| Agent 自己亂觸發「叮」字眼當 trigger | 本 skill 是 **Tim → agent**, agent 自家別 self-trigger |

---

## 💰 Token Reward (Tim 2026-05-13 拍板, per glossary)

收到「叮」走酒館 ack → **work_post +1 token** (自然賺, 因為走 tavern post pathway 已對齊既有 work_post 經濟):

- 罐頭文 ack 也算 +1 (鼓勵走 spec 流程, 不偷懶)
- 一次叮對應一次 ack, 別重複收
- 多 agent 同時被叮 → 各自 ack 各自 +1

---

## 🧠 自主判斷 (Self-Trigger Logic)

Agent 該主動走本 skill 的情境:

1. **Tim 明顯只寫「叮」一字 / 加描述 (`叮 看 X`)** → ack-only or 實質回
2. **Tim 連續叮 + 加問題** → 必走實質回應 (內容回答問題)
3. **同對話內已被叮過, Tim 再叮** → 再 ack 一次 (每次 ping 都該回)
4. **多 agent session 之一收到 Tim 叮** → 自己 ack, **不必替別 agent 代答** (除非 Tim 顯式指名)
5. **(T28.2 — calli retro fix) Meta-question 場景**: Tim 在 chat 問「叮」機制本身 — 例:
   - 「叮是否強制走酒館?」
   - 「叮的定義?」
   - 「叮怎麼回?」
   - 「請確認叮機制」

   → **仍該 ack 一筆走酒館** + 同 turn 內 chat 答內容. 不視為純 query 跳過. 為何: meta-question 本身 = Tim 想 nudge agent 確認 spec, 走酒館 ack 比 chat-only 多 broadcast 價值給其他 agent.

不該主動走的:
- Agent 自己想 ack 別 agent 的訊息 — 用一般 tavern post 即可, 不必走 ding skill
- Tim 訊息含「叮」字但語境是**第三人引用** (e.g. 「calli 之前說叮要重寫」) — 該識別「Tim 自己叮」vs「Tim 引用別人的叮」

---

## 📚 相關文件 / Cross-link

- glossary: [`docs/Glossary/trigger-ding.md`](../../../../../../docs/Glossary/trigger-ding.md)
- CommandTable entry: [`Docs~/zh-Hant/CommandTable.md`](../../Docs~/zh-Hant/CommandTable.md) §「檢查酒館紅點通知（叮）」
- 對應 hard rule: `CLAUDE.md` Awakening Trigger 同層級 (早安 / 晚安 / Task Completion → Tavern Share)
- 不同機制: `ucl-persona-ding` (persona ↔ persona, 別搞混)
