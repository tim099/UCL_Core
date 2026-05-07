---
title: Cmd_Invoke API
description: 通用反射 Cmd — 把字符串描述（type / member / args）喂给 UCL_ReflectionInvoker，动态触发 Unity 内建任意 public static method / property / field，免为每支 API 写专用 Cmd。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_Invoke.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [reflection invoke cmd, dynamic api call, generic unity invoker]
tags: [api, agent-command, reflection, editor]
---

# Cmd_Invoke

## 1. 概览

`Cmd_Invoke` 是**通用反射调用**指令 — 不必为每个 Unity 内建 API 写专用 Cmd，agent 可直接以字符串描述（type / member / args）触发任意 `public static` method / property / field。

### 设计分层（触发来源不限 Cmd）

```
   Cmd_Invoke           Editor 按钮         自定 runtime tool      其他 Cmd
        │                    │                    │                    │
        ▼                    ▼                    ▼                    ▼
   ┌────────────────────────────────────────────────────────────────────────┐
   │  UCL.Core.UCL_ReflectionInvoker（UtilCore，runtime-available，纯逻辑） │
   │   ParseRequest(IDictionary<string,string>) → Request                    │
   │   Invoke(Request) → Result                                              │
   └────────────────────────────────────────────────────────────────────────┘
              │
              ├─ 用 AssemblyExtensions.GetTypeByFullName 严格解析 Type
              │  （cache 由整个 UCL_Core 共用；严格区分大小写不做 fallback）
              │
              └─ 用 Type.TryConvertFromString(string) 做参数转型
                 （extension method，primitive / string / enum / "null" 字面值）
              │
              ▼
   调用的 Unity 内建 API（CompilationPipeline / AssetDatabase / EditorPrefs / EditorApplication ...）
```

**解耦三层**：
| 层 | 位置 | 用途 |
|---|---|---|
| 触发 | `Cmd_Invoke` / 自定 Editor button / runtime call | 任何来源都能喂 dict 或 直接 new request |
| 反射执行 | `UCL.Core.UCL_ReflectionInvoker`（UtilCore） | 纯逻辑，可单元测试，可 runtime 用 |
| Type / 转型 | `AssemblyExtensions.GetTypeByFullName`（严格）/ `Type.TryConvertFromString` | 共用 cache + 可继续扩充支持更多类型 |

### 直接调用范例（不走 Cmd，runtime 也能跑）

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

## 2. 参数 schema

| key | 必填 | 默认 | 说明 |
|---|---|---|---|
| `type` | ✅ | — | **完整 `Type.FullName`，严格区分大小写**（例 `UnityEditor.Compilation.CompilationPipeline`）；错一个字母就 fail |
| `member` | ✅ | — | 成员名（method / property / field 名）— 严格区分大小写 |
| `kind` | | `method` | `method` / `property` / `field` |
| `paramTypes` | | (空) | 多载消歧：分号分隔的完整类型清单（例 `int;string;UnityEditor.ImportAssetOptions`） |
| `args` | | (空) | 分号分隔的字符串参数，按 `paramTypes` 顺序转型 |
| `getter` | | `true` | 对 property / field — `false` 改成 setter，`args[0]` 为要赋的值 |
| `nonPublic` | | `false` | `true` 时 BindingFlags 多开 `NonPublic`，可搜 internal / private static 成员（Unity 内建 API 大量是 internal） |

### 2.1 args 转型规则

| 目标类型 | 字符串范例 → 转换 |
|---|---|
| `string` | `"hello"` → `"hello"` |
| `bool` | `"true"` / `"false"` → `bool.Parse` |
| primitive 数值（int/long/float/double…） | `"42"` → `Convert.ChangeType` |
| enum | `"Default"` → `Enum.Parse(type, value, ignoreCase: true)` |
| reference type / `Nullable<T>` | `"null"` 字面值 → `null` |
| 其他复杂类型 | ❌ v1 不支持；改写专用 Cmd |

### 2.2 type 解析

走 `AssemblyExtensions.GetTypeByFullName` **严格匹配**：
1. 从 `AssemblyExtensions.TypeDic`（全 UCL_Core 共用 cache）查 FQN 精确匹配 — O(1)
2. 找不到 → 直接回 null，调用端报 `type not found: ... (use exact Type.FullName, case-sensitive)`

> [!IMPORTANT]
> **type 必须是完整 `Type.FullName` 且严格区分大小写** — 不做 ignoreCase fallback。
> 这是刻意设计：agent 喂错字应该被立刻拦下，避免「拼错却意外撞到别的同名 type」隐性错误。
> `paramTypes` 同规则。

---

## 3. 范例

### 3.1 触发 Unity 重编（无参数 method）

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.Compilation.CompilationPipeline" \
  --arg "member=RequestScriptCompilation"
```

等同 `Cmd_Recompile` 的核心逻辑（差别：`Cmd_Recompile` 多了 `AssetDatabase.Refresh()` + 走 `recompile` 子命令时会等到 compile 完成）。

### 3.2 读属性

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.EditorApplication" \
  --arg "member=isCompiling" \
  --arg "kind=property"
```

Unity Console 印 `[AgentCmd:Invoke] OK (System.Boolean) = False`。

### 3.3 带 enum 参数的 method（多载消歧）

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.AssetDatabase" \
  --arg "member=Refresh" \
  --arg "paramTypes=UnityEditor.ImportAssetOptions" \
  --arg "args=Default"
```

`AssetDatabase.Refresh` 有两个多载；`paramTypes` 锁定有 enum 参数那个。

### 3.4 设定 EditorPrefs（property setter）

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.EditorPrefs" \
  --arg "member=SetString" \
  --arg "paramTypes=System.String;System.String" \
  --arg "args=MyKey;MyValue"
```

