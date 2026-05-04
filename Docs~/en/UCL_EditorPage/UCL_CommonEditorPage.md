---
title: UCL_CommonEditorPage
description: Standard base class for UCL editor pages, providing the TypeName label and Copy button.
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_CommonEditorPage.cs
namespace: UCL.Core.EditorLib.Page
---

# UCL_CommonEditorPage

## 1. Overview

`UCL_CommonEditorPage` is the **standard base class** for all custom editor pages in the UCL framework. It is a thin extension over [`UCL_EditorPage`](./UCL_EditorPage.md) that provides two universally useful behaviors out of the box:

1. **TypeName label** — automatically displays the page's class name in the top bar
2. **Copy button** — one-click copy the class name to system clipboard (handy for jumping between page subclasses during development)

When you build any non-trivial editor page, **inherit from `UCL_CommonEditorPage` rather than `UCL_EditorPage` directly** — you get the conventional top-bar layout for free, and your page integrates visually with the rest of the UCL editor ecosystem ([`UCL_ModuleEditPage`](./UCL_ModuleEditPage.md), [`UCL_ModuleServiceEditPage`](./UCL_ModuleServiceEditPage.md), [`UCL_ModulePlayListPage`](./UCL_ModulePlayListPage.md), [`UCL_SelectAssetPage`](./UCL_SelectAssetPage.md), …).

## 2. What It Provides

### 2.1 Class Definition (excerpted)

```csharp
public class UCL_CommonEditorPage : UCL_EditorPage
{
    protected string m_TypeName;

    public override void Init(UCL_GUIPageController iGUIPageController)
    {
        base.Init(iGUIPageController);
        m_TypeName = this.GetType().Name;
    }

    protected override void TopBarButtons()
    {
        base.TopBarButtons();
        GUILayout.Label(m_TypeName, UCL_GUIStyle.LabelStyle);
        if (GUILayout.Button(UCL_LocalizeManager.Get("Copy"),
                             UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
        {
            GUIUtility.systemCopyBuffer = m_TypeName;
        }
    }
}
```

### 2.2 Top Bar Layout (default)

```
┌──────────────────────────────────────────────────────┐
│ [Back] [Close] │ <YourPageClassName> │ [Copy] │ ...  │
└──────────────────────────────────────────────────────┘
   ↑ from UCL_EditorPage    ↑ from UCL_CommonEditorPage
```

Subclasses extend the right side by overriding `TopBarButtons()` and calling `base.TopBarButtons()` first.

## 3. When to Use

| Scenario | Recommended Base Class |
|---|---|
| A simple page with no top-bar controls | `UCL_EditorPage` |
| **Most custom editor pages** | **`UCL_CommonEditorPage`** ⭐ |
| Page edits a single Module instance | `UCL_ModuleEditPage` (already a subclass) |
| Page picks an asset from a list | `UCL_SelectAssetPage` (already a subclass) |

Rule of thumb: if you do not have a strong reason to suppress the TypeName label, **default to `UCL_CommonEditorPage`**.

## 4. How to Extend — Standard Pattern

### 4.1 Minimum Subclass

```csharp
namespace YourGame.Page
{
    public class YourEditorPage : UCL_CommonEditorPage
    {
        public override string WindowName => "Your Page Title";

        public static YourEditorPage Create()
        {
            // ★ Use the static factory; do NOT new + Push manually.
            return UCL_EditorPage.Create<YourEditorPage>();
        }

        protected override void ContentOnGUI()
        {
            // your IMGUI here
            GUILayout.Label("Hello", UCL_GUIStyle.LabelStyle);
        }
    }
}
```

### 4.2 Adding Top-Bar Buttons

Always call `base.TopBarButtons()` first so that the `TypeName + Copy` block stays at the leftmost position:

