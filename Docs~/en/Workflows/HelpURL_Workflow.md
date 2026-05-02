# HelpURL System & Workflow

## 1. Core Concepts
UCL extends Unity's native `HelpURLAttribute` to create a help system that supports **cross-environment resolution**, **multi-language support**, and **downstream-module extensibility**.

### 1.1 Special Prefix & Prefix Resolver Mechanism
`UCL_URL` is built on a **Resolver registry**. Any URL of the form `xxx:RelativePath` (where the colon is *not* followed by `//`) is dispatched to a registered Resolver:

*   **Format**: `{prefix}:Docs~/{lang}/YourDoc.md`
*   **Resolution Logic (`UCL_URL`)**:
    *   **Prefix matched**: Calls `Resolve` on the matching Resolver. Editor / Build differences are decided **at the registration site** via `#if UNITY_EDITOR`; the interface itself exposes only a single `Resolve` method.
    *   **Unknown prefix**: The URL is left untouched and continues through `{lang}` replacement and local-path completion.

> [!NOTE]
> UCL_Core's own `ucl_core:` prefix is registered through the same mechanism — there is no special-casing. To add your own prefix (e.g. `eov_docs:`), register it once at startup; **no UCL_Core changes required**.

### 1.2 Localization Placeholder: `{lang}`
*   **Purpose**: Automatically switches document paths based on the current language.
*   **Logic**: Replaced by `UCL_LocalizeService.CurLang` (e.g., `en`, `zh-Hans`, `ja`).
*   **Editor Fallback**: If a localized file is missing, the system in Editor will attempt to find the `en` version as a fallback to avoid 404 errors.
*   **Ownership**: `{lang}` substitution is handled centrally by `UCL_URL`. **Resolvers do not need to handle it themselves.**

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

## 3. Extending with a Custom Prefix

### 3.1 When to Extend
When your downstream project (e.g. a closed-source game whose docs are kept in a public open-source repo) wants `[HelpURL]` to point at its own documentation **without hardcoding any project-specific URL inside UCL_Core**.

### 3.2 Registration: Lambda Form (Recommended)
Most cases only need `Path.Combine` / string concatenation. Use `UCL_UrlPrefixResolver` — no need to implement the interface:

```csharp
using UCL.Core;
using UnityEngine;

public static class EoV_DocsResolverBootstrap
{
    private const string BUILD_BASE_URL = "https://github.com/tim099/EmblemOfValorDocuments/blob/main/";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoadMethod]
#endif
    private static void Register()
    {
        UCL_URL.RegisterResolver(new UCL_UrlPrefixResolver(
            prefix: "eov_docs",
#if UNITY_EDITOR
            // [Editor] Compose against the local submodule path for offline reading.
            resolver: (relativePath) => System.IO.Path.Combine(EoV_DocsPath.Root, relativePath)
#else
            // [Build] Compose against the GitHub blob URL so players can open it in a browser.
            resolver: (relativePath) => BUILD_BASE_URL + relativePath
#endif
        ));
    }
}
```

### 3.3 Registration: Interface Form
If your Resolver needs state or branching logic, implement `IUCL_UrlPrefixResolver` directly:

```csharp
public sealed class MyComplexResolver : IUCL_UrlPrefixResolver
{
    public string Prefix => "my_proj";
    public string Resolve(string relativePath)
    {
#if UNITY_EDITOR
        // [Editor] return local path
        return /* ... */;
#else
        // [Build] return cloud URL
        return /* ... */;
#endif
    }
}
```

### 3.4 Using a Registered Prefix
Identical to `ucl_core:`:

```csharp
[HelpURL("eov_docs:Docs~/{lang}/Mechanics/CombineSetting.md")]
public class CombineSettingAsset { ... }
```

> [!IMPORTANT]
> **Registration timing pitfall**: if `UCL_URL.OpenURL` may be called before your Resolver is registered, the link will silently fail to resolve. Always attach **both** `[InitializeOnLoadMethod]` (Editor) and `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` (Runtime).

> [!NOTE]
> Last-registered wins for the same prefix. `UCL_URL` logs a warning on overwrite but allows it, so downstream projects can replace UCL's default cloud URL (e.g. point to a fork).

---

## 4. System Components
*   **`UCL_URL.cs`**: Main URL resolver. Owns the prefix → resolver registry and handles `{lang}` substitution + `en` fallback.
*   **`IUCL_UrlPrefixResolver`**: Resolver contract (lives in the same file as `UCL_URL`). Defines only `Prefix` and a single `Resolve` method; Editor / Build switching is the registrar's responsibility.
*   **`UCL_UrlPrefixResolver`**: Lambda-backed Resolver implementation, so downstream code does not need a class per prefix.
*   **`UCL_GUILayoutDrawObject.DrawHelpButton`**: GUI-level wrapper that draws the `?` button and calls `UCL_URL.OpenURL`.
*   **`UCL_EditorPage.cs`**: Base page class that automatically caches the `HelpURL` attribute and draws it in the TopBar.
