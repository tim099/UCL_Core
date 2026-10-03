---
title: Unity 官方 CLI 評估 —— 編譯排錯與 Editor 存活訊號能不能改走 `unity` CLI
slug: unity-cli-evaluation
status: draft（2026-10-03 只讀過官方文件，**本機沒有裝 CLI、沒有任何實測**；下一步是 §5 的 spike）
created_at: 2026-10-03T03:30:00Z
created_by: basecamp
last_updated: 2026-10-03
location: UCL_Core (cross-project)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Docs~/{lang}/Workflows/CompileError_Diagnose_Workflow.md | 編譯排錯 SOP | 現行做法；本案評估的是它的觸發端與存活訊號
  - repo:AgentCommands/Tasks/tasks/0365.md | TASK-0365 酒保重做 | §E1：心跳拆出酒保、遷到 Library/ —— 本案回答「心跳要不要改走 CLI」
---

# Unity 官方 CLI 評估

> 一句話：**可以用，但不取代心跳檔** —— 心跳照 TASK-0365 §E1 拆出來；CLI 先當第二條讀數，等 §5 三格量完再決定要不要接進 `unity-recompile`。

## 1. 背景

- Tim 2026-10-03 問：Unity 推出了新的 CLI，可以分析 compile；心跳是否可以改走 CLI。
- 牽涉的現行機制有兩個：
  - **編譯**：`senate cmd unity-recompile` 經 AgentCommand 佇列交給 Editor 觸發重編並等那一趟結束；`senate cmd unity-compile-status` 本地讀編譯狀態檔＋ErrorLog，**不需要 Editor**。
  - **Editor 存活**：酒保 daemon 每 0.5 秒寫 `<data_root>/ChatTavern/bartender/_heartbeat.txt`，停跳記進 `_heartbeat_stalls.jsonl`。讀它的是 Senate `ProjectProbe`（專案頁／Doctor 頁）、`Cmd_Goodnight.EditorAlive`，以及編譯排錯文件教人 stat 它。詳見 TASK-0365 §E1。

## 2. 這支 CLI 是什麼（照官方文件，未實測）

- 名稱：Unity command-line interface，指令名 `unity`。
- 版本：**beta**，2026-09-30 是 `1.0.0-beta.12`；`unity recompile` 在 beta.11（09-22）加入。
- 連 Editor 的前提：專案要裝 **`com.unity.pipeline`** 套件（Unity 6.0 以上），套件在 Editor 內開 **7800** 埠。
  - 本專案 `ProjectVersion.txt` ＝ `6000.3.5f2` ⇒ 版本符合；`Packages/manifest.json` **沒有**這個套件。
- 平台：macOS／Linux／Windows；`open --wait` 在 Windows 還不支援。

### 2.1 跟我們相關的指令

| 指令 | 做什麼 | 讀數 |
|---|---|---|
| `unity recompile [--strict]` | 讓**正在跑的** Editor 重編，列出每個錯誤的檔案與行號；`--strict` 連警告也算失敗 | exit `0` 成功／`6` 有編譯錯誤／`7` Editor 沒回應（可重試） |
| `unity status [--until-ready] [--format json]` | 列出正在跑的 Editor：埠、專案路徑、版本、PID；`--until-ready` 等到 Editor 就緒（預設 300 秒） | Play 模式下 JSON 另有 `frameCount`、`playerLoopTicking` |
| `unity editors running` | 列出執行中的 Editor | —— |
| `unity doctor` | 環境診斷 | `--format json` |

- CLI 全體 exit code：`0` 成功／`1` 一般錯誤／`2` 用法錯／`3` 授權／`4` 缺設定／`6` 主要動作失敗／`7` 服務或 Editor 連不上（可重試）／`8` 測試有失敗。
  ⇒ `6`／`7` 的語意剛好跟我們 Senate CLI 的「確定沒做／不知道」慣例同號，但**意思不同**（它的 `6` 是「做了但失敗」），接的時候要逐支翻譯，⛔ 不能直接沿用。

### 2.2 文件明寫的限制

- 🩸 **Safe Mode 連不上**（⚠ Tim 2026-10-03：不開 Safe Mode ⇒ 這格對本專案不構成阻礙，留著只當文件紀錄）：專案有 C# 編譯錯誤時，Editor 以 Safe Mode 開啟，Pipeline 套件不載入 ⇒ CLI 連不上。文件的處置是「先修編譯錯誤再重開」。
  ⇒ 偏偏「有編譯錯誤」正是排錯最需要它的時候。（我們的心跳在 Safe Mode 下一樣不跳；那種狀況現在靠 `unity-compile-status` 讀檔。）
