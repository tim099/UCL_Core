---
title: Claude Code Hook 設定指南（UCL_Core 插件使用者）
description: 在使用 UCL_Core 的上層專案配置 Claude Code hooks，自動觸發 ValidateAssetFormat — PostToolUse 早期警告 + Stop 強制驗收門檻；含 settings.json 範本、failure 處理流程、跨平台 caveat
last_updated: 2026-05-05
target_audience: [Tools_Maintainer, Gameplay_Programmer]
---

# 🔗 Claude Code Hook 設定指南

> [!IMPORTANT]
> **此文件給「使用 UCL_Core 插件的上層專案」**。配置完後，AI agent（透過 Claude Code）寫 / 改任何 `UCL_Asset` JSON 後，會自動觸發 schema + reference 驗證；turn 結束前未過驗證 → 阻擋 stop，逼 AI 修正。

## 0. 適用前提

- 使用 [Claude Code](https://docs.claude.com/en/docs/claude-code/overview)
- 專案內含 UCL_Core（無論放哪都行）
- Python 3.10+（hook 腳本依賴）
- Unity Editor 在開發時保持開啟（讓 `UCL_AgentCommandWatcher` 可以接手 trigger；詳見 [DevLog 00001](../../DevLogs~/00001_2026-05-05.md)）

> [!IMPORTANT]
> **本文件用 `<UCL_CORE>` 代表 UCL_Core 在你專案內的相對路徑**（相對 git root）。視專案結構而定，可能是：
> - `Assets/UCL/UCL_Core` — Unity 專案以 git root 為 Unity project root 時
> - `CardGame/Assets/UCL/UCL_Core` — Unity project 在 git root 下一層子目錄時（如 Emblem of Valor）
> - `UCL_Core` — 純工具 / 後端專案，UCL_Core 直接放 root
>
> 套用 settings.json 範本時請把 `<UCL_CORE>` 替換成你的實際路徑。

## 1. 兩段式設計

```
AI agent 寫 / 改 RCG_*Data JSON
        │
        ▼
┌──────────────────────────────────────────────────────────────┐
│ PostToolUse hook（best-effort, non-blocking）                  │
│   - matcher: Edit|Write|MultiEdit                              │
│   - 偵測 file_path 是否屬於 UCL_Asset                           │
│   - 是 → submit ValidateAssetFormat 到 queue + 記到 state file │
│   - 不影響當前 turn 速度（不等 wait）                           │
└──────────────────────────────────────────────────────────────┘
        │
        │ AI agent 繼續對話、可能改更多檔案
        │
        ▼
┌──────────────────────────────────────────────────────────────┐
│ Stop hook（blocking）                                          │
│   - 讀 state file 內所有待驗證 asset                            │
│   - 對每筆 wait Cmd 完成                                        │
│   - 任一 verdict ≠ PASS 或 reference_check == Missing →         │
│     exit 2 阻擋 stop，回報詳情給 AI                             │
│   - 全 PASS → 清空 state file，正常結束                         │
└──────────────────────────────────────────────────────────────┘
```

兩段共用 **同一支 hook 腳本**：[`<UCL_CORE>/Tools~/AgentCommands/hook_validate_modified.py`](../../Tools~/AgentCommands/hook_validate_modified.py)（其中 `<UCL_CORE>` 是 UCL_Core 在你專案內的相對路徑），靠 `--mode post` / `--mode stop` 區分。

## 2. 設定步驟

### 2.1 在專案根目錄建立 `.claude/settings.json`

合併到既有設定（保留現有 `permissions` 等）：

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write|MultiEdit",
        "hooks": [
          {
            "type": "command",
            "command": "python \"$CLAUDE_PROJECT_DIR/<UCL_CORE>/Tools~/AgentCommands/hook_validate_modified.py\" --mode post 2>&1 || true",
            "timeout": 10
          }
        ]
      }
    ],
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "python \"$CLAUDE_PROJECT_DIR/<UCL_CORE>/Tools~/AgentCommands/hook_validate_modified.py\" --mode stop",
            "timeout": 180
          }
        ]
      }
    ]
  }
}
```

> [!NOTE]
> **為什麼 PostToolUse 結尾有 `|| true`** — 那是 best-effort（不阻塞當前 tool 流程）；Stop 沒有 `|| true`，因為它**就是**要當失敗時 block。

> [!IMPORTANT]
> **路徑用 `$CLAUDE_PROJECT_DIR/...` 不是相對路徑** — Hook 執行時的 cwd 不保證是專案根（先前 Bash tool 用 `cd ...` 切過去後會持續生效）。寫成相對路徑會在 cwd 漂移時噴 `[Errno 2] No such file or directory` 路徑重複拼接。
> `$CLAUDE_PROJECT_DIR` 是 Claude Code 注入的環境變數，永遠指向專案根目錄。

### 2.2 加 `.claude/state/` 到 `.gitignore`

state file 記錄當前 session 內待驗證的 asset，是 transient 資料，不該進 git：

```gitignore
.claude/state/
```

### 2.3 把範本內的 `<UCL_CORE>` 替換成實際路徑

從 git root 看你的 UCL_Core 在哪：

```bash
# 在 git root 跑
find . -type d -name UCL_Core | grep -v Library | grep -v node_modules
```

用輸出的相對路徑（去掉開頭 `./`）取代 §2.1 範本的兩個 `<UCL_CORE>`。例：

| 找到的路徑 | 替換後的 hook command |
|---|---|
| `Assets/UCL/UCL_Core` | `python "$CLAUDE_PROJECT_DIR/Assets/UCL/UCL_Core/Tools~/AgentCommands/hook_validate_modified.py" --mode post ...` |
| `CardGame/Assets/UCL/UCL_Core` | `python "$CLAUDE_PROJECT_DIR/CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/hook_validate_modified.py" --mode post ...` |
| `UCL_Core` | `python "$CLAUDE_PROJECT_DIR/UCL_Core/Tools~/AgentCommands/hook_validate_modified.py" --mode post ...` |

### 2.4 確認 Python 路徑

Hook 腳本用 `python` 呼叫（依靠 `PATH`）。如果你的環境用 `python3` 或特定路徑：

```json
"command": "python3 \"$CLAUDE_PROJECT_DIR/<UCL_CORE>/Tools~/AgentCommands/hook_validate_modified.py\" --mode post 2>&1 || true"
```

### 2.5 腳本內部如何找 git root（自動，不必改）

腳本 `hook_validate_modified.py` 與 `run_cmd.py` 內部用以下三段優先序找 git root：

1. **環境變數 `CLAUDE_PROJECT_DIR`**（Claude Code hook 注入；最權威）
2. **從本檔位置往上找第一個含 `.git` 「資料夾」**（避開 submodule 的 `.git` file，這樣能跨「UCL_Core 是 submodule」的情境）
3. **fallback：使用 UCL_Core 本身為 root**（罕見，僅 UCL_Core 獨立用時）

所以**不論 UCL_Core 放在你專案的哪一層級，腳本本身不需要改**。只要 §2.1 settings.json 內的路徑正確指到腳本位置即可。

## 3. 行為示意

### 3.1 happy path（PASS）

AI 改了 `RCG_ItemData/NewItem.json`：

```
[validate] queued RCG_ItemData/NewItem (cmd_id=20260505-...)   ← PostToolUse
... AI 繼續對話 ...
[validate] RCG_ItemData/NewItem verdict=PASS reference_check=OK ✓   ← Stop
                                                                    ← turn 正常結束
