---
title: 編輯 → 重編 → 修錯迴圈工作流
description: 步驟化 SOP — agent / 工具開發者改完 .cs 後如何強制 Unity 重編、確認 compile error、迴圈修到 0 errors。建立在 Cmd_Recompile + UCL_CompileErrorTracker + run_cmd.py 之上。
source_root: AgentCommands/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [edit recompile loop, script edit loop, compile error fix loop, agent compile loop]
tags: [workflow, agent, compile, recompile, error-fix]
---

# 🔁 編輯 → 重編 → 修錯迴圈工作流

> [!IMPORTANT]
> 本工作流負責「**改完 .cs 後怎麼確認真的編進去 + 沒踩 compile error**」這條 SOP。
> agent 改完檔不要假設 Unity 已 reload — 在 Editor 沒有焦點 / Auto Refresh 關閉的情況，
> 你寫的 code 可能根本還沒進 assembly，後續 Cmd 全部跑舊版。

> 設計哲學：**強制同步點**。`Cmd_Recompile` + Python `recompile` 子命令是「**這之前的所有 .cs 變動都已被反映**」的承諾邊界。

---

## 0. TL;DR — 五分鐘吃懂迴圈

```
[1] 編輯 / 生成 .cs 檔（Edit / Write）
       ▼
[2] senate ucmd run Recompile     ← 觸發 Unity 重編 + 等到完成
       │
       ├── exit 0  → clean，繼續後續流程
       └── exit 1  → 有 compile error
              ▼
[3] 讀 AgentCommands/.compile_status.json 的 messages
       ▼
[4] 對每個 error 看 file:line 修源
       ▼
[5] goto [2]（最多 N 輪，建議 ≤ 5；超過代表方向錯，叫人類）
```

> [!IMPORTANT]
> **`compile clean ≠ runtime clean`**。改完 code 跑遊戲時可能還會炸（NullReferenceException / MissingReferenceException 等）— 那些錯**不在** `.compile_status.json`，而在專案各自的 runtime error log。
>
> EOV 專案：見 [`docs/Workflows/RuntimeError_Diagnose_Workflow.md`](docs/Workflows/RuntimeError_Diagnose_Workflow.md)（讀 `CardGame/Assets/DebugLogs/Errors_latest.log`）。
> 別專案：依自家 logger 慣例。

---

## 1. 前置條件（每個 session 開始檢一次）

| # | 檢查項 | 怎麼確認 | 沒過怎辦 |
|---|---|---|---|
| 1 | Unity Editor 開著 | 系統工作列 / 視窗能看到 | 開 Unity，載入專案 |
| 2 | Auto-Watcher 啟用 | UCL_AgentCommandsPage 看 `Auto-Watcher ✔` | 點 checkbox 切到 ✔ |
| 3 | `run_cmd.py` 可呼叫 | `python <路徑> --help` 印出 usage | 修 PATH / 確認 Python 安裝 |
| 4 | `.compile_status.json` 存在 | `AgentCommands/.compile_status.json` | 在 Unity 觸發過一次 compile（任意改檔再儲存） |

> [!CAUTION]
> Auto-Watcher 若 Idle，所有 Cmd 會卡 pending。沒啟用就**用不了** `recompile` 子命令。

---

## 2. 為什麼要強制走 `recompile`？

agent 寫完 `.cs` 後 Unity 不一定立刻編譯：

| Unity 狀態 | 行為 |
|---|---|
| Editor 有焦點 + Auto Refresh ON | 立刻 detect file change → 編譯（最理想） |
| Editor 在背景 + Auto Refresh ON | 焦點回來才 detect（agent 角度看不到此時機） |
| Auto Refresh OFF | 完全不會自動編譯，得手動 Ctrl+R |
| 上一次 compile 失敗 | 卡在錯誤狀態，新 Cmd handler 載入不進來 |

**結論**：agent 改完 `.cs` **不能假設**修改已生效。必跑 `recompile` 強制同步，並從 exit code 確認 0 errors。

---

## 3. 核心迴圈（pseudocode）

```python
import subprocess, json
from pathlib import Path

RUN_CMD = "CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py"
STATUS  = Path("AgentCommands/.compile_status.json")

def recompile_and_check() -> tuple[int, list]:
    """returns (errors_count, messages); errors_count==0 → clean"""
    rc = subprocess.run(["python", RUN_CMD, "recompile"], capture_output=False)
    if rc.returncode == 0:
        return 0, []
    if rc.returncode == 1:
        st = json.loads(STATUS.read_text(encoding="utf-8-sig"))
        return st["total_errors"], [m for m in st["messages"] if m["type"] == "Error"]
    raise RuntimeError(f"infra failure: exit code {rc.returncode}")

# 主迴圈
MAX_ROUNDS = 5
for round_idx in range(MAX_ROUNDS):
    edit_files(...)            # agent 改 / 生成 .cs
    err_count, errors = recompile_and_check()
    if err_count == 0:
        break
    for e in errors:
        print(f"× {e['file']}:{e['line']}  {e['message']}")
        fix_error(e)            # 讀源 + Edit
else:
    raise RuntimeError(f"still {err_count} errors after {MAX_ROUNDS} rounds — STOP, ask human")
```

---

## 4. 詳細步驟

### 4.1 編輯 / 生成 .cs 檔
- 用 Edit / Write 工具改源
- **不要**手動建 `.meta`（Unity 自動生成；見 memory `feedback_no_direct_meta.md`）
- 多檔變動可一次改完，最後再 recompile（避免每 1 檔跑 1 次）

### 4.2 觸發 recompile
```bash
senate ucmd run Recompile
```

**Exit code 對照**：

