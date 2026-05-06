---
title: UCL_WelcomePage
description: UCL_Core's Welcome / overview page; auto-opens on first install or major version bump, introducing UCL_Asset / Localize / Agent Commands / Editor Pages with quick-access buttons
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_WelcomePage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [welcome, getting started, overview, onboarding, first install, auto open]
tags: [editor_page, onboarding, welcome]
---

# UCL_WelcomePage

## 1. Overview

`UCL_WelcomePage` is the cross-project welcome / overview page for UCL_Core, addressing the "newcomer doesn't know where to start" problem.

Three trigger paths:

| # | Trigger | Use |
|---|---|---|
| 1 | **Auto-open** on first install / version bump | `[InitializeOnLoad]` in [`UCL_WelcomeAutoOpen`](../../../UCL_WelcomeAutoOpen.cs) |
| 2 | EditorMenu main page → "👋 Welcome / 總覽" button | Manual revisit |
| 3 | Menu `UCL → Welcome` | No need to open EditorMenu first |

## 2. Auto-open detection

```text
[Domain reload]
       │
       ▼
[InitializeOnLoad] static ctor → EditorApplication.delayCall +=
       │
       ▼
TryAutoOpen():
  if EditorPrefs(AutoOpenDisabled) → skip
  if EditorPrefs(ShownVersion) == UCL_WelcomePage.CurrentVersion → skip
  else → write ShownVersion + call UCL_WelcomePage.OpenAndShow()
```

Controlled EditorPrefs:

| Key | Type | Default | Meaning |
|---|---|---|---|
| `UCL_Core.Welcome.ShownVersion` | string | `""` | Last shown content version; mismatch with `CurrentVersion` triggers popup |
| `UCL_Core.Welcome.AutoOpenDisabled` | bool | `false` | User opt-out (manual entry still works) |

EditorPrefs is **per-user / per-machine** — new clones trigger first popup for each developer.

## 3. Content sections

Header / Intro / Feature cards (UCL_Asset / Localize / Agent Commands / Editor Pages — each with a primary button + docs link) / Doc index links / Footer (auto-open toggle + reset).

## 4. Bumping content version

When Welcome content changes significantly, increment `UCL_WelcomePage.CurrentVersion`. Users with `AutoOpenDisabled=false` will see the new version once.

## 5. Cross-page navigation pattern

```csharp
public static void OpenAndShow()
{
    UCL_EditorMenuPage.s_OnFirstDraw = (controller) =>
        UCL_EditorPage.Create<UCL_WelcomePage>(controller);
    UCL_MenuWindow.ShowMenu();
    EditorPrefs.SetString(PrefKey_ShownVersion, CurrentVersion);
}
```

Why a hook instead of direct `Create<T>()`? Because the window's `m_GUIPageController` is private and `Create<T>()` falls back to the singleton. Hook ensures the page is pushed onto the actual window controller during its first OnGUI.

## 6. Limitations

- EditorPrefs is per-user, won't follow git
- `[InitializeOnLoad]` runs on every domain reload — debounced via version key + delayCall
- Hardcoded IMGUI content; could be markdown-driven if needed

## 7. Related

- [UCL_EditorMenuPage](UCL_EditorMenuPage.md)
- [UCL_AgentCommand_Architecture](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)
