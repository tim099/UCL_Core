---
title: UCL_GUIStyle 概览
description: UCL_Core 的 IMGUI 样式中央 — 提供 BoxStyle / ButtonStyle / LabelStyle / TextField/Area / Slider 等共用样式，附 DPI 全局缩放与 EditorWindow / Runtime 双 cache 机制；包含一个关键反指守则（LabelStyle 不可给交互控件）。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUIStyle.cs
namespace: UCL.Core.UI
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [UCL_GUIStyle, GUIStyle 中央, IMGUI styles]
tags: [api, ui, imgui, editor, style]
---

# UCL_GUIStyle 概览

`UCL_GUIStyle` 是 UCL_Core 的 IMGUI 样式集中地。所有页面（`UCL_CommonEditorPage` 系列、`UCL_DocSearchPage`、`RCG_*EditorPage`…）都该从这里取共用 GUIStyle，**不要**自己 `new GUIStyle(GUI.skin.xxx)`，否则没办法吃 DPI 缩放与 EditorWindow / Runtime 双 cache。

---

## 1. 分层

```
   调用端：UCL_GUILayout / *EditorPage
              │
              ▼  静态入口（IsInEditorWindow 自动分流）
   UCL_GUIStyle
     ├── BoxStyle / ButtonStyle / LabelStyle / TextFieldStyle / TextAreaStyle
     ├── GetButtonStyle(Color, fontSize)   ← Tuple key 内部 cache
     ├── GetLabelStyle(Color, fontSize)    ← Tuple key 内部 cache
     ├── ButtonTextRed / Yellow / Green    ← 预设色快捷方式
     ├── PushGUIColor / PopGUIColor / UCL_GUIColorScope
     ├── SetSizeOnGUI()                    ← Small / Medium / Big / XL 切换 Scale
     └── CurStyleData → StyleData          ← 实际样式持有者
                              │
                              ├── Scale（PlayerPrefs 持久化的全局缩放）
                              ├── ApplyScale()  / SetScale(value)
                              └── m_*StyleDic（依 (Color, fontSize) cache）
```

双 cache：
- `IsInEditorWindow == false`（Runtime / 一般 GUI）→ `s_Data`
- `IsInEditorWindow == true`（编辑器 OnGUI）→ `s_EditorWindowData`

EditorWindow 的 OnGUI 开头请设 `IsInEditorWindow = true`，结尾还原；推荐用 `IsInEditorWindowScope`（using-disposable）。

---

## 2. API 速查

### 2.1 样式入口（直接拿 GUIStyle）

| API | 用途 |
|---|---|
| `BoxStyle` | `GUILayout.Box` — 白字、richText、wordWrap |
| `ButtonStyle` | `GUILayout.Button` 标准白字；button-like Toggle 也吃这个 |
| `TextFieldStyle` | 单行 `GUILayout.TextField` |
| `TextAreaStyle` | 多行 `GUILayout.TextArea` |
| `LabelStyle` ⚠ | `GUILayout.Label` 纯文本；**禁止**当 Toggle / Button / TextField 的 GUIStyle 参 |
| `ButtonTextRed/Yellow/Green` | 红 / 黄 / 绿 字色 Button（危险 / 提醒 / 确认） |

### 2.2 定制（吃颜色 / 字号）

| API | 用途 |
|---|---|
| `GetButtonStyle(Color, fontSize)` | 内部依 `(Color, int)` cache |
| `GetLabelStyle(Color, fontSize)` | 同上；⚠ 一样**不要**给交互控件 |
| `GetScaledSize(float)` | 把任意尺寸乘上当前 `Scale`（自定义宽高 / fontSize 用） |

### 2.3 GUI.color stack

| API | 用途 |
|---|---|
| `PushGUIColor(Color)` / `PopGUIColor()` | 配对使用 |
| `UCL_GUIColorScope`（IDisposable） | `using (new UCL_GUIColorScope(Color.red)) {...}` 区段内覆写，离开自动还原 |

### 2.4 缩放控制

| API | 用途 |
|---|---|
| `StyleData.Scale`（读） / `StyleData.SetScale(value)`（写） | 全局 GUI 缩放（PlayerPrefs 持久化） |
| `StyleData.ApplyScale()` | 手动触发 cache 样式重算字号（`SetScale` 内部会调用） |
| `SetSizeOnGUI()` | 绘制 Small / Medium / Big / XL 四颗按钮，给用户自选 Scale |

### 2.5 EditorWindow / Runtime 切换

| API | 用途 |
|---|---|
| `IsInEditorWindow`（bool field） | OnGUI 期间设 true / 结束设 false |
| `IsInEditorWindowScope` | using 包起来的安全版本（避免忘了还原） |
| `CurStyleData` | 依当前 `IsInEditorWindow` 自动返回对应的 `StyleData` 实例 |

---

## 3. 反指守则（从 `LabelStyle` 重复出现的禁忌）

`LabelStyle` / `GetLabelStyle(...)` 返回的样式 **没有 toggle / button / textfield 的两态 background sprite 与 padding 设定**。传给交互控件当第三 GUIStyle 参会出问题：

| 控件 | 症状 |
|---|---|
| `Toggle` | 复选框图标消失、按了没反应 |
| `Button` | 按下没有反馈态 |
| `TextField` | 边框 / padding 失常 |

**正解**：

| 想做的事 | 用什么 |
|---|---|
| 纯 checkbox toggle | `GUILayout.Toggle(value, label)`（省略第三参数，吃 `GUI.skin.toggle` 默认） |
| Button-like 两态（AND/OR、Tab） | 第三参传 `UCL_GUIStyle.ButtonStyle` |
| 想要彩色 / 大字 label | `GUILayout.Label(text, UCL_GUIStyle.GetLabelStyle(Color, size))` |

---

## 4. 写新 GUIStyle 的时机

**通常不需要**。先试这条决策树：

```
要显示什么？
├── 纯文本 → LabelStyle / Label(name, Color) / LabelAutoSize
├── 按钮   → ButtonStyle / GetButtonStyle / ButtonTextRed/Yellow/Green / ButtonAutoSize
├── 输入框 → TextFieldStyle / TextAreaStyle
├── 容器框 → BoxStyle
└── 其他特殊样式（折叠头、code 区块等）
        → 在自己的 page 内 lazy 建一个 new GUIStyle(UCL_GUIStyle.LabelStyle) {...}
          并覆写 wordWrap / richText / fontSize 等
```

页面内自建样式的范例见 `UCL_MarkdownViewerPage.cs` 的 `m_HeadingStyles[]` / `m_CodeBlockStyle`（依 `LabelStyle` 派生、调整 fontSize 与 richText）。

---

## 5. 相关文档

- [UCL_GUILayout 整体概览](../UCL_GUILayout/UCL_GUILayout_Overview.md) — 真正画 UI 的工具集（吃这层提供的 GUIStyle）
- [Create_EditorPage_Workflow](../../Workflows/Create_EditorPage_Workflow.md) — 怎么开新 Editor 页、什么时候用什么 style
