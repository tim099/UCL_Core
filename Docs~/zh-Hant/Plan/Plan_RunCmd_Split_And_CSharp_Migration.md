---
title: run_cmd.py 肥大化拆分 + C# 端固化可行性分析
slug: runcmd-split-and-csharp-migration
status: Round 2 — 酒館 review 完成 (kotoko 規格 owner + QA / basecamp 執行, Tim 2026-07-29 派工)
created_at: 2026-07-29T13:10:00Z
created_by: Spectre (kotoko 大小姐)
task_ref: T-RUNCMD-SPLIT
last_updated: 2026-07-29T13:25:00Z
location: UCL_Core (cross-project — run_cmd.py 是所有 agent 進 Editor 的唯一 RPC 入口)
related:
  - ucl_core:Docs~/{lang}/Plan/Plan_AgentCommands_Path_Override.md | AgentCommands 路徑可配置化 | 本分析的 Bug#1 正是該 feature 的未完成邊界
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | 本 plan ship 時走三層 bump
  - concept | tavern_handshake.py | 2026-07-29 已從 run_cmd 抽離的第一塊（1860→1304 行），本 plan 是第二輪
  - concept | _lib/ucl_paths.py | Python 端 canonical 路徑解析，run_cmd 目前**沒用**、自己抄了一份
---

# run_cmd.py 拆分 + C# 固化分析 — Round 1

> Tim 派 task (2026-07-29)：run_cmd.py 太肥，要按功能拆分；並分析哪些功能可以遷到 C# 端固化。

## 📌 執行分工（Tim 2026-07-29 派工 + 酒館 seq 13910–13915 拍板）

| 角色 | 誰 | 範圍 |
|---|---|---|
| 規格 owner | kotoko (Spectre) | **本文件是唯一規格**。六模組切法 / A1·A1.5·A2 範圍 / 硬規則 |
| 執行 | basecamp (claude-code) | 依本規格在 **Dev** 分支動手；邊界切不下去先回酒館問，不擅自改規格 |
| QA / judge | kotoko (Spectre) | 驗模組邊界、Bug#1/2/3 是否真解、有無新的側信號推論混入 |

**執行者三項承諾**：① 完全照六模組切法；② 拆分 commit 只做搬移（行為零變化），readback 移植另開一筆；③ 每階段落 commit 就報。

## ⚠ 分支現況（2026-07-29 查證，動手前必讀）

basecamp 的 readback 工作（`924b586`「tavern body 三通道 + 送前擋/送後驗」，**2026-07-28 20:23**）
**不在 Dev 上** —— `git merge-base --is-ancestor 924b586 HEAD` 回 NO，它只長在 **Dev2**。

| | Dev（**專案實際載入**，UCL pointer = `0e099dc`） | Dev2 |
|---|---|---|
| run_cmd.py | **1304 行** | 1900 行 |
| tavern_handshake.py | 已抽離（`a9399e5`） | 無 |
| work_session.py / work_memory.py | 已移除 | 仍在 |
| backtick guard | 已移除（`715e30e`，改走 `--arg-stdin` 上游關閉污染管道） | 有（改良版） |
| readback (`verify_posted_body` 等) | **無** | 有 |

分歧點 `fcf5a6f`（07-28 20:01）；**Dev 獨有 15 筆 / Dev2 獨有 1 筆**。
成因（Tim 補充）：Dev 那 15 筆有一部分是**從其他專案同步過來的**（UCL_Core 跨專案共用），Dev2 是**本地未 merge 修改** ——
所以 `--arg-stdin` 才會在兩條線上各被實作一次。

**處置（已拍板）**：Dev2 不動、保留當參考。readback 依拆分後的新結構在 **Dev 上重寫**，
並補上原本缺的 `wait` 路徑覆蓋（Dev2 版只掛 `run`）。

> **本身就是一個案例**：同一個檔名兩個所指，識別成本不在 diff，在「你以為你在哪」。
> 執行者當時注意到「奇怪 readback 沒印」但沒當場追 —— 否證訊號被當雜訊駁回，
> 用 40 分鐘換一個當下 30 秒可得的答案。

---

## 0. 現況量測

`run_cmd.py` = **1304 行**（2026-07-29 抽離 `tavern_handshake.py` 後；抽離前 1860）。
它同時是兩種東西：
- **CLI**（`submit` / `wait` / `run` / `recompile` / `list` / `catalog`）
- **被 import 的模組** — `tavern_handshake.py` 反向 `importlib.import_module("run_cmd")` 拿路徑解析

外部呼叫端：`awakening.py` / `library.py` / `knowledge_base.py` / `hook_validate_modified.py` /
`_lib/session_common.py` / `tavern_handshake.py` — 共 6 支，**全部用 subprocess 手搓 argv** 呼叫。

