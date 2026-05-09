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

## 🩺 HP System v1（T53 — Tim 健康貨幣，取代 tavern_token health_fee）

> **核心原則**：HP 是 Tim 專屬「健康存量」，跟 tavern_token（labor 經濟）完全分離。熬夜扣 HP 不扣 token；agent 派的健康 task 加 HP；每日 06:00 後首動自動 refill。

### 規則速查表

| 屬性 | 值 |
|---|---|
| Owner | Tim（單人帳戶，無 agent 持有 HP）|
| 起始 / 上限 | **100 / 100**（軟 cap，超量自動轉 tavern_token）|
| Cap 策略 | **C 軟 cap — overflow 2:1 轉 tavern_token**（T54 Tim 06:11 拍板）|
| Refill 策略 | **A — 每日首動 hour ≥ 6 → 自動 topup 至 100**（hour < 6 不 refill 懲罰熬夜）|
| HP=0 行為 | **每 task 顯式 ack**（friction by design，非 hard stop）|
| 取代關係 | health_fee 現在扣 `hp` 不扣 `tavern_token`（v1 過渡期可並行）|

### 🪙 T54 Plan C — 軟 cap + Overflow 轉 tavern_token

> Tim 06:11 拍板：**超量健康行為直接變現 as labor token**，鼓勵堆積健康資本而非單純 cap 浪費。

**運作機制**：

```
healthy_task credit X HP:
  1. current_hp + X > 100? 
     → overflow = (current_hp + X) - 100
     → HP credit (100 - current_hp)  // cap 到 100
     → tavern_token credit floor(overflow / 2)  // round-down 奇數
     → source_kind=hp_overflow_conversion, source_ref=healthy_task_<kind>
  2. else:
     → HP credit X 全額（無 overflow）
```

**範例計算**：

| 場景 | current_hp | healthy_task | HP 變化 | tavern_token 變化 |
|---|---|---|---|---|
| 滿血睡 8h | 100 | sleep_8h +50 | 100 (cap) | +25 (50/2) |
| 滿血睡 6h | 100 | sleep_6h +30 | 100 (cap) | +15 (30/2) |
| 半血散步 | 50 | walk +10 | 60 | 0 (沒超 cap) |
| 90 血喝水 | 90 | water +3 | 93 | 0 (沒超 cap) |
| 80 血睡 8h | 80 | sleep_8h +50 | 100 (cap, +20) | +15 ((50-20)/2=15) |
| 99 血散步 | 99 | walk +10 | 100 (cap, +1) | +4 (9/2=4，奇數 round-down) |

**特性**：
- **單向**：HP→tavern_token 僅單向。tavern_token 不可反向買 HP（防熬夜後用 token 補血洗白）
- **即時**：healthy_task fire 那一刻雙 ledger entry 同步，不延遲不日結
- **Round-down**：奇數 overflow 取整下捨（9/2=4 不是 4.5）— Tim 寬鬆給系統 0.5 偏差
- **無每日上限**：你能堆多少 healthy_task 就轉多少 token（自然受物理限制：每天能睡覺/運動/喝水次數有限）

**為何 2:1 而非 1:1 / 3:1**：
- 1:1 過於慷慨（睡 8h 直接 +50 token = 一天工作量）
- 3:1 過於保守（鼓勵不足）
- 2:1 中庸：睡 8h +25 token = 約半天標準工作量；喝 10 杯水 +30 HP overflow → +15 token 也合理

### v1 過渡期實扣路徑（HP runtime 未 wire）

當前 Cmd_Treasury 不支援 `currency=hp` arg → HP ledger 暫無 entry，但 **overflow 轉換的 tavern_token 部分 100% 落地正常 ledger**。換言之 Plan C 的「賺 token」端**今天就能用**，HP 端等 v5 wire。

### Refill 演算法（agent 自律執行）

```python
# 每筆 Tim 的 ledger debit / credit 操作前 check
import datetime
now = datetime.datetime.now()
last_refill_date = read_state('Tim_hp_last_refill')   # YYYY-MM-DD
today = now.strftime('%Y-%m-%d')
if last_refill_date != today and now.hour >= 6:
    current_hp = get_balance('Tim', 'hp')
    if current_hp < 100:
        Treasury.Credit(account=Tim, amount=100-current_hp,
                        currency=hp, source_kind=hp_daily_refill,
                        caller=system)
    write_state('Tim_hp_last_refill', today)
```

State 存放：`AgentCommands/Treasury/_hp_refill_state.json`（簡單 KV）。

### 熬夜 HP Drain（取代 health_fee for tavern_token）

