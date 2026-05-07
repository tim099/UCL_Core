---
title: UCL_GUILayout Overview
description: UCL_Core's IMGUI toolkit (the partial class UCL_GUILayout + the standalone UCL_GUILayoutPainter) — public API quick reference, file responsibilities, recurring patterns, and the three less-known helpers most worth remembering for downstream pages
source_files: |
  Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayout.cs
  Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayout.DrawList.cs
  Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayout.DrawDictionary.cs
  Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayout.DrawHashSet.cs
  Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayoutDrawObject.cs
  Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayoutPopup.cs
  Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayoutDrawableTexture.cs
  Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayoutPainter.cs
namespace: UCL.Core.UI
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [UCL_GUILayout, GUILayout 工具集, IMGUI helpers, DrawObject, Popup, DrawableTexture]
tags: [api, ui, imgui, editor]
---

# UCL_GUILayout Overview

`UCL_GUILayout` is the **static toolkit** UCL_Core layers on top of Unity IMGUI; it owns every "auto field editor / list editor / polymorphic field draw / dropdown / interactive paint" capability.

It is a `partial class` made up of 8 files (plus a standalone `UCL_GUILayoutPainter`), all under `namespace UCL.Core.UI`. Every downstream Editor page (`UCL_CommonEditorPage` family, `UCL_AgentCommandsPage`, `RCG_*EditorPage`, the new `UCL_DocSearchPage` / `UCL_MarkdownViewerPage`, etc.) relies on it for field rendering.

---

## 1. Design Layers

```
                ┌──────────────────────────────┐
                │  Callers: UCL_*EditorPage /   │
                │           RCG_*EditorPage     │
                └──────────────┬───────────────┘
                               │ calls static API
                               ▼
   ┌─────────────────── partial class UCL_GUILayout ─────────────────┐
   │                                                                  │
   │  UCL_GUILayout.cs              Basic fields: NumField/Slider/Toggle │
   │  UCL_GUILayout.DrawList.cs     IList editing (paging + polymorphic Add) │
   │  UCL_GUILayout.DrawDictionary  IDictionary editing               │
   │  UCL_GUILayout.DrawHashSet     HashSet (reflection Add/Remove)   │
   │  UCL_GUILayoutDrawObject.cs    Recursive drawing for any object (hub) │
   │  UCL_GUILayoutPopup.cs         Dropdowns / enums / color picker  │
   │  UCL_GUILayoutDrawableTexture  Interactive canvas (mouse paint)  │
   │                                                                  │
   └──────────────────────────────┬───────────────────────────────────┘
                                  │ depends on
                                  ▼
       UCL_GUIStyle / UCL_ObjectDictionary / UCL_LocalizeManager /
       UCL_TypeReflectCache / UCL_PolymorphicHelper / Unity IMGUI

   Standalone class:
     UCL_GUILayoutPainter.cs       Self-contained painter (wraps DrawableTexture
                                   + SelectColor + Clear)
```

`DrawObjectData` is the real hub: it inspects an object's type and routes to `DrawList` / `DrawDictionary` / `DrawHashSet` / `DrawField`, which then recurse back into it.

---

## 2. File Responsibilities

