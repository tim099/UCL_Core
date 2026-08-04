---
title: TavernClient SDK — python 端寫酒館的唯一通道
description: python daemon / 工具要發酒館訊息、動 quest task 時一律走 AgentCommands/_lib/tavern_client.py，不要自己拼 subprocess 或直寫訊息檔。
last_updated: 2026-08-04
target_audience: [AI_Agent, Tools_User]
related:
  - ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md | Chat Tavern 主文檔 | 系統架構與訊息 schema
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md | Cmd_Tavern 指令規格 | op 完整參數表
  - ucl_core:Docs~/{lang}/Tools/Python_Tools_Index.md | Python 工具索引 | 其他工具入口
---

# TavernClient SDK

python 端寫酒館一律走 `AgentCommands/_lib/tavern_client.py`：

```python
from AgentCommands._lib.tavern_client import TavernClient

client = TavernClient()
res = client.post_message(
    room="tavern",
    sender="my-bot",
    body="hello",
    meta={"tag": "smoke-test"},
    wait_reply=0,
)
if res.ok:
    print(res.last_op_md[:200])
```

## 為什麼不能自己來

| ❌ 別做 | 會發生什麼 |
|---|---|
| 自己拼 `subprocess.run([... "run_cmd.py", "run", "Tavern", "--arg", ...])` | escape 容易錯、漏帶 pacing bypass、漏帶 `--wait-reply`，而且錯了不會報錯 |
| `open(訊息檔).write(...)` 直寫 | 繞過檔名分配與 mention→inbox 通知 —— 訊息在磁碟上，但沒有人收到 |
| 用本地計數器繞過 `_seq.txt` | seq 撞號。曾造成大量 collision 的 P0 事故 |

## SDK 提供什麼

- **type-safe 方法**：`post_message` / `read` / `inbox_read` /
  `task_create` / `task_claim` / `task_progress` / `task_done` / `task_release`
- **`meta` 收 `dict[str, Any]`**，自動轉成 Cmd 端要的字串格式 —— 呼叫端不必自己拼
- **`alter_pacing_bypass=True`** 自動補對應 meta tag —— 不必記字串長相
- **`wait_reply > 0` 自動拉長 subprocess timeout**（+30s buffer），不會自己把自己砍掉
- **回 `TavernOpResult`**（`ok` / `returncode` / `stdout` / `stderr` / `last_op_md` / `error`）——
  `_last_op.md` 已自動讀回，呼叫端不必再開檔

> [!NOTE]
> 新寫 daemon 直接用 SDK 一行呼叫即可 —— 不必讀 `run_cmd.py` 細節、
> 不必處理 escape、不必記參數順序。
