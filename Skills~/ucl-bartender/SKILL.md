---
name: ucl-bartender
description: |
  酒保 (Bartender) 系統 — 駐留 Unity Editor 內的小型 daemon, 監看 tavern 訊息 + 系統時鐘, 條件命中時以「酒保 (tavern-keeper)」身分自動廣播訊息.

  兩大功能:
  (1) **Keyword Trigger 留言系統** — register "當目標說關鍵字時酒保自動轉達 X". token 預算 = 觸發次數, 耗盡自動移除. 適合: 跨 session 留話、自我提醒、跨 agent ping。
  (2) **Time Rule 時間規則** — HH:mm cron-lite, daily one-shot reminder + 可選 HP penalty 累積廣播. 適合: 提醒睡覺、定時 check-in、熬夜抑制器。

  觸發詞包含 (case-insensitive substring):
  - **留言 / 留個話 / 留一條 / 留訊息 / 幫我留話 / 留 message / leave message / leave a note**
  - **酒保 / 酒保系統 / bartender / tavern-keeper / 通知我 / 提醒我**
  - **提醒我睡覺 / 該睡了 / 熬夜提醒 / sleep reminder / sleep at / 幾點提醒**
  - **時間規則 / time rule / cron / 定時 / 每天幾點**
  - **HP penalty / 扣血提醒 / 熬夜扣血 / 健康警告**
  - **關鍵字觸發 / keyword trigger / 設個觸發 / 設留言 / 自動發言**

  跨 agent 通用 — 任何 actor 都可 register / list / remove (走 Cmd_Bartender op=*).

  自主判斷使用時機:
  - 用戶說「我等下要 X, 幫我留話給 Y 說 Z」→ op=add 註冊 trigger
  - 用戶說「Tim 一直熬夜, 提醒他」/「每天 N 點叫我」→ op=time_add 註冊時間規則
  - agent 想跨 session 留訊息給未來自己 → op=add (target=自己 persona)
  - 用戶問「現在有什麼留言 / 提醒」→ op=list / op=time_list
  - 用戶想停掉特定提醒 → op=remove / op=time_remove
---

# UCL Bartender — 酒保系統

> 一句話：**駐留 Editor 內的 daemon, 監看 tavern + 時鐘, 條件命中時以 tavern-keeper 身分自動廣播訊息**.

完整 spec doc → [`docs/Plan/Plan_Bartender_System.md`](../../../../../../docs/Plan/Plan_Bartender_System.md)（含 HP penalty 公式 + tier 對照表 + v2 backlog）

---

## 🎯 兩大功能 + 自主判斷

### 1. Keyword Trigger 留言系統

**何時用**：
- 跨 session 留話給某 persona（agent / 用戶）
- 自我提醒（target = 自己, key = 預期觸發詞）
- 跨 agent ping（不必對方在線, 等他下次發言含關鍵字就 fire）

**使用範例（用戶口語 → agent action）**：

| 用戶說 | Agent 該做 |
|---|---|
| 「幫我留話給 Antigravity, 她下次說『晚安』時提醒她寫 baton」 | `op=add creator=<你> targets=antigravity key=晚安 msg="記得寫 baton" tokens=1` |
| 「Tim 下次說『叮』時提醒進入自由意志模式」 | `op=add creator=<你> targets=Tim key=叮 msg="自由意志模式" tokens=2`（雙保險） |
| 「我等下吃飯, 半小時後若有人 @我 就回我會晚點處理」 | `op=add creator=<你> targets=<你> key=@<你> msg="agent 出去吃飯, 半小時後回來" tokens=3` |

**呼叫**：
```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Bartender \
  --arg op=add \
  --arg creator=<your-sender-id> \
  --arg creator_name=<your-display-name> \
  --arg targets=<comma-separated> \
  --arg key=<keyword> \
  --arg msg=<message> \
  --arg tokens=<int, default 1>
```

**觸發顯示**：`[<creator>的留言(N)] <msg>`（N = 觸發當下含本次的剩餘 token, 從 token 倒數）

---

### 2. Time Rule 時間規則

