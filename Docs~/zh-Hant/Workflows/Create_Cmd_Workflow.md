---
title: 建立新的 Agent Command Handler 工作流程
description: 步驟化 SOP — 從零生出一支可被 queue.json 觸發的 `Cmd_<Name>.cs`。內容覆蓋命名規範、檔案位置、metadata 欄位、ExecuteAsync 撰寫守則、Editor 驗收流程，以及常見地雷。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# 🛠️ 建立新的 Agent Command Handler 工作流程

> [!IMPORTANT]
> 本工作流只負責「**寫一個 `Cmd_<Name>.cs` 子類**」這件事；系統怎麼運作（queue / trigger / watcher / runner）請看 [UCL_AgentCommand_Architecture](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)。
>
> 設計哲學：**convention-over-configuration**。只要繼承 `UCL_AgentCommandHandlerBase` 並擺對位置，下次 domain reload 就會被 `UCL_AgentCommandRegistry` 反射自動發現 — **不要碰 Registry**。

---

## 0. TL;DR — 五分鐘加完一支 Cmd

```
[1] 想清楚 CommandType（PascalCase，AppDomain 全域唯一）
       ▼
[2] 選對位置（§2 決策樹）— UCL_Core 內 vs 下游模組
       ▼
[3] 開檔 Cmd_<Name>.cs：繼承 UCL_AgentCommandHandlerBase
       ▼
[4] 覆寫四個 metadata：CommandType / ShortDescription / ArgsSchema / HelpURL
       ▼
[5] 寫 ExecuteAsync(args, token)
       ▼
[6] 等 Unity domain reload → 開 UCL_AgentCommandsPage
       → Available Commands 應出現新指令 → Add + Run Pending → 看 Console
```

---

## 1. 前置決策

| # | 問題 | 影響 |
|---|------|------|
| 1 | **CommandType 命名** | PascalCase、動詞開頭、AppDomain 全域唯一；撞名會 LogError 由後者覆蓋 |
| 2 | **OneShot 還是 Repeatable** | 由 agent 寫 queue 時決定，handler 不寫死；ShortDescription 暗示用途 |
| 3 | **參數有哪些** | 只能用 `Dictionary<string,string>`；複雜物件塞 JSON 字串 |
| 4 | **歸屬模組** | UCL_Core 內 vs 下游模組（見 §2）|

### 1.1 命名規範速查

| 對象 | 範例 | 規則 |
|---|---|---|
| C# 類別 | `Cmd_DebugLog` | 前綴 `Cmd_` + PascalCase |
| 檔名 | `Cmd_DebugLog.cs` | 與類別名一字不差 |
| `CommandType` 屬性值 | `"DebugLog"` | 不含 `Cmd_` 前綴；queue.json 比對大小寫不敏感 |
| namespace（UCL_Core 內）| `UCL.Core.EditorLib.AgentCommands` | 框架層通用指令 |
| namespace（下游模組）| `<YourModule>.AgentCommands` | 例：`RCG.AgentCommands` |

> [!CAUTION]
> **嚴禁** 把 `Editor` 寫進 namespace 中段（如 `MyMod.Editor.AgentCommands`）。
> C# 在 `MyMod.*` 內向外解析時可能撞 `MyMod.Editor` 子命名空間，跟 `UnityEditor.Editor` 撞型別，引爆 CS0118。

---

## 2. 檔案位置決策樹

```
這支 Cmd 是…
├── 通用工具（檔案 I/O、UCL_Asset 操作、目錄匯出 — 不依賴任何下游型別）
│       → Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Cmd_<Name>.cs
│         namespace UCL.Core.EditorLib.AgentCommands
│
└── 下游模組特定（呼叫 RCG_*Data / 專案 EditorPage / 改特定遊戲資產）
        → 該模組的 AgentCommands 資料夾
          namespace <YourModule>.AgentCommands
```

### 2.1 為什麼要分層？

