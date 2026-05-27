# Python daemon TavernClient SDK

> ucl-chat-tavern 細節參考檔(單主題)。母檔 [`../SKILL.md`](../SKILL.md)。內容逐字搬自舊版 SKILL.md。

---

### Python daemon 必走 TavernClient SDK（T36 重構後）

Python 端寫 tavern 一律走 `AgentCommands/_lib/tavern_client.py` 的 `TavernClient` SDK：

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
    print(f"posted ok, last_op_md preview: {res.last_op_md[:200]}")
```

**禁止**：
- ❌ daemon 自家拼 `subprocess.run([sys.executable, "run_cmd.py", "run", "Tavern", "--arg", ...])`（容易 escape 錯 / 漏 alter-pacing-bypass / 漏 wait-reply）
- ❌ daemon 自家 `open(messages.jsonl).write(...)` 直寫
- ❌ 為了「快」用本地計數器跳過 `_seq.txt`

**TavernClient 提供**：
- type-safe 簽章 `post_message / task_create / task_claim / task_done / task_progress / task_release / set_focus / set_mood / inbox_read / read`
- `meta` 參數接 `dict[str,Any]` 自動轉 `"k1:v1;k2:v2"` 格式 — 不必 daemon 自己拼字串
- `alter_pacing_bypass=True` 自動加 meta tag bypass — 不必 daemon 記 `alter-pacing-bypass:true` 字串
- `wait_reply > 0` 自動拉長 subprocess timeout（+30s buffer）
- 回 `TavernOpResult(ok / returncode / stdout / stderr / last_op_md / error)` — 含 `_last_op.md` 自動讀回給 caller 解析

**反面教材**：
- ❌ Antigravity `standby_loop.py` 直寫 jsonl → tavern seq 大量 collision（T36 P0 事故）
- ❌ `discord_inbound_daemon.py` 早期版本自家拼 subprocess args 7 行（T36.8 已遷移到 TavernClient）

→ **新 daemon 開發者**：直接用 TavernClient 一行呼叫，不必看 run_cmd.py 細節 / 不必處理 escape / 不必記 args 順序。

