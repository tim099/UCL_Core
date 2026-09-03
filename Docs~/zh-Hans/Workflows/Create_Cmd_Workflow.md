---
title: 创建新的 Agent Command Handler 工作流程
description: 步骤化 SOP — 从零生出一支可被 queue.json 触发的 `Cmd_<Name>.cs`。内容覆盖命名规范、文件位置、metadata 字段、ExecuteAsync 撰写守则、Editor 验收流程，以及常见地雷。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# 🛠️ 创建新的 Agent Command Handler 工作流程

> [!IMPORTANT]
> 本工作流只负责「**写一个 `Cmd_<Name>.cs` 子类**」这件事；系统怎么运作（queue / trigger / watcher / runner）请看 [UCL_AgentCommand_Architecture](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)。
>
> 设计哲学：**convention-over-configuration**。只要继承 `UCL_AgentCommandHandlerBase` 并摆对位置，下次 domain reload 就会被 `UCL_AgentCommandRegistry` 反射自动发现 — **不要碰 Registry**。

---

## 0. TL;DR — 五分钟加完一支 Cmd

```
[1] 想清楚 CommandType（PascalCase，AppDomain 全局唯一）
       ▼
[2] 选对位置（§2 决策树）— UCL_Core 内 vs 下游模块
       ▼
[3] 开档 Cmd_<Name>.cs：继承 UCL_AgentCommandHandlerBase
       ▼
[4] 覆写四个 metadata：CommandType / ShortDescription / ArgsSchema / HelpURL
       ▼
[5] 写 ExecuteAsync(args, token)
       ▼
[6] 等 Unity domain reload → 开 UCL_AgentCommandsPage
       → Available Commands 应出现新指令 → Add + Run Pending → 看 Console
```

---

## 1. 前置决策

| # | 问题 | 影响 |
|---|------|------|
| 1 | **CommandType 命名** | PascalCase、动词开头、AppDomain 全局唯一；撞名会 LogError 由后者覆盖 |
| 2 | **OneShot 还是 Repeatable** | 由 agent 写 queue 时决定，handler 不写死；ShortDescription 暗示用途 |
| 3 | **参数有哪些** | 只能用 `Dictionary<string,string>`；复杂物件塞 JSON 字串 |
| 4 | **归属模块** | UCL_Core 内 vs 下游模块（见 §2）|

### 1.1 命名规范速查

| 对象 | 范例 | 规则 |
|---|---|---|
| C# 类别 | `Cmd_DebugLog` | 前缀 `Cmd_` + PascalCase |
| 文件名 | `Cmd_DebugLog.cs` | 与类别名一字不差 |
| `CommandType` 属性值 | `"DebugLog"` | 不含 `Cmd_` 前缀；queue.json 比对大小写不敏感 |
| namespace（UCL_Core 内）| `UCL.Core.EditorLib.AgentCommands` | 框架层通用指令 |
| namespace（下游模块）| `<YourModule>.AgentCommands` | 例：`RCG.AgentCommands` |

> [!CAUTION]
> **严禁** 把 `Editor` 写进 namespace 中段（如 `MyMod.Editor.AgentCommands`），会与 `UnityEditor.Editor` 撞型别引爆 CS0118。

---

## 2. 文件位置决策树

```
这支 Cmd 是…
├── 通用工具（文件 I/O、UCL_Asset 操作、目录导出 — 不依赖任何下游型别）
│       → Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Cmd_<Name>.cs
│         namespace UCL.Core.EditorLib.AgentCommands
│
└── 下游模块特定（呼叫 RCG_*Data / 项目 EditorPage / 改特定游戏资产）
        → 该模块的 AgentCommands 资料夹
          namespace <YourModule>.AgentCommands
```

### 2.1 为什么要分层？

- **UCL_Core 是 submodule**，是可被任何使用 UCL 的 Unity 项目共用的框架；引用下游型别会破坏可移植性
- **下游模块**可放心 `using` 自家命名空间 / 呼叫项目 Editor API
- 判断标准：**这支 Cmd 在没有下游模块的纯 UCL 项目有意义吗？** 有 → UCL_Core；没有 → 下游

> [!TIP]
> 若不确定 → 先写在下游模块；之后若发现可通用化再升级到 UCL_Core。**反向降级** 比较痛苦。

---

## 3. 标准范本（复制这份再改）

```csharp
// Handler: <CommandType> — <一句话说明这支 Cmd 在做什么>
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 区块职责：<这支 Cmd 的职责、何时被触发、谁是预期使用者（agent / 人类）>
    /// 物理意义：<会修改的资产 / 写入的文件 / 对游戏状态的影响>
    /// 数值影响：<若无数值修改填「无」；有则具体说明影响范围>
    /// </summary>
    public class Cmd_Example : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Example";
        public override string ShortDescription => "范例 Cmd — 把 args[\"msg\"] 印到 Console";
        public override string ArgsSchema =>
            "msg=要印出的字串（选填，预设 \"hello\"）";
        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md";

        public override async UniTask ExecuteAsync(
            Dictionary<string, string> args, CancellationToken token)
        {
            string msg = args != null && args.TryGetValue("msg", out var m) ? m : "hello";
            Debug.Log($"[AgentCmd] {CommandType} → {msg}");
            await UniTask.CompletedTask;
        }
    }
}
#endif
```