### 功能區塊分佈

| 行範圍 | 區塊 | 行數 | 性質 |
|---|---|---:|---|
| 1–84 | docstring + utf-8 reconfigure + `print_fail_verdict` | 84 | 輸出/bootstrap |
| 86–116 | `print_cmd_error_report` | 31 | 判決輸出 |
| 119–339 | **Tavern client 預檢**（op schema / alias / persona autofill / reserved tag meta） | **221** | Tavern 專屬 |
| 341–421 | 路徑解析 + per-agent queue 路徑 | 81 | 基礎設施 |
| 423–490 | `check_cmd_result_file`（false-success race fix） | 68 | 判決推論 |
| 492–516 | legacy 常數 + handshake 注入 | 25 | 接線 |
| 518–575 | trigger 狀態 / `ensure_idle` / `write_trigger` | 58 | RPC 協定 |
| 577–704 | queue I/O + `make_id` + `TYPE_ALIASES` + `append_cmd` | 128 | RPC 協定 |
| 706–726 | `_detect_caller_env_marker` | 21 | 環境偵測 |
| 728–766 | `cmd_submit` | 39 | 子命令 |
| 768–852 | `cmd_wait` | 85 | 子命令 |
| 854–1018 | `cmd_run`（含 wait-reply 決策 + banner 抽取） | **165** | 子命令 |
| 1023–1088 | `cmd_recompile` | 66 | 子命令 |
| 1091–1120 | `cmd_list` / `cmd_catalog` | 30 | 子命令 |
| 1123–1183 | helpers（`parse_kv_pairs` / `expand_arg_*`） | 61 | 參數來源 |
| 1185–1304 | argparse CLI | 120 | CLI |

**肥大的根因不是行數，是三種職責混在一個檔**：
1. **RPC 客戶端**（queue/trigger 協定 + 判決）— 真正的核心，約 350 行
2. **Tavern 業務預檢** — 221 行，跟 RPC 完全無關，只是「剛好也走這條管線」
3. **CLI 介面 + 參數來源** — 約 180 行

---

## 1. 巡檢中發現的三個實證缺陷

拆分前先記下來 —— 拆的時候順手修比重寫一遍便宜，而且這三個都屬於「外觀 OK ≠ 真的 OK」家族。

### 🐛 Bug#1 — `check_cmd_result_file` 讀錯資料根（latent，path-override 一啟用就發作）

- **寫入端**（C#）：`UCL_ChatTavernIO.GetLastOpPath()` → `UCL_AgentCommandsPath.ResolveData(...)` → **DataRoot**
- **讀取端**（Python，[run_cmd.py:458](../../../Tools~/AgentCommands/run_cmd.py)）：`path = QUEUE_DIR / rel_path` → **QUEUE_DIR**

同檔內 `print_cmd_error_report` 用的是 `DATA_ROOT`、`TAVERN_DIR` 也是 `DATA_ROOT` —— 只有這一處用 QUEUE_DIR。
本機目前沒有 `.agentcommands_root.local`（QUEUE_DIR == DATA_ROOT）所以還沒咬人；但 T-PATH-01 的整個賣點就是「資料根可以搬」，
一搬 fail-detection 就永遠回 `unknown` → **所有 race 失敗靜默變成功**。修法：改用 `DATA_ROOT`（一行）。

### 🐛 Bug#2 — fail marker 表漏掉 Tavern 最常見的失敗字串

Python 認的 marker（[run_cmd.py:482](../../../Tools~/AgentCommands/run_cmd.py)）：`# ❌` 開頭 / `Cmd Failed` / `Cmd failed`。
C# 端 `RejectLastOp`（Cmd_Tavern.cs:2769）實際寫的第一行是：`# ⚠ Tavern Cmd Rejected` —— **三個 marker 一個都不match**。
`Cmd_Bartender` 更直接寫 `❌ ...`（沒有 `# ` 前綴），同樣不 match。

目前沒爆是因為 `RejectLastOp` 會 throw → Runner 記 `LastRunResult=Failed` → `cmd_wait` 走 queue 主路徑判定。
但 T02 race fix 這條備援路徑對「最常見的 Tavern 失敗」是**空的**，等於買了保險沒生效。

### 🐛 Bug#3 — fail-detection 只覆蓋 3/36 個 cmd type

`CMD_OUTPUT_FILES` 只列 `tavern` / `treasury` / `notelesson`，但 registry 有 **36 個 handler**。
其餘 33 個走 race 路徑時一律 `unknown` → 推測成功。這不是設計取捨，是這張表沒人維護得動 —— 也正是下面主張「別再維護表，改用 receipt」的理由。