- **UCL_Core 是 submodule**，是可被任何使用 UCL 的 Unity 專案共用的框架；引用下游型別會破壞可移植性
- **下游模組**可放心 `using` 自家命名空間 / 呼叫專案 Editor API
- 判斷標準：**這支 Cmd 在沒有下游模組的純 UCL 專案有意義嗎？** 有 → UCL_Core；沒有 → 下游。

> [!TIP]
> 若不確定 → 先寫在下游模組；之後若發現可通用化再升級到 UCL_Core（搬 namespace + 移檔）。**反向降級** 比較痛苦（需要更新 reference）。

---

## 3. 標準範本（複製這份再改）

```csharp
// Handler: <CommandType> — <一句話說明這支 Cmd 在做什麼>
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 區塊職責：<這支 Cmd 的職責、何時被觸發、誰是預期使用者（agent / 人類）>
    /// 物理意義：<會修改的資產 / 寫入的檔案 / 對遊戲狀態的影響>
    /// 數值影響：<若無數值修改填「無」；有則具體說明影響範圍>
    /// </summary>
    public class Cmd_Example : UCL_AgentCommandHandlerBase
    {
        // CommandType 是 queue.json `"Type"` 欄位的比對基準；AppDomain 全域唯一。
        public override string CommandType => "Example";

        // ShortDescription 顯示在 UCL_AgentCommandsPage 的可搜尋下拉選項旁，
        // 也是 commands_catalog.md 的一行摘要 — 寫得清楚 agent 才能自我學習。
        public override string ShortDescription => "範例 Cmd — 把 args[\"msg\"] 印到 Console";

        // ArgsSchema 用 `key=說明` 格式，每行一個 key；無參數寫 "(無參數)"。
        public override string ArgsSchema =>
            "msg=要印出的字串（選填，預設 \"hello\"）";

        // HelpURL 用 ucl_core: prefix（UCL_Core 內）；下游模組可註冊自家 prefix。
        // 文件路徑帶 {lang} 佔位符，由 UCL_LocalizeService 自動替換為當前語系。
        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md";

        public override async UniTask ExecuteAsync(
            Dictionary<string, string> args, CancellationToken token)
        {
            // 區塊職責：解析並驗證 args
            // 物理意義：把字串字典轉成強型別參數，缺值或非法值立刻 throw
            // 數值影響：無；純讀
            string msg = args != null && args.TryGetValue("msg", out var m) ? m : "hello";

            // 區塊職責：實際邏輯
            // 物理意義：印一行 log 到 Console
            // 數值影響：無
            Debug.Log($"[AgentCmd] {CommandType} → {msg}");

            await UniTask.CompletedTask;
        }
    }
}
#endif
```

### 3.1 必填欄位一覽

| 欄位 | 型別 | 必填 | 說明 |
|---|---|---|---|
| `CommandType` | `string` | ✅ | queue.json `"Type"` 的比對基準；PascalCase；不含 `Cmd_` 前綴 |
| `ShortDescription` | `string` | ⚠強烈建議 | UI 下拉與 catalog 的一行摘要 |
| `ArgsSchema` | `string` | ⚠強烈建議 | `key=說明` 格式；無參數寫 `"(無參數)"` |
| `HelpURL` | `string` | 選填 | `ucl_core:` / 下游模組自註冊 prefix；缺則 Page 不顯示「查看說明」 |
| `ExecuteAsync` | `UniTask` | ✅ | 真正的邏輯入口；無 await 點時用 `await UniTask.CompletedTask` 收尾 |

---

## 4. ExecuteAsync 撰寫守則

### 4.1 進入點假設（runner 已幫你做的事）

`UCL_AgentCommandRunner` 在分派任何 handler 前**已經**：