```csharp
protected override void TopBarButtons()
{
    base.TopBarButtons();   // ★ TypeName + Copy from UCL_CommonEditorPage

    if (GUILayout.Button("Refresh",
            UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
    {
        Reload();
    }
    if (GUILayout.Button("Run",
            UCL_GUIStyle.GetButtonStyle(Color.green), GUILayout.ExpandWidth(false)))
    {
        Execute();
    }
}
```

> [!IMPORTANT]
> Use `GUILayout.ExpandWidth(false)` on every top-bar button. Without it, the buttons greedily fill horizontal space and break the layout when the window is wide.

### 4.3 Init Override

If your page needs constructor-like setup, override `Init()` and call `base.Init()` so that `m_TypeName` is populated:

```csharp
public override void Init(UCL_GUIPageController iGUIPageController)
{
    base.Init(iGUIPageController);   // ★ MUST come first
    LoadInitialData();
}
```

### 4.4 OnClose Override

If your page needs to clean up state when the user navigates away:

```csharp
public override void OnClose()
{
    SaveDirtyChanges();
    base.OnClose();   // ★ call base last
}
```

## 5. Reference Subclasses

The following classes inherit from `UCL_CommonEditorPage` and demonstrate idiomatic extension:

| Subclass | Purpose |
|---|---|
| [`UCL_ModuleEditPage`](./UCL_ModuleEditPage.md) | Edit a single Module's settings & content |
| [`UCL_ModuleServiceEditPage`](./UCL_ModuleServiceEditPage.md) | Manage all installed Modules |
| [`UCL_ModulePlayListPage`](./UCL_ModulePlayListPage.md) | Manage Module load order playlist |
| [`UCL_SelectAssetPage`](./UCL_SelectAssetPage.md) | Pick one asset from a typed list |

When designing a new editor page, **read `UCL_ModuleEditPage` first** — it is the canonical example of the override pattern (Init / TopBarButtons / ContentOnGUI / OnClose all demonstrated).

## 6. Common Patterns

### 6.1 Caching `UCL_ObjectDictionary` for Sub-State

Many pages need to remember per-foldout / per-toggle UI state across frames. Convention:

```csharp
private UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();

protected override void ContentOnGUI()
{
    UCL_GUILayout.DrawObjectData(myObject, m_DataDic.GetSubDic("MyObject"), "MyObject");
}
```

### 6.2 Async Work from Buttons

Use `UniTask` and `.Forget()` to avoid blocking the IMGUI thread:

```csharp
if (GUILayout.Button("Run Async Task", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
{
    DoWorkAsync().Forget();
}

private async UniTask DoWorkAsync()
{
    await SomeService.InitAsync(default);
    // …
}
```

### 6.3 Constant Repaint (when data changes outside user input)

If your page reflects external state that mutates without GUI input (timers, progress, etc.):

```csharp
[UCL.Core.ATTR.RequiresConstantRepaint]
public class YourEditorPage : UCL_CommonEditorPage { … }
```

## 7. Pitfalls

> [!CAUTION]
> **Do not skip `base.TopBarButtons()`**. Skipping it removes the TypeName label and the Copy button — visually the page no longer matches the UCL editor family, and you lose the convenience of copying the class name during debugging.

> [!CAUTION]
> **Do not subclass `UCL_EditorPage` directly when you want the standard top bar**. Re-implementing the TypeName + Copy block manually duplicates code and risks drifting from future framework updates.

> [!IMPORTANT]
> When creating a page instance from outside, **always use the static factory `UCL_EditorPage.Create<T>()`** rather than `new T(); UCL_GUIPageController.CurrentRenderIns.Push(p);`. The factory handles duplicate-page detection and proper initialization.

## 8. Related

- [`UCL_EditorPage`](./UCL_EditorPage.md) — direct base class
- [`UCL_ModuleEditPage`](./UCL_ModuleEditPage.md) — canonical override example
- [`UCL_GUIPage`](./UCL_GUIPage.md) — root page abstraction
