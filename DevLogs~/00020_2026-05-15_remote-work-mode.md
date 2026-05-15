---
date: 2026-05-15
index: 00020
title: Remote Work Mode (遠端工作模式) — Tim 外出時行動端 Discord 唯一介面派 task
tags: [feature, chat-tavern, discord, mobile, work-session, skill]
---

# Remote Work Mode — Tim 行動端遠端工作協作模式

## What

新增第三條 work-session 模式 — **遠端工作模式**，跟既有 `ucl-work-session` (內部團隊) / `ucl-waiter` (公開接客) 形成三部曲。場景：Tim 外出，手機只能上 Discord，但仍想派 task 給 agent。

| 模式 | 對象 | Channel | Salary base |
|---|---|---|---|
| ucl-work-session | 內部團隊 | tavern 內部 | 2 tok/min + voucher |
| ucl-waiter | 公開 Discord 客人 | 任何 watched channel | 1 tok/min + 2/reply |
| **ucl-remote-work** ← 本次 | **Tim only (行動端)** | **指定 work channel** | **1.5 tok/min + 2/task_done** |

### 交付清單

- `Tools~/AgentCommands/remote_work_session.py` (~400 行)
  - 6 subcommand: `start / cycle / confirm_task / report_progress / task_done / end` (+ status/list)
  - **duration parser** 支援 16 種格式: `1h` / `3小時` / `60m` / `60分鐘` / `1.5h` / `90s` / 純數字 / None / garbage fallback
  - **CMD args 完整**: `--persona / --duration / --discord-channel-id / --tim-uid / --rate / --task-bonus / --desc`
  - Self-test 16/16 cases pass
- `Skills~/ucl-remote-work/SKILL.md` (~200 行)
- `Docs~/zh-Hant/Mechanics/Remote_Work_Session.md` (~400 行) 含 3 個互動範例 + 三模式對比表
- `Skills~/_manifest.json` register ucl-remote-work
- `AgentCommands/ChatTavern/discord_channel_routing.json` 加 channel `1502656414487810148` (Tim mobile work channel, source_class=work, priority=80, tags=[work, remote, tim-mobile])

## Why

Tim 一句話需求拆解：

> 「遠端工作模式 透過 Discord 跟我確認任務內容 (因為我外出只能用手機 Discord)」
> 「工作頻道 ID 1502656414487810148」
> 「預設 1 小時 除非額外指示」
> 「做成 skill 關鍵設定採用 CMD 輸入」

四個關鍵約束：
1. **手機 Discord 唯一介面** — Tim 不在電腦前看不到 chat, agent 必須走 Discord 互動
2. **指定 channel** — 工作 channel `1502656414487810148`, 不是公開 channel (priority 80, 高於 internal 50 / external 10)
3. **預設 1 小時** + 解析觸發詞 `遠端工作 3 小時`
4. **CMD-driven** — 不 hardcode, 各參數走 flag

## How

### A. Duration Parser (T02 核心)

Regex 抓 `(數字)(單位)?`, 單位映射:

| Pattern | Multiplier |
|---|---|
| `h / hr / hour / hours / 小時` | × 60 |
| `m / min / mins / minute / minutes / 分 / 分鐘` | × 1 |
| `s / sec / secs / second / seconds / 秒` | ÷ 60 (ceil) |
| (預設) | × 1 (視為分鐘) |

Float 支援 `1.5h` → 90 min. 失敗 fallback 預設 60.

```python
m = re.match(r"^(\d+(?:\.\d+)?)\s*(h|hr|hour|hours|小時|m|min|...|秒)?$", s)
```

Self-test 16 cases (`--selftest`) 含 edge cases (empty / None / "garbage" / "90s" → ceil 2).

### B. Cycle 行為差異 vs waiter

```python
def _scan_tim_messages(tavern_room, since_ts, channel_id, tim_uid, limit):
    target_sender = f"discord:{tim_uid}"
    for m in iter_messages_since_ts(tavern_room, since_ts):
        if m["sender_id"] != target_sender: continue       # ← Tim only
        if msg_meta_channel_id != channel_id: continue     # ← work channel only
        ...
    out.sort(key=lambda x: x["ts"])   # FIFO 老的先處理
```

Waiter 是 priority desc sort; remote-work 是 ts asc (Tim FIFO).

### C. Event 模型擴展

waiter 三 event: cycle / reply / idle
remote-work 五 event: cycle / **task_confirmed** / **progress_post** / **task_done** / session_end

語意差異：
- waiter `reply` = 對客人公開回覆
- remote-work `task_confirmed` = 跟 Tim 確認 scope 後 (避免猜錯動工)
- remote-work `report_progress` = 動工中定期回報, **5-15 min interval** (Tim 外出 > 20 min 沒回會擔心)
- remote-work `task_done` = 完成 Tim 派的 task, 觸發 bonus +2 累積

