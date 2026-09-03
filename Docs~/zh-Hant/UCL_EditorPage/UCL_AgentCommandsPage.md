---
title: UCL_AgentCommandsPage
description: 用於排隊、檢視、觸發儲存於 AgentCommands/queue.json 的 agent 指令的編輯器頁面。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AgentCommandsPage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-08-21
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# UCL_AgentCommandsPage

## 1. 概觀

`UCL_AgentCommandsPage` 是 **Agent Commands** 系統的編輯器 UI — 一條輕量管線：AI agent 把要做的編輯器動作寫進 JSON 檔，使用者（或 agent 間接）在 Unity Editor 內按按鈕觸發執行。

頁面位置：

- **程式檔**：`Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AgentCommandsPage.cs`
- **UCL Core 選單**：`Tools/UCL/Agent Commands/...`（由 [`UCL_AgentCommandRunner`](#5-相關類別) 提供）
- **專案入口（RCG / Emblem of Valor）**：`EditorMenu → Agent Commands` 按鈕

它是個薄薄的 IMGUI 殼，組合多個協作型別：

| 型別 | 角色 |
|---|---|
| `UCL_AgentCommand` | 一筆排隊指令的資料模型（Id / Type / Mode / Args / 執行結果） |
| `UCL_AgentCommandQueue` | 讀寫 queue.json + trigger 路徑 helpers（全 method 加 `agentId` overload — id 形狀 `<persona>` 或 `<persona>/<lane>` → `queues/<persona>/queue[-<lane>].json`；null → `queues/anonymous/`，見下方 §Multi-Queue Mode）|
| `UCL_AgentCommandTrigger` ★ | lock-file ops 封裝（Pending/Running/Idle 狀態機；File.Move 接手；全 method 加 `agentId` overload） |
| `UCL_AgentCommandWatcher` ★ | `[InitializeOnLoad]` + `EditorApplication.update` 1Hz；掃 `queues/<persona>/pending[-<lane>].trigger` 多 trigger，per-persona 並行 dispatch |
| `UCL_AgentCommandHandlerBase` | 所有 handler 的抽象基底 — 反射自動發現 |
| `UCL_AgentCommandRegistry` | 收集已發現的 handler，依 `CommandType`（大小寫不敏感）索引 |
| `UCL_AgentCommandRunner` | 非同步 runner — 分派前先 await `UCL_ModuleService.WaitUntilInitialized`；per-agent `IsRunningForAgent(agentId)` flag（HashSet）防同 agent 重入；finally 清 per-agent trigger |

## 2. 頁面佈局

```
┌─ TopBar ────────────────────────────────────────────────────────────┐
│ [Back] [Close] | UCL_AgentCommandsPage [Copy] [Refresh] [Run] [Open]│
├─ Queue 路徑 ────────────────────────────────────────────────────────┤
│ Queue: <repo>/AgentCommands/queues/<persona>/queue.json             │
├─ Watcher 狀態列 ★ ──────────────────────────────────────────────────┤
│ ☑ Auto-Watcher  ● Idle/Pending/Running  Last trigger: HH:MM:SS  [Simulate]│
├─ 統計 ──────────────────────────────────────────────────────────────┤
│ Total: 3 | OneShot: 1 | Repeatable: 2                               │
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
| `Run Pending Commands` | 呼叫 `UCL_AgentCommandRunner.Menu_RunPending()`（async）；約 1.5 秒後自動 refresh。**Auto-Watcher 啟用時通常不必手動按**。 |
| `Open Folder` | 直接打開 `AgentCommands/` 資料夾 |

> [!NOTE]
> 舊版的「Export Cmd Catalog」獨立按鈕已移除。改用方式：在 Add Command 區塊加一筆 `ExportCommandCatalog` Cmd（與其他 Cmd 走同一管線，內容一致）。

## 3a. Watcher 狀態列 ★ NEW

| 元素 | 意義 |
|---|---|
| `☑ Auto-Watcher` toggle | 啟 / 停用 `UCL_AgentCommandWatcher`（寫入 EditorPrefs `UCL.AgentCmd.Watcher.Enabled`）；停用時退回為純手動模式 |
| `● Idle / Pending / Running` 燈號 | 當前 trigger 檔狀態（Idle = 都沒有 / Pending = 有 `pending.trigger` / Running = 有 `pending.trigger.running`）|
| `Last trigger` | Watcher 最近一次接手 trigger 的時間（給除錯用）|
| `Simulate Trigger` 按鈕 | 手動寫一個 `pending.trigger`（驗證 watcher 是否在跑）|

## 3b. 區塊折疊（Tim 2026-08-21）

本頁的大區塊（佇列現況 / 新增指令 / 失敗紀錄 / 模板 / 歷史 / 提示）各自可折疊，
折疊鈕一律走 `UCL_GUILayout.Toggle`（▼/►），狀態存頁面 instance 的 `m_FoldDic`。

| 區塊 | 預設 |
|---|---|
| 📋 佇列現況（queue 路徑 / Watcher 狀態列 / 指令清單） | 展開 |
| ➕ 新增指令（指令下拉 + 表單） | 展開 |
| ❌ 失敗紀錄 | **有失敗時展開**，沒有就收合 |
| 模板 / 歷史 / 💡 提示 | 收合 |

> [!NOTE]
> **queue 選擇器刻意不可折疊** —— 它決定其他每個區塊在講哪條 queue，
> 收起來會讓底下所有讀數失去主詞。
>
> 折疊的標題列**收合時仍顯示摘要**（queue 統計、失敗筆數、模板/歷史筆數）。
> 收合把資訊一起藏掉的話，人得先展開才知道「這裡有沒有事」，那等於沒有折疊。

## 3c. ❌ 失敗紀錄面板（可補跑，Tim 2026-08-21）

失敗的 OneShot 自 2026-08-07 起會**即時出隊**（避免 queue 堵塞與副作用重放），
所以從 queue 清單上看不到它們。本面板列出 `<DataRoot>/_cmd_failed/<cmdId>.json` ——
**所有**失敗的 Cmd，不限於某一種。

| 檔案 | 內容 | 保存期 |
|---|---|---|
| `_cmd_results/<id>.json` | 機器可讀 verdict（**不含 Args**） | 3 天後自動清除 |
| `_cmd_errors/<id>.md` | 給人讀的完整 stack + Args | 永久 |
| `_cmd_failed/<id>.json` ★ | **結構化、可補跑**：Type / Mode / Args / error / queueId / 補跑痕跡 | 直到補跑或手動刪除 |

★ 由 `UCL_AgentCommandFailedStore` 在 Runner 的失敗分支寫入。
為什麼要第三份：前兩份都補跑不了 —— 一份沒有 Args，另一份是**給人讀的視圖**
（對人類視圖寫 parser 等於第二份真相源，格式一改就靜默壞掉）。

| 按鈕 | 行為 |
|---|---|
| `補跑` | 以**原本那條 queue**（紀錄的 `QueueId`）新增一筆新 cmd（新 id，Description 帶 `retry of <原 id>`）並立刻執行；原紀錄保留並累加 `RetryCount` |
| `填回表單` | 把 Type / Mode / Args 填回「新增指令」表單 —— 打錯參數那類失敗要**改完再跑**就走這裡 |
| `刪除` / `清除全部紀錄` | 只刪紀錄，不動任何 queue（全部清除有二段確認） |

> [!CAUTION]
> **補跑＝重新執行一次，副作用會重放** —— 酒館公告會重發（同 SHA 領兩次薪）、轉帳會重轉。
> 所以這裡只有人按的按鈕，**沒有自動重試**：`ensure_idle` 逾時那種失敗代表「沒送出」（重試安全），
> 但**送出之後**的失敗可能其實已經生效了，而兩者在畫面上長得一樣。按之前先確認原本那筆真的沒生效。

> [!IMPORTANT]
> **補跑會擋在「那條 queue 正在跑」的情況** —— Runner 開跑時把 queue 讀成記憶體清單、收尾時整批寫回，
> 期間任何 load→add→save 都會被覆蓋（lost update）。
> 🩸 首次驗收實測：從一個正在該 queue 執行的 Cmd 裡呼叫補跑，紀錄標成「已補跑」、log 印了新 cmd id，
> 而 queue.json 收尾後是空的、verdict 與錯誤報告都沒有 —— **補跑憑空消失且零錯誤訊息**。
> 現在的行為：先檢查 `IsRunningForAgent`，寫入後**回讀驗證新 id 在不在**，驗不到就不標記補跑。

> [!NOTE]
> 本 store 是 2026-08-21 才加的 ⇒ **之前的失敗沒有結構化紀錄，補跑不了**。
> 標題列會另外顯示那個筆數（由 `_cmd_errors/` 數出來），刻意不把「不能補」畫成「沒有失敗」。

## 3d. 選定指令後自動填入範例值（Tim 2026-08-21）

在「新增指令」下拉切換指令時，Args 欄位會**自動填入該 handler 的 `ExampleArgs`**
（沒有宣告範例則清空）—— 換了指令，欄位裡的舊 args 就屬於別的指令了，留著比清空更糟：
它看起來像一組有效參數。「填入範例」按鈕保留（改壞了想退回範例時用）。

> [!NOTE]
> 從模板 / 歷史 / 失敗紀錄「填回表單」時**不會**被範例值蓋掉 ——
> 那些路徑會同步自動填入的偵測基準（否則下一幀就被覆寫，而那看起來像「Apply 沒生效」）。

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

> [!IMPORTANT]
> **新增／修改 Cmd 後請同步 schema**（Tim 2026-07-29 拍板）。Python client 的參數預檢依據
> `<RepoRoot>/AgentCommands/commands_schema.json`，那是 C# 反射 handler `ArgsSpec` 生成的產物。
> 三個等價入口：
> 1. 控制台 → **🧾 Cmd 後台** → 「重新生成 commands_schema.json」
> 2. `senate ucmd run ExportCmdSchema`
> 3. 編譯完成自動檢查（**每台機器每天最多一次**）
>
> 忘了同步不會壞掉 —— Python 端比對內容雜湊，不符就把參數預檢**自動降級為不擋**。
> 詳見 [`UCL_AgentCommand_Architecture` §5.1](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)。

## 4a. 🧾 Cmd 後台管理頁（UCL_AgentCmdAdminPage）

入口：`UCL_ControlPanelPage` → **🧾 Cmd 後台** → 「開啟 Cmd 後台管理頁」。

| 區塊 | 內容 |
|---|---|
| 🔄 Cmd Schema 同步 | 同步狀態（✅ 已同步 / ⚠ 未同步）、手動重新生成按鈕、產物路徑與雙方 hash、每日自動同步的上次檢查時間 |
| 🧾 已註冊 Cmd | reflection 掃到的全部 handler，標示各自有無宣告 `ArgsSpec`（無 = 不做 client 預檢，屬合法狀態） |

與 `Cmd_ExportCmdSchema` **等價** —— 兩者呼叫同一個 `UCL_CmdSchemaExporter.Export()`，
產出逐字相同、內容未變則不寫檔（產物入 git，避免製造 diff 噪音）。

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
- [`UCL_AgentCommand_Architecture`](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md) §8.1 — **Multi-Queue：persona 資料夾制**（Tim 2026-08-01 拍板 `--persona` + `queues/<persona>/` 隔離，取代 05-13 的平鋪檔名制）
- `Workflows/HelpURL_Workflow.md`（本 repo） — `ucl_core:` / `eov_docs:` URL 機制

## 9. 陷阱

> [!CAUTION]
> **不要寫 `Register(...)` 呼叫** 像舊 RCG 版那樣。新 registry 純反射驅動 — `UCL_AgentCommandRegistry` 上根本沒有 `Register` 方法。

> [!IMPORTANT]
> Editor-only。整套系統包在 `#if UNITY_EDITOR` 內，runtime 程式碼路徑不得引用。
