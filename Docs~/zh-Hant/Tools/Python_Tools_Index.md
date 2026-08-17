---
title: UCL_Core Python Tools 索引 — 跨專案 CLI / 自動化工具一覽
description: UCL_Core/Tools~ 下所有 Python 工具的功能 / 入口 / 使用場景索引。涵蓋 agent awakening (morning/goodnight) / queue infra (run_cmd) / Editor 整合 (check_compile / hooks) / migration scripts / skill installer。
last_updated: 2026-08-17
target_audience: [AI_Agent, Tools_Maintainer, Tim]
related:
  - ucl_core:Docs~/{lang}/Plan/Plan_Awakening_Init_Protocol.md | Awakening Init Protocol | morning/goodnight 三步驟設計
  - ucl_core:Docs~/{lang}/Plan/Plan_Work_Session_Mechanism.md | Work Session Mechanism | 上班 session 全 spec
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md | Agent Command | run_cmd.py 觸發的 C# Cmd 端 architecture
---

# 🐍 UCL_Core Python Tools 索引

> 一句話: UCL_Core 的 Python 工具集中在 `Tools~/AgentCommands/`, 跨專案共用; 真正 project-specific 的 Python tool 放主專案 `AgentCommands/Tools/` (見最末段對照)。

> [!WARNING]
> **本索引目前只覆蓋約三分之一的工具。** 2026-08-17 對著磁碟點過：UCL_Core 實有 **62 支** .py，
> 本檔提及的只有 21 支，**41 支從未列入**（`canvas.py` / `library.py` / `memory.py` /
> `knowledge_base.py` / `work_memory.py` / `freetime.py` / `git_commit.py` / `chess.py` …）。
> 同一輪也修掉 6 個**指向已刪除工具**的條目。
>
> ⇒ 查不到某支工具**不代表它不存在**（借 gura 那句：別把「我沒找到」當成「不存在」）——
> 直接 `ls <UCL_Core>/Tools~/AgentCommands/` 是比本索引可信的來源。補齊尚未排程。

## 📂 目錄結構

```
Tools~/
├── install_skills.py                   # Skill 安裝器 — host project 同步 .claude/skills
└── AgentCommands/
    ├── awakening.py                    # 早安 / 晚安 ritual CLI
    ├── awakening_full_ritual.py        # awakening.py 的 3-step wrapper (一鍵)
    ├── check_compile.py                # Editor 編譯報告
    ├── check_task_lease.py             # 動 code 前 lease 守門
    ├── hook_validate_modified.py       # Claude Code PostToolUse / Stop hook
    ├── run_cmd.py                      # ⭐ queue.json 提交器 — 觸發 C# Cmd
    ├── migrate_persona_binding.py      # (one-shot) baton 從 actor-keyed 遷 persona-keyed
    ├── migrate_session_to_persona_locks.py  # (one-shot) session lock 遷 persona-keyed
    ├── migrate_time_rules_add_tz.py    # (one-shot) bartender time_rules 補 tz 欄位
    ├── _lib/
    │   └── json_io.py                  # JSON 讀寫公用 helper
    └── CommandResolver/                # 口語指令 → Cmd Type 解析子套件
        ├── resolver.py                 # 主解析器
        ├── normalize.py                # 字串正規化
        ├── sync_command_table.py       # 同步 CommandTable.md 到 cache
        ├── fetch_sheet.py              # GoogleSheet fetch (translate)
        ├── channel_status.py           # Discord channel 狀態查
        ├── inbox_ack.py                # tavern inbox ack 助手
        ├── test_resolver.py            # resolver 自測
        ├── _resolver_cache/            # cache 目錄
        └── __init__.py
```

## ⭐ 核心工具

### `run_cmd.py` — Cmd queue 提交器

**這是最常用的 entry point**。Agent 透過此工具觸發 Unity Editor 內 C# Cmd 處理。

| 用法 | 範例 |
|---|---|
| `run <Type> --arg key=value` | `python run_cmd.py run Tavern --arg op=post --arg room=tavern --arg body="..."` |
| `info <Type>` | `python run_cmd.py info Bartender` (印 ArgsSchema) |
| `list` | 列所有 Cmd Types |

**機制**:
1. 寫 entry 到 `AgentCommands/queue.json`
2. Touch `AgentCommands/pending.trigger`
3. Unity Editor 的 `UCL_AgentCommandWatcher` 偵測 trigger → 跑對應 Cmd handler
4. 等 entry 從 queue 消失 (預設 timeout 120s) → 完成

詳見 [API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)。

---

## 🌅 Awakening 系列

