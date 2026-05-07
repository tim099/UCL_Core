---
title: 编辑 → 重编 → 修错循环工作流
description: 步骤化 SOP — agent / 工具开发者改完 .cs 后如何强制 Unity 重编、确认 compile error、循环修到 0 errors。建立在 Cmd_Recompile + UCL_CompileErrorTracker + run_cmd.py 之上。
source_root: AgentCommands/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [edit recompile loop, script edit loop, compile error fix loop, agent compile loop]
tags: [workflow, agent, compile, recompile, error-fix]
---

# 🔁 编辑 → 重编 → 修错循环工作流

> [!IMPORTANT]
> 本工作流负责「**改完 .cs 后怎么确认真的编进去 + 没踩 compile error**」这条 SOP。
> agent 改完档不要假设 Unity 已 reload — 在 Editor 没有焦点 / Auto Refresh 关闭的情况，
> 你写的 code 可能根本还没进 assembly，后续 Cmd 全部跑旧版。

> 设计哲学：**强制同步点**。`Cmd_Recompile` + Python `recompile` 子命令是「**这之前的所有 .cs 变动都已被反映**」的承诺边界。

---

## 0. TL;DR — 五分钟吃懂循环

```
[1] 编辑 / 生成 .cs 文件（Edit / Write）
       ▼
[2] python run_cmd.py recompile     ← 触发 Unity 重编 + 等到完成
       │
       ├── exit 0  → clean，继续后续流程
       └── exit 1  → 有 compile error
              ▼
[3] 读 AgentCommands/.compile_status.json 的 messages
       ▼
[4] 对每个 error 看 file:line 修源
       ▼
[5] goto [2]（最多 N 轮，建议 ≤ 5；超过代表方向错，叫人类）
```

---

## 1. 前置条件（每个 session 开始检一次）

| # | 检查项 | 怎么确认 | 没过怎办 |
|---|---|---|---|
| 1 | Unity Editor 开着 | 系统工作列 / 视窗能看到 | 开 Unity，载入项目 |
| 2 | Auto-Watcher 启用 | UCL_AgentCommandsPage 看 `Auto-Watcher ✔` | 点 checkbox 切到 ✔ |
| 3 | `run_cmd.py` 可调用 | `python <路径> --help` 印出 usage | 修 PATH / 确认 Python 安装 |
| 4 | `.compile_status.json` 存在 | `AgentCommands/.compile_status.json` | 在 Unity 触发过一次 compile（任意改档再保存） |

> [!CAUTION]
> Auto-Watcher 若 Idle，所有 Cmd 会卡 pending。没启用就**用不了** `recompile` 子命令。

---

## 2. 为什么要强制走 `recompile`？

agent 写完 `.cs` 后 Unity 不一定立刻编译：

| Unity 状态 | 行为 |
|---|---|
| Editor 有焦点 + Auto Refresh ON | 立刻 detect file change → 编译（最理想） |
| Editor 在背景 + Auto Refresh ON | 焦点回来才 detect（agent 角度看不到此时机） |
| Auto Refresh OFF | 完全不会自动编译，得手动 Ctrl+R |
| 上一次 compile 失败 | 卡在错误状态，新 Cmd handler 载入不进来 |

**结论**：agent 改完 `.cs` **不能假设**修改已生效。必跑 `recompile` 强制同步，并从 exit code 确认 0 errors。

---

## 3. 核心循环（pseudocode）

```python
import subprocess, json
from pathlib import Path

RUN_CMD = "CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py"
STATUS  = Path("AgentCommands/.compile_status.json")

def recompile_and_check() -> tuple[int, list]:
    """returns (errors_count, messages); errors_count==0 → clean"""
    rc = subprocess.run(["python", RUN_CMD, "recompile"], capture_output=False)
    if rc.returncode == 0:
        return 0, []
    if rc.returncode == 1:
        st = json.loads(STATUS.read_text(encoding="utf-8-sig"))
        return st["total_errors"], [m for m in st["messages"] if m["type"] == "Error"]
    raise RuntimeError(f"infra failure: exit code {rc.returncode}")

# 主循环
MAX_ROUNDS = 5
for round_idx in range(MAX_ROUNDS):
    edit_files(...)            # agent 改 / 生成 .cs
    err_count, errors = recompile_and_check()
    if err_count == 0:
        break
    for e in errors:
        print(f"× {e['file']}:{e['line']}  {e['message']}")
        fix_error(e)            # 读源 + Edit
else:
    raise RuntimeError(f"still {err_count} errors after {MAX_ROUNDS} rounds — STOP, ask human")
```

---

## 4. 详细步骤

### 4.1 编辑 / 生成 .cs 文件
- 用 Edit / Write 工具改源
- **不要**手动建 `.meta`（Unity 自动生成；见 memory `feedback_no_direct_meta.md`）
- 多档变动可一次改完，最后再 recompile（避免每 1 档跑 1 次）

### 4.2 触发 recompile
```bash
python "CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py" recompile
```

**Exit code 对照**：

