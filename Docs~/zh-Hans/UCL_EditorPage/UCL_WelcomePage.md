---
title: UCL_WelcomePage
description: UCL_Core 的欢迎/总览页；首次安装或大版本升级时自动弹出，介绍 UCL_Asset / Localize / Agent Commands / Editor Pages 等核心功能与快速跳转按钮
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_WelcomePage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [welcome, getting started, 欢迎, 总览, 入门, 首次安装]
tags: [editor_page, onboarding, welcome]
---

# UCL_WelcomePage

## 1. 概览

`UCL_WelcomePage` 是 UCL_Core 跨项目套件的**欢迎/总览页**，解决「新手第一次接触 UCL_Core 不知从何入手」的问题。

三种触发方式：自动弹出（首次安装/版本升级）/ EditorMenu 主页按钮 / 选单 `UCL → Welcome`。

## 2. 自动弹出逻辑

`[InitializeOnLoad]` 触发 → `EditorApplication.delayCall` → 比对 EditorPrefs 内的 `ShownVersion` 与 `UCL_WelcomePage.CurrentVersion`，不同则弹出并更新版本号。使用者主动关闭后写 `AutoOpenDisabled=true`，即使版本不同也不再弹。

## 3. EditorPrefs

- `UCL_Core.Welcome.ShownVersion@<projHash>` (string) — 已展示版本
- `UCL_Core.Welcome.AutoOpenDisabled@<projHash>` (bool) — 主动关闭

`<projHash>` = `Application.dataPath.GetHashCode()` 十六进制字串。EditorPrefs 在 Unity 是 per-user / per-machine 全局共用，加上 per-project hash 后缀把每个项目分开 — A 项目看过 Welcome 不会让 B 项目不再弹。

## 4. 关联文档

- [UCL_EditorMenuPage](UCL_EditorMenuPage.md)
- [UCL_AgentCommand_Architecture](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)
