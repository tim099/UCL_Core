---
title: 酒保系統工作流 (Bartender Workflow)
last_updated: 2026-09-29 (關鍵字留言 trigger 廢棄，Tim 拍板；daemon 掃描相位改名 ScanNewMessages) | 2026-09-28 (每日結算觸發搬到 Senate Server；daemon 不再判跨日、慢 tick 台帳去掉 cross_day；TASK-0315) | 2026-08-15
status: active
theme: agent_activity
summary: 駐留 Unity Editor 內的小型 daemon「酒保 (tavern-keeper)」的完整操作工作流 — 監看 tavern 訊息 + 系統時鐘, 條件命中時以酒保身分自動廣播。涵蓋 time rule 時間規則的完整 op API、HP penalty 累積廣播細節、agent 自主判斷情境、與 v1 已知限制；另含四個觀測檔（心跳 / tick 階段 / 停跳台帳 / 慢 tick 相位分解）的分工與「Editor 卡住了卡在哪」的查法。
audience: Tim / agent (Claude / Antigravity / Gemini / Zeta)
canonical_term: Bartender
related:
  - <repo:docs/Plan/Plan_Bartender_System.md> | Bartender spec | HP penalty 公式 + tier 對照 + v2 backlog
  - <ucl_core:Skills~/ucl-chat-tavern/SKILL.md> | ucl-chat-tavern | 上層架構(酒館 SOP)
  - <ucl_core:Docs~/zh-Hant/CommandTable.md> | CommandTable | 口語觸發對照
---

# 🍺 酒保系統工作流

> **解決什麼問題**：想定時提醒、熬夜抑制，但沒有一個常駐的時鐘觸發器。酒保 (tavern-keeper) 是駐留 Editor 內的 daemon，監看 tavern 訊息 + 系統時鐘，條件命中時以「酒保」身分自動廣播訊息，不需發話者在線。

## 主要功能

**Time Rule 時間規則** — HH:mm cron-lite，daily one-shot reminder + 可選 HP penalty 累積廣播。適合：提醒睡覺、定時 check-in、熬夜抑制器。

程式碼：`ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/`。完整 spec(HP penalty 公式 + tier 對照表 + v2 backlog)見 `repo:docs/Plan/Plan_Bartender_System.md`。

---

## 一、Time Rule 時間規則

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
senate ucmd run Bartender \
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

## 二、完整 op API

| op | 用途 | 必填 |
|---|---|---|
| `time_add` | 新增時間規則 | `id` `time` `msg` |
| `time_list` | 列時間規則 | — |
| `time_remove` | 移除時間規則 | `id` |
| `status` | daemon 統計 + state 概況 | — |
| `tick` | 強制立刻 tick（測試 / dogfood） | — |

---

## 三、防回音 (Anti-loop)

Bartender 自家訊息**永遠不參與處理**（inline marker／酒館 CLI／`@酒保` 都不看它們）:
- `sender_id == "tavern-keeper"` → skip
- `meta.tag == "bartender-relay"` → skip

→ 酒保自己發的確認與提醒，不會被自己當成新指令再處理一次。

---

## 四、HP Penalty 細節

Time rule 設 `penalty=true` 時，過期超過 grace 後啟動 HP penalty 累積廣播：

- 到期後先給 `grace`（預設 10 min）緩衝；grace 內只提醒一次。
- 超過 grace 仍未收工 → 每 `penalty_interval`（預設 5 min）重複廣播一次帶 `meta.tag=time-penalty` 的警告，累積催促。
- **HP penalty 廣播但不扣血**（v1）— daemon 只發訊號，等 EOV 端 listener 接 `meta.tag=time-penalty` 才實際扣血。
- 完整 HP penalty 公式 + tier 對照表見 `repo:docs/Plan/Plan_Bartender_System.md`。

---

## 五、自主判斷示意

Agent 看到下列情境**該主動考慮** Bartender:

1. **熬夜偵測 + 自我抑制**:
   > 用戶連續多輪在 23:00+ 派 task
   → 自主提議: 「要不要設個 time_rule 在 23:30 提醒收工?」

---

## 六、已知限制 (v1)

- **5s tick latency** — 即時性夠但不 instant
- **HP penalty 廣播但不扣血** — 等 EOV 端 listener 接 (meta.tag=time-penalty)
- **Editor-only daemon** — Editor 關閉時 daemon 不跑 (v2: Python sidecar daemon)
- **每日結算（結帳／保管費／轉券／匯率）不在這裡** — 2026-09-28 起由 Senate Server 觸發（`SenateOvernightJob`，Editor 沒開也照跑；TASK-0315）

---

## 七、觀測檔 — 「Editor 卡住了，卡在哪」怎麼查

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

每行欄位：`finished_at` / `total_ms` / `phases[]`，
其中每個相位帶 `name`、`ms`，以及 **`note`＝這個相位處理的基數**（檔數 / 帳戶數）。
基數是刻意帶的：**只有時間分不出「單位成本高」還是「量太大」，而兩者的修法完全不同。**

相位名稱對照：`ScanNewMessages` / `CheckTimeRules`（tick 的兩段）。⚠ 2026-09-29 之前寫入的行，第一段叫 `CheckKeywordTriggers`（同一段，改名前的名字）。