> 注意：这里用 `EditorPrefs.SetString(key, value)` method 而非 setter property，因为 EditorPrefs 没有 indexed property。

### 3.5 多参数 method

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.AssetDatabase" \
  --arg "member=ImportAsset" \
  --arg "paramTypes=System.String;UnityEditor.ImportAssetOptions" \
  --arg "args=Assets/SomeFile.txt;ForceUpdate"
```

### 3.6 internal / private static API（`nonPublic=true`）

很多 Unity 内建 API 是 `internal`（例如 `UnityEditorInternal.LogEntries.Clear` / 某些 build pipeline 工具）。默认 `nonPublic=false` 找不到，加上 `nonPublic=true` 即可：

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditorInternal.LogEntries" \
  --arg "member=Clear" \
  --arg "nonPublic=true"
```

对应的错误信息也会带提示：找不到 public 成员时报 `... — try nonPublic=true`，提醒下次补上 flag。

---

## 4. 结果处理

| 情境 | Unity Console | Cmd 结果 |
|---|---|---|
| void method 成功 | `OK (void / null)` | Success |
| method 回传值 | `OK (TypeName) = value.ToString()` | Success（值在 Console；要结构化请写专用 Cmd） |
| 解析失败（type / member 找不到 / args 转型失败） | `LogError + throw` | Failed |
| 内部异常 | `target threw {ExceptionType}: ...` | Failed |

> [!CAUTION]
> **回传值目前只进 Unity Console**（`Debug.Log`），不写进磁盘也不丢回 Python。
> 需要结构化拿值请：(a) 写专用 Cmd，或 (b) 等本 Cmd 之后加 `outputPath` 参数。

---

## 5. 安全 / 限制

| 项 | 说明 |
|---|---|
| **scope** | 只支持 `Static`（`Public` 永远开；`NonPublic` 由 `nonPublic=true` 加开）；instance 未支持 |
| **side effect** | 视被调用 API 而定 — 调用 `RequestScriptCompilation` 会触发 domain reload，所有 in-flight async cmd 会被杀掉（这也是为什么 `Cmd_Recompile` 是「丢出请求即返回」的设计） |
| **type ambiguity** | 多载 method：必填 `paramTypes` 否则错（candidates 列表会印在错误信息） |
| **threading** | 全在 Unity main thread；不要调会 block thread 的 API |
| **destructive call** | agent 不该用本 Cmd 调会造成数据遗失的 API（如 `AssetDatabase.DeleteAsset`）— 用专用 Cmd 并加确认流程 |

---

## 6. 跟其他 Cmd / 工具的关系

| 工具 | 何时用 |
|---|---|
| **Cmd_Invoke**（本档） | 一次性调用 / 探索 / agent 想触发但没专用 Cmd 时 |
| **Cmd_Recompile** | 专用：触发重编 + 配合 Python `recompile` 子命令等到 compile 完成 |
| **Cmd_ResolveAssetReferences** | 专用：BFS 走 UCL_Asset 引用链（复杂输出，不适合 Invoke） |
| **Cmd_FindAssetUsages** | 专用：反向查询被引用位置 |
| 写新 Cmd | 反复用同一组 args / 需要结构化输出 / 要做防呆检查 — 走 [Create_Cmd_Workflow](../../Workflows/Create_Cmd_Workflow.md) |

> [!TIP]
> **rule of thumb**：探索期用 `Cmd_Invoke`，稳定后抽成专用 Cmd。

---

## 7. 故障排除

| 症状 | 可能原因 | 解法 |
|---|---|---|
| `type not found: X (use exact Type.FullName, case-sensitive)` | FQN 拼错 / namespace 缺 / 大小写错 | 从 Unity 反编译抓正确 `Type.FullName`（严格区分大小写）— `unityeditor.AssetDatabase` 跟 `UnityEditor.AssetDatabase` 不一样 |
| `static method not found ... — try nonPublic=true` | method 是 internal / private static | 加 `nonPublic=true` |
| `static method not found` 但 nonPublic 已开 | method 是 instance | 本 Cmd v1 不支持 instance；改写专用 Cmd |
| `ambiguous method (need paramTypes)` | 同名多载 | 补 `paramTypes` 锁一个多载；错误信息会列出所有 candidates |
| `enum parse failed` | 写了 enum 数值而非名称（如 `0`） | 写名称（如 `Default`） |
| Cmd 标 Failed 但没看到错误 | Unity Console 在背景 | 开 Console 视窗看 `[AgentCmd:Invoke] FAILED: ...` |

---

## 8. 相关文档

- [Edit_Recompile_Loop_Workflow](../../Workflows/Edit_Recompile_Loop_Workflow.md) — 改完 .cs 后的同步循环（本 Cmd 加新档后也要走这条）
- [Create_Cmd_Workflow](../../Workflows/Create_Cmd_Workflow.md) — 何时该抽出专用 Cmd 取代 Invoke
- [Cmd_Recompile](Cmd_Recompile.md) — 等同 `Invoke(CompilationPipeline.RequestScriptCompilation)` + 等待
- `UCL.Core.UCL_ReflectionInvoker`（位于 `UtilCore/`，runtime-available）— 纯逻辑解析 / 执行层；任何 Cmd / Editor button / runtime tool 都可直接调用
- `AssemblyExtensions`（`ExtensionMethodCore/`）— Type 解析 cache（`GetTypeByFullName` 严格匹配）+ `TryConvertFromString`，可继续扩充支持更多参数类型转换
