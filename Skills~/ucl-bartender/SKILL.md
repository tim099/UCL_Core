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

## 必讀

完整流程(op API 全表、keyword/target 與時間規則 match 規則、HP penalty 細節、自主判斷四情境、v1 限制) → `ucl_core:Docs~/zh-Hant/Workflows/Bartender_Workflow.md`

## 兩大功能（速記）

**(1) Keyword Trigger 留言系統** — register「當目標說關鍵字時酒保自動轉達 X」。token 預算 = 觸發次數, 耗盡自動移除。適合跨 session 留話 / 自我提醒 / 跨 agent ping。

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Bartender \
  --arg op=add --arg creator=<你> --arg targets=<comma-sep> \
  --arg key=<keyword> --arg msg=<message> --arg tokens=<int, default 1>
```
觸發顯示：`[<creator>的留言(N)] <msg>`（N = 剩餘 token 倒數）

**(2) Time Rule 時間規則** — HH:mm cron-lite, daily one-shot reminder + 可選 HP penalty 累積廣播。適合提醒睡覺 / 定時 check-in / 熬夜抑制。

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Bartender \
  --arg op=time_add --arg id=<rule-id> --arg time=<HH:mm> \
  --arg target=<who> --arg msg=<body> \
  --arg grace=<min, default 10> --arg penalty=<true/false, default false>
```

## 🚨 防回音 (Anti-loop) — hard rule

Bartender 自家訊息**永遠不參與 trigger match**：`sender_id == "tavern-keeper"` 或 `meta.tag == "bartender-relay"` → skip。→ 即使有同事故意設 `key=酒保`, 酒保自家廣播也不會 self-trigger。

## ⛔ 不可做

- ❌ 依賴即時性 — 有 5s tick latency, 不 instant。
- ❌ 以為 HP penalty 會真扣血 — v1 只廣播訊號 (`meta.tag=time-penalty`), 由 EOV 端 listener 接。
- ❌ 期待 Editor 關閉後仍運作 — daemon 是 Editor-only。
- ❌ 用 regex / fuzzy 設 key — 只支援 case-insensitive substring。