| exit | 意義 | 行動 |
|---|---|---|
| 0 | compile 完成、0 errors | 繼續 |
| 1 | compile 完成、有 errors | 進 4.3 修錯 |
| 2 | Cmd_Recompile 沒被 Unity 接手（queue 沒清） | 檢前置 §1 — Watcher / Editor 狀態 |
| 3 | `.compile_status.json` 解析失敗 | 檔案損毀 / 編碼問題 |
| 4 | mtime 沒推進（compile 沒跑） | UCL_CompileErrorTracker 沒掛上事件？看 [CompileError_Diagnose_Workflow](CompileError_Diagnose_Workflow.md) |

### 4.3 讀錯誤訊息
- stdout 印前 5 條
- 完整：`AgentCommands/.compile_status.json` 的 `messages` 陣列，欄位：
  - `type`: `"Error"` / `"Warning"`
  - `file`: 相對路徑（從 Unity project root 算起）
  - `line`: 行號
  - `column`: 欄號
  - `message`: 錯誤文字（含 CS 編號，如 `CS0103: ...`）

### 4.4 修錯
對每個 error：
1. **開源**：用 Read 工具看 `file:line` 的程式碼上下文（前後 ±10 行）
2. **判錯**：對照常見 CS 錯誤（見 [CompileError_Diagnose_Workflow §常見錯誤](CompileError_Diagnose_Workflow.md)）
3. **修源**：用 Edit 改最小範圍
4. **避免聯動破壞**：改 `RCG_X` 看是否有別處引用（先 Grep 一下）

### 4.5 回到 4.2
跑 recompile，確認該 error 消失（且沒引入新的 error）。

### 4.6 退出條件
- ✅ exit 0 → 進入後續工作流（如 ExportNotes / 測試 / commit）
- ❌ 連續 5 輪仍有 error → **停下來**，把錯誤列表 + 你嘗試過的 fix 給人類接手。盲目改下去只會越搞越糟。

---

## 5. 故障模式對照

| 症狀 | 可能原因 | 排查 / 修法 |
|---|---|---|
| `recompile` exit 2，queue 卡 Recompile cmd | Auto-Watcher 沒啟用 | 開 UCL_AgentCommandsPage，確認 `✔ Auto-Watcher` |
| `recompile` exit 4 | UCL_CompileErrorTracker 沒寫 status | 看 `Tracker just loaded, no compile event captured yet` placeholder；任意改檔觸發一次 compile 即可 |
| 同一 error 改了沒消 | Unity 沒重編到目標 file | 確認 file 真的存了；再跑 `recompile` |
| 改 file A 卻 file B 報錯 | namespace / asmdef 隔離；CS0246 缺 using | 看 [CompileError_Diagnose_Workflow §asmdef](CompileError_Diagnose_Workflow.md) |
| 反覆引發新 error | 改源時 break 了 contract | 退回原版 + 重新規劃；可能該停下找人類 |
| `recompile` 跑了但內容沒生效 | 改的檔在 `_Editor` 子模組 / 在 `Editor/` 子目錄 | 對應 asmdef 是否 dirty + script type 是否 Editor-only |

---

## 6. 跟其他工作流的關係

```
   Create_EditorPage_Workflow          建立新 page
   Create_Cmd_Workflow                 建立新 Cmd
              │
              ▼ 改完 .cs 後
   ┌──────────────────────────────────────┐
   │  Edit_Recompile_Loop_Workflow（本檔）│  ← 強制同步 + 修錯
   └──────────────────────────────────────┘
              │
              ▼ compile 0 errors 後
   後續：跑 Cmd_ExportNotes / 自動測試 / commit
   
   compile error 解析細節：見 CompileError_Diagnose_Workflow
```

---

## 7. 使用範例

### 範例 A：agent 加新 Cmd 後驗證
```bash
# 1. 用 Edit / Write 建立 Cmd_Foo.cs
# 2. 觸發 recompile
senate ucmd run Recompile
# → 預期 exit 0；若 exit 1 看 compile_status.json 修錯後再跑

# 3. 確認新 Cmd 已註冊
senate ucmd run ExportCommandCatalog | grep "Foo"

# 4. 跑新 Cmd
senate ucmd run Foo --arg x=1
```

### 範例 B：agent 重構某個 EditorPage 後驗證
```bash
# 1. 改 RCG_StoryDataEditorPage.cs
# 2. recompile
senate ucmd run Recompile
# 3. 跑 ExportNotes 驗證輸出對齊
senate ucmd run ExportNotes --arg targets=story
# 4. 開檔目視 / git diff 比對
```

---

## 8. 驗收清單

agent 自我檢查（每輪結束時跑一次）：

- [ ] 最近一次 `recompile` exit 0
- [ ] `.compile_status.json` 的 `total_errors == 0`
- [ ] 改的目標 .cs 沒有殘留 `__DELETE_ME__` / `_Deprecated` 等暫時 marker
- [ ] 沒手動建任何 `.meta`
- [ ] 退出迴圈時 round 數 ≤ 5（超過代表卡死，不該繼續）

---

## 9. 相關文件

- [Create_Cmd_Workflow](Create_Cmd_Workflow.md) — 建立新 `Cmd_<Name>.cs`
- [Create_EditorPage_Workflow](Create_EditorPage_Workflow.md) — 建立新 `UCL_*Page`
- [CompileError_Diagnose_Workflow](CompileError_Diagnose_Workflow.md) — 細緻的 compile error 排查（asmdef / CS0246 等）
- [HelpURL_Workflow](HelpURL_Workflow.md) — `[HelpURL]` prefix 解析
- `run_cmd.py` — Python CLI 包裝器（`recompile` / `run` / `submit` / `wait` / `catalog`）
- `Cmd_Recompile` — Editor 端觸發重編的 Agent Command
