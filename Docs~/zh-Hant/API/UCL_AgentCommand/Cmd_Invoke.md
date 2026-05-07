---
title: Cmd_Invoke API
description: 通用反射 Cmd — 把字串描述（type / member / args）餵給 UCL_ReflectionInvoker，動態觸發 Unity 內建任意 public static method / property / field，免為每支 API 寫專用 Cmd。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_Invoke.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [reflection invoke cmd, dynamic api call, generic unity invoker]
tags: [api, agent-command, reflection, editor]
---

# Cmd_Invoke

## 1. 概覽

`Cmd_Invoke` 是**通用反射調用**指令 — 不必為每個 Unity 內建 API 寫專用 Cmd，agent 可直接以字串描述（type / member / args）觸發任意 `public static` method / property / field。

### 設計分層（觸發來源不限 Cmd）

```
   Cmd_Invoke           Editor 按鈕         自訂 runtime tool      其他 Cmd
        │                    │                    │                    │
        ▼                    ▼                    ▼                    ▼
   ┌────────────────────────────────────────────────────────────────────────┐
   │  UCL.Core.UCL_ReflectionInvoker（UtilCore，runtime-available，純邏輯） │
   │   ParseRequest(IDictionary<string,string>) → Request                    │
   │   Invoke(Request) → Result                                              │
   └────────────────────────────────────────────────────────────────────────┘
              │
              ├─ 用 AssemblyExtensions.GetTypeByFullName 嚴格解析 Type
              │  （cache 由整個 UCL_Core 共用；大小寫精確不做 fallback）
              │
              └─ 用 Type.TryConvertFromString(string) 做參數轉型
                 （extension method，primitive / string / enum / "null" 字面值）
              │
              ▼
   呼叫的 Unity 內建 API（CompilationPipeline / AssetDatabase / EditorPrefs / EditorApplication ...）
```

**解耦三層**：
| 層 | 位置 | 用途 |
|---|---|---|
| 觸發 | `Cmd_Invoke` / 自訂 Editor button / runtime call | 任何來源都能餵 dict 或 直接 new request |
| 反射執行 | `UCL.Core.UCL_ReflectionInvoker`（UtilCore） | 純邏輯，可單元測試，可 runtime 用 |
| Type / 轉型 | `AssemblyExtensions.GetTypeByFullName`（嚴格）/ `Type.TryConvertFromString` | 共用 cache + 可繼續擴充支援更多型別 |

### 直接呼叫範例（不走 Cmd，runtime 也能跑）

```csharp
using UCL.Core;

var req = new UCL_ReflectionInvokeRequest
{
    TypeName = "UnityEditor.Compilation.CompilationPipeline",
    MemberName = "RequestScriptCompilation",
};
var result = UCL_ReflectionInvoker.Invoke(req);
if (!result.Success) Debug.LogError(result.Error);
```

---

## 2. 參數 schema

| key | 必填 | 預設 | 說明 |
|---|---|---|---|
| `type` | ✅ | — | **完整 `Type.FullName`，大小寫精確**（例 `UnityEditor.Compilation.CompilationPipeline`）；錯一個字母就 fail |
| `member` | ✅ | — | 成員名（method / property / field 名）— 大小寫精確 |
| `kind` | | `method` | `method` / `property` / `field` |
| `paramTypes` | | (空) | 多載消歧：分號分隔的完整型別清單（例 `int;string;UnityEditor.ImportAssetOptions`） |
| `args` | | (空) | 分號分隔的字串參數，按 `paramTypes` 順序轉型 |
| `getter` | | `true` | 對 property / field — `false` 改成 setter，`args[0]` 為要賦的值 |
| `nonPublic` | | `false` | `true` 時 BindingFlags 多開 `NonPublic`，可搜 internal / private static 成員（Unity 內建 API 大量是 internal） |

### 2.1 args 轉型規則