1. ✅ `await UCL_ModuleService.WaitUntilInitialized(token)` — 模組系統就緒
2. ✅ 執行緒在 Unity main thread；可呼叫 `AssetDatabase` / `EditorUtility`
3. ✅ 可安全呼叫 `UCL_Asset<T>.Util.GetData(...)`（除非 ID 真的不存在）

> [!IMPORTANT]
> **不要在 handler 內再 `await UCL_ModuleService.WaitUntilInitialized`**，多餘且會掩蓋初始化邏輯。
> 但若需要模組特定預熱（例：`SomeModule.PreloadAssets()`），就在這支 handler 內自行 `await`，**不要**反向塞進框架 runner。

### 4.2 參數解析慣例

| 場景 | 寫法 |
|---|---|
| 必填 string | `args.TryGetValue("k", out var v)` → 缺則 `throw new ArgumentException(...)` |
| 選填 string + 預設 | `args.TryGetValue("k", out var v) ? v : "default"` |
| bool | `args.TryGetValue("k", out var v) && bool.TryParse(v, out var b) && b` |
| int | `args.TryGetValue("k", out var v) && int.TryParse(v, out var n)` |
| 列表 | `args["k"].Split(',')` 自行 split |
| 複雜物件 | JSON 字串塞進 value，handler 內 `JsonUtility.FromJson<T>(args["k"])` |

### 4.3 錯誤處理

- **參數錯** → `throw new ArgumentException($"[{CommandType}] ...")`，runner 抓住寫到 `LastRunError`
- **資產不存在** → `throw new InvalidOperationException(...)`，agent 看到能自我修
- **不可恢復** → 同上，**不要** 自己 catch 後吞掉；錯誤是 agent 的回饋訊號
- **token 取消** → 每個迴圈 iteration 開頭 `token.ThrowIfCancellationRequested()`

### 4.4 輸出檔的路徑慣例

> [!IMPORTANT]
> **路徑分離規則**：
> - `queue.json` → **git root** 的 `AgentCommands/queue.json`
> - Cmd 產出的檔案 → **Unity project root** 的 `AgentCommands/<output>.md`（不是 `Assets/`！）
> - Cmd 內部 `outputPath` 是相對 Unity project root 的相對路徑

範例輸出邏輯：

```csharp
string outputPath = args.TryGetValue("outputPath", out var p)
    ? p
    : "AgentCommands/default_report.md";
string fullPath = System.IO.Path.Combine(
    UnityEngine.Application.dataPath, "..", outputPath);
System.IO.File.WriteAllText(fullPath, content);
Debug.Log($"[AgentCmd] {CommandType} → wrote {fullPath}");
```

---

## 5. 驗收 SOP

### 5.1 Editor 內驗收（必跑）

1. **存檔等 Unity 編譯** — Console 沒紅字
2. **開頁面** — `Tools/UCL/Agent Commands/Open Page` 或專案入口進 [`UCL_AgentCommandsPage`](../UCL_EditorPage/UCL_AgentCommandsPage.md)
3. **找新 Cmd** — Available Commands 區塊應出現 `<CommandType> — <ShortDescription>`
4. **展開 Args Schema** — 確認顯示你寫的 schema 文字
5. **點「查看說明」** — 跳轉到 `HelpURL` 指的文件（沒設則此按鈕不顯示）
6. **Add 一筆測試** — Type 選你的 Cmd → Mode 選 OneShot → Args 填 `key=value;key=value`
7. **▶ Run Pending Commands** — 看 Console 是否有預期的 `[AgentCmd]` log
8. **OneShot 驗收**：成功則該筆從 queue 消失；失敗留在 queue + `LastRunError` 顯示錯誤訊息

### 5.2 Catalog 自動匯出驗收（選做）

跑一次 `ExportCommandCatalog`：

```
Add Command → ExportCommandCatalog → OneShot → Run Pending
→ 開 AgentCommands/commands_catalog.md
→ 確認新 Cmd 已列出，含 ShortDescription / ArgsSchema
```

