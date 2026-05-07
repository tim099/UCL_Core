---
date: 2026-05-07
index: 00010
title: UCL_ReflectionInvoker v2 — instance method + 跨 invoke 變數鏈 + BaseType hierarchy walk
tags: [feature, refactor]
---

# UCL_ReflectionInvoker v2 — instance + variable chain

## What

[`UCL_ReflectionInvoker`](../UCL_Core_Scripts/UtilCore/UCL_ReflectionInvoker.cs) 從 v1（純 static）擴成 v2，三項新能力：

1. **instance method / property / field** — 透過 `Target` 欄位提供目標物件
2. **跨 invoke 變數鏈** — `Variables` static dict + `$varname` 語法
3. **BaseType hierarchy walk** — 補強 `BindingFlags.FlattenHierarchy` 對 generic base class static 失效的場景

實測完整鏈通過：

```
ClearVariables
  → RCG.RCG_StoryData.Util              (kind=property, storeAs=util)        // 繼承自 UCL_Util<T> 的 static
  → $util.GetData("AbandonedTemple")    (storeAs=story)                      // instance method, bool 第二參自動補 default
  → $story.GetSubStory("Start")         (storeAs=sub)                        // instance method
```

## Why

### 痛點：v1 卡在 static 邊界
v1 已能呼叫 Unity 內建 API（`CompilationPipeline.RequestScriptCompilation` / `EditorApplication.isCompiling` 等），但**完全無法觸碰 UCL_Asset**：
- `UCL_Asset<T>.Util` 是繼承自 `UCL_Util<T>` 的 static property — `BindingFlags.Static | Public` 找不到（generic base class static 的反射陷阱）
- `Util.GetData(id)` 是 instance method — v1 只走 static，target 一律傳 null 即直接死

### v2 設計目標
讓 agent 可以「拿一個 UCL_Asset → 對它做事」這個最常見場景全靠通用 invoke 完成，**不必為每支 instance API 寫專用 Cmd**。

### 為什麼是 dictionary，不是「在 Cmd 內 inline 一系列呼叫」
考慮過：用一支 Cmd 接受多步驟 script（DSL）。砍掉因為：
- Cmd args 是 `Dictionary<string, string>`，序列化巢狀步驟太醜
- 多步驟 script 要解析跳轉 / 條件 / 錯誤處理，做下去又是另一個 sub-language
- agent 一次 submit 一個 Cmd 已經是穩定的執行單元，串接交給 Python wrapper 端做就行

最終：**static `Variables` 字典 + `$varname` 引用** — Editor session 全域，跨 Cmd 共享，domain reload 清空（不持久化、不污染）。

### 為什麼 BaseType walk 不只靠 FlattenHierarchy
`UCL_Util<T>` 是 generic base class，`UCL_Util<RCG_StoryData>.Util` 的 static slot 在某些 .NET runtime / 某些泛型情境下用 `BindingFlags.FlattenHierarchy` 找不到。**手動沿 `type.BaseType` 走鏈**是兜底，每層自己做 `GetProperty / GetField / GetMethod`，找到就停。

## How

### 三層解耦不變，新增資料流

```
Cmd_Invoke / Editor button / runtime tool
        │
        ▼ Dictionary<string,string>
ParseRequest(dict) → UCL_ReflectionInvokeRequest
        │              ├─ Target (instance variable name from Variables)
        │              ├─ StoreAs (where to write Result.Value)
        │              └─ args 內 $name → Variables[name]
        ▼
Invoke(req)
        ├─ if Target → Variables[Target] 取 instance；BindingFlags 切 Instance
        ├─ if !Target → static；BindingFlags 加 FlattenHierarchy + 手動 walk BaseType
        ├─ 找到 MemberInfo
        ├─ 參數轉換：每個 arg 字串若 $-prefix → Variables[name]；否則 TryConvertFromString
        ├─ Tail 缺的參數 → ParameterInfo.HasDefaultValue → DefaultValue
        ├─ method.Invoke(target, argv)
        └─ if StoreAs → Variables[StoreAs] = result.Value
```

### `Variables` 生命週期

