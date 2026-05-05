---
title: Cmd_ExportCommandCatalog API
description: 匯出當前 UCL_AgentCommandRegistry 中所有已註冊 Handler 為單一 Markdown 目錄；與 UCL_AgentCommandsPage 的「Export Cmd Catalog」按鈕等價，讓 AI agent 也能透過 queue.json 觸發
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Cmd_ExportCommandCatalog.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-05
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# Cmd_ExportCommandCatalog

## 1. 概覽

把 `UCL_AgentCommandRegistry.ListHandlers()` 內所有 Handler 一次性匯出成單一 Markdown 檔，讓 AI agent 用一次 Read 就掃過全部可用指令；與 [UCL_AgentCommandsPage](../../UCL_EditorPage/UCL_AgentCommandsPage.md) 的 **Export Cmd Catalog** 按鈕**共用** `RenderCatalogMarkdown()` 靜態方法（DRY），確保兩處輸出一致。

典型用途：
- AI agent 啟動工作前先跑一次 → 拿到完整指令目錄 → 才知道有哪些 Cmd 可用
- 文件系統定期匯出 → 給專案 docs 同步最新指令清單

## 2. 參數格式 (Args Schema)

| 參數 | 必填 | 預設 | 說明 |
|---|:-:|---|---|
| `outputPath` | ❌ | `AgentCommands/commands_catalog.md` | 輸出檔案路徑（**相對 Unity project root，即 `CardGame/`**）→ 預設實際落在 `CardGame/AgentCommands/commands_catalog.md` |

## 3. 輸出格式

### 3.1 Frontmatter
```yaml
---
title: Agent Commands Catalog (Auto-Generated)
generated: 2026-05-05T12:34:56
total_commands: 8
source: Cmd_ExportCommandCatalog or UCL_AgentCommandsPage "Export Cmd Catalog" button
---
```

### 3.2 速查表（表格）
列出所有 `(CommandType, ShortDescription, ArgsSchema)`，一行一個 Handler。

### 3.3 詳細 Metadata
每個 Handler 一個 `### CommandType` 區塊，含：
- Handler 完整 Class 名稱 + Assembly
- ShortDescription / HelpURL
- Args Schema（fenced code block）
- 直接可複製貼到 `queue.json` 的範例 JSON 區塊

### 3.4 觸發方式
列出 4 種觸發路徑（UI / queue.json / Python / batchmode），詳見 [UCL_AgentCommand_Architecture §7](UCL_AgentCommand_Architecture.md#7-觸發方式對照)。

## 4. 在 queue.json 中呼叫

```json
{
  "Id": "20260505-export-catalog",
  "Type": "ExportCommandCatalog",
  "Mode": "OneShot",
  "Args": {},
  "Description": "匯出當前所有已註冊的 Agent Command Handler 為 markdown 目錄"
}
```

或自訂輸出位置：
```json
{
  "Type": "ExportCommandCatalog",
  "Args": { "outputPath": "docs/AgentCommands_Catalog.md" }
}
```

## 5. Python 包裝器呼叫

```bash
python Tools~/AgentCommands/run_cmd.py run ExportCommandCatalog \
    --output-file CardGame/AgentCommands/commands_catalog.md
```

## 6. 與 Page 按鈕的關係

```
┌───────────────────────────────────┐
│  Cmd_ExportCommandCatalog         │
│  (queue.json 入口)                 │
│       │                            │
│       ↓                            │
└───────│───────────────────────────┘
        │
        │ ┌──────────── 共用渲染邏輯 ────────────┐
        ├→│ Cmd_ExportCommandCatalog                │
        │ │   .RenderCatalogMarkdown(handlers)      │
        │ │   → string Markdown                     │
        │ └─────────────────────────────────────────┘
        ↑
        │
┌───────│───────────────────────────┐
│  UCL_AgentCommandsPage             │
│  「Export Cmd Catalog」按鈕         │
└───────────────────────────────────┘
```

兩個入口 → 同一份輸出。

## 7. 關聯文件
- [UCL_AgentCommand_Architecture](UCL_AgentCommand_Architecture.md) — 系統整體架構
- [UCL_AgentCommand](UCL_AgentCommand.md) — 資料模型
- [UCL_AgentCommandsPage](../../UCL_EditorPage/UCL_AgentCommandsPage.md) — UI 觸發點