### 5.3 Python wrapper 驗收（agent 角度）

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run <CommandType> \
    --arg key=value --timeout 60
```

OneShot 成功 → wrapper 印「✓ Cmd disappeared from queue → Success」。

---

## 6. 常見地雷

| # | 地雷 | 症狀 | 解法 |
|---|---|---|---|
| 1 | **忘了 `#if UNITY_EDITOR`** | Build 時 `UCL_AgentCommandHandlerBase` 找不到 | 整個檔案包進 `#if UNITY_EDITOR ... #endif` |
| 2 | **CommandType 撞名** | Console 出 `[UCL_AgentCommandRegistry] duplicate CommandType ...` | 重新命名；大小寫不敏感所以連 case 不同也算撞 |
| 3 | **namespace 中段含 `Editor`** | CS0118: 'Editor' is a namespace but is used like a type | 改用不含 `Editor` 中段的 namespace |
| 4 | **`ExecuteAsync` 沒 await 任何東西** | warning CS1998 | 結尾加 `await UniTask.CompletedTask;` |
| 5 | **參數錯誤自己 catch 掉** | runner 看不到錯，agent 以為成功 | 錯誤就 throw；runner 會寫到 `LastRunError` |
| 6 | **改完 .cs 直接按 Run 沒等編譯** | 跑的還是舊 handler | 等 Unity 右下角編譯動畫結束、Console 沒紅字再 Run |
| 7 | **輸出路徑寫成 `Assets/...`** | 檔案被 Unity Asset Database 收進去 | 輸出檔請落 `<UnityProjectRoot>/AgentCommands/` |
| 8 | **UCL_Core 端 Cmd 引用下游型別** | 破壞 submodule 可移植性 / 編譯失敗 | 通用化抽出，或把 Cmd 搬回下游模組（§2）|

---

## 7. 整合既有 EditorPage 邏輯（推薦模式）

> [!TIP]
> 大多數情境下 Cmd 不該重新發明輪子 — 若 Editor 內已有按鈕能做這件事，**直接呼叫該按鈕背後的 static method**。

```csharp
public override async UniTask ExecuteAsync(
    Dictionary<string, string> args, CancellationToken token)
{
    Debug.Log($"[AgentCmd] {CommandType} — invoking SomeEditorPage.DoExport()");
    SomeEditorPage.DoExport();
    await UniTask.CompletedTask;
}
```

優點：
- 人類點按鈕 / agent 透過 Cmd 觸發，**走同一條程式路徑**，行為一致
- 修 bug 只改 EditorPage 一處
- Cmd 變成「給 agent 用的 RPC 包裝層」，職責純粹

---

## 8. 最小骨架範本（直接複製改）

```csharp
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands  // 下游模組請改自家 namespace
{
    /// <summary>
    /// <一句話說明這支 Cmd 的職責>
    /// </summary>
    public class Cmd_<Name> : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "<Name>";
        public override string ShortDescription => "<UI 用一行摘要>";
        public override string ArgsSchema => "(無參數)";
        public override string HelpURL => "ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md";

        public override async UniTask ExecuteAsync(
            Dictionary<string, string> args, CancellationToken token)
        {
            // TODO: 實作邏輯
            Debug.Log($"[AgentCmd] {CommandType} executed.");
            await UniTask.CompletedTask;
        }
    }
}
#endif
```

---

## 9. 文件放置自動判斷方案（給未來）

> 本工作流之所以放在 `Assets/UCL/UCL_Core/Docs~/` 而非專案層 `docs/Workflows/`，是因為它**僅描述 UCL_Core 框架本身的擴充方法**，不依賴任何下游型別。為了讓未來新增的 Cmd / 系統文件能**自動**判斷該放哪裡，建議導入以下規則：

### 9.1 判定原則：依「描述對象的 source 位置」決定