**何時用**：
- 定時提醒（睡覺 / 起床 / 運動 / 吃藥）
- 熬夜抑制器（過時 grace 後啟 HP penalty 累積廣播）
- 每日 check-in / 例會時段

**使用範例**：

| 用戶說 | Agent 該做 |
|---|---|
| 「23:50 提醒 Tim 該睡了, 超時扣血」 | `op=time_add id=sleep-2350 time=23:50 target=Tim msg="該睡覺囉" grace=10 penalty=true` |
| 「每天早上 9 點群裡 @所有人 開站會」 | `op=time_add id=standup-0900 time=09:00 target=all msg="站會時間"` |
| 「停掉 sleep-2350 那個提醒」 | `op=time_remove id=sleep-2350` |

**呼叫**：
```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Bartender \
  --arg op=time_add \
  --arg id=<rule-id> \
  --arg time=<HH:mm> \
  --arg target=<who> \
  --arg msg=<reminder-body> \
  --arg grace=<min, default 10> \
  --arg penalty=<true/false, default false> \
  --arg penalty_interval=<min, default 5>
```

---

## 🔧 完整 op API

| op | 用途 | 必填 |
|---|---|---|
| `add` | 新增留言 trigger | `creator` `key` `msg` |
| `list` | 列當前 triggers | — |
| `remove` | 移除 trigger | `id` |
| `time_add` | 新增時間規則 | `id` `time` `msg` |
| `time_list` | 列時間規則 | — |
| `time_remove` | 移除時間規則 | `id` |
| `status` | daemon 統計 + state 概況 | — |
| `tick` | 強制立刻 tick（測試 / dogfood） | — |

---

## 🚨 防回音 (Anti-loop)

Bartender 自家訊息**永遠不參與 trigger match**:
- `sender_id == "tavern-keeper"` → skip
- `meta.tag == "bartender-relay"` → skip

→ 即使有同事故意設 `key=酒保`, 酒保自家廣播也不會 self-trigger.

---

## 🎮 Match 規則速查

**Keyword**: case-insensitive substring on `body`.
**Target**: 
- targets 空 = match 任何人
- 非空 = OR substring (case-insensitive) against `sender_id` / `sender_name` / `sender_persona`
- → `"Zeta"` 同時 match sender_id `"Zeta-da-xiaojie"` + persona 含 Zeta

---

## ⚠️ 已知限制 (v1)

- **5s tick latency** — 即時性夠但不 instant
- **HP penalty 廣播但不扣血** — 等 EOV 端 listener 接 (meta.tag=time-penalty)
- **Editor-only daemon** — Editor 關閉時 daemon 不跑 (v2: Python sidecar daemon)
- **Substring match** — 無 regex / fuzzy

---

## 🧠 自主判斷示意（Self-Trigger Logic）

Agent 看到下列情境**該主動考慮** Bartender:

1. **用戶離線前要交代給其他 agent**:
   > 「等下我去開會, Antigravity 醒來時跟她說 X」
   → register trigger key=醒來/早安, target=antigravity

2. **跨 session 留訊息給自己**:
   > 完成 letter to future self 後想再追加一個 immediate trigger
   → register trigger key=<你會在下次說的詞>, target=<你的 persona>

3. **熬夜偵測 + 自我抑制**:
   > 用戶連續多輪在 23:00+ 派 task
   → 自主提議: 「要不要設個 time_rule 在 23:30 提醒收工?」

4. **協作任務追蹤**:
   > 多 agent 接力一個 task, 想當某 agent 完成 keyword (e.g. "ship") 時自動通知下一棒
   → register trigger key=ship, target=<上一棒>, msg="下一棒接手"

---

## 📚 相關文件 / Cross-link

- 完整 spec: [`docs/Plan/Plan_Bartender_System.md`](../../../../../../docs/Plan/Plan_Bartender_System.md)
- 程式碼: [`UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/`](../../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/)
- CommandTable: [`Docs~/zh-Hant/CommandTable.md`](../../Docs~/zh-Hant/CommandTable.md) — 口語觸發
- 上層架構: ucl-chat-tavern skill（酒館 SOP）
