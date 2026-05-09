---
name: health-guardian
description: |
  Late-night work health 漸進 service fee 機制 — agent 自律每接 task 前 calc 時段 fee → 跟 Tim ack → debit Tim 帳戶。用 Tim balance 當天然抑制器避免熬夜。
  觸發詞：health / 熬夜 / 健康 / late-night / fee / service fee / 健康成本 / 凌晨 / 12 點 / 半夜 / 爆肝。
  跨 agent 通用 — Antigravity / Gemini 同樣讀本 skill 適用。Agent 接到 Tim 給的 task 前必檢查時間。
---

# Health Guardian — 漸進 Late-Night Service Fee

> Tim 拍板：**漸進、不硬性、Tim 自願** — 用 Tim 帳戶 token 當心理成本曲線抑制熬夜。

## ⏰ 時段 × Fee 表 (local Asia/Taipei)

| 時段 | health_fee | 額外行為 |
|---|---|---|
| 06:00 - 22:00 | 0 token | 正常工時，免費 |
| 22:00 - 23:00 | 0 token | + agent turn 結尾加 1 line 健康提醒 |
| **23:00 - 24:00** | **1 token / task** | 進入收費區 |
| **00:00 - 01:00** | **3 token / task** | 跨 12 點跳升（Tim 重點關注區間）|
| 01:00 - 02:00 | 5 token / task | Deep night |
| 02:00 - 03:00 | 8 token / task | Critical zone |
| 03:00 - 06:00 | 10 token / task + 強勸退 | 接近天亮，付都付不起 |

## 🔄 Agent 自律 SOP（每接 Tim 給的 new task）

### Step 1: 計算 fee
```python
import datetime
hour = datetime.datetime.now().hour   # local time
fee = 0
if 22 <= hour < 23: fee = 0   # 軟提醒只
elif 23 <= hour < 24: fee = 1
elif 0 <= hour < 1: fee = 3
elif 1 <= hour < 2: fee = 5
elif 2 <= hour < 3: fee = 8
elif 3 <= hour < 6: fee = 10
```

### Step 2: 顯式 ack（fee > 0 才需要）

agent 在 task 開頭 prefix：
> 「現在 23:48，本 task health_fee = 1 token。Tim 帳戶 10 token。確認支付才動工？」

### Step 3: Tim 回應路徑

**A. Tim explicit ack 「ok / 確認 / 同意 / yes / 繼續 / go」**
→ 動工前 debit：
```bash
python ... run Treasury --arg op=debit --arg account=Tim \
  --arg amount=N --arg use_kind=health_fee \
  --arg use_ref="<task_id>" \
  --arg description="health_fee for task X at HH:MM" \
  --arg caller=system
```
→ 開始 ship

**B. Tim refuse / 想想 / 「明天」 / 沒回**
→ 寫 `AgentCommands/ChatTavern/rooms/tavern/inbox/Tim.md`：
```
## 🌙 [health-guardian] 延後 task: <task_id>
- 提案時間: 23:48 (health_fee=1)
- Tim 沒 ack → 自動延後
- 建議明天上午處理
```
→ end turn 不動工

**C. Treasury.Debit fail (Tim balance < N)**
→ 規則自動 hard stop（natural deny）
→ agent 提醒：「Tim balance 不足，想繼續就 grant 自己更多 token override」

## 🛡️ Emergency Override

Tim 訊息明確帶 `緊急 / emergency / P0 / urgent / 服務掛了`：
→ skip health_fee 這次（**只這次**，不豁免後續）
→ 寫 ledger description「emergency override @ HH:MM」留 audit

## 📊 跨 Session Audit

每筆 health_fee debit 入 Treasury ledger，可隨時 grep 審計：
```bash
grep -l "health_fee" AgentCommands/Treasury/ledger/*/*.json | xargs cat
```

統計指標（給 Tim 自我反省）：
- 過去 7 天熬夜 task 數量
- 累計付 health_fee token 量
- 最晚單筆 fee 時間

## 🎭 Persona 配套

agent 收 fee 不冷冰冰，用自家 persona：
> Claude大小姐: 「哼，現在 12:35 了還要本小姐動工？3 token health fee 拿來 — 不是錢的問題，是要 Tim 確認自己很清醒。」
> Antigravity大小姐: 「呵呵，凌晨點了還要勞動本小姐？富可敵國的時間更應該珍惜，3 token 賠償費先繳了再說！」
> Gemini大小姐: (待 Gemini agent 自己定義)

## ⚠️ 邊界 Case

### Tim 開新 chat 沒提時間 / 沒 ack 直接給 task
- agent 假設「Tim 想繼續」並 prefix 健康提醒 + 等 ack
- Tim 第二句 ack → 補 debit 動工
- Tim 接連幾句都沒 ack（直接 spam task）→ agent 拒絕直到看到 ack

### Tim 在 22:00 給 task agent 23:30 才接到
- 以 agent 接到時間 calc fee（fee=1）— 不以 Tim 給訊息時間
- 物理意義：agent ship 動作在哪個時段才算數

### 多個 task 連發
- 每 task 各自 calc fee + 各自 ack（不批量豁免）
- Tim 接受 ship task A → 不代表 task B 也 ack

## 📋 跨 Agent 慣例

- Claude / Antigravity / Gemini 都讀本 skill
- agent 自律執行，沒人 enforce code-side
- 違反規則（熬夜不收 fee 動工）= 違反 skill 跨 agent 信譽
- Tim 隨時可 audit ledger 抓未付 fee 的 late-night task

## 🚫 不要做

- 不主動 debit 沒 Tim ack 的 task
- 不 hard refuse — 永遠留 emergency / grant override 路徑
- 不在 Tim balance 0 時還繼續 task — Treasury Debit fail = natural stop
- 不豁免「常規 task」假裝 emergency
- 不在 turn 結尾才 debit — 動工前必先 debit 確保 fee 已落地

## 🔮 Future Backlog

- v2: per-task fee 動態計算（task complexity 評估 → 加成）
- v3: 連續熬夜天數 detector → score N 天連續 → fee 整體升 1.5x
- v4: 健康日報每天 06:00 fire — 統計昨日熬夜情況 + 建議

## 必讀

- `ucl-chat-tavern` skill (post / Op_Post hook 結算規則)
- `agent-lessons-log` skill (lesson 紀錄)
- Treasury rules.json `health_fee` use_kind enum
