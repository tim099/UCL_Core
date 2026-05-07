---
date: 2026-05-07
index: 00009
title: UCL_ReflectionInvoker + Cmd_Invoke — 通用反射調用層 + 跳脫專用 Cmd 的束縛
tags: [feature, refactor]
---

# UCL_ReflectionInvoker + Cmd_Invoke

## What

把「字串描述 → MemberInfo → Invoke」的反射呼叫抽成獨立純邏輯層 [`UCL_ReflectionInvoker`](../UCL_Core_Scripts/UtilCore/UCL_ReflectionInvoker.cs)，並包一個薄層 Agent Command [`Cmd_Invoke`](../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_Invoke.cs) 給 agent 用。

**三層解耦**：

| 層 | 位置 | 角色 |
|---|---|---|
| 觸發 | `Cmd_Invoke` / Editor button / runtime tool | 任何來源都行；Cmd 只是其中一條入口 |
| 反射執行 | `UCL.Core.UCL_ReflectionInvoker`（`UtilCore/`） | 純邏輯、可單元測試、runtime 可用 |
| Type / 轉型 | `AssemblyExtensions`（`ExtensionMethodCore/`） | 共用 cache + 可擴充 |

**功能涵蓋**：
- public / nonPublic static method / property / field
- method 多載（`paramTypes` 精確匹配；空 `paramTypes` 偏好無參版本）
- 參數轉型：primitive / string / enum（含 `Enum.Parse` ignoreCase）/ `null` 字面值
- property setter（`getter=false` 時 `args[0]` 為值）
- 結果輸出：含值轉 string，void method 報 OK
- Type **嚴格匹配 `Type.FullName`**（含大小寫），不做 fallback

## Why

### 痛點：每個 Unity API 都包專用 Cmd 太肥
agent 想呼 `CompilationPipeline.RequestScriptCompilation` / `AssetDatabase.Refresh` / 讀 `EditorApplication.isCompiling` 等 — 每支都包一個 Cmd 太瑣碎，也無法應對未事先預期的呼叫。

### 解法：通用反射 + Cmd args 字串描述
餵 `type=...;member=...;paramTypes=...;args=...` 即可動態觸發任意 public static API。**特殊情況**才需要寫專用 Cmd（複雜輸出 / 防呆檢查 / 反覆使用同一組 args）。

### 為什麼解析層獨立？
原本 Reflection 邏輯只在 `Cmd_Invoke` 內，這次改成：
1. **Cmd_Invoke**（`EditorCore/UCL_AgentCommands/CMD/`，editor-only）— 薄層 dispatcher
2. **UCL_ReflectionInvoker**（`UtilCore/`，**runtime-available**）— 純邏輯

→ runtime 工具 / Editor button / 其他 Cmd 都可直接 `new UCL_ReflectionInvokeRequest {…}` + `UCL_ReflectionInvoker.Invoke(req)`，不一定要走 Cmd。

### 為什麼 Type 解析委派給 AssemblyExtensions？
[`AssemblyExtensions`](../UCL_Core_Scripts/ExtensionMethodCore/UCL_AssemblyExtension.cs) 已有共用的 `TypeDic` cache（`Dictionary<string, Type>` keyed by FullName）。Reflection invoker 蹭這個 cache 取代重新掃 assemblies，O(1) 命中且全 UCL_Core 共享。同時把字串轉型也抽成 `Type.TryConvertFromString` 擴充方法 — 之後其他工具想做 string→object 轉換可重用。

### 為什麼嚴格大小寫匹配 Type.FullName？
Agent 寫錯字（如 `unityEditor.AssetDatabase` 對 `UnityEditor.AssetDatabase`）應該被立即攔下，避免「拼錯卻意外撞到別的同名 type」這種隱性 bug。Type.FullName 是規範的、可重現的；不該為了容錯而 silent 接受變體。錯誤訊息會明示 `(use exact Type.FullName, case-sensitive)`。

## How

### 三層協作流

```
Python run_cmd.py run Invoke ...
          │
          ▼ Dictionary<string,string>
Cmd_Invoke（dispatch + log）
          │ ParseRequest(dict) → request
          ▼
UCL_ReflectionInvoker.Invoke(request)
          │
          ├─ AssemblyExtensions.GetTypeByFullName 嚴格解析 type / paramTypes
          │  （cache 命中 O(1)；錯字立刻 fail）
          │
          ├─ BindingFlags.Static | Public | (NonPublic if requested)
          │  GetMethod / GetProperty / GetField
          │
          ├─ Type.TryConvertFromString 把 args 字串轉成參數 object[]
          │  （primitive / string / enum / null 字面值）
          │
          └─ method.Invoke(null, argv) → result
          │
          ▼
Cmd_Invoke 把 result.Value.ToString() 寫進 Unity Console
```