| File | Responsibility | Main public API |
|---|---|---|
| `UCL_GUILayout.cs` | Basic fields, Sprite/Texture drawing, FolderExplorer | `NumField` / `IntField` / `FloatField` / `TextField` / `TextArea` / `Toggle` / `BoolField` / `CheckBox` / `Slider` / `Vector2/3Field` / `DrawSprite` / `DrawTexture` / `LabelAutoSize` / `ButtonAutoSize` / `Label(name, Color)` / `FolderExplorer` |
| `UCL_GUILayout.DrawList.cs` | `IList` editing (incl. 1D/2D arrays), paging + polymorphic Add | `DrawList(IList, ...)` (4 overloads) |
| `UCL_GUILayout.DrawDictionary.cs` | `IDictionary` editing | `DrawDictionary(IDictionary, ...)` (3 overloads) |
| `UCL_GUILayout.DrawHashSet.cs` | `HashSet` editing (reflection-based Add/Remove; works for any IEnumerable + Add/Remove type) | `DrawHashSet(object, DrawObjectParams)` |
| `UCL_GUILayoutDrawObject.cs` | Recursive object drawing, field reflection, `[SerializeReference]` polymorphism, `[Header]`, `DrawHelpButton` | `DrawObjectData` / `DrawField` / `DrawCopyPaste` / `DrawHelpButton` / `Preview.OnGUI` |
| `UCL_GUILayoutPopup.cs` | Dropdowns (plain / searchable / cached), enum variant, color picker, page navigation | `Popup` / `PopupAuto` / `PopupSearch` / `PopupSearchCache` / `Popup<T>(enum)` / `DrawSelectPage` / `SelectColor` / `ValueDropdown` |
| `UCL_GUILayoutDrawableTexture.cs` | Mouse-painted interactive texture + `GL_DrawLine` and friends | `DrawableTexture` / `GetMousePosInGrid` / `DrawPolyLine` / `GL_DrawPolyLine` / `GL_DrawLine` / `DrawLine` |
| `UCL_GUILayoutPainter.cs` (standalone) | Complete painter UI container (texture + color + Clear) | `Init` / `SetTexture` / `Clear` / `OnTextureUpdate` / `OnGUI` |

---

## 3. Public API Quick Reference (Grouped by Use)

### 3.1 Basic Fields (`UCL_GUILayout.cs`)

| API | Use |
|---|---|
| `NumField<T>(label, value, minWidth)` | Generic numeric field; supports int/float/double; filters non-numeric keys |
| `IntField(label, value, ...)` / `FloatField(label, value, minWidth)` | Type-specific variants |
| `IntFieldAuto(value, dic, ...)` ⭐ | Integer field that **auto-clears the cache when the external value changes** (avoids showing stale values) |
| `TextField(label, value, ...)` / `TextArea(label, value)` | Single-line / multi-line text |
| `Toggle(value, size)` | Renders `▼` / `►` (folding-icon semantics) |
| `Toggle(dic, key, ...)` | Same as above but persists state in `UCL_ObjectDictionary` |
| `BoolField(value, size)` / `BoolField(dic, key, size, default)` | Renders `✔` / blank |
| `CheckBox(value, size)` | Standard checkbox |
| `Slider(label, value, min, max, dic)` | Slider + numeric input + sync |
| `Vector2Field` / `Vector3Field` / `VectorField` | Per-component vector editing (with IntVec variants) |
| `DrawSprite(sprite, ...)` / `DrawTexture(tex, ...)` / `GraphicsDrawTexture(...)` | Drawing (plain / Graphics.DrawTexture with custom Material) |
| `LabelAutoSize(name, fontSize, color)` / `ButtonAutoSize(name, fontSize, ...)` | Auto-sized width |
| `Label(name, Color color)` | One-line colored label (no need for rich text) |
| `FolderExplorer(dic, path, ...)` | Path navigation + file filter UI |

### 3.2 Collection Editing (`DrawList` / `DrawDictionary` / `DrawHashSet`)

| API | Use |
|---|---|
| `DrawList(IList, dic, name, alwaysShowDetail)` | List editing: collapsible header + auto paging (10 / page) + Copy/Paste + polymorphic Add (when the element type implements `UCLI_TypeListable`) |
| `DrawList(IList, DrawObjectParams)` | Parameterized variant (passes fieldNameFunc / overrideDrawElement) |
| `DrawDictionary(IDictionary, dataDic, name, alwaysShowDetail, fieldNameFunc)` | Dictionary editing; key and value each recurse |
| `DrawHashSet(object, DrawObjectParams)` | Reflection-based set editing (not limited to HashSet — anything with Add/Remove methods works) |

> **Common behavior**: paging cap = `MaxItemsPerPage = 10`; move/delete modes are optional; the header row carries Copy/Paste; both rank-1 and rank-2 arrays are supported.

### 3.3 Recursive Object Drawing (`UCL_GUILayoutDrawObject.cs`)