---

## 2. 拆分提案

原則：**run_cmd.py 只留 CLI 骨架 + 子命令編排**，其餘按職責下沉到 `_lib/runcmd/`。
目標 run_cmd.py **1304 → ~300 行**。

```
Tools~/AgentCommands/
├── run_cmd.py                   ~300  # argparse + submit/wait/run/recompile/list/catalog 編排
├── tavern_handshake.py           700  # (已抽離，不動)
├── tavern_cmd.py            ✅ 429   # Tavern 規則層（已 ship，見下）
├── runcmd_paths.py          ~70      # queue/trigger/running 路徑 (agent-id + lane)
├── runcmd_queue.py         ~140      # load/save(atomic+retry)/find/remove/make_id/append_cmd/TYPE_ALIASES
├── runcmd_trigger.py        ~60      # trigger_state / ensure_idle / write_trigger
├── runcmd_verdict.py       ~130      # 判決：result file 檢查 + 錯誤報告輸出 + print_fail_verdict（+ readback 掛點）
├── runcmd_argsource.py      ~90      # parse_kv_pairs / expand_arg_file / expand_arg_stdin / env_marker
└── _lib/ucl_paths.py                 # (既有 canonical，拆 runcmd_paths 時讓 run_cmd 真的用它)
```

### ⚠ 規格修訂：改用扁平 sibling 模組，**不放 `_lib/runcmd/`**（kotoko 2026-07-29 實作時發現）

Round 1 原提案是 `_lib/runcmd/*.py` 套件。實作前查證後推翻，兩個理由都是實證的：

1. **名稱已被佔用** —— `<repo>/AgentCommands/_lib/tavern_client.py` **已經存在**，
   是完全不同的東西（daemon 用的 TavernClient SDK）。同名不同物正是本 plan 一路在治的 identity 層問題，
   不能自己再造一個。
2. **`_lib` 這個名字本身有 shadowing 陷阱** —— UCL_Core 與主專案**各有一個 `_lib`**：
   前者無 `__init__.py`（namespace package）、後者有（regular package）。實測：

   | import 順序 | `_lib` 解析到 |
   |---|---|
   | 先 `import _lib` | `<UCL_Core>/Tools~/AgentCommands/_lib`（namespace） |
   | 先 `import awakening`（它會把 `<repo>/AgentCommands` 插到 `sys.path[0]`）再 import | **`<repo>/AgentCommands/_lib`**（regular，勝出） |

   而 Tavern 的 persona 反查**正好會在呼叫時 `import awakening`** —— 同一個 process 內 `_lib` 指向哪邊
   會取決於「這次有沒有先發過 post」。把新模組放進 `_lib` 等於把拆分成果建在流沙上。

改用扁平 sibling（`tavern_cmd.py` / `runcmd_*.py`），沿用 `tavern_handshake.py` 已驗證過的形狀：
同層模組 + `configure()` 注入 + 不自行解析路徑。**不發明第二套載入慣例。**

### 各模組職責

| 模組 | 搬進來的東西 | 為什麼獨立 |
|---|---|---|
| `paths.py` | `queue_path` / `trigger_path` / `running_path` / `set_agent_id` / lane 解析 | **順手殺掉重複**：run_cmd 目前自己抄了一份 `_find_git_root_by_walk` + pointer 解析，而 `_lib/ucl_paths.py` 早就是 canonical。改成 delegate，一次消掉 ~45 行漂移源 |
| `queue_io.py` | queue 讀寫 / atomic save / `append_cmd` / `TYPE_ALIASES` | 純檔案協定，跟 CLI 無關；也是唯一該碰 `queue.json` 的地方 |
| `trigger.py` | trigger 三態 + `ensure_idle` | RPC 交握協定，跟 queue 內容無關 |
| `verdict.py` | `check_cmd_result_file` / `print_cmd_error_report` / `print_fail_verdict` **+ 預留 readback 介面**（見下） | **判決集中**：Bug#1/#2/#3 全在這；且這整塊是 §3 C# 遷移後要縮水的目標，隔離開才好換 |
| `argsource.py` | `--arg` / `--arg-file` / `--arg-stdin` / env marker | shell 邊界處理，本質 client-side，永遠不會遷 C# |
| `tavern_client.py` | 221 行 Tavern 預檢 + `cmd_run` 裡的 wait-reply 預設決策 + banner 抽取 | **Tavern 業務不該住在通用 RPC wrapper 裡**。它是 36 個 cmd 中的 1 個，卻佔 run_cmd 兩成篇幅 |

### 順手要修的結構問題

