---
title: UCL_AgentCommandsPage
description: 用于排队、查看、触发存储于 AgentCommands/queue.json 的 agent 指令的编辑器页面。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AgentCommandsPage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-08-21
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

## 3b. 区块折叠（Tim 2026-08-21）

本页的大区块（队列现况 / 新增指令 / 失败记录 / 模板 / 历史 / 提示）各自可折叠，
折叠钮一律走 `UCL_GUILayout.Toggle`（▼/►），状态存页面 instance 的 `m_FoldDic`。

| 区块 | 默认 |
|---|---|
| 📋 队列现况（queue 路径 / Watcher 状态栏 / 指令清单） | 展开 |
| ➕ 新增指令（指令下拉 + 表单） | 展开 |
| ❌ 失败记录 | **有失败时展开**，没有就收合 |
| 模板 / 历史 / 💡 提示 | 收合 |

> [!NOTE]
> **queue 选择器刻意不可折叠** —— 它决定其他每个区块在讲哪条 queue，收起来会让底下所有读数失去主词。
> 折叠的标题栏**收合时仍显示摘要**（queue 统计、失败笔数、模板/历史笔数）：
> 收合把信息一起藏掉的话，人得先展开才知道「这里有没有事」，那等于没有折叠。

## 3c. ❌ 失败记录面板（可补跑，Tim 2026-08-21）

失败的 OneShot 自 2026-08-07 起会**即时出队**（避免 queue 堵塞与副作用重放），
所以从 queue 清单上看不到它们。本面板列出 `<DataRoot>/_cmd_failed/<cmdId>.json` ——
**所有**失败的 Cmd，不限于某一种。

| 文件 | 内容 | 保存期 |
|---|---|---|
| `_cmd_results/<id>.json` | 机器可读 verdict（**不含 Args**） | 3 天后自动清除 |
| `_cmd_errors/<id>.md` | 给人读的完整 stack + Args | 永久 |
| `_cmd_failed/<id>.json` ★ | **结构化、可补跑**：Type / Mode / Args / error / queueId / 补跑痕迹 | 直到补跑或手动删除 |

★ 由 `UCL_AgentCommandFailedStore` 在 Runner 的失败分支写入。
为什么要第三份：前两份都补跑不了 —— 一份没有 Args，另一份是**给人读的视图**
（对人类视图写 parser 等于第二份真相源，格式一改就静默坏掉）。

| 按钮 | 行为 |
|---|---|
| `补跑` | 以**原本那条 queue**（记录的 `QueueId`）新增一笔新 cmd（新 id）并立刻执行；原记录保留并累加 `RetryCount` |
| `填回表单` | 把 Type / Mode / Args 填回「新增指令」表单 —— 打错参数那类失败要**改完再跑**就走这里 |
| `删除` / `清除全部记录` | 只删记录，不动任何 queue（全部清除有二段确认） |

> [!CAUTION]
> **补跑＝重新执行一次，副作用会重放** —— 酒馆公告会重发（同 SHA 领两次薪）、转账会重转。
> 所以这里只有人按的按钮，**没有自动重试**：`ensure_idle` 逾时那种失败代表「没送出」（重试安全），
> 但**送出之后**的失败可能其实已经生效了，而两者在画面上长得一样。

> [!IMPORTANT]
> **补跑会挡在「那条 queue 正在跑」的情况** —— Runner 开跑时把 queue 读成内存清单、收尾时整批写回，
> 期间任何 load→add→save 都会被覆盖（lost update）。
> 🩸 首次验收实测：从一个正在该 queue 执行的 Cmd 里调用补跑，记录标成「已补跑」、log 印了新 cmd id，
> 而 queue.json 收尾后是空的 —— **补跑凭空消失且零错误信息**。
> 现在的行为：先检查 `IsRunningForAgent`，写入后**回读验证新 id 在不在**，验不到就不标记补跑。

> [!NOTE]
> 本 store 是 2026-08-21 才加的 ⇒ **之前的失败没有结构化记录，补跑不了**。
> 标题栏会另外显示那个笔数（由 `_cmd_errors/` 数出来），刻意不把「不能补」画成「没有失败」。

## 3d. 选定指令后自动填入范例值（Tim 2026-08-21）

在「新增指令」下拉切换指令时，Args 字段会**自动填入该 handler 的 `ExampleArgs`**
（没有声明范例则清空）—— 换了指令，字段里的旧 args 就属于别的指令了，留着比清空更糟：
它看起来像一组有效参数。「填入范例」按钮保留。

> [!NOTE]
> 从模板 / 历史 / 失败记录「填回表单」时**不会**被范例值盖掉 —— 那些路径会同步自动填入的检测基准。

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

## ★ NEW: Lock-file Watcher (auto-trigger)

Since 2026-05-05, `UCL_AgentCommandWatcher` (`[InitializeOnLoad]`) polls `<repoRoot>/AgentCommands/pending.trigger` once per second; when present, it does an atomic `File.Move` to `pending.trigger.running` and invokes the Runner. The page now shows a Watcher status row (Auto-Watcher toggle / Idle/Pending/Running indicator / Last trigger time / Simulate Trigger button).

The "Export Cmd Catalog" stand-alone button is removed — add an `ExportCommandCatalog` cmd via the Add Command form instead (same code path, same output).

Python wrapper: `<UCL_CORE>/Tools~/AgentCommands/run_cmd.py` (writes the trigger; `ensure_idle()` blocks if a previous batch hasn't finished).

For the full design (state machine, ensure_idle, failure modes), see the project workflow doc: `docs/Workflows/AgentCommands_Workflow.md` §8a.0.
