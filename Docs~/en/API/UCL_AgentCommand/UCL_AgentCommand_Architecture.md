---
title: UCL Agent Command 系統整體架構
description: AI agent 與 Unity Editor 的跨 process 指令系統 — 自動發現 / 反射註冊 / async 執行 / 多種觸發方式（UI / queue.json / Python / batchmode）
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-05
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# 🤖 UCL Agent Command 系統整體架構

> **一句話**：讓 AI agent（在 Unity 外）對 Unity Editor 內的工具呼叫 RPC — agent 寫 `queue.json`、人類（或 batchmode）按 Run Pending、async runner 確認模組系統就緒後依序分發到對應 Handler。

---

## 1. 系統定位

UCL Agent Command 解決的問題：**AI agent 沒有 Unity 環境**，但需要呼叫 Unity 內的工具（編輯器頁面 / 模組系統 / Asset 資料庫）才能完成許多開發工作（解析 asset 依賴、匯出 markdown 目錄、執行批次資產處理⋯）。

**設計取捨**：
- ✅ 跨 process 通訊用**檔案系統**（`queue.json`）— 最簡單、可審計、可離線編輯
- ✅ Handler 由**反射自動發現** — 新增指令零樣板（一個 class 即可）
- ✅ Async 執行 + 等待模組系統就緒 — 避免 race condition
- ❌ 不做 socket / IPC — 部署複雜度爆炸
- ❌ 不做 schedule（cron）— 重複任務改用 Repeatable + 使用者觸發

---

## 2. 元件圖

```
┌─────────────────────────────────────────────────────────────────────┐
│  AI Agent (Claude / GPT / human)                                    │
│      │                                                              │
│      │ 1) 寫指令到 queue                                              │
│      ↓                                                              │
│   AgentCommands/queue.json                                          │
│      │                                                              │
│      ↓ 2) 觸發（4 種方式）                                            │
└──────┬──────────────────────────────────────────────────────────────┘
       │
       │   ┌──────────────────────────────────────────────────┐
       ├──→│ a) UCL_AgentCommandsPage（Editor IMGUI）          │
       ├──→│ b) Tools/UCL/Agent Commands/Run Pending（Menu）    │
       ├──→│ c) Tools~/AgentCommands/run_cmd.py（Python CLI）   │
       └──→│ d) Unity batchmode -executeMethod（headless CI）  │
           └──────────────────────────────────────────────────┘
                                │
                                ↓
                   UCL_AgentCommandRunner.Menu_RunPending()
                                │
                                │ 3) await UCL_ModuleService.WaitUntilInitialized
                                ↓
                   依序處理 queue.Commands
                                │
                                │ 4) 依 Type 查 Registry
                                ↓
                   UCL_AgentCommandRegistry.Get(type)
                                │
                                │ 5) 呼叫對應 Handler
                                ↓
                   handler.ExecuteAsync(args, token)
                                │
                                │ 6) 寫回 queue（OneShot 移除 / Repeatable RunCount++ / 失敗記錯誤）
                                ↓
                   AgentCommands/queue.json （更新後）
```

---

## 3. 核心類別速查

| 類別 | 路徑 | 角色 |
|---|---|---|
| `UCL_AgentCommand` | `UCL_AgentCommand.cs` | 單一指令的資料模型（`Id` / `Type` / `Mode` / `Args` / `LastRunResult` / 等）— 對應 queue.json 一筆 |
| `UCL_AgentCommandQueue` | `UCL_AgentCommandQueue.cs` | queue.json 的讀寫 helper（`Load()` / `Save()` / `GetQueuePath()`）|
| `UCL_AgentCommandRunner` | `UCL_AgentCommandRunner.cs` | 主執行器；含 `[MenuItem] Tools/UCL/Agent Commands/Run Pending` 入口 |
| `UCL_AgentCommandRegistry` | `UCL_AgentCommandRegistry.cs` | 反射發現所有 `UCL_AgentCommandHandlerBase` 子類；`Get(type)` / `ListHandlers()` |
| `UCL_AgentCommandHandlerBase` | `UCL_AgentCommandHandlerBase.cs` | **新增指令的擴充點** — 抽象基底，子類覆寫 `CommandType` + `ExecuteAsync()` |
| `UCL_AgentCommandsPage` | `UCL_EditorMenuPages/UCL_AgentCommandsPage.cs` | Editor IMGUI 頁面（人類友善 UI）|

---

## 4. 指令生命週期

### 4.1 OneShot（預設）

```
[1] Agent 寫進 queue → Executed=false, LastRunResult=null
[2] Run Pending 觸發 → runner 跑 ExecuteAsync
[3a] 成功 → 從 queue 移除（不留紀錄；agent 看 queue 沒這筆即知 ✓）
[3b] 失敗 → 留在 queue，LastRunResult="Failed"，LastRunError=詳情
```