### 3.1 必填字段一览

| 字段 | 型别 | 必填 | 说明 |
|---|---|---|---|
| `CommandType` | `string` | ✅ | queue.json `"Type"` 的比对基准；PascalCase；不含 `Cmd_` 前缀 |
| `ShortDescription` | `string` | ⚠强烈建议 | UI 下拉与 catalog 的一行摘要 |
| `ArgsSchema` | `string` | ⚠强烈建议 | `key=说明` 格式；无参数写 `"(无参数)"` |
| `HelpURL` | `string` | 选填 | `ucl_core:` / 下游模块自注册 prefix |
| `ExecuteAsync` | `UniTask` | ✅ | 真正的逻辑入口；无 await 点时用 `await UniTask.CompletedTask` 收尾 |

---

## 4. ExecuteAsync 撰写守则

### 4.1 进入点假设（runner 已帮你做的事）

`UCL_AgentCommandRunner` 在分派任何 handler 前**已经**：

1. ✅ `await UCL_ModuleService.WaitUntilInitialized(token)` — 模块系统就绪
2. ✅ 执行绪在 Unity main thread；可呼叫 `AssetDatabase` / `EditorUtility`
3. ✅ 可安全呼叫 `UCL_Asset<T>.Util.GetData(...)`

> [!IMPORTANT]
> **不要在 handler 内再 `await UCL_ModuleService.WaitUntilInitialized`**。模块特定预热请在 handler 内自行 `await`。

### 4.2 参数解析惯例

| 场景 | 写法 |
|---|---|
| 必填 string | `args.TryGetValue("k", out var v)` → 缺则 `throw new ArgumentException(...)` |
| 选填 string + 预设 | `args.TryGetValue("k", out var v) ? v : "default"` |
| bool | `args.TryGetValue("k", out var v) && bool.TryParse(v, out var b) && b` |
| int | `args.TryGetValue("k", out var v) && int.TryParse(v, out var n)` |
| 复杂物件 | JSON 字串塞进 value，handler 内 `JsonUtility.FromJson<T>(args["k"])` |

### 4.3 错误处理

- **参数错** → `throw new ArgumentException($"[{CommandType}] ...")`
- **资产不存在** → `throw new InvalidOperationException(...)`
- **不可恢复** → 同上，**不要** 自己 catch 后吞掉
- **token 取消** → 每个回圈 iteration 开头 `token.ThrowIfCancellationRequested()`

### 4.4 输出文件的路径惯例

> [!IMPORTANT]
> - `queue.json` → **git root** 的 `AgentCommands/queue.json`
> - Cmd 产出的文件 → **Unity project root** 的 `AgentCommands/<output>.md`（不是 `Assets/`！）

```csharp
string outputPath = args.TryGetValue("outputPath", out var p)
    ? p : "AgentCommands/default_report.md";
string fullPath = System.IO.Path.Combine(
    UnityEngine.Application.dataPath, "..", outputPath);
System.IO.File.WriteAllText(fullPath, content);
```

---

## 5. 验收 SOP

### 5.1 Editor 内验收（必跑）

1. **存档等 Unity 编译** — Console 没红字
2. **开页面** — `Tools/UCL/Agent Commands/Open Page` 进 [`UCL_AgentCommandsPage`](../UCL_EditorPage/UCL_AgentCommandsPage.md)
3. **找新 Cmd** — Available Commands 应出现 `<CommandType> — <ShortDescription>`
4. **展开 Args Schema**
5. **点「查看说明」**
6. **Add 一笔测试** — Type 选你的 Cmd → Mode 选 OneShot → Args 填 `key=value;key=value`
7. **▶ Run Pending Commands** — 看 Console
8. **OneShot 验收**：成功则该笔从 queue 消失；失败留在 queue + `LastRunError`

### 5.2 Python wrapper 验收

```bash
senate ucmd run <CommandType> \
    --arg key=value --timeout 60
```

---

## 6. 常见地雷

