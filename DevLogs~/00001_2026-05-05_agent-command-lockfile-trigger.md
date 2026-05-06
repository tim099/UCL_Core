---
date: 2026-05-05
index: 00001
title: Agent Command 系統新增 Lock-file 自動觸發機制
tags: [feature, refactor, docs]
---

# Agent Command 系統新增 Lock-file 自動觸發機制

## What

Agent Command 系統從「**人工點按鈕**」升級為「**Python 寫 trigger → Editor 自動接手**」的全自動觸發流程。

新增元件：

| 檔案 | 角色 |
|---|---|
| `UCL_Core_Scripts/EditorCore/UCL_AgentCommands/UCL_AgentCommandTrigger.cs` | Lock-file 三方狀態機（Idle / Pending / Running）封裝；File.Move 原子接手 |
| `UCL_Core_Scripts/EditorCore/UCL_AgentCommands/UCL_AgentCommandWatcher.cs` | `[InitializeOnLoad]` + `EditorApplication.update` 1Hz 輪詢 trigger |
| `Tools~/AgentCommands/run_cmd.py` | Python wrapper（`submit / wait / run / list / catalog` 子命令，含 `ensure_idle()` pre-flight） |

修改元件：

- `UCL_AgentCommandQueue.cs` — 補 `GetTriggerPath()` / `GetRunningTriggerPath()` helpers
- `UCL_AgentCommandRunner.cs` — `RunAsync` finally 加 `Trigger.Clear()`；公開 `IsRunning`
- `UCL_AgentCommandsPage.cs` — 移除 `Export Cmd Catalog` 獨立按鈕；新增 Watcher 狀態列（toggle / 狀態燈 / Last trigger / Simulate Trigger）

## Why

舊流程下 AI agent 透過 `queue.json` 排隊指令後，使用者必須手動切到 Unity Editor 按 `Tools/UCL/Agent Commands/Run Pending`。對於開發循環（agent 連續 submit 多筆 cmd 等執行結果）來說，「人類在迴圈裡」是嚴重瓶頸。

Lock-file 機制讓 agent 透過 Python wrapper `submit` 後，Unity Editor 內的 `UCL_AgentCommandWatcher`（`[InitializeOnLoad]` 自動啟動）會在 1 秒內偵測 `pending.trigger` 並自動接手執行。對 agent 來說，呼叫 `python run_cmd.py run <CmdType> ...` 就像直接執行同步 API。

## How to use

### 三方狀態機

```
[idle]   ──Python.submit (ensure_idle 通過)──▶ AgentCommands/pending.trigger
                                                       │
                                                       │ Watcher.OnEditorUpdate (1Hz)
                                                       ▼
[pending] ──File.Move (atomic)──▶ AgentCommands/pending.trigger.running
                                                       │
                                                       │ Runner.RunAsync finally
                                                       ▼
[running] ──Trigger.Clear──▶ [idle]
```

### Python CLI

```bash
# submit + wait 一次完成
python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py run ExportCommandCatalog --timeout 60

# 只 submit 不 wait
python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py submit Ping --arg msg=hi

# 列當前 queue + trigger 狀態
python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py list
```

### Watcher 開關

`UCL_AgentCommandsPage` 上方新增 Watcher 狀態列：
- `☑ Auto-Watcher` toggle — 啟 / 停用 Watcher（寫入 EditorPrefs `UCL.AgentCmd.Watcher.Enabled`）
- `● Idle/Pending/Running` 燈號 — 當前 trigger 檔狀態
- `Last trigger` — Watcher 最近一次接手 trigger 的時間
- `Simulate Trigger` 按鈕 — 手動寫一個 trigger 驗證 watcher

### Python pre-flight：ensure_idle

`submit` 之前自動呼叫 `ensure_idle(timeout=60s)`：若前一輪 `pending.trigger` 或 `.running` 還在，會等到消失才寫入新批次。避免：
1. 兩個 Python 同時 submit → race
2. 把新 cmd 蓋到 Editor 還沒處理完的舊批次

ack-timeout 到還沒 idle → wrapper SystemExit 並提示手動清理檔案（通常代表 Editor crash 或 Watcher 停用）。

## Breaking changes

1. **Python 工具路徑變更**
   - 舊：`Tools/AgentCommands/run_cmd.py`（外層 repo）
   - 新：`CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py`（移入 UCL_Core 內，跟著插件走）
   - 既有 CI / 腳本若呼叫舊路徑會找不到檔。

2. **Python wrapper 新增 pre-flight**
   - 舊版 `submit` 直接寫 queue.json
   - 新版會先 `ensure_idle()` block 等到 idle；若你的工作流會「快速連續 submit 多筆」，需考慮 ack-timeout 設定（預設 60s）

3. **`UCL_AgentCommandsPage` UI**
   - 移除「Export Cmd Catalog」按鈕 → 改加一筆 `ExportCommandCatalog` Cmd 走標準管線

## Migration

- **既有 RCG / 上層專案**：把外層 `Tools/AgentCommands/run_cmd.py` 的呼叫路徑改成新的 `CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py` 即可。queue.json schema 不變（向後相容 `Executed` 欄位仍可解析）
- **不想用 Watcher**：在 `UCL_AgentCommandsPage` 把 `Auto-Watcher` toggle 關掉（或設 EditorPrefs `UCL.AgentCmd.Watcher.Enabled = false`），系統退回為純手動模式

## 相關文件

- 完整工作流（外層專案視角）：`docs/Workflows/AgentCommands_Workflow.md` §8a.0
- 多語系 Page 文件：`Docs~/{lang}/UCL_EditorPage/UCL_AgentCommandsPage.md`
- Python wrapper：[`Tools~/AgentCommands/run_cmd.py`](../Tools~/AgentCommands/run_cmd.py)
