---
title: Unity Compile Error 排查工作流程
description: 用 UCL_CompileErrorTracker 写的 .compile_status.json + check_compile.py 工具，让 agent 即使在 Cmd 系统因 compile error 也载不进来的鸡生蛋情境下也能读到完整错误清单；含 dedupe / log fallback / session 边界侦测 / 4 步排查 SOP / 8 大常见错误类型对照 / 实战 case study
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [编译错误, compile error, CompileError, CS0103, CS0117, CS1503, asmdef, debug, troubleshooting]
tags: [compile, debug, agent_commands, workflow]
---

# 🔧 Unity Compile Error 排查工作流程

> [!IMPORTANT]
> **解决什么问题**：编译失败时 Cmd 系统也跟着挂掉 → 最需要查错误时反而没法用 Cmd。**核心工具是 standalone Python 脚本** [`check_compile.py`](../../../Tools~/AgentCommands/check_compile.py)，**完全不依赖 Cmd 系统**。

## 0. TL;DR

```bash
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only --fallback-log
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --watch --watch-timeout 60
```

退出码：`0` clean / `2` 有 error / `3` 找不到 status file。

## 1. 两条数据来源

- ⭐ `.compile_status.json` — Tracker 写的，**最新** 单次结果
- Editor.log fallback — 累积多次 compile，需 dedupe + session 边界侦测

## 2. 4 步排查 SOP

1. 跑 check_compile.py 看 dedupe 后的错误数
2. **Stale vs Fresh** 交叉验证 — 打开档案对应行确认错误描述是否仍吻合
3. 找 root cause（cascade 错误别一个一个修）
4. 修后 focus Unity 触发 recompile，循环

## 3. 常见错误类型

| Code | 典型修法 |
|---|---|
| CS0103 | 加 using / fully qualify / 该 type 自己有错先修 |
| CS1503 (tuple lambda) | 把 tuple 拆成平铺参数 |
| CS0246 / CS0234 | asmdef references 缺 / namespace 错 |

## 4. asmdef 跨界

UCL_Core 单向依赖：`UCL_CoreEditor → UCL_Core`。`UCL_Core` 看不到 `UCL_CoreEditor` 的 type → 把 type 搬进 UCL_Core asm 或用 `EditorApplication.ExecuteMenuItem`。

## 5. Tracker 的 chicken-and-egg

只在 domain reload（前次成功 compile）时跑 ctor。首次启动带 error 时 `.compile_status.json` 不会出现 — 用 `--fallback-log` 解 Editor.log。

## 6. 关联文档

- [Cmd_GetCompileErrors](../API/UCL_AgentCommand/Cmd_GetCompileErrors.md)（待补）
- [Create_Cmd_Workflow](Create_Cmd_Workflow.md)
- [UCL_AgentCommand_Architecture](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)

---

## 其他语系

- 🇬🇧 [English](../../en/Workflows/CompileError_Diagnose_Workflow.md)
- 🇯🇵 [日本語](../../ja/Workflows/CompileError_Diagnose_Workflow.md)
- 🇨🇳 简体中文（本档）
- 🇹🇼 [繁體中文](../../zh-Hant/Workflows/CompileError_Diagnose_Workflow.md)
