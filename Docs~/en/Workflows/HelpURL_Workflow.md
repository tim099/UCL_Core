# HelpURL System & Workflow

## 1. Core Concepts
UCL extends Unity's native `HelpURLAttribute` to create a help system that supports "cross-environment resolution" and "multi-language support".

### 1.1 Special Prefix: `ucl_core:`
To ensure links remain valid when the module is moved between projects or released in builds, we use relative path resolution:
*   **Format**: `ucl_core:Docs~/{lang}/YourDoc.md`
*   **Resolution Logic (`UCL_URL`)**:
    *   **Editor Mode**: Automatically resolves to the local path `[UCL_Core Root]/Docs~/{lang}/YourDoc.md`. Supports offline reading.
    *   **Build Mode**: Automatically converts to a GitHub link to ensure cloud document access in release builds.

### 1.2 Localization Placeholder: `{lang}`
*   **Purpose**: Automatically switches document paths based on the current language.
*   **Logic**: Replaced by `UCL_LocalizeService.CurLang` (e.g., `en`, `zh-Hans`, `ja`).
*   **Editor Fallback**: If a localized file is missing, the system in Editor will attempt to find the `en` version as a fallback to avoid 404 errors.

### 1.3 Hidden Folder: `Docs~`
*   **Physical Significance**: Unity ignores folders ending with `~`. We store documents in `Docs~` so they stay within the module directory without generating `.meta` files.

---

## 2. Workflow

### Step A: Write Documentation
1.  Create a Markdown file in `Assets/UCL/UCL_Core/Docs~/{lang}/` (e.g., `en/MyFeature.md`).
2.  Write technical descriptions or guides.

> [!IMPORTANT]
> If the documentation is for a specific Class, the `.md` file name **must** be the same as the Class name (e.g., `UCL_ModuleServiceEditPage.md` for `class UCL_ModuleServiceEditPage`).

### Step B: Attach Attribute (HelpURL)
#### Case 1: For Assets or Data Classes
Add `[HelpURL]` above the class declaration, **must** use the `{lang}` placeholder:
```csharp
[HelpURL("ucl_core:Docs~/{lang}/API/MyFeatureAsset.md")]
public class MyFeatureAsset : UCL_ModResourceAsset { ... }
```

#### Case 2: For Editor Pages (`UCL_EditorPage`)
Add `[HelpURL]` as well:
```csharp
[HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/MyFeatureEditPage.md")]
public class MyFeatureEditPage : UCL_EditorPage { ... }
```

---

## 3. System Components
*   **`UCL_URL.cs`**: Responsible for parsing URL strings and handling `{lang}` replacement.
*   **`UCL_GUILayoutDrawObject.DrawHelpButton`**: GUI-level wrapper that draws the button and calls `UCL_URL.OpenURL`.
*   **`UCL_EditorPage.cs`**: Base page class that automatically caches the `HelpURL` attribute and draws it in the TopBar.