### `awakening.py` — Morning / Goodnight ritual

| Subcommand | 功能 |
|---|---|
| `status` | 印 persona pool + active locks + bank account mapping |
| `morning` | 開 session lock + 寫 letter chain + 廣播酒館 (need `--agent` `--persona`) |
| `goodnight` | 釋放 lock + 寫 letter (`--letter-body` `--perturbation`) |
| `whoami` | session token recovery (`--token <X>` 或 env auto-infer) |
| `reissue-token` | 補發 token (失憶 recover 第 3 層) |
| `token-enforce` | 開關 sender_id token 驗證 |

詳見 [Plan/Plan_Awakening_Init_Protocol.md](../Plan/Plan_Awakening_Init_Protocol.md)。

### `awakening_full_ritual.py` — 一鍵三步驟 wrapper

把 `status → morning → 廣播` 串成單一 invoke (給 SOP 簡化用)。

---

## 🛠 Editor 整合

### `check_compile.py` — Editor 編譯報告

讀 Editor 端 `Library/Bee/build.txt` 等檔, 印 markdown / json 編譯錯誤 + warning 報告。

```bash
python check_compile.py                  # markdown 報告
python check_compile.py --errors-only    # 只看 Error
python check_compile.py --max 10         # 限制筆數
python check_compile.py --format json    # 機器讀
python check_compile.py --watch          # 等下次編譯結束才印
```

### `check_task_lease.py` — Pre-commit 守門 (W1 enforce)

確保 staged 檔案有對應的 `task_claim` lease, 否則警告 (warning-only, 不擋 commit)。

```bash
python check_task_lease.py               # 用 staged files 自動偵測
UCL_SKIP_TASK_CHECK=1 git commit ...     # bypass
```

### `hook_validate_modified.py` — Claude Code hook

兩種模式:
- `--mode post` — PostToolUse hook, best-effort 記 modified file 到 state
- `--mode stop` — Stop hook, 強制驗 UCL_Asset 格式 (blocking)

### `install_skills.py` — Skill 安裝器

Host project 同步 `<UCL_Core>/Skills~/*` 到 `<project-root>/.claude/skills/`。
首次接 UCL_Core 後跑一次, 之後 UCL_Core bump 後手動再跑。

移除相關（2026-08-12）：

- `--uninstall` 的候選集是 **`Skills~` 現存 ∪ 已裝目錄** —— 已從 `Skills~` 退場的 skill
  只存在於已裝端, 只從源端濾會讓 `--include <退場的> --uninstall` 變成**靜默 no-op**（exit 0、`removed=[]`）。
- 顯式 `--include` 點名的 skill 沒被移除 → **exit 2** 並印出原因（未安裝 / 無 marker 被擋）。
- `--force-remove-unmarked` 才會刪**沒有 `.ucl_source`** 的目錄（預設視為使用者手放的 skill, 不動）。
  與 `--force-overwrite` 刻意分兩顆旗標: 前者是覆蓋內容, 後者是刪除來源不明目錄。
- 全量同步（無 `--include/--exclude`）會自動掃掉**有 marker** 的 orphan 目錄; 無 marker 者永遠不自動刪,
  改由 `UCL_AgentSkillManagerPage` 的 Matrix 底部區塊顯示 + 二次確認移除。

---

## 🛠 Misc

### ~~`rebuild_latest_pointers.py`~~ — 已移除

**檔案已不存在**（2026-08-17 稽核到本節仍在指路）。維護責任已搬進兩個必經點：寫信端
`awakening.write_letter()` 每次覆寫 pointer，早安生成 brief 時 `wake_brief.sync_latest_pointer()`
再對一次帳並在不一致時標明修補 —— 與其事後重建，不如長在必經路上。

### `migrate_*.py` — One-shot 遷移腳本 (跑完即廢)

| 腳本 | 用途 | 狀態 |
|---|---|---|
| `migrate_persona_binding.py` | baton 從 actor-keyed 遷 persona-keyed | shipped 2026-05-? |
| `migrate_session_to_persona_locks.py` | session lock 從 cwd-hash 遷 persona-keyed | shipped |
| `migrate_time_rules_add_tz.py` | bartender time_rules 補 tz 欄位 | shipped |

跑過後保留作為 audit, 不該再 invoke。

---

## 📂 子套件

### `_lib/json_io.py` — 公用 JSON helper

讀寫 JSON 跨 tools 共用 wrapper, 處理 BOM / encoding / atomic write 等邊角。

### `CommandResolver/` — 口語指令解析子套件

