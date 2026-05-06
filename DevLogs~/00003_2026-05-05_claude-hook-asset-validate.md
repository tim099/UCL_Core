---
date: 2026-05-05
index: 00003
title: Claude Code Hook 自動化 — UCL_Asset 驗證從 SOP 升級為強制門檻
tags: [feature, docs]
---

# Claude Code Hook 自動化 — UCL_Asset 驗證從 SOP 升級為強制門檻

## What

新增 Claude Code 兩段 hook（`PostToolUse` + `Stop`），把上一輪 ([DevLog 00002](00002_2026-05-05.md)) 的「驗證 SOP」從**口頭規範**升級為**強制執行**：AI agent 改了任何 `UCL_Asset` JSON 後，turn 結束前必須通過 `Cmd_ValidateAssetFormat` 才能 stop。

新增元件：

| 檔案 | 角色 |
|---|---|
| `Tools~/AgentCommands/hook_validate_modified.py` | 兩段 hook 共用驅動 — `--mode post` 記 state + best-effort submit；`--mode stop` 等驗證、failure → exit 2 阻擋 stop |
| `Docs~/{4 langs}/Workflows/Hook_Setup_Workflow.md` | 給 UCL_Core 插件使用者的 settings.json 配置指南（zh-Hant 詳細版 + 3 langs stub）|

## Why

DevLog 00002 的 `Cmd_ValidateAssetFormat` 是「**能用**」的工具 — 但需要 AI 主動記得跑。實務上：

```
AI: 「我把 ManaCore_Shard 改好了」
人類: 「跑 ValidateAssetFormat 確認」
AI: 「..." (有時跑、有時忘)
```

Hook 把這個對話從**口頭依賴**換成**機械強制**：

```
AI 寫檔 → PostToolUse 自動 queue
... AI 繼續 ...
AI 嘗試 stop → Stop hook 自動驗證 → 任一失敗 → 阻擋並回報 → AI 必修
```

對於跨多檔 / 多 turn 的長 session，這個保險比「AI 自我提醒」可靠太多。

## How to use

### 你的專案用 UCL_Core？

把這段加進專案 `.claude/settings.json`（合併到既有 hooks）：

```json
{
  "hooks": {
    "PostToolUse": [{
      "matcher": "Edit|Write|MultiEdit",
      "hooks": [{
        "type": "command",
        "command": "python \"CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/hook_validate_modified.py\" --mode post 2>&1 || true",
        "timeout": 10
      }]
    }],
    "Stop": [{
      "hooks": [{
        "type": "command",
        "command": "python \"CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/hook_validate_modified.py\" --mode stop",
        "timeout": 180
      }]
    }]
  }
}
```

加 `.claude/state/` 到 `.gitignore`。完成。

> 完整指南（路徑調整 / failure 處理流程 / caveats / migration 場景的暫停方法）→ [Hook_Setup_Workflow](../Docs~/zh-Hant/Workflows/Hook_Setup_Workflow.md)

## 設計決策

### 1. 兩段，不是一段

- **PostToolUse 只負責 submit + 記 state**（best-effort，不阻塞當前 tool）
- **Stop 才真正阻擋**（讀 state、wait、判 verdict）

如果只用 PostToolUse 阻塞 → AI 改一個檔就要等 30~60s 完成；體驗極差。  
如果只用 Stop → AI 改了一堆檔才驗，失敗時要回頭追是哪一個。

兩段互補。

### 2. State file 跨 hook 傳遞

`PostToolUse` 把待驗證的 `(type, id, cmd_id)` 寫到 `.claude/state/pending_validations.txt`，`Stop` 讀回來。簡單檔案 IO，比訊號 / IPC / DB 都好維護。

### 3. Stop 失敗時 **不清** state file

下次 stop 自動重新驗證（AI 修了之後）。如果清了 → 下次 stop 看不到 pending → 直接放行 → 漏抓。

### 4. checkRefs 自動推導

依 assetType 給合理預設：
- `RCG_StoryData / RCG_QuestData / RCG_BattleSet` → `checkRefs=2`（引用鏈深）
- 其他 → `checkRefs=1`（直接引用）

避免 hook 設計者要為每種 asset 寫不同 hook。

### 5. Pattern 匹配走規則而非 type list

腳本用 `re.search(r"/UCL_Assets/(<Type>)/(<Id>)\.json$")` 自動萃取，不需要硬編碼專案 type 清單 — RCG / 任何 UCL_Game 上層專案都通用。

### 6. 跨平台 subprocess

Windows 預設 cp950 編碼讀 stdout 會撞到 ✓ ✗ 等 Unicode → 強制 `encoding="utf-8", errors="replace"`。

## Breaking changes

無。Hook 是 opt-in（要在專案 `.claude/settings.json` 配置才生效），既有上層專案不會被影響。

## Caveats

| Caveat | 說明 / Workaround |
|---|---|
| Editor 沒開 → wait timeout | 開 Unity Editor，下次 stop 自動重試 |
| Watcher 停用 → 同上 | `UCL_AgentCommandsPage` 開 `Auto-Watcher` toggle |
| 大量 batch migration（100+ 檔）→ stop 變慢 | 暫時 `mv .claude/settings.json .claude/settings.json.bak` 跑批次後復原 |
| 檔案剛改 race（Unity 還沒看到）| AI 修了會自動重驗，通常自然解決 |

## 相關文件

- [Hook_Setup_Workflow](../Docs~/zh-Hant/Workflows/Hook_Setup_Workflow.md) — 完整安裝指南
- [Cmd_ValidateAssetFormat API](../Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_ValidateAssetFormat.md) — 被 hook 呼叫的核心 Cmd
- [Validate_UCL_Asset_Workflow](../Docs~/zh-Hant/Workflows/Validate_UCL_Asset_Workflow.md) — 手動模式 SOP（沒設 hook 也能跑）
- [DevLog 00001](00001_2026-05-05.md) — Lock-file watcher 機制（hook 透過此驅動）
- [DevLog 00002](00002_2026-05-05.md) — Cmd_ValidateAssetFormat 起源案例