| 事件 | 影響 |
|---|---|
| `storeAs=name` 成功 | `Variables[name] = value` |
| `Cmd_Invoke` 失敗 | Variables 不寫入（只在 Success 時寫）|
| Domain reload（含 `Cmd_Recompile`）| **全清空**（static 欄位重置）|
| Play mode 切換 | 視 Unity domain reload 設定（一般也會清）|
| `ClearVariables()` 呼叫 | 立刻清空 |

刻意不持久化 — 若需要跨 session 的資料請走 EditorPrefs / 自寫 Cmd 處理。

### `$varname` 引用範圍

| 位置 | 行為 |
|---|---|
| `target=$name` | 取 instance object；找不到 / null 直接 fail |
| `args=...;$name;...` | 該 arg 直接拿 Variables 物件，跳過字串轉型；型別不符讓 method.Invoke 自己 throw |
| property setter `args=$name` | 同上 |
| field setter `args=$name` | 同上 |

### Default value 自動補齊

```csharp
// RCG_StoryData.GetData(string iID, bool iUseCache = true)
// caller: target=$util member=GetData args=AbandonedTemple
// invoker: 給 1 個 arg，method 要 2 個 → ps[1].HasDefaultValue=true → 自動填 true
```

`req.Args.Count > ps.Length` 直接 fail；`<` 時 tail 缺的位置須有 default，否則 fail。

## Pitfalls

### 1. `FlattenHierarchy` 對 generic base 的 static 失效

第一輪 fix 只加 `BindingFlags.FlattenHierarchy` 就以為夠了，實測 `RCG.RCG_StoryData.Util` 還是找不到（property 在 `UCL_Util<RCG_StoryData>`）。最終要**手動走 `type.BaseType` 鏈**。

### 2. wrapper 的 Success / Failed 偵測 vs `tail -5`

`run_cmd.py` 失敗會把 `✗ Cmd failed: ...` 印到 stderr，但用 `tail -5 2>&1` 看時 stderr/stdout 交錯可能截掉這行 → 誤以為「queue 變空 = Success」。**正解**：grep `Cmd failed|Success` 篩，或讀 Unity Console 確認。

### 3. 早期測試 `UnityEditorInternal.LogEntries` 撞牆

我把 namespace 拼錯成 `UnityEditorInternal.LogEntries`（正確是 `UnityEditor.LogEntries`），加上 `tail -5` 把錯誤行截掉，**連續兩個失敗測試誤報 Success**。被使用者抓出來：「大小姐有注意到嗎」。教訓：嚴格輸出檢查 + 不過度信任 wrapper 的成功訊號。

### 4. `Type.FullName` 強制嚴格大小寫

中途有把 `GetTypeByFullNameIgnoreCase` 加進 `AssemblyExtensions` 用作 fallback，後來砍掉 — agent 餵錯字應該被立刻攔下，避免「拼錯卻撞到別的同名 type」。錯誤訊息附 `(use exact Type.FullName, case-sensitive)` 提示。

## Files

修改：
- [`UCL_Core_Scripts/UtilCore/UCL_ReflectionInvoker.cs`](../UCL_Core_Scripts/UtilCore/UCL_ReflectionInvoker.cs) — 加 `Target` / `StoreAs` 欄位、`Variables` static dict、`ClearVariables()`、`FindMember` walk helper、default value 補齊邏輯、`$varname` 引用
- [`UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_Invoke.cs`](../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_Invoke.cs) — ArgsSchema / ExampleArgs 反映新功能
- 4 份 [`Docs~/{lang}/API/UCL_AgentCommand/Cmd_Invoke.md`](../Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_Invoke.md) — §2 schema / §3.7 instance chain / §5 scope / §7 故障表
- 4 份 `index.md` — Invoke 描述補上 instance + 變數鏈

## Future

- **複雜物件構建** — 透過 `Activator.CreateInstance` + 後續 storeAs；或新加 `Cmd_New`
- **Generic method type args** — 目前要 caller 在 `paramTypes` 一併展開，不主動推
- **`$varname` escape** — 字面值 `$abc` 會被誤判；考慮 `\$` 或 `${name}` 語法
- **Cross-session 變數** — 若有人有需求，可加 `persistAs=` 寫 EditorPrefs；但傾向反對（污染）