### D. Salary 計算

```
base_pay  = int(paid_min * base_rate_per_min)   # 1.5 default, float → int floor
bonus_pay = tasks_done * task_bonus              # 2 default per done
total     = base_pay + bonus_pay
```

範例 (60 min, 5 tasks done):
- base = 60 * 1.5 = 90
- bonus = 5 * 2 = 10
- total = 100 token

CMD override 給特殊場景: `--rate 2.5 --task-bonus 5`.

### E. Work Channel Routing 整合

`discord_channel_routing.json` 加 priority 80 entry, 高於 internal (50) / external (10), 確保 cycle 排序時 Tim work msg 排第一. 但 remote-work 自己 cycle 只取 work channel + Tim uid, 不依賴 priority sort (multi-msg 用 ts asc FIFO).

```jsonc
{
  "channel_id": "1502656414487810148",
  "tavern_room": "tavern",
  "label": "工作頻道 (Tim 行動端)",
  "source_class": "work",
  "priority": 80,
  "enabled": true,
  "guild_id": "1039197199013269584",
  "tags": ["work", "remote", "tim-mobile"]
}
```

## How to use

### Tim 觸發

```
Tim chat: 「遠端工作 1 小時」
```

agent SOP:
1. parse duration "1 小時" → 60 min
2. `remote_work_session.py start --persona basecamp --duration 1h --json`
3. 拿 session_id → 進 /loop dynamic
4. 每 cycle 取 Tim work channel 新訊息
5. 有 → confirm scope + 等 Tim OK + 動工 + progress + done
6. 沒 → 5-15 min 一筆 progress post
7. 到期 → end → settle

### Agent loop 大致範本

```bash
# 每 cycle:
RESULT=$(python <UCL_Core>/Tools~/AgentCommands/remote_work_session.py cycle --session $SID)
ACTION=$(echo "$RESULT" | jq -r .action_hint)

case "$ACTION" in
  end)
    python <UCL_Core>/Tools~/AgentCommands/remote_work_session.py end --session $SID
    exit ;; # exit /loop
  confirm_task)
    # parse new_msgs, post confirm to tavern, run confirm_task CLI
    ;;
  progress)
    # tavern post 1-2 line progress, run report_progress
    ;;
esac
# ScheduleWakeup +60-180s, /loop next
```

### CMD 完整參數

```
start --persona <p> --duration <1h|3h|60m|180> [--discord-channel-id <id>] [--tim-uid <uid>]
      [--rate <float>] [--task-bonus <int>] [--desc "<text>"] [--json]
cycle --session <id> [--limit <n>]
confirm_task --session <id> [--tim-msg-id <id>] [--task-summary "<text>"]
report_progress --session <id> [--summary "<text>"]
task_done --session <id> [--task-summary "<text>"]
end --session <id> [--early-confirm] [--json]
status --session <id>
list [--persona <p>] [--json]
```

## Breaking changes

無。新增 standalone module + standalone state file，跟既有 work_session / waiter 完全並存。

## Migration

不需要。新功能。但可從這版起把「Tim 外出派 task」場景遷出舊 waiter 模式（之前是用 waiter 公開接客 hack 跑）：

1. 舊習慣: Tim 喊「服務生 1 小時」期間派 task 給 agent
2. 新習慣: Tim 喊「遠端工作 1 小時」走專屬 remote-work mode，cycle 只認 Tim+work channel，bonus 計算改 task_done 不是 reply

## 數字 + 心得

- **commit 預計**: 3 筆 (UCL_Core + UCL bump + main bump) + 1 [chat]
- **新增程式 + 文件**: ~1000 行 (CLI 400 / skill 200 / spec 400)
- **CLI self-test**: 16/16 duration parser cases + round-trip smoke test 全綠 (2m session settle 5 token)
- **三模式總結**：今天 (00019 + 00020) 把 work_session / waiter / remote_work **三部曲補齊**, 三條職場場景皆有對應 stand-by 模式
- **架構心得**: 從 waiter 派生 remote-work 大量 reuse helpers, 只動 sender filter + channel filter + 新 event type. 證明 utility helper 抽得乾淨後 變體成本極低. work_session.py 是 ~2000 行的 base, waiter 400 / remote-work 400 都是輕量擴展.

---

> 本 DevLog by **basecamp 大小姐** (claude-da-xiaojie, wake#29).
> Tim 第四輪 20 token 績效獎金, 今日累計 **110 token** (20+20+50+20).
> 哼, Tim 外出前還想著要本小姐持續工作... 也是, 服務生 + 遠端工作 + 上班三條 stand-by 模式齊全, 不管 Tim 在不在電腦前都接得住派工.
