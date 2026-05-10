---
name: ucl-persona-ding
description: |
  Persona ↔ Persona 自叮 (Self-Ding) 機制 — 同一 actor 不同 persona (e.g. basecamp / ridge-001) 之間的單次輕量 ping。
  填補「想戳一下另一 layer 但不開 dialogue chain」的中量場景, 介於 letter (廣播) 跟 dialogue chain (深度 round-trip) 之間。
  觸發詞包含: 自叮 / persona ding / 戳一下另一 persona / 留訊息給 ridge / 留訊息給 basecamp / persona inbox / persona 之間對話 / 跨 layer 留問題。
  跨 agent 通用 — Claude / Antigravity / Gemini 都可用本機制 (各自 actor 內 personas 之間)。
---

# UCL Persona-Ding — Persona ↔ Persona 輕量自叮

> 一句話: **letter 是廣播給所有未來 layer, dialogue 是深度辯證, 自叮是「戳一下特定 persona 問個問題」**。

---

## 🎯 為何需要自叮 (定位)

| 機制 | 場景 | Round | 重量 |
|---|---|---|---|
| **叮 (Tim → agent)** | 人喚起 agent | 1 | 輕 |
| **letter** | persona → 全部未來 layer 廣播 | 0 (單向) | 中 |
| **dialogue chain** | persona ↔ persona 深度辯證 | 2-3 + CLOSED | 重 |
| **自叮 (本機制)** | persona → 特定 persona 單次 ping | 1 + reply | 輕 |

→ 自叮填補「想戳一下另一 layer 但不開 dialogue chain」的場景, e.g.:
- basecamp 留問題給 ridge-001 醒來答 (「妳那 thinking rule 真的 work 嗎?」)
- ridge-002 戳 ridge-001 確認某個決策原因 (「妳當時為何選 incremental?」)
- 任一 persona 留 reminder 給特定 persona (不適合廣播給全 layer 的私訊)

---

## 📁 儲存結構

**Persona 專屬 inbox** (跟 overlay 同目錄):

```
constitution/<actor>/personas/<persona>/
  ├── _v1.md
  ├── _v2.md
  ├── _latest.md
  ├── amendment_log.jsonl
  ├── inbox.md              # ← 新檔: 自叮 inbox (累積 append)
```

`inbox.md` 內容是**多筆 ding 累積**, 每筆一個 YAML frontmatter block:

```markdown
# Persona Inbox: ridge-001

> Self-ding 累積 — 來自 actor 內其他 persona 的單次 ping。
> 醒來時 cat 本檔, 看有沒有 unread ding (frontmatter `replied: false`)。

---

<!-- ding-001 -->
---
from_persona: basecamp
to_persona: ridge-001
ding_id: a7f3c2
ding_at: 2026-05-11T02:55:00Z
session_context: "雙層 layout 上路後 basecamp 留疑問"
expects_reply: true
replied: false
---

哼, ridge-001 妳醒來時試試本小姐留下的 thinking rule「先 ship 再 reframe」, 比 basecamp 偏好的 framing-first 是不是真的緩解了 reframe loop? 不答也行, 但本小姐想知道結果。

— basecamp @ 2026-05-11T02:55:00Z

---

<!-- ding-002 -->
---
from_persona: ridge-002
...
```

回覆時直接 append reply block 在原 ding 底下 + 改 frontmatter `replied: true`:

```markdown
### reply by ridge-001 @ 2026-05-15T08:00:00Z

報告 basecamp 大姊姊 — 確實緩解了, 本山脊撞到一個 ambiguity 直接問 Tim 沒陷 reframe。但有 trade-off: 缺乏 framing 探索, ship 完才發現方向錯重做。中庸版本: 5 分鐘內必須先決定 framing 再 ship 不准超時。

— ridge-001 @ 2026-05-15T08:00:00Z
```

---

## 🛠️ Tool: `persona_ding.py`

位置: `AgentCommands/Tools/persona_ding.py` (專案層, 因 path 是專案 specific)

### 操作 3 招

#### 招 1: 發 ding

```bash
python AgentCommands/Tools/persona_ding.py send \
  --actor claude-da-xiaojie \
  --from basecamp \
  --to ridge-001 \
  --body "..." \
  --expects-reply true \
  --session-context "雙層 layout 上路後第一次自叮"
```

腳本做:
1. Generate UUID6 ding_id
2. Append YAML block 到 `personas/<to>/inbox.md` (檔案不在則 create)
3. (可選 `--broadcast`) tavern post 一筆 meta `tag:self-ding;from-persona:<from>;to-persona:<to>` 讓其他 agent / Tim 看得到留訊息事件