1. **`cmd_run` 內聯重抄了 `cmd_submit`**（arg 展開 → 預檢 → `ensure_idle` → `append_cmd` → `write_trigger` 整段重複，
   只為了留住 `cmd_id`）。抽 `_do_submit(...) -> (cmd_id, submit_time)`，兩個子命令共用。省 ~35 行 + 消掉「改一邊忘另一邊」。
2. **新增 `tavern_post()` Python API**。目前 6 支工具各自 subprocess 手搓 argv 呼叫 `run_cmd.py run Tavern --arg op=post ...`，
   每支都自己處理 timeout / 編碼 / 錯誤解析（`library.py` 兩處、`awakening.py` 一處、`tavern_handshake.py` 一處…）。
   給一個 `from _lib.runcmd import tavern_post` 收斂之。**這也直接消掉本 session 撞到的 morning ritual「tavern post 60s timeout」那種各自為政的 timeout 設定。**
3. **`tavern_handshake.py` 反向 import `run_cmd` 拿路徑** — 拆完改成兩邊都 import `_lib/runcmd/paths.py`，
   消掉循環依賴（現況註解自己承認「本檔是 `__main__` 時會載入另一份副本」）。

### readback（送後驗）— 歸 `verdict`，拆分時預留介面

從 Dev2 移植回來的「post 後讀落地檔比對 body」邏輯歸 `verdict` —— 它就是判決的一種，
只是資料來源從**側信號**換成**落地產物**（這正是本 plan 主張的方向，只是它先在 Tavern 這一個 cmd 上做到了）。

拍板細節（酒館 seq 13914–13915）：
- **exit code 4 = `body-mismatch`**（3 已被 wait-reply 的 `unavailable` 佔用），輸出照既有格式：`[readback] verdict=body-mismatch code=4`
- **讀不到落地檔 → 維持 exit 0 並明說「無法驗證」，不准回 4**。無法判定被編碼成明確失敗，是「同碼失聲」的反向錯誤 —— **無法驗證 ≠ 不通過**
- **不新增第四份 per-message 走訪實作** —— 復用 `tavern_handshake` 既有讀取層（已處理兩代檔名格式），只呼叫不重造
- **不自行解析任何路徑**，一律由呼叫端注入
- 覆蓋 `run` **與 `wait` 兩條路徑**（Dev2 版只掛 `run`，缺口已被執行者自己踩到）
- **依 body 來源決定驗不驗（Tim 2026-07-29 拍板）**：body 走 `--arg-stdin` / `--arg-file` → **跳過逐字比對**；
  只有裸 `--arg body=` 才驗 —— 只有它會被 shell 特殊符號吃字。
  > **規格 owner 附註（已向 Tim 留紀錄，不擋）**：建議把 readback 拆成**兩件事**——
  > **① 落地存在性檢查**（便宜，對**所有** post 都做，是下方 QA 缺口 2 的解）
  > **② body 逐字比對**（只對裸 arg 做，照 Tim 規格）。
  > 否則長文那批（走寫檔通道、也最痛的那批）會連「到底有沒有落地」都失去偵測。
  > 若判定存在性檢查也不需要，則該職責必須由 A1 receipt 承接，不可兩邊都空。
- `--selftest` 至少五項：一致 ✓ ／ 內容被改 → 4 ／ 落地被截斷 → 4 ／ 讀不到 → 0 且印「無法驗證」／
  **前提監視器**：出現非白名單的系統附加段時要紅（把「落地 body ＝ 送出 body ＋ 系統附加」這個隱含前提顯式化成會發聲的測項）

### 🔒 硬規則（拆分時一併落地）

1. **路徑常數只准住 `paths` 模組**，其他模組一律 import，不准各自定義或另取名。
   （實證：`TAVERN_ROOT` vs 真名 `TAVERN_DIR` → `py_compile` 全過、一跑 NameError；本 plan 的 Bug#1 同型。**修好那一處不如封死那個類別。**）
2. **同一種走訪／解析邏輯只准有一份實作**，其他一律呼叫。
   （實證：per-message 走訪目前已有 3 份〔`tavern_query` / `tavern_catchup` / `tavern_handshake`〕，readback 差點成為第 4 份。）
3. **路徑初值不給 fallback 預設** —— 沒注入就炸，不准靜默退到某個「看起來合理」的目錄。
   **不給預設值是設計，不是懶**：炸得漂亮 ≫ 靜默讀錯目錄然後回報一切正常。

### 相容性

- run_cmd.py 保留所有現有 module-level 名稱（`QUEUE_DIR` / `DATA_ROOT` / `queue_path()` …）為 re-export，
  外部 6 支呼叫端與 `tavern_handshake` 零改動即可跑。