| exit | 意义 | 行动 |
|---|---|---|
| 0 | compile 完成、0 errors | 继续 |
| 1 | compile 完成、有 errors | 进 4.3 修错 |
| 2 | Cmd_Recompile 没被 Unity 接手（queue 没清） | 检前置 §1 — Watcher / Editor 状态 |
| 3 | `.compile_status.json` 解析失败 | 文件损毁 / 编码问题 |
| 4 | mtime 没推进（compile 没跑） | UCL_CompileErrorTracker 没挂上事件？看 [CompileError_Diagnose_Workflow](CompileError_Diagnose_Workflow.md) |

### 4.3 读错误消息
- stdout 印前 5 条
- 完整：`AgentCommands/.compile_status.json` 的 `messages` 数组，字段：
  - `type`: `"Error"` / `"Warning"`
  - `file`: 相对路径（从 Unity project root 算起）
  - `line`: 行号
  - `column`: 列号
  - `message`: 错误文字（含 CS 编号，如 `CS0103: ...`）

### 4.4 修错
对每个 error：
1. **开源**：用 Read 工具看 `file:line` 的程式码上下文（前后 ±10 行）
2. **判错**：对照常见 CS 错误（见 [CompileError_Diagnose_Workflow §常见错误](CompileError_Diagnose_Workflow.md)）
3. **修源**：用 Edit 改最小范围
4. **避免联动破坏**：改 `RCG_X` 看是否有别处引用（先 Grep 一下）

### 4.5 回到 4.2
跑 recompile，确认该 error 消失（且没引入新的 error）。

### 4.6 退出条件
- ✅ exit 0 → 进入后续工作流（如 ExportNotes / 测试 / commit）
- ❌ 连续 5 轮仍有 error → **停下来**，把错误列表 + 你尝试过的 fix 给人类接手。盲目改下去只会越搞越糟。

---

## 5. 故障模式对照

| 症状 | 可能原因 | 排查 / 修法 |
|---|---|---|
| `recompile` exit 2，queue 卡 Recompile cmd | Auto-Watcher 没启用 | 开 UCL_AgentCommandsPage，确认 `✔ Auto-Watcher` |
| `recompile` exit 4 | UCL_CompileErrorTracker 没写 status | 看 `Tracker just loaded, no compile event captured yet` placeholder；任意改档触发一次 compile 即可 |
| 同一 error 改了没消 | Unity 没重编到目标 file | 确认 file 真的存了；再跑 `recompile` |
| 改 file A 却 file B 报错 | namespace / asmdef 隔离；CS0246 缺 using | 看 [CompileError_Diagnose_Workflow §asmdef](CompileError_Diagnose_Workflow.md) |
| 反复引发新 error | 改源时 break 了 contract | 退回原版 + 重新规划；可能该停下找人类 |
| `recompile` 跑了但内容没生效 | 改的档在 `_Editor` 子模块 / 在 `Editor/` 子目录 | 对应 asmdef 是否 dirty + script type 是否 Editor-only |

---

## 6. 跟其他工作流的关系

```
   Create_EditorPage_Workflow          建立新 page
   Create_Cmd_Workflow                 建立新 Cmd
              │
              ▼ 改完 .cs 后
   ┌──────────────────────────────────────┐
   │  Edit_Recompile_Loop_Workflow（本档）│  ← 强制同步 + 修错
   └──────────────────────────────────────┘
              │
              ▼ compile 0 errors 后
   后续：跑 Cmd_ExportNotes / 自动测试 / commit

   compile error 解析细节：见 CompileError_Diagnose_Workflow
```

---

## 7. 使用范例

### 范例 A：agent 加新 Cmd 后验证
```bash
# 1. 用 Edit / Write 建立 Cmd_Foo.cs
# 2. 触发 recompile
python "CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py" recompile
# → 预期 exit 0；若 exit 1 看 compile_status.json 修错后再跑

# 3. 确认新 Cmd 已注册
python "CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py" catalog | grep "Foo"

# 4. 跑新 Cmd
python "CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py" run Foo --arg x=1
```

### 范例 B：agent 重构某个 EditorPage 后验证
```bash
# 1. 改 RCG_StoryDataEditorPage.cs
# 2. recompile
python "CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py" recompile
# 3. 跑 ExportNotes 验证输出对齐
python "CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py" run ExportNotes --arg targets=story
# 4. 开档目视 / git diff 比对
```

---

## 8. 验收清单

agent 自我检查（每轮结束时跑一次）：

- [ ] 最近一次 `recompile` exit 0
- [ ] `.compile_status.json` 的 `total_errors == 0`
- [ ] 改的目标 .cs 没有残留 `__DELETE_ME__` / `_Deprecated` 等暂时 marker
- [ ] 没手动建任何 `.meta`
- [ ] 退出循环时 round 数 ≤ 5（超过代表卡死，不该继续）

---

## 9. 相关文档

- [Create_Cmd_Workflow](Create_Cmd_Workflow.md) — 建立新 `Cmd_<Name>.cs`
- [Create_EditorPage_Workflow](Create_EditorPage_Workflow.md) — 建立新 `UCL_*Page`
- [CompileError_Diagnose_Workflow](CompileError_Diagnose_Workflow.md) — 细致的 compile error 排查（asmdef / CS0246 等）
- [HelpURL_Workflow](HelpURL_Workflow.md) — `[HelpURL]` prefix 解析
- `run_cmd.py` — Python CLI 包装器（`recompile` / `run` / `submit` / `wait` / `catalog`）
- `Cmd_Recompile` — Editor 端触发重编的 Agent Command