| API | Use |
|---|---|
| `DrawObjectData(obj, dic, displayName, alwaysShowDetail, fieldNameFunc, fieldType, exSetting)` | **Hub**: auto-detects `EObjectType` (String / Bool / Enum / Number / IList / IDictionary / Color / Vector / Component / Struct…) and dispatches |
| `DrawObjectData(target, DrawObjectParams)` | Parameterized variant |
| `DrawField(obj, dic, displayName, ...)` | Reflection-expand every field (recurses back into itself) |
| `DrawCopyPaste(ref obj, dic, fieldType)` | Copy/Paste button group (JSON-based); returns `true` when a paste lands |
| `DrawHelpButton(url)` | The "?" button bound to `[HelpURLAttribute]`; opens the URL on click (already used by `UCL_EditorPage.TopBar`) |
| `Preview.OnGUI(name, target, dic, space)` | Read-only preview (recursive but uneditable) |

Supported attribute extensions: `[Header]` (auto-localized), `[SerializeReference]` polymorphism, `IShowInCondition` (conditional display), `IStrList` (string dropdown), `IValueDropdown`, `ITexture2D`, `UCL_FolderExplorerAttribute`, `UCL_IntSliderAttribute`, `UCL_SliderAttribute`.

### 3.4 Dropdowns and Paging (`UCL_GUILayoutPopup.cs`)

| API | Use |
|---|---|
| `Popup(selectedIndex, options, dic, key, ...)` | Basic dropdown (open/closed state stored in `dic[key]`) |
| `Popup(selectedIndex, options, ref bool opened, ...)` | Manual `ref bool` variant |
| `PopupAuto(selectedIndex, options, dic, key, searchThreshold, ...)` ⭐ | When item count ≥ `searchThreshold`, auto-adds a search box; the most common entry point |
| `PopupSearch(selectedIndex, options, dic, key, ...)` | Always shows a search box (regex; matches highlighted in red) |
| `PopupSearchCache(index, displayOptions, dic, key, ...)` ⭐ | Additionally caches the regex and filtered indices — **the performant choice for 100+ items with repeated interaction** |
| `Popup<T>(enumValue, dic, getNameFunc, ...)` | Enum-specific; uses `UCL_LocalizeLib.GetEnumLocalize(...)` for localization |
| `PopupAuto<T>(enumValue, dic, [key], searchThreshold, ...)` | Enum + auto search threshold |
| `DrawSelectPage(dic, itemsCount, maxItemsPerPage)` | Paging row (`|<` `<` `>` `>|` + direct page-number input); returns `(pageIndex, startIndex)` |
| `SelectColor(initialColor)` | Color picker (preset palette + RGBA sliders) |
| `ValueDropdown(selectedIndex, options, dic, key, ...)` | Like PopupSearch with extra option-hash cache |

### 3.5 Interactive Drawing

| API | Use |
|---|---|
| `DrawableTexture(texture2D, dic, w, h, drawColor)` | Mouse-painted texture (handles cross-Drag boundaries and interpolation gaps automatically) |
| `GetMousePosInGrid(rect, w, h)` | Returns the mouse cell coordinate within a grid (returns `Vector2Int.left` when out of bounds) |
| `DrawPolyLine` / `GL_DrawPolyLine` / `GL_DrawLine` / `DrawLine` | Polylines / rotated segments |
| `UCL_GUILayoutPainter.OnGUI()` | Full painter (canvas + color picker + Clear) |

---

## 4. Cross-File Patterns

