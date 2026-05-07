---
title: Workflow — Creating a New UCL_CommonEditorPage Subclass
description: Step-by-step SOP — spin up a new Editor page that GUIPageController can push, from zero. Covers the inheritance chain, required/optional overrides, TopBar customization, entry-point wiring, style-selection rules (linking to UCL_GUILayout / UCL_GUIStyle docs), and common pitfalls.
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [Create EditorPage, UCL_CommonEditorPage workflow, 寫新 editor 頁]
tags: [workflow, editor, ui, imgui]
---

# 🛠️ Workflow — Creating a New UCL_CommonEditorPage Subclass

> [!IMPORTANT]
> This workflow covers exactly "**writing one Editor page that inherits `UCL_CommonEditorPage`**". For UI components (fields / lists / dropdowns), see [UCL_GUILayout Overview](../API/UCL_GUILayout/UCL_GUILayout_Overview.md); for picking styles, see [UCL_GUIStyle Overview](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md).
>
> Design philosophy: **inheritance + override hooks**. The base already handles TopBar / Back / Close / ScrollView / HelpURL parsing; the subclass only fills in `WindowName` and `ContentOnGUI()`, and overrides hooks for customization.

---

## 0. TL;DR — Add a Page in Three Minutes

```
[1] Pin down the page's responsibility (single focus, one page one job)
       ▼
[2] Create UCL_<Name>Page.cs : UCL_CommonEditorPage
       ▼
[3] override WindowName and ContentOnGUI()
       ▼
[4] Add [HelpURL] pointing at the doc path you plan to write (file may not exist yet)
       ▼
[5] Provide a static Create() (pushes onto GUIPageController)
       ▼
[6] Add a button on a parent page / menu / WelcomePage → Create()
```

---

## 1. Inheritance Chain

```
UCL_GUIPage (UICore)
  └── UCL_EditorPage (EditorMenuPages)        ← provides TopBar / Back / Close / HelpURL parsing
        └── UCL_CommonEditorPage              ← shows ClassName + Copy in the TopBar
              └── UCL_<Name>Page              ← the page you're writing
```

| Class | Responsibility |
|---|---|
| `UCL_GUIPage` | Outermost flow: `WindowName` / `IsWindow` / `OnGUI()` |
| `UCL_EditorPage` | TopBar (Back / Close / Help), ContentOnGUI ScrollView wrapping, HelpURL reflection cache, `Create<T>()` factory |
| `UCL_CommonEditorPage` | Shows "TypeName + Copy" in the TopBar to ease debugging and align with support docs |

---

## 2. Required Overrides

| Member | Type | Required | Description |
|---|---|---|---|
| `WindowName` | `string` | **required** | Window title; in multi-window switching `UCL_GUIPageController.WindowName` reads this |
| `ContentOnGUI()` | `void` | **required** | Main content drawing (the ScrollView is wrapped by base; just paint here) |

### 2.1 Minimal Skeleton

```csharp
#if UNITY_EDITOR
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_<Name>Page.md")]
    public class UCL_<Name>Page : UCL_CommonEditorPage
    {
        public override string WindowName => "UCL_<Name>";

        public static UCL_<Name>Page Create()
        {
            return UCL_EditorPage.Create<UCL_<Name>Page>();
        }

        protected override void ContentOnGUI()
        {
            // Start drawing here; ScrollView is already wrapped by base.OnGUI
            GUILayout.Label("Hello", UCL_GUIStyle.LabelStyle);
        }
    }
}
#endif
```

