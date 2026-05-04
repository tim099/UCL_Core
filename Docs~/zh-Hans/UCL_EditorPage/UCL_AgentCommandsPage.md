---
title: UCL_AgentCommandsPage
description: 用于排队、查看、触发存储于 AgentCommands/queue.json 的 agent 指令的编辑器页面。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AgentCommandsPage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-05-04
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# UCL_AgentCommandsPage

## 1. 概览

`UCL_AgentCommandsPage` 是 **Agent Commands** 系统的编辑器 UI — 一条轻量管线：AI agent 把要做的编辑器动作写进 JSON 文件，使用者（或 agent 间接）在 Unity Editor 内按按钮触发执行。

页面位置：

- **代码**：`Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AgentCommandsPage.cs`
- **UCL Core 菜单**：`Tools/UCL/Agent Commands/...`（由 [`UCL_AgentCommandRunner`](#5-相关类) 提供）
- **项目入口（RCG / Emblem of Valor）**：`EditorMenu → Agent Commands` 按钮

它是个薄薄的 IMGUI 壳，组合四个协作类型：

| 类型 | 角色 |
|---|---|
| `UCL_AgentCommand` | 一条排队指令的数据模型（Id / Type / Mode / Args / 执行结果） |
| `UCL_AgentCommandQueue` | 读写 `<repoRoot>/AgentCommands/queue.json` |
| `UCL_AgentCommandHandlerBase` | 所有 handler 的抽象基类 — 反射自动发现 |
| `UCL_AgentCommandRegistry` | 收集已发现的 handler，按 `CommandType`（大小写不敏感）索引 |
| `UCL_AgentCommandRunner` | 异步 runner — 分派前先 await `UCL_ModuleService.WaitUntilInitialized` |

## 2. 页面布局

```
┌─ TopBar ────────────────────────────────────────────────────────────┐
│ [Back] [Close] | UCL_AgentCommandsPage [Copy] [Refresh] [Run] [...] │
├─ Queue 路径 / 统计 ─────────────────────────────────────────────────┤
│ Queue: <repo>/AgentCommands/queue.json                              │
│ Total: 3 | Pending: 1 | Done: 1 | Repeatable: 1                     │
├─ Commands（queue.json 内容） ───────────────────────────────────────┤
│ ● [Pending] ExportEquipmentNotes (OneShot)            [Remove]      │
│ ● [Done]    Ping                  (Repeatable)        [Remove]      │
├─ Available Commands（从 Registry 自动列出） ────────────────────────┤
│ ExportEquipmentNotes  [查看说明] [+ OneShot] [+ Repeatable]         │
│   导出全部 Equipment 的 Note / Description 为 Markdown              │
│   ▶ Args Schema                                                     │
│ Ping  [查看说明] [+ OneShot] [+ Repeatable]                         │
│   Sanity check — 把 args["msg"] 打印到 Console                      │
│   ▶ Args Schema                                                     │
├─ Add Command（手动 fallback） ──────────────────────────────────────┤
│ Type: [已注册类型 grid]                                             │
│ Schema: msg=任意字符串（选填，默认 "pong"）                          │
│ Mode: ( ) OneShot  ( ) Repeatable                                   │
│ Description: [...]   Args: [k=v;k=v]                                │
│ [Add 'Ping' (OneShot)]                                              │
└─────────────────────────────────────────────────────────────────────┘
```

## 3. 顶部按钮

| 按钮 | 行为 |
|---|---|
| `Refresh` | 从硬盘重新加载 `queue.json` 到内存缓存 |
| `Run Pending Commands` | 调用 `UCL_AgentCommandRunner.Menu_RunPending()`（async）；约 1.5 秒后自动 refresh |
| `Open Folder` | 直接打开 `AgentCommands/` 文件夹 |

## 4. 如何新增一个指令类型

指令系统采用 **convention-over-configuration**：写一个继承 `UCL_AgentCommandHandlerBase` 的 class 就好。下次 domain reload 时，registry 会通过反射自动发现。

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
        public override string ShortDescription => "在 Console 打印一句问候。";
        public override string ArgsSchema => "name=要问候的对象";
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

Unity 重新编译后，`HelloWorld` 会自动出现在 **Available Commands** 列表中，包含 `ShortDescription`、可折叠的 `ArgsSchema`，以及 `[查看说明]` 按钮（若设置了 `HelpURL`）。

> [!IMPORTANT]
> `CommandType` 比对**大小写不敏感**，但在整个 AppDomain 中必须**唯一**。重复的 type 会 log error 并由后注册者覆盖。

## 5. 相关类

| 类 | 文件 | 备注 |
|---|---|---|
| `UCL_AgentCommand` | `EditorCore/UCL_AgentCommands/UCL_AgentCommand.cs` | 数据模型 + `UCL_AgentCommandMode` enum（OneShot / Repeatable） |
| `UCL_AgentCommandQueue` | `EditorCore/UCL_AgentCommands/UCL_AgentCommandQueue.cs` | 手写 JSON I/O（Unity `JsonUtility` 不支持 `Dictionary`） |
| `UCL_AgentCommandHandlerBase` | `EditorCore/UCL_AgentCommands/UCL_AgentCommandHandlerBase.cs` | 重写 `CommandType` + `ExecuteAsync` 即可，其余皆选填 |
| `UCL_AgentCommandRegistry` | `EditorCore/UCL_AgentCommands/UCL_AgentCommandRegistry.cs` | static ctor 内扫 `typeof(UCL_AgentCommandHandlerBase).GetAllSubclass()` |
| `UCL_AgentCommandRunner` | `EditorCore/UCL_AgentCommands/UCL_AgentCommandRunner.cs` | 分派前 `await UCL_ModuleService.WaitUntilInitialized(token)` |

## 6. queue.json 结构

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
      "Description": "（选填）agent 留下的人类可读备注"
    }
  ]
}
```

| 字段 | 意义 |
|---|---|
| `Id` | 唯一标识符；惯例为 `yyyyMMdd-HHmmss-<typelower>` |
| `Type` | 必须对应已注册 handler 的 `CommandType`（大小写不敏感） |
| `Mode` | `"OneShot"`（成功后直接从 queue 移除）或 `"Repeatable"`（每次都跑） |
| `RunCount` | 成功执行的次数，由 runner 累加。OneShot 在 RunCount 增至 1 之前就已被移除，因此此字段主要对 Repeatable 有意义。 |
| `Args` | 自由格式 `string→string` map，传给 `ExecuteAsync` |
| `LastRun*` | runner 写入；`Result` 为 `"Success"` / `"Failed"` |

## 7. 初始化契约

Runner 在分派任何 handler 之前先调用 `UCL_ModuleService.WaitUntilInitialized(token)`。这个 API 同时负责**触发**延迟初始化（通过访问 `UCL_ModuleService.Ins`）与**等待**完成 — 因此 handler 可以安全假设模块系统就绪、`UCL_Asset.Util.GetData()` 返回非 null。

> [!NOTE]
> 若 handler 需要项目专属的预热（如 `RCG_IconSprite.InitSpriteAsset`），请在该 handler 内自行 `await`。框架 runner 不应反向依赖项目层。

## 8. 相关文档

- [`UCL_CommonEditorPage`](./UCL_CommonEditorPage.md) — 直接父类
- [`UCL_ModuleService_API`](../UCL_ModuleService/UCL_ModuleService_API.md) — 解释 `WaitUntilInitialized`
- `Workflows/HelpURL_Workflow.md`（本 repo） — `ucl_core:` / `eov_docs:` URL 机制

## 9. 陷阱

> [!CAUTION]
> **不要写 `Register(...)` 调用**，像旧 RCG 版那样。新 registry 纯反射驱动 — `UCL_AgentCommandRegistry` 上根本没有 `Register` 方法。

> [!IMPORTANT]
> Editor-only。整套系统包在 `#if UNITY_EDITOR` 内，runtime 代码路径不得引用。