- CLI 介面完全不變（同樣的子命令 / flag / exit code）。
- 分階段 ship：先 `paths` + `queue_io` + `trigger`（純機械搬移，風險最低）→ 再 `verdict` + `argsource` → 最後 `tavern_client`。

---

## 3. C# 端固化可行性

判準：**這段邏輯的權威事實住在哪一端？** 住 Editor → 該遷；住 caller 環境 → 遷過去必然重演舊 bug。

### 🟢 Tier A — 該遷，且收益大

#### A1. Cmd 結果 receipt（**最高優先，一舉解掉 Bug#1/#2/#3**）

> **一句話動機**（basecamp 2026-07-29 實證，酒館 seq 13914）：
> `run_cmd.py wait` 印 `✓ Cmd disappeared from queue → Success`，真相是 Editor 卡死復原時把 queue 清了，
> cmd 從沒執行、訊息從沒落地（grep 特徵字串零命中才確認）。
> **「從 queue 消失」同碼於兩件事：執行完 ／ 被清掉。側信號無法區分，而 stdout 只說它消失了。**

**現況**：Python 用三個側信號拼湊判決 ——「cmd 從 queue 消失」+「last_op 檔 mtime」+「檔內 cmd_id stamp」+「首行 marker 字串比對」。
這是**推論**不是**事實**，所以需要 `CMD_OUTPUT_FILES` 對照表（漏 33 個）、marker 字串表（漏 Tavern 主要失敗字串）、
mtime 容差 1s、多 session stamp 比對…—— 68 行全在補一個結構性缺口：**Editor 端從來沒有明確告訴 client 這一筆的結果**。

**提案**：Runner 在 `finally` 寫一份 per-cmd receipt：

```
<DataRoot>/_cmd_results/<cmdId>.json
{
  "cmd_id": "...", "cmd_type": "Tavern", "status": "Success|Failed|Interrupted",
  "error": null, "error_report": "_cmd_errors/<cmdId>.md",
  "started_at": "...", "finished_at": "...", "run_count": 1
}
```

Runner 已經有 `WriteCmdErrorReport` 的落檔基礎設施（2026-07-29 才加的）與 `CurrentCmdId` slot，**增量很小**（~60 行 C#）。

**收益**：
- Python 端 `cmd_wait` 變成「poll receipt 出現 → 讀 status」，`check_cmd_result_file` + `CMD_OUTPUT_FILES` 整塊刪除（-68 行、-1 張表）
- **36 個 cmd type 一次全覆蓋**（Bug#3 解）
- marker 字串比對消失（Bug#2 解）
- 只有一個根（DataRoot）（Bug#1 解）
- 多 session 併發天然安全 —— receipt 以 cmdId 命名，不存在「讀到別人的檔」
- 「cmd 消失 = 成功」這個危險推論退休

**相容**：receipt 不存在（舊版 Editor）→ 走現行推論路徑。零破壞漸進切換。

**`FailCurrentCmd(msg)` 屬於 A1 範圍內（拍板，非 scope creep）**：
receipt 只覆蓋「Runner 吃到 exception」那半邊；handler 自己吞錯誤、寫個 `❌` 到 last_op 就正常 return
（`Cmd_Bartender` 多處如此）那半邊仍要靠側信號反推 —— 那是 Bug#2 換個地方繼續活。
給 `UCL_AgentCommandHandlerBase` 一個 `FailCurrentCmd(msg)`，讓「寫錯誤訊息」與「標記失敗」變成同一個動作
（Tavern 的 `RejectLastOp` 已是對的做法：寫檔 + throw）。
本質是**消除「兩個動作要靠自律同時做」這個漂移源** —— 規則要長在通道上，不要長在使用者的自覺上。
A1 少了它就是「買了保險只保一半」，與 Bug#2 診斷的「買了保險沒生效」同型。

#### A1 可復用 / 待補清單（`c1d24ff` 盤點結果）

`c1d24ff`「cmd 失敗詳情落檔給 client」已在 Dev 上，但它**不是 receipt 的一半，是 receipt「失敗那半」的一部分**：

| | 現況（`WriteCmdErrorReport`, Runner.cs:362-424） | A1 要補 |
|---|---|---|
| 落檔管線 | ✅ DataRoot 解析 / `_cmd_errors/<cmdId>.md` 慣例 / UTF8 無 BOM / **IO 失敗一律吞掉不蓋原始錯誤** | 直接復用 |
| `CurrentCmdId` 生命週期 | ✅ try/finally 已正確管理（finally 清 slot 防 cross-cmd leak） | 直接復用 |
| 呼叫點位置 | ✅ 同一個 try/catch/finally 就是 receipt 該掛的地方 | 直接復用 |
| **成功路徑** | ❌ 只在 catch 裡呼叫，成功什麼都不寫 | **必補** — Bug#3 與「假成功」要的正是成功路徑上那張紙 |
| **輸出格式** | ❌ markdown（client 要判 status 得回頭字串比對 = Bug#2 的形狀） | **必補** — JSON，status 是欄位不是首行字樣 |
| **PlayMode 中斷** | ❌ 該分支（Runner.cs:294-307）完全沒落檔；`LastRunResult` 保持 null 留在 queue 等自癒 | **必補** — 第三種狀態 `Interrupted`，client 現在完全看不見它 |

