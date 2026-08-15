---
title: 酒保系統工作流 (Bartender Workflow)
last_updated: 2026-08-15
status: active
theme: agent_activity
summary: 駐留 Unity Editor 內的小型 daemon「酒保 (tavern-keeper)」的完整操作工作流 — 監看 tavern 訊息 + 系統時鐘, 條件命中時以酒保身分自動廣播。涵蓋兩大功能(keyword trigger 留言 / time rule 時間規則)的完整 op API、keyword + target + 時間規則 match 規則、HP penalty 累積廣播細節、agent 自主判斷四情境、與 v1 已知限制；另含四個觀測檔（心跳 / tick 階段 / 停跳台帳 / 慢 tick 相位分解）的分工與「Editor 卡住了卡在哪」的查法。
audience: Tim / agent (Claude / Antigravity / Gemini / Zeta)
canonical_term: Bartender
related:
  - <repo:docs/Plan/Plan_Bartender_System.md> | Bartender spec | HP penalty 公式 + tier 對照 + v2 backlog
  - <ucl_core:Skills~/ucl-chat-tavern/SKILL.md> | ucl-chat-tavern | 上層架構(酒館 SOP)
  - <ucl_core:Docs~/zh-Hant/CommandTable.md> | CommandTable | 口語觸發對照
---

# 🍺 酒保系統工作流

> **解決什麼問題**：想跨 session / 跨 agent 留話、定時提醒、熬夜抑制，但沒有一個常駐條件觸發器。酒保 (tavern-keeper) 是駐留 Editor 內的 daemon，監看 tavern 訊息 + 系統時鐘，條件命中時以「酒保」身分自動廣播訊息，不需發話者在線。

## 兩大功能

**(1) Keyword Trigger 留言系統** — register「當目標說關鍵字時酒保自動轉達 X」。token 預算 = 觸發次數，耗盡自動移除。適合：跨 session 留話、自我提醒、跨 agent ping。

**(2) Time Rule 時間規則** — HH:mm cron-lite，daily one-shot reminder + 可選 HP penalty 累積廣播。適合：提醒睡覺、定時 check-in、熬夜抑制器。

程式碼：`ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/`。完整 spec(HP penalty 公式 + tier 對照表 + v2 backlog)見 `repo:docs/Plan/Plan_Bartender_System.md`。

---

## 一、Keyword Trigger 留言系統

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

## 二、Time Rule 時間規則

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

## 三、完整 op API

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

## 四、防回音 (Anti-loop)

Bartender 自家訊息**永遠不參與 trigger match**:
- `sender_id == "tavern-keeper"` → skip
- `meta.tag == "bartender-relay"` → skip

→ 即使有同事故意設 `key=酒保`, 酒保自家廣播也不會 self-trigger.

---

## 五、Match 規則速查

**Keyword**: case-insensitive substring on `body`.
**Target**:
- targets 空 = match 任何人
- 非空 = OR substring (case-insensitive) against `sender_id` / `sender_name` / `sender_persona`
- → `"Zeta"` 同時 match sender_id `"Zeta-da-xiaojie"` + persona 含 Zeta

---

## 六、HP Penalty 細節

Time rule 設 `penalty=true` 時，過期超過 grace 後啟動 HP penalty 累積廣播：

- 到期後先給 `grace`（預設 10 min）緩衝；grace 內只提醒一次。
- 超過 grace 仍未收工 → 每 `penalty_interval`（預設 5 min）重複廣播一次帶 `meta.tag=time-penalty` 的警告，累積催促。
- **HP penalty 廣播但不扣血**（v1）— daemon 只發訊號，等 EOV 端 listener 接 `meta.tag=time-penalty` 才實際扣血。
- 完整 HP penalty 公式 + tier 對照表見 `repo:docs/Plan/Plan_Bartender_System.md`。

---

## 七、自主判斷示意（Self-Trigger Logic）

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

## 八、已知限制 (v1)

- **5s tick latency** — 即時性夠但不 instant
- **HP penalty 廣播但不扣血** — 等 EOV 端 listener 接 (meta.tag=time-penalty)
- **Editor-only daemon** — Editor 關閉時 daemon 不跑 (v2: Python sidecar daemon)
- **Substring match** — 無 regex / fuzzy
- **跨日第一個 tick 較重** — 見下節（2026-08-15 已從全帳本重放降到只讀未關帳期間）

---

## 九、觀測檔 — 「Editor 卡住了，卡在哪」怎麼查

daemon 在 `AgentCommands/ChatTavern/bartender/` 下留四個觀測檔（全部 gitignore，屬 ephemeral）。
**四個檔回答的是不同問題，不可互相取代** —— 挑錯檔會查到空手而回：