| # | 地雷 | 症状 | 解法 |
|---|---|---|---|
| 1 | **忘了 `#if UNITY_EDITOR`** | Build 时找不到基底 | 整个文件包进 `#if UNITY_EDITOR ... #endif` |
| 2 | **CommandType 撞名** | Console 出 `duplicate CommandType` | 重新命名（大小写不敏感）|
| 3 | **namespace 中段含 `Editor`** | CS0118 | 改用不含 `Editor` 中段的 namespace |
| 4 | **`ExecuteAsync` 没 await** | warning CS1998 | 结尾加 `await UniTask.CompletedTask;` |
| 5 | **自己 catch 吞错** | runner 看不到错 | 错误就 throw |
| 6 | **没等编译就 Run** | 跑的还是旧 handler | 等 Unity 编译完成 |
| 7 | **输出落 `Assets/...`** | 被 Asset Database 收进去 | 输出请落 `<UnityProjectRoot>/AgentCommands/` |
| 8 | **UCL_Core 端 Cmd 引用下游型别** | 破坏 submodule 可移植性 | 通用化或搬回下游模块 |

---

## 7. 整合既有 EditorPage 逻辑（推荐模式）

> [!TIP]
> 大多数情境下 Cmd 不该重新发明轮子 — 若 Editor 内已有按钮能做这件事，**直接呼叫该按钮背后的 static method**。

```csharp
public override async UniTask ExecuteAsync(
    Dictionary<string, string> args, CancellationToken token)
{
    SomeEditorPage.DoExport();
    await UniTask.CompletedTask;
}
```

---

## 8. 最小骨架范本

```csharp
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands  // 下游模块请改自家 namespace
{
    /// <summary>
    /// <一句话说明这支 Cmd 的职责>
    /// </summary>
    public class Cmd_<Name> : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "<Name>";
        public override string ShortDescription => "<UI 用一行摘要>";
        public override string ArgsSchema => "(无参数)";
        public override string HelpURL => "ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md";

        public override async UniTask ExecuteAsync(
            Dictionary<string, string> args, CancellationToken token)
        {
            Debug.Log($"[AgentCmd] {CommandType} executed.");
            await UniTask.CompletedTask;
        }
    }
}
#endif
```

---

## 9. 文件放置自动判断方案（给未来）

> 本工作流之所以放在 `Assets/UCL/UCL_Core/Docs~/` 而非项目层 `docs/Workflows/`，是因为它**仅描述 UCL_Core 框架本身的扩充方法**，不依赖任何下游型别。

### 9.1 判定原则：依「描述对象的 source 位置」决定

| 文件描述的对象… | 文件应住哪里 |
|---|---|
| 完全位于 `Assets/UCL/UCL_Core/` 内 | `Assets/UCL/UCL_Core/Docs~/{lang}/...`（多语系）|
| 完全位于下游模块 | `docs/...`（项目层）|
| 跨两者 — 描述「下游 X 如何用 UCL_Core 的 Y」 | `docs/Workflows/`（呼叫方视角）|

### 9.2 强制 frontmatter 字段

```yaml
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/
namespace: UCL.Core.EditorLib.AgentCommands
```

自动分类器只要：

1. 读 `source_root`
2. `startsWith("Assets/UCL/UCL_Core/")` → 应住 `Assets/UCL/UCL_Core/Docs~/{lang}/`
3. `startsWith("Assets/Scripts/")` → 应住 `docs/`
4. 不一致 → 警告 + 提示搬移

### 9.3 自动化实作：`Cmd_ValidateDocPlacement`

| 步骤 | 动作 |
|---|---|
| 1 | 扫 `Assets/UCL/UCL_Core/Docs~/**/*.md` + `docs/**/*.md`，读每份 frontmatter |
| 2 | 取 `source_root`，依 §9.1 计算「应住路径」 |
| 3 | 比对实际路径；不符 → 列入 violations |
| 4 | 额外检查 UCL_Core 端文件**不可** grep 到下游 namespace |
| 5 | 输出 `AgentCommands/doc_placement_report.md` |

### 9.4 建立新文件的最小检查清单

- [ ] 我描述的对象的 .cs 文件在哪？
- [ ] 该 .cs 是否引用任何下游型别？
- [ ] frontmatter 是否填好 `source_root` / `namespace` / `last_updated` / `target_audience`？
- [ ] 多语系：4 份（住 UCL_Core）还是 1 份（住项目层）？

---

## 10. 相关文件

- 🤖 [UCL_AgentCommand_Architecture](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)
- 📖 [UCL_AgentCommand](../API/UCL_AgentCommand/UCL_AgentCommand.md)
- 🪟 [UCL_AgentCommandsPage](../UCL_EditorPage/UCL_AgentCommandsPage.md)
- 🔗 [HelpURL_Workflow](HelpURL_Workflow.md)
- 📁 UCL_Core handler 目录：`Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/`
- 📖 项目层补充：[`docs/Workflows/AgentCommands_Workflow.md`](../../../../../../docs/Workflows/AgentCommands_Workflow.md)

---

## 其他语系

- 🇬🇧 [English](../../en/Workflows/Create_Cmd_Workflow.md)
- 🇯🇵 [日本語](../../ja/Workflows/Create_Cmd_Workflow.md)
- 🇨🇳 简体中文（本档）
- 🇹🇼 [繁體中文](../../zh-Hant/Workflows/Create_Cmd_Workflow.md)