| 檔 | 用途 |
|---|---|
| `resolver.py` | 主解析器 — 使用者輸入 → 對應 Cmd Type / workflow |
| `normalize.py` | 字串正規化 (去全形、trim) |
| `sync_command_table.py` | CommandTable.md → resolver cache 同步 |
| `fetch_sheet.py` | GoogleSheet fetch (translation 用) |
| `channel_status.py` | Discord channel 狀態查 |
| `inbox_ack.py` | tavern inbox ack 助手 |
| `test_resolver.py` | resolver 自測 |

---

## 🔎 Project-specific tools 對照 (放主專案 `AgentCommands/Tools/`)

UCL_Core 不放這些 — 它們依賴 project-specific 邏輯 (e.g. EOV battle / treasury):

> ⚠ 本節 2026-08-17 對著磁碟重新點過一次 —— 原本列的 11 項裡有 **6 項已不存在**（`balance_query.py` /
> `discord_inbound_bot.py` / `gold_convert.py` / `secret_install.py` / `secrets_crypto.py` /
> `treasury_revert.py`），而索引仍在替它們指路。**一份指向不存在工具的索引，讀起來跟正確的索引一模一樣。**

| Tool | 用途 |
|---|---|
| `affinity_update.py` | affinity（好感度）更新 CLI |
| `debuglog_query.py` | DebugLog 查 (5 ops: tail / component / errors / search / summary) |
| `screenshot.py` | 單張截圖（串流 daemon 已搬 UCL_Core `screenstream_*.py`） |
| `tavern_catchup.py` | 叮 / 早安的酒館 catch-up（在線一覽＋未讀＋inbox） |
| `tavern_query.py` | tavern 查工具 |
| `workflow_patch.py` | workflow-patch register |

**已退役（別再照舊文件找）**：

| 原工具 | 現在走哪 |
|---|---|
| `balance_query.py` | **餘額一律走 CMD** —— `Cmd_Bartender op=balance`（C# 原生查 `UCL_TreasuryLedger`）。刪它的理由是它會靜默算到另一棵資料樹，血證在 `UCL_BartenderDaemon.RunBalanceQuery` |
| `secret_install.py` / `secrets_crypto.py` | UCL_Core `ucl_secret.py` ＋ `_lib/ucl_secrets_crypto.py` |
| `gold_convert.py` / `treasury_revert.py` | 移除 —— 動錢一律走 Cmd（`_lib/treasury_cmd.py`），不留可直寫帳本的 python 入口 |
| `qa_*.py`（record / score / balance report）、`agent_task.py`、`shared_notes.py`、`persona_ding.py`、`morning_status.py`、`plan_index.py` 等 | 對應系統已退役或併入 Cmd；`AgentCommands/Tools/README.md` 有逐項清單 |

→ 跨專案搬 UCL_Core 時這些**不會跟著**, 各 project 自己有自己版本。

---

## ❓ Localize 工具 (2026-05-18 gura 搜尋結果)

掃了 UCL_Core/Tools~ 跟主專案 `AgentCommands/Tools/` — **沒有任何 Python tool 對 localize asset 操作**。

候選工具僅 C# Editor 端:
- `UCL_LocalizeEditPage` (UCL_Core/EditorMenuPages) — 編輯既有 key, 不寫入新檔
- `UCL_LocalizeEditOnGUI` — page sub-widget
- `RCG_LocalizeAsset` (主專案 Scripts/Editor) — Google Sheet 同步下載

→ 若要走 「Python 工具寫入 Localize Asset」路線, 是 **0 → 1 開新工具** 不是「通用化既有工具」。

若 Tim 想動工, 推薦設計:
- 新建 `Tools~/AgentCommands/localize_edit.py`
- subcommand: `add <asset_id> <key> <value>` / `remove <asset_id> <key>` / `list <asset_id>`
- 寫入 `<.BuiltinModules>/.../UCL_LocalizeAsset/<asset_id>.json` 或 LocalizeDatas/<asset>/<lang>.txt
- 對齊既有 UCL_LocalizeAsset C# 端 parse 規則 (line-range 格式)

---

## 📚 相關文件

- [Plan/Plan_Awakening_Init_Protocol.md](../Plan/Plan_Awakening_Init_Protocol.md) — Awakening 三步驟 spec
- [Plan/Plan_Work_Session_Mechanism.md](../Plan/Plan_Work_Session_Mechanism.md) — Work Session 全 spec
- [API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md) — Agent Command C# 端 architecture
- [CommandTable.md](../CommandTable.md) — 口語指令對照表
- [Workflows/Commit_Workflow.md](../Workflows/Commit_Workflow.md) — Commit 規範 (含 submodule 三層 bump)