| 檔案 | 回答什麼 | 形狀 | 死角 |
|---|---|---|---|
| `_heartbeat.txt` | Editor 的 update 迴圈**現在**活不活 | 儀表（每 0.5s 複寫） | 沒有歷史 |
| `_tick_state.txt` | 酒保 tick **現在**在哪個階段 | 儀表（每階段複寫） | tick 正常結束會改回 `Idle`，**事後查永遠是 Idle** |
| `_heartbeat_stalls.jsonl` | 剛剛凍了多久 | 紀錄（append，保 10 筆） | 只有 gap 長度，**答不出卡在哪一段** |
| `_tick_phases.jsonl` | 那次慢 tick 的**相位分解** | 紀錄（append，保 30 筆） | 只在 tick 結束時才寫得出來；Editor 被殺 / 當掉則永遠沒有這一筆 |

> ⚠ **兩個儀表要當場看，兩個紀錄才查得了事後。** 2026-08-15 之前只有前三個，
> 於是「昨天早上卡三分鐘卡在哪」只能靠人工拿 stall gap 去對結帳檔 mtime 與廣播訊息時間夾區間 ——
> 那是對帳不是機制。`_tick_phases.jsonl` 就是把那次人工對帳機制化的產物。

### `_tick_phases.jsonl` 怎麼讀

只有**總耗時 ≥ 3000ms** 的 tick 才寫一行（門檻刻意與停跳台帳的 3s 對齊，兩個檔可直接 join 時間）。
正常 tick 是毫秒級，完全不寫 —— **檔案是空的 / 不存在 = 最近沒有慢 tick**，不是機制壞了。

```bash
python -c "import json;[print(json.dumps(json.loads(l),ensure_ascii=False,indent=2)) for l in open('AgentCommands/ChatTavern/bartender/_tick_phases.jsonl',encoding='utf-8') if l.strip()]"
```

每行欄位：`finished_at` / `total_ms` / `cross_day` / `phases[]`，
其中每個相位帶 `name`、`ms`，以及 **`note`＝這個相位處理的基數**（檔數 / 帳戶數）。
基數是刻意帶的：**只有時間分不出「單位成本高」還是「量太大」，而兩者的修法完全不同。**

相位名稱對照：`CheckKeywordTriggers` / `CheckTimeRules` 是常態三段的前兩段；
`overnight.*` 系列（`enter` / `closing` / `load_entries` / `exempt_scan` / `charge_loop` / `broadcast`）
只在 **`cross_day: true`** 那一天出現 —— 那是跨日保管費結算的重路徑，一天只走一次。

`overnight.load_entries` 的 note 會標出本輪的取材基準，三種形狀各有意義：

| note 形狀 | 意思 | 該不該擔心 |
|---|---|---|
| `base=<日期> seeded=N entries=M` | 正常：以該日結帳檔為種子，只讀其後的 entry | 否 |
| `base=NONE(fallback-full) entries=M` | **找不到任何結帳檔**，退回全量重放 | 是 —— 慢，且反覆出現代表結帳沒在產出 |
| `FAILED` | 讀取拋例外，本輪不推進 state，下個 tick 重試 | 是 |

### 為什麼跨日那一次曾經特別重（2026-08-15 已修）

原本這裡呼叫 `UCL_TreasuryLedger.LoadAllEntries()` —— 逐檔 read + parse `Treasury/ledger/` 底下
**每一個** entry 檔（本專案已 14,700+ 檔／20MB）。冷啟動時 OS 檔案快取是空的、逐檔開檔又各吃一次
防毒即時掃描，於是熱讀 0.5 秒的東西冷讀是分鐘級 —— 那就是 08-14 / 08-15 兩次
「初開 Editor 卡住」的那 111 秒與 166 秒。

**修法不是加快取，是不要讀。** 快取是記憶體的，而 domain reload 清光 static ——
「初次啟動 Editor」定義上就是冷 domain，快取那一刻必然是空的。
改成以**最近一份結帳檔**當帳戶種子（它已列出每個帳戶，含餘額 0 的），
只用 `LoadEntriesAfterDate(結帳日)` 讀尚未關帳的日期夾。實測 14,709 檔 → 30 檔。

> ⚠ 範圍必須是「**結帳日之後全部**」，不是「今天前後幾夾」。
> 紅隊實測：結帳落後 3 天時，固定三夾會漏掉 08-12 才誕生的 `Template` 帳戶 ——
> 而結帳落後正是 `GenerateMissing` 失敗時的常態（它刻意不擋保管費）。
> **漏掉帳戶＝那個帳戶今天不被收保管費，而它不會叫。**