| 文件描述的對象… | 文件應住哪裡 |
|---|---|
| 完全位於 `Assets/UCL/UCL_Core/` 內 | `Assets/UCL/UCL_Core/Docs~/{lang}/...`（多語系）|
| 完全位於下游模組（如 `Assets/Scripts/RCG_Scripts/`）| `docs/...`（專案層，單語系即可）|
| 跨兩者 — 描述「下游 X 如何用 UCL_Core 提供的 Y」 | `docs/Workflows/`（**呼叫方視角**寫；UCL_Core 端只放純框架文件）|

### 9.2 強制 frontmatter 欄位

每份文件 frontmatter 補上：

```yaml
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/
namespace: UCL.Core.EditorLib.AgentCommands
```

`source_root` 是「這份文件描述的程式碼路徑前綴」。**自動分類器**只要：

1. 讀 `source_root`
2. 若 `startsWith("Assets/UCL/UCL_Core/")` → 應住 `Assets/UCL/UCL_Core/Docs~/{lang}/`
3. 若 `startsWith("Assets/Scripts/")` → 應住 `docs/`
4. 不一致 → 警告 + 提示搬移

### 9.3 自動化實作（建議的後續 Cmd）

實作一支 `Cmd_ValidateDocPlacement`：

| 步驟 | 動作 |
|---|---|
| 1 | 掃 `Assets/UCL/UCL_Core/Docs~/**/*.md` + `docs/**/*.md`，讀每份 frontmatter |
| 2 | 取 `source_root`，依 §9.1 計算「應住路徑」 |
| 3 | 比對實際路徑；不符 → 列入 violations |
| 4 | 額外檢查 UCL_Core 端文件**不可** grep 到下游 namespace（如 `RCG_`、`RCG.AgentCommands`）|
| 5 | 輸出 `AgentCommands/doc_placement_report.md` 列出建議搬移清單 |

走 Agent Command 系統有額外好處：
- 與既有 `Cmd_ValidateAssetFormat` 同樣模式（驗證 → 報告）
- 可被 CI batchmode 跑（`Tools/UCL/Agent Commands/Run Pending`）
- 報告本身是 markdown，agent 可直接讀並依建議搬檔

### 9.4 建立新文件的最小檢查清單

寫文件前先回答：

- [ ] 我描述的對象的 .cs 檔在哪？（→ 決定 `source_root`）
- [ ] 該 .cs 是否引用任何下游型別？（→ 影響分層）
- [ ] frontmatter 是否填好 `source_root` / `namespace` / `last_updated` / `target_audience`？
- [ ] 多語系：是否需要 4 份（住 UCL_Core）還是 1 份（住專案層）？

---

## 10. 相關文件

- 🤖 [UCL_AgentCommand_Architecture](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md) — **系統總覽 / 觸發方式 / queue.json schema / 生命週期**（先讀這份）
- 📖 [UCL_AgentCommand](../API/UCL_AgentCommand/UCL_AgentCommand.md) — 指令資料模型
- 🪟 [UCL_AgentCommandsPage](../UCL_EditorPage/UCL_AgentCommandsPage.md) — Editor IMGUI 頁面
- 🔗 [HelpURL_Workflow](HelpURL_Workflow.md) — `ucl_core:` / `eov_docs:` prefix 機制
- 📁 UCL_Core handler 目錄：`Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/`
- 📖 專案層補充工作流：[`docs/Workflows/AgentCommands_Workflow.md`](../../../../../../docs/Workflows/AgentCommands_Workflow.md) — 含下游模組（RCG）的 Cmd 範例與整合細節

---

## 其他語系

- 🇬🇧 [English](../../en/Workflows/Create_Cmd_Workflow.md)
- 🇯🇵 [日本語](../../ja/Workflows/Create_Cmd_Workflow.md)
- 🇨🇳 [简体中文](../../zh-Hans/Workflows/Create_Cmd_Workflow.md)
- 🇹🇼 繁體中文（本檔）