### 4.2 Repeatable

```
[1] Agent 寫進 queue → RunCount=0
[2] Run Pending 觸發 → 跑一次
[3a] 成功 → RunCount++ ，留在 queue 裡，LastRunResult="Success"
[3b] 失敗 → 同 OneShot 失敗（留 queue + 錯誤訊息）
[4] 下次 Run Pending → 又跑一次（RunCount++）
```

### 4.3 失敗的指令會留在 queue

刻意設計 — agent 看到失敗指令還在，可以：
1. 看 LastRunError 修問題
2. 改 Args 或修 Handler，重新 Run Pending（同一筆繼續嘗試）
3. 確認沒救 → 從 queue.json 手動刪除

---

## 5. 自動發現 Handler

`UCL_AgentCommandRegistry` 的 static ctor 透過 `AssemblyExtensions.GetAllSubclass(typeof(UCL_AgentCommandHandlerBase))` 掃描全部 assembly，反射建立每個非抽象子類的 instance。**新增指令零樣板** — 寫一個 class 繼承基底就會被自動註冊。

```csharp
public class Cmd_MyCustom : UCL_AgentCommandHandlerBase
{
    public override string CommandType => "MyCustom";
    public override string ShortDescription => "Description shown in UI dropdown.";
    public override string ArgsSchema => "key1=描述\nkey2=描述";
    public override string HelpURL => "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_MyCustom.md";

    public override async UniTask ExecuteAsync(Dictionary<string,string> args, CancellationToken token)
    {
        // ... 你的邏輯
        await UniTask.CompletedTask;
    }
}
```

**重要**：
- `CommandType` 必須唯一（相同會 LogError 並覆蓋既有）
- `CommandType` 大小寫不敏感（queue.json 寫成 `"myCustom"` 也會 match）
- 撞名 → registry 會 LogError + 用後寫入者覆蓋前者

---

## 6. 內建指令（持續擴充中）

> 完整最新清單請走 [`Cmd_ExportCommandCatalog`](Cmd_ExportCommandCatalog.md) 自動產出 → `AgentCommands/commands_catalog.md`

| Cmd Type | Mode | 用途 | 來源 |
|---|---|---|---|
| `DebugLog` | Repeatable | 印 `Args["msg"]` 到 Console（連線測試 / 範例）| UCL_Core |
| `ResolveAssetReferences` ⭐ | OneShot | 批次解析 UCL_Asset 連動鏈（BFS + 反射 + maxDepth + 去重）| UCL_Core |
| `ExportCommandCatalog` ⭐ | OneShot | 匯出當前所有已註冊 Handler 到 markdown 目錄 | UCL_Core |
| `ExportEquipmentNotes` | OneShot | 匯出 Equipment Note 到 docs/Catalogs/ | RCG (專案層) |
| `ExportCardNotes` | OneShot | 匯出 Card Note | RCG |
| `ExportItemNotes` | OneShot | 匯出 Item Note | RCG |
| `ExportAllNotes` | OneShot | 上面三個一次跑完 | RCG |
| `Ping` | Repeatable | 印 `Args["msg"]`（與 DebugLog 平行的 RCG 端範例）| RCG |

**架構分層**：
- **UCL_Core 層**（本文件覆蓋範圍）— 框架本身 + 通用指令（DebugLog / ResolveAssetReferences / ExportCommandCatalog）
- **RCG 專案層** — 專案特定指令（Export*Notes / Ping）住在 `Assets/Scripts/RCG_Scripts/RCG_AgentCommands/`

---

## 7. 觸發方式對照

| # | 方式 | 自動化 | 適用 | 啟動延遲 |
|---|---|---|---|---|
| 1 | `UCL_AgentCommandsPage` UI 內 **Run Pending** 按鈕 | 半 | 人類 | 即時 |
| 2 | `Tools/UCL/Agent Commands/Run Pending` Editor 選單 | 半 | 人類 | 即時 |
| 3 | 直接編輯 `queue.json` + 上面任一觸發 | 半 | Agent + 人類點按鈕 | 即時 |
| 4 | **Python 包裝器** [`Tools~/AgentCommands/run_cmd.py`](../../../../Tools~/AgentCommands/run_cmd.py) ⭐ | 半（Editor 必須開）| **Agent CLI 推薦** | 即時 |
| 5 | **Unity Batchmode** `-batchmode -executeMethod` | **全** | CI / 排程 | ~30 秒（啟 Unity）|

### Python 包裝器範例

```bash
# submit + wait（適合 Agent CLI）
python Tools~/AgentCommands/run_cmd.py run ResolveAssetReferences \
    --arg assetType=RCG_StoryData --arg assetIds=AbandonedTemple \
    --arg maxDepth=3 --arg format=md \
    --output-file CardGame/AgentCommands/asset_refs_AbandonedTemple.md

# 列 queue
python Tools~/AgentCommands/run_cmd.py list

# 顯示 catalog
python Tools~/AgentCommands/run_cmd.py catalog
```