| 目標型別 | 字串範例 → 轉換 |
|---|---|
| `string` | `"hello"` → `"hello"` |
| `bool` | `"true"` / `"false"` → `bool.Parse` |
| primitive 數值（int/long/float/double…） | `"42"` → `Convert.ChangeType` |
| enum | `"Default"` → `Enum.Parse(type, value, ignoreCase: true)` |
| reference type / `Nullable<T>` | `"null"` 字面值 → `null` |
| 其他複雜型別 | ❌ v1 不支援；改寫專用 Cmd |

### 2.2 type 解析

走 `AssemblyExtensions.GetTypeByFullName` **嚴格匹配**：
1. 從 `AssemblyExtensions.TypeDic`（全 UCL_Core 共用 cache）查 FQN 精確匹配 — O(1)
2. 找不到 → 直接回 null，呼叫端報 `type not found: ... (use exact Type.FullName, case-sensitive)`

> [!IMPORTANT]
> **type 必須是完整 `Type.FullName` 且大小寫精確** — 不做 ignoreCase fallback。
> 這是刻意設計：agent 餵錯字應該被立刻攔下，避免「拼錯卻意外撞到別的同名 type」隱性錯誤。
> `paramTypes` 同規則。

---

## 3. 範例

### 3.1 觸發 Unity 重編（無參數 method）

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.Compilation.CompilationPipeline" \
  --arg "member=RequestScriptCompilation"
```

等同 `Cmd_Recompile` 的核心邏輯（差別：`Cmd_Recompile` 多了 `AssetDatabase.Refresh()` + 走 `recompile` 子命令時會等到 compile 完成）。

### 3.2 讀屬性

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.EditorApplication" \
  --arg "member=isCompiling" \
  --arg "kind=property"
```

Unity Console 印 `[AgentCmd:Invoke] OK (System.Boolean) = False`。

### 3.3 帶 enum 參數的 method（多載消歧）

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.AssetDatabase" \
  --arg "member=Refresh" \
  --arg "paramTypes=UnityEditor.ImportAssetOptions" \
  --arg "args=Default"
```

`AssetDatabase.Refresh` 有兩個多載；`paramTypes` 鎖定有 enum 參數那個。

### 3.4 設定 EditorPrefs（property setter）

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.EditorPrefs" \
  --arg "member=SetString" \
  --arg "paramTypes=System.String;System.String" \
  --arg "args=MyKey;MyValue"
```

> 注意：這裡用 `EditorPrefs.SetString(key, value)` method 而非 setter property，因為 EditorPrefs 沒有 indexed property。

### 3.5 多參數 method

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.AssetDatabase" \
  --arg "member=ImportAsset" \
  --arg "paramTypes=System.String;UnityEditor.ImportAssetOptions" \
  --arg "args=Assets/SomeFile.txt;ForceUpdate"
```

### 3.6 internal / private static API（`nonPublic=true`）

很多 Unity 內建 API 是 `internal`（例如 `UnityEditorInternal.LogEntries.Clear` / 某些 build pipeline 工具）。預設 `nonPublic=false` 找不到，加上 `nonPublic=true` 即可：

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditorInternal.LogEntries" \
  --arg "member=Clear" \
  --arg "nonPublic=true"
```

對應的錯誤訊息也會帶提示：找不到 public 成員時報 `... — try nonPublic=true`，提醒下次補上 flag。

---

## 4. 結果處理

| 情境 | Unity Console | Cmd 結果 |
|---|---|---|
| void method 成功 | `OK (void / null)` | Success |
| method 回傳值 | `OK (TypeName) = value.ToString()` | Success（值在 Console；要結構化請寫專用 Cmd） |
| 解析失敗（type / member 找不到 / args 轉型失敗） | `LogError + throw` | Failed |
| 內部例外 | `target threw {ExceptionType}: ...` | Failed |

> [!CAUTION]
> **回傳值目前只進 Unity Console**（`Debug.Log`），不寫進磁碟也不丟回 Python。
> 需要結構化拿值請：(a) 寫專用 Cmd，或 (b) 等本 Cmd 之後加 `outputPath` 參數。