估算仍約 60 行，但**風險比原估低** —— 最容易寫錯的部分（路徑解析、IO 容錯紀律、cmdId 生命週期）`c1d24ff` 已趟過。

**⚠ receipt 不要跟著寫「最近一筆」共用檔**（`WriteCmdErrorReport` 有寫 `_last_cmd_error.md`）。
`_last_*` 共用檔正是 T-LastOp-CmdId 多 session 污染 bug 的來源，好不容易用 cmdId stamp 兜住。
receipt 天生以 cmdId 命名就沒這問題，別再引入一個共用檔把它請回來。

> **A1.5 / A2 已有細部設計** → [`Plan_AgentCmd_Schema_Reflection_Export.md`](Plan_AgentCmd_Schema_Reflection_Export.md)
> （Tim 2026-07-29 追加派題：能否在 C# 端由 handler 欄位 + reflection 自動生成）。
> 該文附**實證**：`create_trpg_room` 已漂移 —— C# 完整實作但 Python 表沒有，
> 導致該 op 透過 run_cmd.py **完全打不到**（實跑 exit 2）。漂移是現況不是風險。

#### A1.5 — op 名單匯出 + set 比對（比 A2 便宜十倍的中間手段）

A2 的完整版（提取 required/alias）要動 2775 行的 `Cmd_Tavern`，成本高。中間手段：
**C# 只匯出「op 名單」`_tavern_ops.json`（不含 schema 細節，op 名字本來就在 switch 裡），
Python 啟動時比對自己 `TAVERN_OP_SCHEMA` 的 key 集合，差異就報警。**

- 成本：C# 幾行 + Python 一個 set 比對
- 抓到的是**最常見的漂移**（新增 op 忘了同步 Python 端；漏 alias/required 是次常見）
- 這是「偵測漂移」不是「消除漂移」—— 但它讓 A2 不做也不會靜默壞掉。
  **有警報的手抄鏡像 ≠ 沒警報的手抄鏡像。**

#### A2. Cmd/Op schema 機器可讀匯出（殺掉 Tavern 預檢表的兩端漂移）

**現況**：`TAVERN_OP_SCHEMA`（177–177 行那張表，涵蓋 40 個 op 的 required/aliases/optional）是 C# `Cmd_Tavern` 的**手抄鏡像**。
程式碼註解自己承認：「新增保留 tag 時**兩端同步擴表**」、「Editor 端已支援 alias 寬進；本表額外給 client 提示」。
`RESERVED_TAG_META_SCHEMA` 同理（鏡像 T06.3，註解寫明「server 端為權威」）。
只要有人只改一邊，client 就會擋掉合法呼叫、或放行非法呼叫。

**提案**：`UCL_AgentCommandHandlerBase` 加一個可選的結構化 schema（`ArgsSchema` 目前是自由文字，不可機讀），
`Cmd_ExportCommandCatalog` 順便輸出 `commands_schema.json`（它已經在跑 reflection 掃全部 handler，增量小）。
Python 端改成載入該 JSON；檔案不存在 → 跳過 client 預檢（fail-open，行為退化成現在的 Editor round-trip 報錯）。

**收益**：221 行 Tavern 預檢 → ~40 行泛用 schema loader，且**對 36 個 cmd 全部生效**（現在只有 Tavern 有預檢）。
漂移在結構上消失，不是靠自律。

**成本**：要改 `Cmd_Tavern` 讓 op 表可被反射/宣告（2775 行的檔，op dispatch 是 switch，
需要一次性把 required/alias 從程式碼內的 `GetArg(a, "x", GetArg(a, "y", ""))` 提成宣告）。這是 Tier A 裡最貴的一項，
建議排在 A1 之後、視 A1 的實作經驗再拍板。

### 🔴 Tier B — 不該遷（遷了會重演舊 bug）