- 2026-09-14 的修正：`unity recompile` 剛編譯完時曾把健康的 Editor 報成連不上，現在會先帶新 token 重試一次 ⇒ 編譯前後的連線狀態本來就不穩，beta 還在修。

### 2.3 文件沒寫、要實測的

- 編譯中／domain reload 時，`status` 與 `recompile` 回什麼（推測是 `7` 連不上，⚠ 未驗）。
- 主執行緒卡住（不是編譯，是某支 Cmd 凍住）時，`status` 會不會照樣回應 —— 如果套件的 server 不在主執行緒上，它可能回「活著」而 Editor 其實凍了。
- 每次呼叫的耗時（要起一個 process 再走網路）。

## 3. 能不能取代心跳檔

| | 心跳檔（現行） | `unity status` |
|---|---|---|
| 讀的成本 | stat 一個檔，微秒級 | 起一個 process＋走埠問 Editor（⚠ 未量） |
| 歷史 | 有停跳台帳：「剛剛凍過多久」 | 只有問的那一刻 |
| 前提 | 不用裝任何東西 | 每台機器裝 CLI、每個專案裝套件（含 LY 那台） |
| 量的是什麼 | **主執行緒有沒有在跑**（綁 `EditorApplication.update`） | **套件的 server 有沒有回應**（跟主執行緒的關係未知，見 §2.3） |
| 成熟度 | 跑了兩個月 | beta |

⇒ **不取代**。心跳量的是「主執行緒有沒有在動」，這正是編譯排錯要的那一格；CLI 量的是另一件事，而且有安裝前提與每次呼叫的成本。
Senate 的專案頁與晚安流程每次刷新都要讀存活訊號，它們需要的是被動、便宜的那一種。

## 4. 建議的用法

1. **心跳檔照 TASK-0365 §E1**：從酒保拆成獨立小元件、遷到 `<Unity 專案根>/Library/UCL/`。
2. **`unity recompile` 當觸發端的候選**：它跟 `senate cmd unity-recompile` 做同一件事，而我們那支要經過自己的 AgentCommand 佇列（Editor 的 Cmd 系統要先載得起來）。
   - 可能的接法：`unity-recompile` 先試官方 CLI，exit `7`／沒裝時退回現行路徑；結果與 `unity-compile-status` 的讀檔對帳。
3. **`unity status` 當第二條讀數**：只放在排錯流程裡 —— 心跳說停了，再問一次 CLI，用兩條讀數分辨「Editor 關了」「卡在編譯」「主執行緒凍住但 server 還在」。⛔ 不放進每次刷新都會跑的路徑。

## 5. 下一步：spike（量完再決定）

在這台 Windows、Bar 專案上裝 CLI 與 `com.unity.pipeline`，量下面幾格。
📌 CLI 的安裝與版本偵測已做成 Editor 頁：工具集 →「🧰 環境安裝」→ 套件安裝（`UCL_PackageInstallPage`，2026-10-03）；Pipeline 套件還沒做進那頁。

- [ ] 編譯中、domain reload 中，`unity status --format json` 與 `unity recompile` 各回什麼（exit code＋輸出）
- ~~Safe Mode 是否真的完全連不上~~ —— ⛔ 不量（Tim 2026-10-03：不開 Safe Mode）
- [ ] 主執行緒凍住（例如一支 Cmd 卡 10 秒）時，`status` 回「活著」還是「連不上」—— 對照同時刻的心跳停跳台帳
- [ ] 每次呼叫的耗時（`status`、`recompile` 各 10 次取中位數）
- [ ] 反向對照：Editor 完全關閉時兩者都回「沒有 Editor」

⚠ 裝套件會改 `Packages/manifest.json` 與 `packages-lock.json`（進版控）⇒ spike 前先問 Tim 要不要入版控，或只在本機試、試完還原。

## 6. 出處

- [Unity CLI reference](https://docs.unity.com/en-us/unity-cli/unity-cli-reference)
- [Unity CLI release notes](https://docs.unity.com/en-us/unity-cli/release-notes)
- [Unity Pipeline package](https://docs.unity.com/en-us/unity-cli/unity-pipeline/unity-pipeline-package)
