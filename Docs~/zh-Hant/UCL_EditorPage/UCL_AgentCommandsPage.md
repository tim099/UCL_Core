---
title: UCL_AgentCommandsPage
description: 用於排隊、檢視、觸發儲存於 AgentCommands/queue.json 的 agent 指令的編輯器頁面。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AgentCommandsPage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-05-04
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# UCL_AgentCommandsPage

## 1. 概觀

`UCL_AgentCommandsPage` 是 **Agent Commands** 系統的編輯器 UI — 一條輕量管線：AI agent 把要做的編輯器動作寫進 JSON 檔，使用者（或 agent 間接）在 Unity Editor 內按按鈕觸發執行。

頁面位置：

- **程式檔**：`Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AgentCommandsPage.cs`
- **UCL Core 選單**：`Tools/UCL/Agent Commands/...`（由 [`UCL_AgentCommandRunner`](#5-相關類別) 提供）
- **專案入口（RCG / Emblem of Valor）**：`EditorMenu → Agent Commands` 按鈕

它是個薄薄的 IMGUI 殼，組合四個協作型別：

| 型別 | 角色 |
|---|---|
| `UCL_AgentCommand` | 一筆排隊指令的資料模型（Id / Type / Mode / Args / 執行結果） |
| `UCL_AgentCommandQueue` | 讀寫 `<repoRoot>/AgentCommands/queue.json` |
| `UCL_AgentCommandHandlerBase` | 所有 handler 的抽象基底 — 反射自動發現 |
| `UCL_AgentCommandRegistry` | 收集已發現的 handler，依 `CommandType`（大小寫不敏感）索引 |
| `UCL_AgentCommandRunner` | 非同步 runner — 在分派前先 await `UCL_ModuleService.WaitUntilInitialized` |

## 2. 頁面佈局

```
┌─ TopBar ────────────────────────────────────────────────────────────┐
│ [Back] [Close] | UCL_AgentCommandsPage [Copy] [Refresh] [Run] [...] │
├─ Queue 路徑 / 統計 ─────────────────────────────────────────────────┤
│ Queue: <repo>/AgentCommands/queue.json                              │
│ Total: 3 | Pending: 1 | Done: 1 | Repeatable: 1                     │
├─ Commands（queue.json 內容） ───────────────────────────────────────┤
│ ● [Pending] ExportEquipmentNotes (OneShot)            [Remove]      │
│ ● [Done]    Ping                  (Repeatable)        [Remove]      │
├─ Available Commands（從 Registry 自動列出） ────────────────────────┤
│ ExportEquipmentNotes  [查看說明] [+ OneShot] [+ Repeatable]         │
│   匯出全部 Equipment 的 Note / Description 為 Markdown              │
│   ▶ Args Schema                                                     │
│ Ping  [查看說明] [+ OneShot] [+ Repeatable]                         │
│   Sanity check — 把 args["msg"] 印到 Console                        │
│   ▶ Args Schema                                                     │
├─ Add Command（手動 fallback） ──────────────────────────────────────┤
│ Type: [已註冊型別 grid]                                             │
│ Schema: msg=任意字串（選填，預設 "pong"）                            │
│ Mode: ( ) OneShot  ( ) Repeatable                                   │
│ Description: [...]   Args: [k=v;k=v]                                │
│ [Add 'Ping' (OneShot)]                                              │
└─────────────────────────────────────────────────────────────────────┘
```

## 3. 頂端列操作

| 按鈕 | 行為 |
|---|---|
| `Refresh` | 從硬碟重新讀 `queue.json` 進記憶體快取 |
| `Run Pending Commands` | 呼叫 `UCL_AgentCommandRunner.Menu_RunPending()`（async）；約 1.5 秒後自動 refresh |
| `Open Folder` | 直接打開 `AgentCommands/` 資料夾 |

## 4. 如何新增一個指令類型

指令系統採 **convention-over-configuration**：寫一個繼承 `UCL_AgentCommandHandlerBase` 的 class 就好。下次 domain reload 時，registry 會反射自動發現。

```csharp
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands;
using UnityEngine;

namespace YourGame.AgentCommands
{
    public class Cmd_HelloWorld : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "HelloWorld";
        public override string ShortDescription => "在 Console 印一句問候。";
        public override string ArgsSchema => "name=要問候的對象";
        public override string HelpURL => "ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_AgentCommandsPage.md";

        public override async UniTask ExecuteAsync(
            Dictionary<string, string> args, CancellationToken token)
        {
            string name = args != null && args.TryGetValue("name", out var n) ? n : "world";
            Debug.Log($"[AgentCmd] Hello, {name}!");
            await UniTask.CompletedTask;
        }
    }
}
#endif
```

Unity 重新編譯後，`HelloWorld` 會自動出現在 **Available Commands** 清單上，含 `ShortDescription`、可折疊的 `ArgsSchema`，以及 `[查看說明]` 按鈕（若有設 `HelpURL`）。

> [!IMPORTANT]
> `CommandType` 比對**大小寫不敏感**，但在整個 AppDomain 中必須**唯一**。重複的 type 會 log error 並由後註冊者覆蓋。

## 5. 相關類別

| 類別 | 檔案 | 備註 |
|---|---|---|
| `UCL_AgentCommand` | `EditorCore/UCL_AgentCommands/UCL_AgentCommand.cs` | 資料模型 + `UCL_AgentCommandMode` enum（OneShot / Repeatable） |
| `UCL_AgentCommandQueue` | `EditorCore/UCL_AgentCommands/UCL_AgentCommandQueue.cs` | 手寫 JSON I/O（Unity `JsonUtility` 不支援 `Dictionary`） |
| `UCL_AgentCommandHandlerBase` | `EditorCore/UCL_AgentCommands/UCL_AgentCommandHandlerBase.cs` | 覆寫 `CommandType` + `ExecuteAsync` 即可，其餘皆選填 |
| `UCL_AgentCommandRegistry` | `EditorCore/UCL_AgentCommands/UCL_AgentCommandRegistry.cs` | static ctor 內掃 `typeof(UCL_AgentCommandHandlerBase).GetAllSubclass()` |
| `UCL_AgentCommandRunner` | `EditorCore/UCL_AgentCommands/UCL_AgentCommandRunner.cs` | 分派前 `await UCL_ModuleService.WaitUntilInitialized(token)` |

## 6. queue.json 結構

```json
{
  "Commands": [
    {
      "Id": "20260504-120000-helloworld",
      "Type": "HelloWorld",
      "Mode": "OneShot",
      "RunCount": 0,
      "Args": { "name": "Tim" },
      "CreatedAt": "2026-05-04T12:00:00.0000000Z",
      "LastRunAt": null,
      "LastRunResult": null,
      "LastRunError": null,
      "Description": "（選填）agent 留下的人類可讀備註"
    }
  ]
}
```

| 欄位 | 意義 |
|---|---|
| `Id` | 唯一識別碼；慣例為 `yyyyMMdd-HHmmss-<typelower>` |
| `Type` | 必須對應已註冊 handler 的 `CommandType`（大小寫不敏感） |
| `Mode` | `"OneShot"`（成功後直接從 queue 移除）或 `"Repeatable"`（每次都跑） |
| `RunCount` | 成功執行的次數，由 runner 累加。OneShot 在 RunCount 增至 1 之前就已被移除，因此此欄位主要對 Repeatable 有意義。 |
| `Args` | 自由格式 `string→string` map，傳給 `ExecuteAsync` |
| `LastRun*` | runner 寫入；`Result` 為 `"Success"` / `"Failed"` |

## 7. 初始化合約

Runner 在分派任何 handler 之前先呼叫 `UCL_ModuleService.WaitUntilInitialized(token)`。這個 API 同時負責**觸發**延遲初始化（透過存取 `UCL_ModuleService.Ins`）與**等待**完成 — 因此 handler 可以安全假設模組系統就緒、`UCL_Asset.Util.GetData()` 回傳非 null。

> [!NOTE]
> 若 handler 需要專案專屬的預熱（如 `RCG_IconSprite.InitSpriteAsset`），請在該 handler 內自行 `await`。框架 runner 不應反向依賴專案層。

## 8. 相關文件

- [`UCL_CommonEditorPage`](./UCL_CommonEditorPage.md) — 直接父類
- [`UCL_ModuleService_API`](../UCL_ModuleService/UCL_ModuleService_API.md) — 解釋 `WaitUntilInitialized`
- `Workflows/HelpURL_Workflow.md`（本 repo） — `ucl_core:` / `eov_docs:` URL 機制

## 9. 陷阱

> [!CAUTION]
> **不要寫 `Register(...)` 呼叫** 像舊 RCG 版那樣。新 registry 純反射驅動 — `UCL_AgentCommandRegistry` 上根本沒有 `Register` 方法。

> [!IMPORTANT]
> Editor-only。整套系統包在 `#if UNITY_EDITOR` 內，runtime 程式碼路徑不得引用。
