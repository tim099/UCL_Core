---
title: UCL_GUIStyle Overview
description: UCL_Core's IMGUI style hub — provides shared BoxStyle / ButtonStyle / LabelStyle / TextField/Area / Slider styles, with global DPI scaling and a dual EditorWindow / Runtime cache; includes one critical anti-rule (LabelStyle must not be passed to interactive controls).
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUIStyle.cs
namespace: UCL.Core.UI
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [UCL_GUIStyle, GUIStyle 中央, IMGUI styles]
tags: [api, ui, imgui, editor, style]
---

# UCL_GUIStyle Overview

`UCL_GUIStyle` is UCL_Core's central home for IMGUI styles. Every page (`UCL_CommonEditorPage` family, `UCL_DocSearchPage`, `RCG_*EditorPage`, …) should pull shared GUIStyles from here. **Don't** `new GUIStyle(GUI.skin.xxx)` yourself — you'd lose DPI scaling and the dual EditorWindow / Runtime cache.

---

## 1. Layers

```
   Callers: UCL_GUILayout / *EditorPage
              │
              ▼  static entry points (auto-routed by IsInEditorWindow)
   UCL_GUIStyle
     ├── BoxStyle / ButtonStyle / LabelStyle / TextFieldStyle / TextAreaStyle
     ├── GetButtonStyle(Color, fontSize)   ← internal cache keyed by tuple
     ├── GetLabelStyle(Color, fontSize)    ← internal cache keyed by tuple
     ├── ButtonTextRed / Yellow / Green    ← preset color shortcuts
     ├── PushGUIColor / PopGUIColor / UCL_GUIColorScope
     ├── SetSizeOnGUI()                    ← Small / Medium / Big / XL scale switch
     └── CurStyleData → StyleData          ← actual style holder
                              │
                              ├── Scale (PlayerPrefs-persisted global scale)
                              ├── ApplyScale()  / SetScale(value)
                              └── m_*StyleDic (cached by (Color, fontSize))
```

Dual cache:
- `IsInEditorWindow == false` (Runtime / regular GUI) → `s_Data`
- `IsInEditorWindow == true` (Editor OnGUI) → `s_EditorWindowData`

At the top of an EditorWindow's OnGUI, set `IsInEditorWindow = true` and reset it at the end; the recommended way is `IsInEditorWindowScope` (using-disposable).

---

## 2. API Quick Reference

### 2.1 Style Entry Points (Direct GUIStyle access)

| API | Use |
|---|---|
| `BoxStyle` | `GUILayout.Box` — white text, richText, wordWrap |
| `ButtonStyle` | Standard `GUILayout.Button` white text; button-like Toggles use this too |
| `TextFieldStyle` | Single-line `GUILayout.TextField` |
| `TextAreaStyle` | Multi-line `GUILayout.TextArea` |
| `LabelStyle` ⚠ | `GUILayout.Label` plain text; **never** pass as the GUIStyle param to Toggle / Button / TextField |
| `ButtonTextRed/Yellow/Green` | Red / yellow / green text Buttons (danger / warn / confirm) |

### 2.2 Customization (Color / fontSize)

| API | Use |
|---|---|
| `GetButtonStyle(Color, fontSize)` | Internal cache keyed by `(Color, int)` |
| `GetLabelStyle(Color, fontSize)` | Same; ⚠ likewise **do not** pass to interactive controls |
| `GetScaledSize(float)` | Multiplies a size by the current `Scale` (use for custom width/height / fontSize) |

### 2.3 GUI.color Stack

| API | Use |
|---|---|
| `PushGUIColor(Color)` / `PopGUIColor()` | Use as a matched pair |
| `UCL_GUIColorScope` (IDisposable) | `using (new UCL_GUIColorScope(Color.red)) {...}` overrides within the block; auto-restores on exit |

### 2.4 Scale Control

| API | Use |
|---|---|
| `StyleData.Scale` (read) / `StyleData.SetScale(value)` (write) | Global GUI scale (PlayerPrefs-persisted) |
| `StyleData.ApplyScale()` | Manually rebuilds cached-style font sizes (`SetScale` calls this internally) |
| `SetSizeOnGUI()` | Renders Small / Medium / Big / XL buttons so the user can pick a Scale |

### 2.5 EditorWindow / Runtime Switching

| API | Use |
|---|---|
| `IsInEditorWindow` (bool field) | Set true while OnGUI is running; reset to false on exit |
| `IsInEditorWindowScope` | A using-wrapped, safe variant (avoids forgetting to restore) |
| `CurStyleData` | Returns the matching `StyleData` instance for the current `IsInEditorWindow` |

---

## 3. Anti-Rule (the Recurring `LabelStyle` Pitfall)

The styles returned by `LabelStyle` / `GetLabelStyle(...)` **lack the two-state background sprites and padding settings** required by toggle / button / textfield. Passing them as the third GUIStyle parameter to interactive controls breaks behavior:

| Control | Symptom |
|---|---|
| `Toggle` | Checkbox icon disappears; clicks do nothing |
| `Button` | No press-state visual feedback |
| `TextField` | Borders / padding misbehave |

**The right way**:

| What you want | Use |
|---|---|
| Plain checkbox toggle | `GUILayout.Toggle(value, label)` (drop the third parameter; default `GUI.skin.toggle` applies) |
| Button-like two-state (AND/OR, Tab) | Pass `UCL_GUIStyle.ButtonStyle` as the third param |
| Colored / large-font label | `GUILayout.Label(text, UCL_GUIStyle.GetLabelStyle(Color, size))` |

---

## 4. When to Author a New GUIStyle

**Usually you don't need to.** Try this decision tree first:

```
What are you displaying?
├── Plain text → LabelStyle / Label(name, Color) / LabelAutoSize
├── Button     → ButtonStyle / GetButtonStyle / ButtonTextRed/Yellow/Green / ButtonAutoSize
├── Text input → TextFieldStyle / TextAreaStyle
├── Container  → BoxStyle
└── Other special style (foldout header, code block, etc.)
        → Lazily create new GUIStyle(UCL_GUIStyle.LabelStyle) {...} inside your page
          and override wordWrap / richText / fontSize as needed
```

For an example of page-local custom styles see `m_HeadingStyles[]` / `m_CodeBlockStyle` in `UCL_MarkdownViewerPage.cs` (derived from `LabelStyle`, with adjusted fontSize and richText).

---

## 5. Related Documents

- [UCL_GUILayout Overview](../UCL_GUILayout/UCL_GUILayout_Overview.md) — the toolkit that actually paints UI (consumes the GUIStyles this layer hands out)
- [Create_EditorPage_Workflow](../../Workflows/Create_EditorPage_Workflow.md) — how to spin up a new Editor page and which style to pick when