---

## 5. 安全 / 限制

| 項 | 說明 |
|---|---|
| **scope** | 只支援 `Static`（`Public` 永遠開；`NonPublic` 由 `nonPublic=true` 加開）；instance 未支援 |
| **side effect** | 視被呼叫 API 而定 — 呼叫 `RequestScriptCompilation` 會觸發 domain reload，所有 in-flight async cmd 會被殺掉（這也是為什麼 `Cmd_Recompile` 是「丟出請求即返回」的設計） |
| **type ambiguity** | 多載 method：必填 `paramTypes` 否則錯（candidates 列表會印在錯誤訊息） |
| **threading** | 全在 Unity main thread；不要呼會 block thread 的 API |
| **destructive call** | agent 不該用本 Cmd 呼會造成資料遺失的 API（如 `AssetDatabase.DeleteAsset`）— 用專用 Cmd 並加確認流程 |

---

## 6. 跟其他 Cmd / 工具的關係

| 工具 | 何時用 |
|---|---|
| **Cmd_Invoke**（本檔） | 一次性呼叫 / 探索 / agent 想觸發但沒專用 Cmd 時 |
| **Cmd_Recompile** | 專用：觸發重編 + 配合 Python `recompile` 子命令等到 compile 完成 |
| **Cmd_ResolveAssetReferences** | 專用：BFS 走 UCL_Asset 引用鏈（複雜輸出，不適合 Invoke） |
| **Cmd_FindAssetUsages** | 專用：反向查詢被引用位置 |
| 寫新 Cmd | 反覆用同一組 args / 需要結構化輸出 / 要做防呆檢查 — 走 [Create_Cmd_Workflow](../../Workflows/Create_Cmd_Workflow.md) |

> [!TIP]
> **rule of thumb**：探索期用 `Cmd_Invoke`，穩定後抽成專用 Cmd。

---

## 7. 故障排除

| 症狀 | 可能原因 | 解法 |
|---|---|---|
| `type not found: X (use exact Type.FullName, case-sensitive)` | FQN 拼錯 / namespace 缺 / 大小寫錯 | 從 Unity 反組譯抓正確 `Type.FullName`（嚴格大小寫）— `unityeditor.AssetDatabase` 跟 `UnityEditor.AssetDatabase` 不一樣 |
| `static method not found ... — try nonPublic=true` | method 是 internal / private static | 加 `nonPublic=true` |
| `static method not found` 但 nonPublic 已開 | method 是 instance | 本 Cmd v1 不支援 instance；改寫專用 Cmd |
| `ambiguous method (need paramTypes)` | 同名多載 | 補 `paramTypes` 鎖一個多載；錯誤訊息會列出所有 candidates |
| `enum parse failed` | 寫了 enum 數值而非名稱（如 `0`） | 寫名稱（如 `Default`） |
| Cmd 標 Failed 但沒看到錯誤 | Unity Console 在背景 | 開 Console 視窗看 `[AgentCmd:Invoke] FAILED: ...` |

---

## 8. 相關文件

- [Edit_Recompile_Loop_Workflow](../../Workflows/Edit_Recompile_Loop_Workflow.md) — 改完 .cs 後的同步迴圈（本 Cmd 加新檔後也要走這條）
- [Create_Cmd_Workflow](../../Workflows/Create_Cmd_Workflow.md) — 何時該抽出專用 Cmd 取代 Invoke
- [Cmd_Recompile](Cmd_Recompile.md) — 等同 `Invoke(CompilationPipeline.RequestScriptCompilation)` + 等待
- `UCL.Core.UCL_ReflectionInvoker`（位於 `UtilCore/`，runtime-available）— 純邏輯解析 / 執行層；任何 Cmd / Editor button / runtime tool 都可直接呼叫
- `AssemblyExtensions`（`ExtensionMethodCore/`）— Type 解析 cache（`GetTypeByFullName` 嚴格匹配）+ `TryConvertFromString`，可繼續擴充支援更多參數型別轉換
