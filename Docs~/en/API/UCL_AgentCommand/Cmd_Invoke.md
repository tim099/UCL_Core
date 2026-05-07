---
title: Cmd_Invoke API
description: Generic reflection Cmd — feeds a string description (type / member / args) to UCL_ReflectionInvoker to dynamically invoke any built-in Unity public static method / property / field, removing the need for a dedicated Cmd per API.
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_Invoke.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [reflection invoke cmd, dynamic api call, generic unity invoker]
tags: [api, agent-command, reflection, editor]
---

# Cmd_Invoke

## 1. Overview

`Cmd_Invoke` is the **generic reflection invocation** command — instead of writing a dedicated Cmd for every built-in Unity API, an agent can fire any `public static` method / property / field directly through a string description (type / member / args).

### Layered design (trigger source is not limited to Cmd)

```
   Cmd_Invoke           Editor button       Custom runtime tool    Other Cmd
        │                    │                    │                    │
        ▼                    ▼                    ▼                    ▼
   ┌────────────────────────────────────────────────────────────────────────┐
   │  UCL.Core.UCL_ReflectionInvoker (UtilCore, runtime-available, pure logic) │
   │   ParseRequest(IDictionary<string,string>) → Request                    │
   │   Invoke(Request) → Result                                              │
   └────────────────────────────────────────────────────────────────────────┘
              │
              ├─ Strict Type resolution via AssemblyExtensions.GetTypeByFullName
              │  (the cache is shared across UCL_Core; case-sensitive, no fallback)
              │
              └─ Argument coercion via Type.TryConvertFromString(string)
                 (extension method; primitive / string / enum / "null" literal)
              │
              ▼
   The targeted built-in Unity API (CompilationPipeline / AssetDatabase / EditorPrefs / EditorApplication ...)
```

**Three decoupled layers**:
| Layer | Location | Purpose |
|---|---|---|
| Trigger | `Cmd_Invoke` / custom Editor button / runtime call | Any source can feed a dict or build a request directly |
| Reflection executor | `UCL.Core.UCL_ReflectionInvoker` (UtilCore) | Pure logic, unit-testable, usable at runtime |
| Type / coercion | `AssemblyExtensions.GetTypeByFullName` (strict) / `Type.TryConvertFromString` | Shared cache and extensible support for more types |

### Direct call example (no Cmd needed; works at runtime too)

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

## 2. Args schema

| key | Required | Default | Description |
|---|---|---|---|
| `type` | ✅ | — | **Fully qualified `Type.FullName`, case-sensitive** (e.g. `UnityEditor.Compilation.CompilationPipeline`); a single mistyped letter fails the call |
| `member` | ✅ | — | Member name (method / property / field) — case-sensitive |
| `kind` | | `method` | `method` / `property` / `field` |
| `paramTypes` | | (empty) | Overload disambiguation: semicolon-separated list of fully qualified types (e.g. `int;string;UnityEditor.ImportAssetOptions`) |
| `args` | | (empty) | Semicolon-separated string arguments, coerced in `paramTypes` order |
| `getter` | | `true` | For property / field — set to `false` to use the setter; `args[0]` is the value to assign |
| `nonPublic` | | `false` | When `true`, BindingFlags additionally include `NonPublic`, allowing internal / private static members to be located (many built-in Unity APIs are internal) |

### 2.1 Argument coercion rules

| Target type | String example → conversion |
|---|---|
| `string` | `"hello"` → `"hello"` |
| `bool` | `"true"` / `"false"` → `bool.Parse` |
| Numeric primitives (int/long/float/double…) | `"42"` → `Convert.ChangeType` |
| enum | `"Default"` → `Enum.Parse(type, value, ignoreCase: true)` |
| reference type / `Nullable<T>` | `"null"` literal → `null` |
| Other complex types | ❌ not supported in v1; write a dedicated Cmd instead |

### 2.2 Type resolution

Goes through `AssemblyExtensions.GetTypeByFullName` for **strict matching**:
1. Exact FQN lookup against `AssemblyExtensions.TypeDic` (cache shared by all of UCL_Core) — O(1)
2. Not found → returns null directly; the caller raises `type not found: ... (use exact Type.FullName, case-sensitive)`

> [!IMPORTANT]
> **`type` must be the fully qualified `Type.FullName` and case-sensitive** — there is no ignoreCase fallback.
> This is intentional: a typo from the agent should be rejected immediately so it cannot silently land on a different type with the same simple name.
> The same rule applies to `paramTypes`.

---

## 3. Examples

### 3.1 Trigger a Unity recompile (parameterless method)

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.Compilation.CompilationPipeline" \
  --arg "member=RequestScriptCompilation"
```

This is equivalent to the core logic of `Cmd_Recompile` (the differences: `Cmd_Recompile` also runs `AssetDatabase.Refresh()` and, when invoked through the `recompile` subcommand, waits for the compile to finish).

### 3.2 Read a property

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.EditorApplication" \
  --arg "member=isCompiling" \
  --arg "kind=property"
```

The Unity Console prints `[AgentCmd:Invoke] OK (System.Boolean) = False`.

### 3.3 Method with an enum argument (overload disambiguation)

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.AssetDatabase" \
  --arg "member=Refresh" \
  --arg "paramTypes=UnityEditor.ImportAssetOptions" \
  --arg "args=Default"