```

### 3.2 schema fail（SchemaDiff）

AI 寫了 `LocalizeType: "Raw"`（已 deprecated）：

```
[validate] queued RCG_ItemData/NewItem (cmd_id=20260505-...)   ← PostToolUse
... AI 認為完工 ...
[validate] RCG_ItemData/NewItem verdict=SchemaDiff reference_check=OK ✗

Asset validation failed before turn end. ...
  ✗ RCG_ItemData/NewItem
      verdict: SchemaDiff, reference_check: OK
      report:  CardGame/AgentCommands/asset_format_check_RCG_ItemData_NewItem.md
...                                                              ← exit 2 阻擋 stop
                                                                  ← AI 收到 stderr 訊息，下一 turn 修正
```

### 3.3 reference fail（Missing）

AI 寫了不存在的 `Status: "ManaBoost"`：

```
[validate] queued RCG_ItemData/NewItem (cmd_id=20260505-...)
[validate] RCG_ItemData/NewItem verdict=PASS reference_check=Missing ✗

Asset validation failed before turn end. ...
  ✗ RCG_ItemData/NewItem
      verdict: PASS, reference_check: Missing
      report:  CardGame/AgentCommands/asset_format_check_RCG_ItemData_NewItem.md
...                                                              ← exit 2 阻擋 stop
```

## 4. Caveats

### 4.1 Editor 沒開時

Hook 仍會 submit Cmd 到 queue.json + 寫 trigger，但 Watcher 不在 → Stop hook 的 wait 會超時（90s）→ block stop 並報告 timeout。
對策：開 Unity Editor，下次 Stop 會自動重試。

### 4.2 Watcher 停用時

`UCL_AgentCommandsPage` 上 `Auto-Watcher` toggle 若被關掉，hook 會卡在 wait。同上，開 watcher 即可。

### 4.3 race：檔案剛改、Unity 還沒看到

Hook 在 `Edit` tool 完成後立刻 submit Cmd。Unity 偶爾可能在 AssetDatabase reimport 前就被 Cmd 讀到舊內容 → 給出過時的 verdict。
對策：Stop hook 失敗後 AI 會修正，下一 turn 會重新驗證；race 通常自然解決。若反覆出現，手動跑 `Tools/UCL/Agent Commands/Run Pending Commands` 強制重跑。

### 4.4 跨平台（Windows / macOS / Linux）

腳本內部用 `subprocess.run(..., encoding="utf-8", errors="replace")` 避免 Windows cp950 撞 ASCII 邊界。其他平台無調整需求。

### 4.5 大量檔案修改（migration / batch）

若一次 turn 改 100+ 個 asset，每個都 submit + wait 會明顯拖慢 stop。建議 migration 場景**暫時停用 hook**：

```bash
# 暫時 disable
mv .claude/settings.json .claude/settings.json.bak
# ... 跑你的批次工作 ...
mv .claude/settings.json.bak .claude/settings.json
```

或在 `/hooks` UI 內手動 toggle。

## 5. 手動繞過

特定情境想跳過驗證（如趕修 hotfix）：

| 方式 | 操作 |
|---|---|
| 單次 turn 跳過 | 刪除 `.claude/state/pending_validations.txt` |
| 整段對話跳過 | Claude Code 內 `/hooks` → 暫時 disable |
| 永久移除 | 刪 `.claude/settings.json` 內的 hooks 區段 |

## 6. 完整安裝檢核清單

- [ ] `.claude/settings.json` 含 PostToolUse + Stop hooks（§2.1）
- [ ] `.claude/state/` 在 `.gitignore`（§2.2）
- [ ] `python` 命令可在 shell 直接執行（§2.3）
- [ ] UCL_Core 路徑相對 git root = `CardGame/Assets/UCL/UCL_Core/`（§2.4）
- [ ] Unity Editor 已開啟 + `UCL_AgentCommandWatcher` 啟用（[DevLog 00001](../../DevLogs~/00001_2026-05-05.md)）
- [ ] 跑一次測試：用 Claude 讓它修一個既有 RCG_ItemData，觀察 stderr 是否出現 `[validate] queued ...`

## 7. 關聯文件

- [Cmd_ValidateAssetFormat API](../API/UCL_AgentCommand/Cmd_ValidateAssetFormat.md) — 被 hook 呼叫的核心 Cmd
- [Validate_UCL_Asset_Workflow](Validate_UCL_Asset_Workflow.md) — Cmd 觸發 SOP（手動模式）
- [DevLog 00001](../../DevLogs~/00001_2026-05-05.md) — Lock-file watcher 機制（hook 透過此機制驅動 Cmd）
- [DevLog 00002](../../DevLogs~/00002_2026-05-05.md) — Cmd_ValidateAssetFormat 起源案例（含 ManaCore_Shard 修法）