> [!CAUTION]
> `Create<T>()` is **public static**, but you must call it through `UCL_EditorPage.Create<T>()` — it pushes the page onto `UCL_GUIPageController.CurrentRenderIns`, so **a controller must already be running** (typically via the parent page's OnGUI or an EditorWindow that has been instantiated).

---

## 3. Optional Hooks to Override

| Member | Type | Default | When to override |
|---|---|---|---|
| `TopBarButtons()` | `void` | Shows ClassName + Copy buttons (provided by CommonEditorPage) | When you want top-row tool buttons like "Refresh / Switch Lang / Toggle Sidebar" |
| `ShowCloseButton` | `bool` | `true` | Set `false` when you don't want the user to close every page in one click |
| `ShowBackButton` | `bool` | `!ShowCloseButton || pages.Count > 1` | Custom navigation flows |
| `BackButtonClicked()` | `void` | `p_Controller.Pop()` | When you need to save / show a confirm dialog before going back |
| `CloseButtonClicked()` | `void` | `p_Controller.PopAll()` | Same as above |
| `Init(controller)` | `void` | Calls base + records `m_TypeName` | One-shot init / event subscription |

### 3.1 TopBarButtons Example

```csharp
protected override void TopBarButtons()
{
    base.TopBarButtons();   // keep ClassName + Copy
    if (GUILayout.Button(UCL_CodeLocalize.Get("DocSearch.Reveal"),
        UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
    {
        EditorUtility.RevealInFinder(m_AbsolutePath);
    }
}
```

For a real example see `UCL_MarkdownViewerPage.TopBarButtons()`: on top of the standard two it adds Reveal / OS Open / Copy raw.

---

## 4. Entry-Point Wiring (How Does This Page Get Opened?)

Four common entry points:

| Entry | Example | Best for |
|---|---|---|
| **Parent-page button** | `UCL_DocSearchPage.DrawResultRow`'s `📄` button → `UCL_MarkdownViewerPage.Create(...)` | Pages with a clear contextual relationship to an existing page |
| **WelcomePage card** | `UCL_WelcomePage`'s "🔍 Doc Search" button → `UCL_DocSearchPage.Create()` | Global features / needs prominent surface |
| **`UCL → ...` menu** | `[MenuItem("UCL/<Name>")]` opens an EditorWindow that pushes the page in `OnGUI` | Standalone tools that don't depend on other pages |
| **HelpURL deep link** | `ucl_core:Docs~/...` jumps from a doc button back to a page | Usually the reverse direction — page → doc |

> [!TIP]
> When wiring an entry point for a new page, follow **least coupling**: prefer a parent-page button over opening a menu, prefer a WelcomePage card over scattering across multiple menus.

---

## 5. Which UI Drawing Should I Pick?

When painting UI inside `ContentOnGUI()`, work down by complexity:

| Goal | Tool | Reference |
|---|---|---|
| Basic Label / Button / TextField / Toggle | `GUILayout.*` + `UCL_GUIStyle.*Style` | [UCL_GUIStyle Overview](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md) |
| Numeric field / Slider / Vector / foldout ▼/► | `UCL_GUILayout.IntField` / `Slider` / `Vector3Field` / `Toggle(bool, size)` | [UCL_GUILayout Overview §3.1](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#31-基礎欄位ucl_guilayoutcs) |
| List / Dictionary / HashSet editing (paging, polymorphic Add) | `UCL_GUILayout.DrawList` / `DrawDictionary` / `DrawHashSet` | [UCL_GUILayout Overview §3.2](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#32-集合編輯drawlist--drawdictionary--drawhashset) |
| Recursive object drawing (reflection fields, `[SerializeReference]` polymorphism) | `UCL_GUILayout.DrawObjectData` | [UCL_GUILayout Overview §3.3](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#33-物件遞迴繪製ucl_guilayoutdrawobjectcs) |
| Dropdowns (search / enum) | `UCL_GUILayout.PopupAuto` / `Popup<T>(enum)` | [UCL_GUILayout Overview §3.4](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#34-下拉選單與分頁ucl_guilayoutpopupcs) |
| Interactive painter | `UCL_GUILayout.DrawableTexture` / `UCL_GUILayoutPainter` | [UCL_GUILayout Overview §3.5](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#35-互動繪圖) |
| Complex-struct Copy/Paste | `UCL_GUILayout.DrawCopyPaste(ref obj, ...)` | [UCL_GUILayout Overview §5.3](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#53-drawcopypasteref-obj-dic-fieldtype) |

### 5.1 When to Author Your Own GUIStyle

If you need something none of the built-in styles cover (e.g. a "16pt bold + wordWrap + richText Heading style"), lazy-create one **derived** from an existing style inside your page:

```csharp
GUIStyle m_HeadingStyle;
GUIStyle HeadingStyle => m_HeadingStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
{
    fontSize = 18,
    fontStyle = FontStyle.Bold,
    richText = true,
    wordWrap = true,
};
```

> [!CAUTION]
> This `m_HeadingStyle` is a pure display style (derived from LabelStyle); it likewise **cannot** be passed as the third GUIStyle parameter to `Toggle` / `Button`. See [UCL_GUIStyle Overview §3](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md#3-反指守則從-labelstyle-重複出現的禁忌).

---

## 6. HelpURL and Multi-Language Docs

### 6.1 Attribute Form

```csharp
[HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_<Name>Page.md")]
public class UCL_<Name>Page : UCL_CommonEditorPage { ... }
```

`{lang}` is auto-substituted by `UCL_GUILayout.DrawHelpButton` to the current `UCL_LocalizeManager.s_LangName` (zh-Hant / en / ja / zh-Hans), so the "?" button jumps to the right file per the user's language. See [HelpURL_Workflow](HelpURL_Workflow.md).

### 6.2 Wire the Attribute Even Before the Doc Exists

- `[HelpURL]` pointing at a not-yet-existing .md file won't crash — clicking just fails to open
- Wiring the attribute up front lets future searches ([Cmd_SearchDocs](../API/UCL_AgentCommand/Cmd_SearchDocs.md) / `UCL_DocSearchPage`) index "doc that should exist but isn't written yet" locations
- Backfill the doc after the page stabilizes (see how `UCL_DocSearchPage` itself follows this rhythm)

---

## 7. Common Pitfalls

| # | Pitfall | Symptom | Fix |
|---|---|---|---|
| 1 | Passing `UCL_GUIStyle.LabelStyle` as the third parameter of `GUILayout.Toggle` | Checkbox icon disappears; clicks do nothing | Drop the third param for plain checkboxes; use `ButtonStyle` for button-like. See [UCL_GUIStyle Overview §3](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md#3-反指守則從-labelstyle-重複出現的禁忌) |
| 2 | Wrapping another ScrollView inside `ContentOnGUI()` | Double scrollbars, weird mouse-wheel behavior | Don't — base already wraps one; if you want a secondary scroll, declare a separately named one |
| 3 | TextField swallows Enter, search doesn't fire | Pressing Enter does nothing | Snapshot `Event.current` before the TextField; see comments around `UCL_DocSearchPage.DrawSearchInput` |
| 4 | Calling `Create<T>()` without a controller present | NullRef | Make sure the parent page / EditorWindow has built a controller, or hold one yourself and pass it via the `Create<T>(controller)` overload |
| 5 | Lazy-built styles aren't cached | A new GUIStyle every frame, performance tanks | Use field + property for laziness (see §5.1), or centralize in `EnsureStyles()` |
| 6 | A rich-text label contains `<...>` (e.g. C# generic `List<T>`) | Text gets parsed as a tag and parts disappear | Disable `richText` on that style, or escape `<` to `&lt;` in user content |
| 7 | Hard-coding a language in `[HelpURL]` (no `{lang}` placeholder) | After switching language the Help button hits the wrong file | Always use `ucl_core:Docs~/{lang}/...` |
| 8 | EditorWindow.OnGUI doesn't set `IsInEditorWindow` | Style cache hits the runtime instance, DPI is off | Use `IsInEditorWindowScope` (using auto-restores) |

---

## 8. Acceptance Checklist

Run through after writing:

- [ ] Inherits `UCL_CommonEditorPage`; file name and class name match exactly
- [ ] `WindowName` is overridden (non-empty string)
- [ ] `ContentOnGUI()` is overridden; uses `UCL_GUIStyle.*` / `UCL_GUILayout.*` for styles and components
- [ ] `LabelStyle` is not handed to interactive controls
- [ ] `[HelpURL("ucl_core:Docs~/{lang}/...")]` carries the `{lang}` placeholder
- [ ] A `static Create()` factory exists and returns the subclass type
- [ ] At least one entry point (parent button / WelcomePage card / menu) opens this page
- [ ] After a domain reload the page actually opens with no NullRef and Back / Close behave correctly
- [ ] When the page renders rich-text content, the relevant GUIStyle has `richText` enabled

---

## 9. Reference Examples

| Page | Highlights |
|---|---|
| `UCL_DocSearchPage` | Standard skeleton + Enter-triggered search input + collapsible advanced options + result-row action buttons |
| `UCL_MarkdownViewerPage` | Loads data via external `Create(args...)` + `EnsureStyles()` centralizing style creation + TopBarButtons with three custom buttons |
| `UCL_WelcomePage` | Card grid layout + multi-entry hub |

---

## 10. Related Documents

- [UCL_GUILayout Overview](../API/UCL_GUILayout/UCL_GUILayout_Overview.md) — IMGUI component layer
- [UCL_GUIStyle Overview](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md) — style layer
- [HelpURL_Workflow](HelpURL_Workflow.md) — `ucl_core:` / `eov_docs:` prefix resolution
- [Hardcoded_Localize](Hardcoded_Localize.md) — TopBar / button text localization (`UCL_CodeLocalize` / `UCL_LocalizeManager`)
- [Polymorphism_In_UCL](../Architecture/Polymorphism_In_UCL.md) — overall architecture of `[SerializeReference]` polymorphic fields across GUI editing and JSON serialization