| Pattern | Description |
|---|---|
| **State management** | Everything goes through `UCL_ObjectDictionary` (keyed map), avoiding per-frame re-init (e.g. cached regex / TypeList / fold state) |
| **Three-tier overloads** | An API typically has: (1) **stateless** (pass value), (2) **stateful** (`dic + key`), (3) **parameterized** (`DrawObjectParams`) — pick by need from light to heavy |
| **Unified styling** | Always go through `UCL_GUIStyle` for automatic DPI scaling (`GetScaledSize()`); **do not** pass `UCL_GUIStyle.LabelStyle` as the third parameter to Toggle/Button/TextField (it breaks interaction visuals; see XML doc on `UCL_GUIStyle.LabelStyle`) |
| **Auto polymorphism detection** | `[SerializeReference]` fields enumerate concrete subtypes via `UCL_PolymorphicHelper.GetConcreteSubtypes()`, switching instances through a dropdown — annotate your data class with `UCLI_TypeListable` + `[SerializeReference]` and you skip writing UI by hand |
| **Reflection cache** | `TypeFieldInfoCache` is shared via `UCL_TypeReflectCache`, parsed once and reused page-wide; **do not** touch services in the constructor (see `Polymorphism_In_UCL.md`) |
| **Paging cap** | Large collections auto-split at `MaxItemsPerPage = 10`; search dropdowns cap at 20; pages wrap around |
| **Built-in Copy/Paste** | List / Dict / HashSet headers ship with it; arbitrary objects can call `DrawCopyPaste(ref obj, ...)` |
| **Return semantics** | Structs are returned by value; classes mutate in place and return the same reference; `IList` / `IDictionary` mutate in-place and don't return |

---

## 5. Three Less-Known Helpers Worth Remembering

Downstream pages usually only use `DrawObjectData` / `DrawList` / basic fields. The three below are **genuine time-savers** that are easy to overlook:

### 5.1 `IntFieldAuto(value, dic, ...)`
**When to use**: the displayed value comes from external data (which may be mutated elsewhere) and you need to track the previous value and clear the in-progress edit cache when a difference shows up.
```csharp
int count = UCL_GUILayout.IntFieldAuto(list.Count, m_DataDic);
// If list.Count is mutated externally, the edit cache is auto-cleared on the next OnGUI
```
**Contrast**: `IntField` doesn't detect external mutation; an in-progress edit can clobber fresh data.

### 5.2 `PopupSearchCache(index, options, dic, key, ...)`
**When to use**: 100+ options and the user filters repeatedly. Plain `PopupSearch` recompiles the regex and re-runs LINQ Where every frame; the `Cache` variant stores the regex and matched-index set inside `dic` and only recomputes when the query changes.
```csharp
int sel = UCL_GUILayout.PopupSearchCache(curIdx, allCardIds, m_DataDic, "CardPicker");
```
**Estimate**: with ~500 items the GUI responsiveness gap is clearly noticeable.

### 5.3 `DrawCopyPaste(ref obj, dic, fieldType)`
**When to use**: you want to copy/paste a complex nested struct across fields/pages without hand-rolling JSON serialization.
```csharp
object o = config;
if (UCL_GUILayout.DrawCopyPaste(ref o, m_DataDic, typeof(GameConfig)))
{
    config = (GameConfig)o; // paste succeeded; o has been replaced
}
```
**Mechanism**: backed by `UCL.Core.CopyPaste` + JSON; type mismatches are blocked.

---

## 6. Related Documents

- **Polymorphic field mechanism**: see [Architecture/Polymorphism_In_UCL.md](../../Architecture/Polymorphism_In_UCL.md) (explains how `[SerializeReference]` × `UCLI_TypeListable` × `UCL_PolymorphicHelper` × `UCL_TypeReflectCache` plug into GUI and serialization)
- **HelpURL mechanism**: see [Workflows/HelpURL_Workflow.md](../../Workflows/HelpURL_Workflow.md) (the `ucl_core:` / `eov_docs:` prefix that `DrawHelpButton` resolves)
- **Style hub**: see the XML docs on `UCL_GUIStyle.cs` (especially the LabelStyle "do not pass to Toggle/Button" warning)

---

## 7. When **Not** to Use UCL_GUILayout

- Plain text Label / Button / simple Layout — go straight to Unity `GUILayout`; no need to detour through UCL (unless you want color / auto sizing).
- Runtime UGUI / UI Toolkit — this toolkit is IMGUI (Editor-time + some runtime debug overlays); it does not target UGUI.
- High-frequency redraws of millions of fields per frame — IMGUI itself can't keep up; this layer shouldn't be forced to either; switch to UI Toolkit + VisualElement.