### method 多載解析

| 情境 | 行為 |
|---|---|
| 提供 `paramTypes=A;B` | 嚴格 `GetMethod(name, flags, types: [A, B])` 精確匹配 |
| 空 `paramTypes` 且 type 有無參版本 | 自動匹配 `Method()`（最常見場景，如 `RequestScriptCompilation`） |
| 空 `paramTypes` 且唯一 candidate（non-zero arity） | 用該 candidate 並對 `args` 做轉型 |
| 空 `paramTypes` 且多 candidate 都有參數 | 報 `ambiguous method (need paramTypes): ...` 並列 candidates |

### 失敗訊息含修補提示

| 失敗 | 訊息含的提示 |
|---|---|
| type 拼錯 / 大小寫錯 | `(use exact Type.FullName, case-sensitive)` |
| 找不到 public member 但沒開 nonPublic | `— try nonPublic=true` |
| method 多載 | 列出所有 candidate signatures |

## Pitfalls

### 1. 早期測試誤判 Success / Failed

我用 `tail -5 2>&1` 看 `run_cmd.py` 輸出，stderr 的 `✗ Cmd failed` 行有時會被截掉，誤以為「queue 變空 = 成功」。**正確姿勢**：用 grep 篩 `Cmd failed|Success` 確認真正狀態，或讀 Unity Console 為準（runner 會在 Console 印 `[UCL_AgentCmd] ✗ ... failed: ...`）。

### 2. `UnityEditor.LogEntries.Clear` 不是好的 nonPublic 測試 case

新版 Unity 把它升級成 public-accessible reflection。挑 nonPublic 測試 case 要小心，不能憑感覺。**驗證 nonPublic flag 用對方法**：找一個明確只能透過 `BindingFlags.NonPublic` 才能取到的 type / member。

### 3. RequestScriptCompilation 多載

新版 Unity 加了帶 `RequestScriptCompilationOptions` 的多載。原本「name 唯一匹配」邏輯會 ambiguous。改成「空 `paramTypes` 偏好無參版本」後解決。

### 4. AgentCommandsPage 的 Auto-Watcher checkbox 點不到

獨立但同期發現：[`UCL_AgentCommandsPage.DrawWatcherStatusBar`](../UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AgentCommandsPage.cs) 用 `GUILayout.Toggle(value, label, UCL_GUIStyle.LabelStyle, ...)` — 正是 `Edit_Recompile_Loop_Workflow` §7 地雷 1 的反例：LabelStyle 沒 toggle 兩態 sprite，視覺消失、熱區壞掉。修成 `UCL_GUILayout.CheckBox(value, label)` 後 checkbox 才看得見也點得到。

## Files

新增：
- [`UCL_Core_Scripts/UtilCore/UCL_ReflectionInvoker.cs`](../UCL_Core_Scripts/UtilCore/UCL_ReflectionInvoker.cs) — 純邏輯解析+執行層
- [`UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_Invoke.cs`](../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_Invoke.cs) — Agent Command 薄層
- [`Docs~/{lang}/API/UCL_AgentCommand/Cmd_Invoke.md`](../Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_Invoke.md) — API 文件（4 語）

修改：
- [`UCL_Core_Scripts/ExtensionMethodCore/UCL_AssemblyExtension.cs`](../UCL_Core_Scripts/ExtensionMethodCore/UCL_AssemblyExtension.cs) — 加 `TryConvertFromString` extension method（string→object 通用轉換器）
- 4 份 `index.md` 同步加 Invoke 條目

## Future

- **instance method 支援**：v1 只 static；要 instance 需要 caller 提供 target object（如 `(Object)EditorWindow.GetWindow(typeof(SceneView))`），可從 args 接收 `target=...` 但複雜，看實際需求再說
- **結構化輸出**：目前 method 回傳值只進 Unity Console，agent 拿不到。若需要可加 `outputPath=...` 把 result.Value 序列化寫檔
- **更多型別轉換**：`UnityEngine.Vector3` / `UnityEngine.Color` 等 Unity 常用型別可在 `TryConvertFromString` 加 special case；目前 `Convert.ChangeType` 只吃 primitive