### Unity Batchmode 範例（CI / 排程）

```powershell
"C:\Program Files\Unity\Hub\Editor\6000.0.60f1\Editor\Unity.exe" `
    -batchmode -nographics `
    -projectPath "D:\Unity\EmblemOfValor" `
    -executeMethod UCL.Core.EditorLib.AgentCommands.UCL_AgentCommandRunner.Menu_RunPending `
    -quit -logFile -
```

---

## 8. queue.json Schema

```json
{
  "Commands": [
    {
      "Id": "yyyyMMdd-HHmmss-uuid-typeslug",
      "Type": "<CommandType>",
      "Mode": "OneShot | Repeatable",
      "Executed": false,
      "Args": { "key": "value", ... },
      "CreatedAt": "ISO 8601 UTC",
      "LastRunAt": null,
      "LastRunResult": null,
      "LastRunError": null,
      "Description": "agent 提供的人類友善註解",
      "RunCount": 0
    }
  ]
}
```

完整欄位語意見 [UCL_AgentCommand API](UCL_AgentCommand.md)。

---

## 9. Async 執行與模組系統

`UCL_AgentCommandRunner.Menu_RunPending()` 走 async：

```csharp
await UCL_ModuleService.WaitUntilInitialized();
// 從 queue 取一筆
foreach (var cmd in queue.Commands)
{
    try {
        var handler = UCL_AgentCommandRegistry.Get(cmd.Type);
        await handler.ExecuteAsync(cmd.Args, cancelToken);
        // 成功 → 寫回 queue
    } catch (Exception ex) {
        // 失敗 → LastRunError = ex.ToString()
    }
}
```

**為什麼要 await ModuleService**：
- UCL Modules（包含 `RCG_*Data` 等資產系統）需要時間掃描磁碟、註冊 type metadata
- Editor 啟動 / Domain reload 後第一次跑指令 → 模組可能還沒註冊
- WaitUntilInitialized 確保所有 `UCL_Asset<>.Util` 可用 → handler 內可放心呼叫

---

## 10. 設計擴充點

### 10.1 加新指令
寫一個 class 繼承 `UCL_AgentCommandHandlerBase`（[第 5 節](#5-自動發現-handler)）。建議：
- 放 UCL_Core 層 → 通用指令（如資產解析、目錄匯出）
- 放 RCG 專案層 → 專案特定（如 Export*Notes）

### 10.2 加新觸發方式
目前有 5 種；可擴充：
- File watcher：檢測 queue.json 變動 → 自動 RunPending（無須點按鈕）
- HTTP endpoint：Editor 開啟時起一個本機 HTTP server 接收 cmd
- WebSocket：雙向通訊（agent 即時收到 stdout / Debug.Log）

### 10.3 加新輸出 sink
目前 Cmd 把結果寫到檔案系統（`AgentCommands/<output>`）。可擴充：
- 寫到 stdout / stderr 由 batchmode log 接收
- 寫到 Editor PlayerPrefs（讓下個 Cmd 接力）

---

## 11. 已知限制

| 限制 | 解法 / 替代方案 |
|---|---|
| Editor 必須開著才能執行 | Batchmode（慢但全自動）|
| 不支援指令間相依（must-run-after） | 靠 `Commands[]` 順序保證 |
| `Args` 只支援 `Dictionary<string,string>` | 複雜物件用 JSON 字串塞進 value，handler 內自行 parse |
| 無排程（cron-like） | 重複任務改 Repeatable + 使用者觸發 |
| Domain reload 後 Registry 重建 | static ctor 每次 reload 都跑，自動處理 |
| 同名 CommandType 後者覆蓋 | LogError 提醒；確保命名唯一 |

---

## 12. 相關文件

### API 細節
- [UCL_AgentCommand](UCL_AgentCommand.md) — 資料模型
- [Cmd_DebugLog](Cmd_DebugLog.md) — 最簡範例
- [Cmd_ResolveAssetReferences](Cmd_ResolveAssetReferences.md) — 資產解析
- [Cmd_ExportCommandCatalog](Cmd_ExportCommandCatalog.md) — 目錄匯出

### 編輯器頁面
- [UCL_AgentCommandsPage](../../UCL_EditorPage/UCL_AgentCommandsPage.md) — IMGUI UI

### 工作流（專案層）
- [`docs/Workflows/AgentCommands_Workflow.md`](../../../../../../../docs/Workflows/AgentCommands_Workflow.md) — 專案層工作流（含完整觸發方式對照、新增指令 SOP、命名空間踩雷紀錄）

### 工具
- [`Tools~/AgentCommands/run_cmd.py`](../../../../Tools~/AgentCommands/run_cmd.py) — Python CLI 包裝器
