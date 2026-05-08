---
title: Create New UCL_Asset Subclass Workflow
description: Step-by-step SOP — Adding persistent data types under the UCL_Core architecture. **All must inherit from UCL_Asset<T>**; bare ScriptableObjects or custom save mechanisms are strictly forbidden. Covers inheritance templates, ID/SaveFolderPath conventions, AssetGroup attributes, JSON serialization, Edit/Preview hooks, and common pitfalls.
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_Assets/
namespace: UCL.Core
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [Create UCL Asset, Add Asset, UCL_Asset Subclass, Persistent Data]
tags: [workflow, asset, scriptableobject, persistence]
related:
  - ucl_core:Docs~/{lang}/Workflows/Create_EditorPage_Workflow.md | Create EditorPage Workflow | New Page Entry (paired with this document — Page is UI, Asset is Data)
  - ucl_core:Docs~/{lang}/Workflows/Validate_UCL_Asset_Workflow.md | Validate UCL Asset Workflow | UCL_Asset serialization validation (validate after agent edits .json)
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_SelectAssetPage.md | UCL_SelectAssetPage | Selection UI that automatically lists all UCL_Asset subclasses (no need to write a custom list page)
---

# 🛠️ Create New UCL_Asset Subclass Workflow

> [!IMPORTANT]
> **Under the UCL_Core framework, all persistent data must inherit from `UCL_Asset<T>`**. The following are strictly forbidden:
> - Bare `ScriptableObject` + `[CreateAssetMenu]` (incompatible with UCL_ModuleService module path mechanism).
> - Custom save mechanisms such as `File.WriteAllText` / `JsonUtility.ToJson` (reinventing the wheel + lacking module path resolution).
> - Dual-store synchronization using FileSystemWatcher / EditorApplication.update polling (UCL_Asset itself is the source-of-truth, no mirror needed).
>
> Design Philosophy: **One .json file = One instance of a UCL_Asset subclass with a unique ID**. The base class handles IO / module paths / Editor UI / serialization — subclasses only need to declare fields and provide two constructors.

---

## 0. TL;DR — Minimum Skeleton

```csharp
using UnityEngine;

namespace UCL.Core
{
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
    public class UCL_<Name>Asset : UCL_Asset<UCL_<Name>Asset>
    {
        public const string DefaultID = "Default";

        public string m_SomeField = string.Empty;
        public int    m_SomeNumber = 0;

        public UCL_<Name>Asset() { ID = DefaultID; }
        public UCL_<Name>Asset(string iID) { Init(iID); }
    }
}
```

**That's it**. No `[CreateAssetMenu]`, no OnValidate, no FileSystemWatcher. Editing, saving, and loading are fully automated.

---

## 1. Why Inherit from UCL_Asset?

| Objective | Bare ScriptableObject | UCL_Asset |
|---|---|---|
| Cross-Module Storage | ❌ Hardcoded `Assets/...` paths | ✅ `UCL_ModuleService` module-relative paths |
| JSON Serialization | ⚠ Custom ToJson/FromJson required | ✅ Built-in `SerializeToJson` in base class |
| Editor Editing UI | ⚠ Custom Inspector required | ✅ Base automatically draws via `DrawObjectData` reflection in OnGUI |
| List / Selection UI | ⚠ Custom EditorWindow required | ✅ Reflection enumeration via `UCL_SelectAssetPage` |
| Mod System Compatibility | ❌ Not part of the module system | ✅ Automatically switches with corresponding `UCL_Module` |
| Per-File Git Diff | ⚠ Asset files are binary YAML | ✅ `.json` is plain text, merge-friendly |

**Conclusion**: Since UCL_Core itself is a mod-friendly asset framework, any persistent data added should inherit from `UCL_Asset`. Unless you have an **extremely specific** reason (there are only a few exceptions in UCL_Core like `UCL_LocalizeAsset` because they require Unity serialization hooks), do not bypass this convention.

---

## 2. Mandatory Components

### 2.1 Inheritance
```csharp
public class UCL_MyAsset : UCL_Asset<UCL_MyAsset>
```
The generic `T` is the subclass itself (CRTP template).

### 2.2 Two Constructors

```csharp
public UCL_MyAsset() { ID = DefaultID; }    // Parameterless — for reflection / new()
public UCL_MyAsset(string iID) { Init(iID); }  // With ID — explicit creation
```

Since `UCL_Asset<T>` constrains `T : new()`, the **parameterless constructor is mandatory**.

### 2.3 ID Default Constant

```csharp
public const string DefaultID = "Default";
```

The parameterless constructor uses this as a placeholder ID (it will be overwritten during Init).

### 2.4 Fields (adhering to m_-prefix convention)

```csharp
public string m_DisplayName = string.Empty;
public List<string> m_Tags = new List<string>();
public Color m_TintColor = Color.white;
```

- Use the `m_` prefix (it is automatically stripped from UI display by `UCL_LocalizeManager` / `LocalizeFieldName`).
- Set default values using inline initializers; **do not** instantiate in constructors (as UCL_Asset deserialization will overwrite them).

---

## 3. Optional Attributes