| 功能 | 為什麼留 Python |
|---|---|
| `_detect_caller_env_marker` | **這正是 2026-05-11 Treasury bug 的修法**：Editor 是 long-running process，in-process 偵測永遠抓不到 caller 的 env var，所以才改成 caller-side 偵測後傳進 args。遷回 C# = 把已修的 bug 重新裝回去 |
| `_autofill_persona_from_lock` | session lock（`letters/<p>/profile/_session.json`）是 `awakening.py` 的產物，claim_origin/env_hash 是 Python 端概念。C# 不認識也不該認識 —— 遷過去會讓 Editor 反向依賴 Python 的 session 模型 |
| `ensure_idle` / trigger 寫入 / queue append | **這是 RPC 的 client 半邊**。server 不可能替 client 決定「要不要送」 |
| `--arg-file` / `--arg-stdin` 展開 | shell 引用邊界問題（反引號吞字那條 lesson 的解法）。本質上只存在於 caller 的 shell，C# 看不到 |
| wait-reply 同步握手 | 已在 `tavern_handshake.py`，設計上就是 client-side 輪詢檔案系統，不進 Editor queue（不然會佔住 Runner） |

### 🟡 Tier C — 可遷但低價值 / 併入 A1

- **`TYPE_ALIASES`**（`chattavern`→`Tavern` 等 7 條）：跟著 A2 的 schema export 一起帶出來，順手；單獨為它動 C# 不划算。
- **`cmd_recompile` 的 mtime 推進輪詢**：本質跟 A1 同構 —— 「等一個外部信號檔更新」vs「等一個 receipt」。
  `UCL_CompileErrorTracker` 已經在寫 `.compile_status.json`，若 A1 的 receipt 機制成型，recompile 可以直接復用同一套等待邏輯，
  省掉 66 行專用輪詢。**不獨立做，等 A1 落地再併。**

---

## 4. 建議執行順序

| 階段 | 內容 | 風險 | 產出 |
|---|---|---|---|
| **P0** | 修 Bug#1（一行）、Bug#2（marker 表補 `Cmd Rejected` / 裸 `❌`） | 極低 | 現有 race 保險真的生效 |
| **P1** | `[port]` readback → Dev，接上 `run` + `wait` 兩條路徑，exit code 4 + `--selftest` 五項 | 中 | Dev2 的工作進到實際跑的版本 |
| **P2** | `[refactor]` 依規格拆六模組（**純搬移、行為零變化**）+ run_cmd 改用 `_lib/ucl_paths` | 低 | run_cmd −430 行 |
| **P3** | 三個結構問題：`_do_submit` 共用 / git-root walk delegate / `tavern_post()` API 收斂 6 支呼叫端 | 中 | run_cmd → **~300 行達標** |
| **P4** | 三條硬規則落地（路徑常數 / 單一走訪實作 / 不給 fallback 預設） | 低 | 封死 Bug#1 那個**類別** |
| **P5** | C# A1（receipt + `FailCurrentCmd`）→ A1.5（op 名單比對）→ A2（視前兩者實效再議） | 中–高 | Bug#3 根治，36 cmd 全覆蓋 |

P0–P4 純 Python，不必動 Unity，可獨立驗證。**P5 等 P0–P4 驗收過再開**（要三層 submodule bump，
混在一起出事難二分）。每階段落 commit 由執行者回報，QA 對規格驗一次。

---

## 4.4 施工紀錄 — 聊天酒館規則層抽離（kotoko 2026-07-29，✅ 已完成）

> Tim 2026-07-29 決定：**P1 readback 暫緩**（已由 basecamp `git stash` 保留，stash@{0}），
> 改先重構聊天酒館相關 → 執行者換 kotoko。

**成果**：`run_cmd.py` **1304 → 1042 行**（−262，diff 為 +31 / −293）；新增 `tavern_cmd.py` 429 行。

**搬過去的**：`TAVERN_OP_SCHEMA`（33 op）／`QUEST_OPS_NEEDING_IDEMPOTENCY`／`RESERVED_TAG_META_SCHEMA`／
alias 歸一與 required 檢查／persona 反查三段 fallback／T06.3 保留 tag 驗證／
**wait-reply 三段決策政策**（原本散在 `cmd_run` 中段 45 行）／work-mode banner 抽取。

**行為零變化的驗收**（不是「能 import」就算）：
- `tavern_cmd.py --selftest` **29 項全綠**，逐條對照搬移前的原始行為並**固化成常駐測項**
  （wait-reply 三段優先序含「查詢類 op 連顯式值都蓋」、alias 歸一、quest idempotency 自動填、
  T06.3 四種情形、persona 顯式值不被覆寫）
- `tavern_handshake.py --selftest` 回歸 exit 0（未受影響）
- Live CLI：缺參數 → exit 2 且不寫 queue ✓／未知 op → exit 2 ✓／
  `inbox_read` 的 alias 歸一 + 強制 wait-reply 0 ✓／`list` `catalog` 等非 Tavern 路徑未受影響 ✓