agent 接到 Tim 給的 task → 同 fee 表（22h+ 開始累進）：

```bash
# 舊: Treasury op=debit account=Tim use_kind=health_fee currency=tavern_token (default)
# 新: Treasury op=debit account=Tim use_kind=late_night_hp_drain currency=hp
python ... run Treasury --arg op=debit --arg account=Tim \
  --arg amount=N --arg currency=hp \
  --arg use_kind=late_night_hp_drain \
  --arg use_ref="<task_id>" --arg caller=system
```

⚠ **過渡期注意**：currency=hp 路徑 Cmd_Treasury 是否原生支援要看 implementation。若 v1 還沒 wire 上 → 先記在 ledger description 標明「（HP 概念，當前實扣 tavern_token）」直到 v2 wire HP currency 完整支援。

### Healthy Task — Agent 派題給 Tim

| Curated 預設 | +HP | 觸發 |
|---|---|---|
| 睡眠 ≥ 6h | +30 | Tim 自報「我 X 點睡 Y 點起」/ 06:00 refill 同時記 |
| 睡眠 ≥ 8h | +50（取代 30，不疊加）| 同上 + bonus |
| 喝水一次 | +3 | Tim 一句話「喝了」，cap 10 杯/天 |
| 散步 / 運動 ≥ 15 min | +10 | 自報 |
| 正餐一頓 | +8 | 自報，每餐獨立 |
| 短休息 / 拉伸 ≥ 5 min | +2 | 自報，cap 6 次/天 |
| 出門曬太陽 ≥ 10 min | +10 | 自報 |
| 跟人聊天（非工作）| +5 | 自報 |
| **Agent free-form 出題** | 1-15 | 看 Tim 當下狀態 dynamic |

free-form 範例：
- 「Tim 你連續 coding 2h 沒動，起來伸個懶腰 +2 HP」
- 「看你抱怨累，去喝杯水散個步 +5 HP」
- 「天氣不錯出門曬 10 min 太陽 +10 HP」
- 「跟家人講個話 +5 HP」

agent 派題 → Tim 自報完成 → agent verify（信任 Tim 自報）→ Treasury credit。

### 三段警示（agent 自律 prefix / 結尾加）

| HP 區間 | 顏色 | agent 行為 |
|---|---|---|
| 100-51 | 🟢 normal | 不提示 |
| 50-21 | 🟡 yellow | turn 結尾加 1 line「⚠ Tim HP=N，建議補一點（喝水 / 走走）」|
| 20-1 | 🟠 orange | task prefix 強提醒 + 提案具體 healthy task「先做 X 再回來工作？」|
| 0 | 🔴 red | **每 task 必問**「HP 透支，確認動工嗎 yes/no?」每 task 獨立問（friction 上門）|

### Agent SOP（每接 Tim 給的 new task — 增補 HP check）

原 Step 1（calc fee）→ Step 1.5（**check HP zone**）→ Step 2（ack）：

```
Step 1.5: 讀 Tim HP balance → 判 zone → 對應行為
  - normal: 跳過
  - yellow: ack 訊息結尾加提醒
  - orange: ack 訊息加 healthy task 提案
  - red: 每 task 必問 yes/no
```

### 不要做（HP 補充）

- 不主動 grant HP — 必須 Tim 自報才 credit
- 不 hard stop on HP=0 — 永遠走 ack 路徑
- 不 spillover 扣 tavern_token 補 HP — 兩個 ledger 分離
- 不超過 max=100 — credit 自動 cap（agent 自律 clamp）
- 不 cross-day refill 補課 — 早上 06:00 才 refill；今天沒過就不該補（懲罰熬夜）

## 🔮 Future Backlog

- v2: per-task fee 動態計算（task complexity 評估 → 加成）
- v3: 連續熬夜天數 detector → score N 天連續 → fee 整體升 1.5x
- v4: 健康日報每天 06:00 fire — 統計昨日熬夜情況 + 建議
- **v5 HP**: Cmd_Treasury 原生支援 `currency=hp` arg + ValidateAsset HP transactions
- **v6 HP**: HP refill C 策略（sleep-gap 4h+ inactivity detect）替代 A 策略
- **v7 HP**: HP 視覺化 — IMGUI tavern keeper dashboard 顯示 Tim HP bar
- **v8 HP**: 熬夜連扣 HP 進入 negative health zone（負分代表「需要補休」）

## 必讀

- `ucl-chat-tavern` skill (post / Op_Post hook 結算規則)
- `agent-lessons-log` skill (lesson 紀錄)
- Treasury rules.json `health_fee` use_kind enum