| Attribute | Purpose |
|---|---|
| `[UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.X)]` | Categorizes the Asset into Data / Config / Editor / Assembly groups (affects sorting in SelectAssetPage) |
| `[UCL.Core.ATTR.UCL_Sort(int)]` | Sorting hint within the group |
| `[HelpURL("ucl_core:Docs~/{lang}/...")]` | Displays a "?" button in the Inspector that links to documentation |

Example from `UCL_ConfigAsset.cs`:
```csharp
[UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Config)]
[UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditConfigType.UCL_ConfigAsset)]
public class UCL_ConfigAsset : UCL_Asset<UCL_ConfigAsset>
```

---

## 4. File Output

The base class automatically handles paths:
- `SaveFolderPath` ➔ `<module>/UCL_Assets/<TypeName>/`
- `AssetPath` ➔ `<SaveFolderPath>/<ID>.json`
- One ID = One `.json` file (plain text, git-diff friendly).

Example (`UCL_ConfigAsset` with ID `CurLangKey`):
```
<module>/UCL_Assets/UCL_ConfigAsset/CurLangKey.json
  ➔ { "m_Value": "MyProj_CurLang" }
```

---

## 5. Editing and Selection UI

### 5.1 Do Not Recreate UI
- `UCL_CommonEditPage` automatically handles editing for any `UCL_Asset` — simply call `UCL_CommonEditPage.Create(asset)`.
- `UCL_SelectAssetPage` lists all `UCL_Asset` subclasses through reflection, grouped and searchable.
- Subclasses only need custom display code if they override `OnGUI` / `Preview`; otherwise, the base class renders UI via reflection using `UCL_GUILayout.DrawObjectData`.

### 5.2 Use Base Entrypoints
```csharp
// Open editing page from external code
UCL_CommonEditPage.Create(myAsset);

// Open selection page from external code (let user select an ID)
// Aligned with UCL_SelectAssetPage conventions — see UCL_SelectAssetPage.md
```

---

## 6. Common Pitfalls

| # | Pitfall | Symptom | Solution |
|---|---|---|---|
| 1 | Inheriting from `ScriptableObject` instead of `UCL_Asset<T>` | Does not show in SelectAssetPage, incompatible with `UCL_ModuleService` | Change to inherit from `UCL_Asset<T>` |
| 2 | Missing parameterless constructor | Compiler error on `UCL_Asset<T> where T : new()` | Add `public UCL_MyAsset() { ID = DefaultID; }` |
| 3 | Instantiating `new List<>()` inside constructor | Deserialization cannot read fields (overwritten by constructor) | Move instantiation to inline field initializer |
| 4 | Field names missing `m_` prefix | Display names do not align with UCL conventions (though it functions) | Rename fields to use `m_FieldName` |
| 5 | Custom FileSystemWatcher / OnValidate write-back dual sync | Reinventing the wheel + race conditions | Remove them; `UCL_Asset` itself is the source-of-truth |
| 6 | Using `[CreateAssetMenu]` to create via Project window | Created `.asset` is not recognized by `UCL_Asset` | Do not use; use `CreateData(iID)` or the `SelectAssetPage` flow |
| 7 | Custom list page built as an EditorWindow | Reinventing the wheel | Use `UCL_SelectAssetPage` directly |
| 8 | Creating a separate roster file (like `identities.json`) on top of `.json` assets | Dual-store synchronization issues | Maintain a single source-of-truth — either 100% `UCL_Asset` or 100% single-file |

---

## 7. When to Trigger This Workflow

- When the user requests to "add data type X", "create config file X", or "persist state X".
- When an agent catches themselves writing `[CreateAssetMenu]` / `ScriptableObject` / custom `.json` serialization — **STOP** and read this document first.
- When you spot a bare `ScriptableObject` in code reviews — propose migrating it to `UCL_Asset`.

---

## 8. Reference Examples

| Asset | Key Feature |
|---|---|
| `UCL_ConfigAsset` | Minimum skeleton (single `m_Value` string), `DefaultID` convention |
| `UCL_BundleAsset` | Implements `IDisposable` + custom field UI (`UCLI_FieldOnGUI`) |
| `UCL_CSVAsset` | Handles large datasets + custom `OnGUI` |
| `UCL_ChatTavernIdentityAsset` | Roster cards (rich persona) — `m_`-prefixed fields, `List<string>` collections |

---

## 9. Related Documents

- [Create_EditorPage_Workflow.md](Create_EditorPage_Workflow.md) — Design guidelines for the corresponding UI layer (Page)
- [Validate_UCL_Asset_Workflow.md](Validate_UCL_Asset_Workflow.md) — Run `ValidateAssetFormat` to validate edited `.json` assets
- [UCL_SelectAssetPage.md](../UCL_EditorPage/UCL_SelectAssetPage.md) — List / selection UI (supports automated reflection of `UCL_Asset`)
- [UCL_CommonEditPage.md](../UCL_EditorPage/UCL_CommonEditPage.md) — Editing UI entrypoint
- [Cmd_MigrateAssetToTemplate.md](../API/UCL_AgentCommand/Cmd_MigrateAssetToTemplate.md) — Flow for promoting customized assets to `Templates~` (simply specify type + id)
- [UCL_CoreBootstrap.md](../UCL_ModuleService/UCL_CoreBootstrap.md) — Bidirectional sync mechanism between `Templates~` and project `.BuiltinModules`
