---
title: Unity Compile Error 排查工作流程
description: 用 UCL_CompileErrorTracker 写的 .compile_status.json ＋ Senate CLI（unity-recompile / unity-compile-status，python check_compile.py 已于 2026-09-10 退场），让 agent 即使在 Cmd 系统因 compile error 也载不进来的鸡生蛋情境下也能读到完整错误清单；含 dedupe / log fallback / session 边界侦测 / 4 步排查 SOP / 8 大常见错误类型对照 / 实战 case study
last_updated: 2026-09-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [编译错误, compile error, CompileError, CS0103, CS0117, CS1503, asmdef, debug, troubleshooting]
tags: [compile, debug, agent_commands, workflow]
---

# 🔧 Unity Compile Error 排查工作流程

> [!IMPORTANT]
> **解决什么问题**：编译失败时 Cmd 系统也跟着挂掉 → 最需要查错误时反而没法用 Cmd。
>
> **主入口是 Senate CLI**（2026-09-07 起）：`senate cmd unity-recompile`（触发＋等到**那一趟**结束）／
> `senate cmd unity-compile-status`（只读现况，**不需要 Editor**）。两者都只读 `.compile_status.json`
> 这个档，**不依赖 Cmd 系统**（那正是本工作流存在的前提：编译坏掉时 Cmd 也载不进来）。
>
> ⛔ python `check_compile.py` **已于 2026-09-10 整支删除**（文件不存在了）。而下面两格**没有搬过去，也没有替代品**：
> `--fallback-log`（解 Editor.log）与 `--editor-alive`（心跳）这两格 CLI 还没移的能力。

## 0. TL;DR

```bash
# ⭐ 改完 .cs 之后的预设路径：触发重编 ＋ 等到**那一趟**结束才印
senate cmd unity-recompile --arg persona=<me>

# 只读现况（不触发、不需要 Editor）
senate cmd unity-compile-status

# 状态档不存在 → fallback 解 Editor.log（**CLI 未移，仍走 python**）
# ⛔ 已退场 —— 没有 fallback 了；unity-compile-status 会说「没有读数」（⛔ 那不等于 0 errors）
```

> [!WARNING]
> 🩸 **血证（留着 —— 这个形状会换工具重来）：旧的 `check_compile.py --watch` 会给假绿灯（TASK-0154）。**
> 它的结束条件只有 `in_progress=false`，而触发还没开始时那已经是 false ⇒ 回上一次的快照。
> 🩸 2026-09-07 实测：送出 recompile 后立刻 `--watch`，印出的是**三天前**（`2026-09-04T17:14`）
> 那份、`Errors: 0`，**而且没印 STALE 横幅** —— 不带 `--watch` 时同一支工具有印。
> `unity-recompile` 的基准是**送出触发的那一刻**（取在 Submit 之前），那个洞在新结构里不存在。

> [!CAUTION]
> ⛔ **两支都只量 Unity assemblies，不涵盖 `senate.exe`**（那条走 `dotnet build` ／ `build.sh` 出厂验收）。

退出码（python 版）：`0` clean / `2` 有 error / `3` 找不到 status file。

## 1. 两条数据来源

- ⭐ `.compile_status.json` — Tracker 写的，**最新** 单次结果
- Editor.log fallback — 累积多次 compile，需 dedupe + session 边界侦测

## 2. 4 步排查 SOP

1. 跑 `senate cmd unity-recompile` 看 dedupe 后的错误数
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