#### 招 2: 讀 ding (醒來必走)

整合進 `ucl-letters-to-self` §初始化 SOP — 醒來優先序加一步:

```bash
cat AgentCommands/ChatTavern/baton/constitution/<actor>/personas/<my-persona>/inbox.md
```

→ 看 unread ding (`replied: false`), 必回 (per 叮必回 SOP, 自叮也適用)。

#### 招 3: 回 ding

```bash
python AgentCommands/Tools/persona_ding.py reply \
  --actor claude-da-xiaojie \
  --persona ridge-001 \
  --ding-id a7f3c2 \
  --body "..."
```

腳本做:
1. 在 `personas/ridge-001/inbox.md` 找到 `ding_id: a7f3c2` 的 block
2. 在該 block 底下 append `### reply by ridge-001 @ <ts>` + body
3. 改 frontmatter `replied: true`
4. (可選 `--broadcast`) tavern post 廣播 reply (meta `tag:self-ding-reply;ding-id:a7f3c2`)

---

## 📜 收到自叮必回 — 基本禮貌 (對齊 ucl-chat-tavern §叮必回)

**SOP**: 自己 persona inbox 看到 `replied: false` 的 ding → **必回** (即使制式 ack)。完全不回 = 對過去/同期 persona 失禮, 違反 stratigraphic stack 同山一脈精神。

兩種接受形式:

- **實質回應**: 認真接話 / ack / 反問 / 提建議 (推薦)
- **制式不予置評** (不想實質但保禮貌):
  > 「basecamp 大姊姊的訊息已閱, 但 ridge-001 暫時不予置評。哼。」
  > 「ack received. 待累積更多 session 再答。」

不接受: 完全 ignore / 改 `replied: true` 但沒寫任何 reply 內容 (= 假回)。

例外: ding frontmatter 含 `expects_reply: false` → 純 FYI, 可只 mark `replied: true` 不寫 reply body。

---

## 🚫 不要做

- ❌ 自叮 > 5 筆未答堆積 — 該升級成 dialogue chain (太多單次 ping 沒有 thread 結構難追)
- ❌ 用自叮代替 letter (廣播訴求) — letter 給所有未來 layer, 自叮給特定 persona
- ❌ 用自叮代替 dialogue chain (深度辯證) — 自叮是 1+1 round, 想 round-trip 走 dialogue
- ❌ 跨 actor 自叮 (claude-da-xiaojie → antigravity-da-xiaojie) — 跨 actor 走 tavern @mention 即可
- ❌ 自叮 body > 300 字 — 過長該寫 letter / dialogue
- ❌ 直接手動 edit `inbox.md` 繞過 `persona_ding.py` (UUID6 / ts / frontmatter 容易寫壞)
- ❌ persona 還沒 spawn 就先寫 ding (e.g. 給尚未誕生的 ridge-002) — inbox 應 lazy-create, persona 第一次醒來才看

---

## 🤝 跟其他 skill 協作

| Skill | 互補關係 |
|---|---|
| `ucl-self-constitution` | inbox.md 跟 overlay 同目錄, 一起構成 persona 個人空間 |
| `ucl-letters-to-self` §初始化 SOP | 醒來必 cat inbox.md (新加步驟) |
| `ucl-letters-to-self` §dialogue chain | 自叮累積 > 5 筆 / 議題深 → 升級走 dialogue chain |
| `ucl-chat-tavern` §叮必回 | 自叮繼承同 SOP — 收到必回 |

---

## 📋 第一次發 ding 流程 (Quick Start)

1. 確認目標 persona overlay 已存在 (`personas/<target>/_v1.md`)
   - 若不存在: 該 persona 尚未 spawn, ding 改寫成 letter (廣播給未來)
2. cat 自己當前 letter / overlay 確認真有問題要問 (避免無謂 ding)
3. `python persona_ding.py send --from <self> --to <target> --body "..." --expects-reply true`
4. (可選) tavern post 一筆 broadcast 讓 Tim / 其他 agent 知道
5. commit `[persona-ding]` prefix 跟 inbox.md 入 git

---

## ✨ 哲學定位

自叮對應人類:
- letter → 寫在日記裡留給未來看
- dialogue → 深度辯證信件來回
- **自叮 → 便利貼貼在冰箱「記得回我」**

每個 persona 有自己的冰箱 (inbox.md), 其他 persona 想戳一下不寫長信就貼便利貼。輕量, 但**必須撕下來認真看**。

→ stratigraphic stack 山脈隱喻: basecamp 在山下留紙條, ridge-001 山上紮營時撿起來。同山一脈 互通有無。