```

`AssetDatabase.Refresh` has two overloads; `paramTypes` pins the one with the enum argument.

### 3.4 Set an EditorPrefs value (property setter)

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.EditorPrefs" \
  --arg "member=SetString" \
  --arg "paramTypes=System.String;System.String" \
  --arg "args=MyKey;MyValue"
```

> Note: this uses the `EditorPrefs.SetString(key, value)` method rather than a setter property, because `EditorPrefs` does not expose an indexed property.

### 3.5 Multi-argument method

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.AssetDatabase" \
  --arg "member=ImportAsset" \
  --arg "paramTypes=System.String;UnityEditor.ImportAssetOptions" \
  --arg "args=Assets/SomeFile.txt;ForceUpdate"
```

### 3.6 internal / private static API (`nonPublic=true`)

Many built-in Unity APIs are `internal` (e.g. `UnityEditorInternal.LogEntries.Clear` or various build-pipeline helpers). They cannot be found with the default `nonPublic=false`; setting `nonPublic=true` is enough:

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditorInternal.LogEntries" \
  --arg "member=Clear" \
  --arg "nonPublic=true"
```

The corresponding error message also includes a hint: when no public member matches, it reports `... — try nonPublic=true` so the next attempt can add the flag.

---

## 4. Result handling

| Situation | Unity Console | Cmd result |
|---|---|---|
| void method success | `OK (void / null)` | Success |
| Method with return value | `OK (TypeName) = value.ToString()` | Success (value is in the Console; for structured output, write a dedicated Cmd) |
| Resolution failure (type / member missing / argument coercion failed) | `LogError + throw` | Failed |
| Internal exception | `target threw {ExceptionType}: ...` | Failed |

> [!CAUTION]
> **Return values currently only land in the Unity Console** (`Debug.Log`); they are not written to disk and not returned to Python.
> If you need a structured value, either (a) write a dedicated Cmd, or (b) wait for a future `outputPath` argument on this Cmd.

---

## 5. Safety / limits

| Item | Description |
|---|---|
| **scope** | Only `Static` is supported (`Public` is always on; `NonPublic` is added when `nonPublic=true`); instance members are not supported |
| **side effect** | Depends on the API being called — calling `RequestScriptCompilation` triggers a domain reload, which kills every in-flight async cmd (this is exactly why `Cmd_Recompile` is designed as "fire request and return") |
| **type ambiguity** | Overloaded methods: `paramTypes` is required, otherwise it errors out (the candidate list is printed in the error) |
| **threading** | Everything runs on the Unity main thread; do not call APIs that block the thread |
| **destructive call** | Agents should not invoke data-loss APIs through this Cmd (e.g. `AssetDatabase.DeleteAsset`) — write a dedicated Cmd with a confirmation flow instead |

---

## 6. Relationship with other Cmds / tools

| Tool | When to use |
|---|---|
| **Cmd_Invoke** (this doc) | One-off calls / exploration / when the agent wants to trigger something but no dedicated Cmd exists |
| **Cmd_Recompile** | Dedicated: triggers recompile and pairs with the Python `recompile` subcommand to wait for completion |
| **Cmd_ResolveAssetReferences** | Dedicated: BFS over the UCL_Asset reference chain (complex output, not a fit for Invoke) |
| **Cmd_FindAssetUsages** | Dedicated: reverse lookup of usage points |
| Write a new Cmd | Same arguments are reused repeatedly / structured output needed / extra validation required — follow [Create_Cmd_Workflow](../../Workflows/Create_Cmd_Workflow.md) |

> [!TIP]
> **Rule of thumb**: use `Cmd_Invoke` while exploring; promote to a dedicated Cmd once it stabilizes.

---

## 7. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `type not found: X (use exact Type.FullName, case-sensitive)` | FQN typo / missing namespace / wrong case | Pull the correct `Type.FullName` from a Unity decompile (case-sensitive) — `unityeditor.AssetDatabase` is not the same as `UnityEditor.AssetDatabase` |
| `static method not found ... — try nonPublic=true` | Method is internal / private static | Add `nonPublic=true` |
| `static method not found` even with nonPublic enabled | Method is an instance member | Instance members are not supported in v1; write a dedicated Cmd |
| `ambiguous method (need paramTypes)` | Same name with multiple overloads | Add `paramTypes` to lock one overload; the error message lists all candidates |
| `enum parse failed` | An enum numeric value was used instead of the name (e.g. `0`) | Use the name (e.g. `Default`) |
| Cmd is marked Failed but no error is visible | The Unity Console is hidden | Open the Console window and look for `[AgentCmd:Invoke] FAILED: ...` |

---

## 8. Related documents

- [Edit_Recompile_Loop_Workflow](../../Workflows/Edit_Recompile_Loop_Workflow.md) — sync loop after editing .cs files (also required after this Cmd ships a new file)
- [Create_Cmd_Workflow](../../Workflows/Create_Cmd_Workflow.md) — when to promote Invoke into a dedicated Cmd
- [Cmd_Recompile](Cmd_Recompile.md) — equivalent to `Invoke(CompilationPipeline.RequestScriptCompilation)` plus waiting
- `UCL.Core.UCL_ReflectionInvoker` (located in `UtilCore/`, runtime-available) — pure logic for parsing / executing; can be called directly from any Cmd / Editor button / runtime tool
- `AssemblyExtensions` (`ExtensionMethodCore/`) — Type resolution cache (`GetTypeByFullName` strict matching) plus `TryConvertFromString`, extensible to support more argument coercions