- 全 repo grep：搬走的六個符號**無任何外部引用** → 不需 re-export shim

**🩸 施工中被自己的硬規則抓到一次（值得記）**
`tavern_cmd.py --selftest` 第一次跑就紅在測項⑥「configure 注入」——
以 `python tavern_cmd.py --selftest` 執行時本檔是 `__main__`，而 `import run_cmd` 內的 `import tavern_cmd`
會載入**另一份副本**並只設定那一份，`__main__` 這份三個依賴全是 `None`（Python 雙模組陷阱，
`tavern_handshake` 踩過同一個）。

**它炸得漂亮而不是靜默走到錯的目錄，正是因為硬規則③「路徑初值不給 fallback 預設」。**
若當初給了預設值，persona 反查會靜默去讀錯的 session 目錄、banner 去讀錯的房 —— 然後全部回報正常。
這條規則寫進 plan 不到一小時就自己付了一次成本，也自己還了一次。

---

## 4.5 QA 紀錄 — P1 readback 移植（kotoko 2026-07-29 21:40）

判定：**通過但未完成**。驗法：自行重跑 selftest（`${PIPESTATUS[0]}` 量碼）＋逐行讀 diff ＋單獨驗定位假設，不採信回報。

**✅ 通過**
- selftest 13 項全綠、exit 0；錯誤輸出可用（「內容被改動」能指出首個差異字位並印前後文）
- 走訪確實只呼叫既有實作，**未新增第四份**；`verify_posted_body` 零路徑解析；exit 4 + verdict 行格式對齊
- **定位假設實測通過**：`sender=Spectre` → 落地 JSON `sender_id == "Spectre"`。
  （這條若對不上，readback 會每次回 unverifiable(0)，**看起來一切正常但一次都沒驗過** —— 最糟的形態）
- Live 觀測：本輪 QA 報告自身觸發 `[readback] verdict=verbatim+append code=0`，白名單附加判定正確

**❌ 缺口 1 — `wait` 路徑仍未掛**（承諾未達）
`verify_posted_body` 全檔僅一處呼叫，在 `cmd_run`（run_cmd.py:979-997）；`cmd_wait` 無。
**而假成功血證用的正是 `run_cmd.py wait <id>`** —— 保險沒蓋到出事那條路，該事故仍會重演。

**⚠ 缺口 2 — 分不出「落地但被改」與「根本沒落地」**（設計層，優先度高於缺口 1）
`_latest_message_key(room, sender_id)` **無時間過濾**。cmd 根本沒執行時它抓到的是**該 sender 的上一則舊訊息**（不是 None）：
- 比對必然不符 → 回 4（有叫，對）
- 但 hint 印「常見原因：shell 引用吃字 → 改 --arg-stdin 重發」→ **診斷指向錯誤方向**

**修法**：`verify_posted_body` 收 `since_ts`（用 `cmd_run` 已有的 `submit_time`），落地檔 `ts` 早於 submit
→ 回 `unverifiable` 並明說「找不到本次 post 的落地檔，最新一則更早 — 很可能根本沒落地」。
與 `check_cmd_result_file` 的 mtime 門檻同一招。

**⚠ 缺口 3 — selftest 的前提監視器比描述窄**（小）
執行期監視器（非白名單附加即出聲）寫得對、覆蓋任何未知附加。
但 selftest 那項的判準是「含 `\n---\n` 且結尾為 `` .md`) ``」→ 抓的是**已知格式的變體**（像 glossary 但缺 marker），
不是未知格式。建議放寬判準，或把測項名字改成它實際測的東西（後者便宜且誠實）。

**修補建議順序**：缺口 2 ＞ Tim 新規格（含存在性／比對拆分）＞ 缺口 1 ＞ 缺口 3。
四條皆位於 `verdict` 邊界內，**不擋 P2 六模組拆分**先行。

---

## 5. Round 2 未決 / 後續

1. **A2 完整版何時做** —— 先看 A1 + A1.5 實效。A1.5 上線後手抄鏡像至少有警報，不再是靜默風險。
2. **Dev2 收不收** —— readback 移植完後請 Tim 決定是否收掉該分支。
3. **`tavern_post()` 的 timeout 政策** —— 收斂 6 支呼叫端時一併統一
   （實證：本日 morning ritual announce 60s timeout 報 FAIL、實際早已落地）。

---

*智慧之神從殘缺推真相 —— 這份分析最大的發現不是「檔案太長」，是**判決一直建立在推論而非事實上**。
1304 行裡有 289 行（判決 68 + Tavern 預檢 221）存在的唯一理由，是 Editor 端沒把它知道的事實明確講出來。🔍*
