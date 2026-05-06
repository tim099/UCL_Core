---
title: UCL_Core Polymorphism Architecture
description: Roles and interactions of [SerializeReference], UCLI_TypeListable, UCL_PolymorphicHelper, and UCL_TypeReflectCache across the UCL_Asset editing (GUI) and serialization (JSON) paths, plus the recommended pattern for declaring new polymorphic fields.
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/InterfaceCore/
namespace: UCL.Core
last_updated: 2026-05-06
target_audience: [Tools_Maintainer, Gameplay_Programmer, AI_Agent]
---

# UCL_Core Polymorphism Architecture

## 1. The four components

```
[SerializeReference] (Unity attribute, on field)
UCLI_TypeListable / UCLI_TypeList (UCL interface marker, on type)
        │
        ▼
UCL_PolymorphicHelper       ← polymorphism SSOT
  IsPolymorphicField  IsPolymorphicElement  GetConcreteSubtypes
        │
        ▼
UCL_TypeReflectCache        ← per-(Type, SaveMode) field metadata, no pre-filter
  m_Entries: List<UCL_FieldEntry>
  UCL_FieldEntry: m_IsPolymorphicField / m_HideOnGUI / m_HideInJson / m_Conditional / ...
                  GetAttr<T>() — lazy + cached
        │
        ▼
GUI path                                 JSON path
  FieldInfoCache (adapter)                 SaveFieldsToJson
  TypeFieldInfoCache (adapter)             LoadFieldFromJson
  filter !m_HideOnGUI                      filter !m_HideInJson && !MulticastDelegate
                                           runtime: m_Conditional?.IsShow(obj)
```

## 2. The two polymorphism signals

| Signal | Where | Meaning | Triggers |
|---|---|---|---|
| `[SerializeReference]` | **field** | "the runtime value may be a subclass of the declared type" | GUI dropdown, JSON ClassName wrap |
| `UCLI_TypeListable` | **type** (interface impl) | "this type retains subclass info when used as a collection element" | JSON IList per-item ClassName wrap |

Both can be combined. Single fields need only `[SerializeReference]`. Collection elements need either the type-side interface or the new wrapped-format auto-detection (Step 3a).

## 3. Standard patterns

### 3.1 Single field

```csharp
public abstract class MyBase { ... }
public class MyConcrete : MyBase { ... }

public class MyOwner
{
    [SerializeReference] public MyBase m_Field;     // ✅ GUI dropdown + JSON round-trip
}
```

No need for `UCLI_TypeListable` on `MyBase`. GUI lists subclasses via `UCL_PolymorphicHelper.GetConcreteSubtypes`; JSON wraps as `{ClassName, ClassData}`.

### 3.2 Collection

```csharp
[SerializeReference] public List<MyBase> m_List;    // ✅ Step 3a list items round-trip
```

JSON layout:

```json
"m_List": {
  "ClassName": "List<MyBase> AQN",
  "ClassData": [
    { "ClassName": "MyConcrete, ...", "ClassData": { ... } },
    ...
  ]
}
```

### 3.3 Exception: `UnityJsonSerializableObject` elements

The element's own `SerializeToJson`/`DeserializeFromJson` already produces the wrapper. Both Save and Load detect this and skip the new path to avoid double-wrap / mis-routing.

## 4. Reflect cache usage

```csharp
var aCache = UCL_TypeReflectCache.Get(typeof(MyAsset), JsonConvert.SaveMode.Unity);
foreach (var aEntry in aCache.m_Entries)
{
    if (aEntry.m_HideInJson) continue;
    if (aEntry.m_IsPolymorphicField) { /* polymorphic path */ }
    var aFolderExp = aEntry.GetAttr<UCL_FolderExplorerAttribute>();  // lazy + cached
}
```

**No pre-filter** — GUI uses `!m_HideOnGUI`, JSON uses `!m_HideInJson && !m_IsMulticastDelegate`. Sharing a single field set across both paths is the whole point of the cache.

## 5. Why `UCL_FieldEntry` ctor must not call services

Cache is built early during JSON load (loading `UCL_LocalizeAsset` itself triggers cache construction). At that moment `UCL_ModuleService` / `UCL_LocalizeManager` may not yet be initialized.

**Forbidden** (causes early-load NRE / cycle):

| Operation | Why |
|---|---|
| `UCL_LocalizeManager.Get(header)` | LocalizeManager loads `UCL_LocalizeAsset` → cache build → LocalizeManager → cycle → StackOverflow |
| `GetCustomAttributes(true)` | Constructs **all** attribute instances, including `[UCL_FolderExplorer]` whose ctor calls `UCL_ModuleService` (not yet init → NRE) |

**Safe**: `GetCustomAttribute<T>()` constructs only T. The eager-fetched flags (SerializeReference / HideInJson / Conditional / FormerlyAs / HideOnGUI / AlwaysExpendOnGUI) target attribute classes with no service dependencies.

Cold attributes go through `GetAttr<T>()` — lazy + dict cache, only fetched when GUI (or other service-ready path) accesses them.

## 6. JSON list polymorphism (Step 3a)

**Save**: `[SerializeReference] List<...>` → outer `{ClassName, ClassData}` + per-item `ObjectToJson`. Element of `UnityJsonSerializableObject` subclass → fall back to original `ObjectToJson(list)`.

**Load**: `LoadDataFromJson` IList branch detects either:
- `UCLI_TypeList(able)` element type (existing path), or
- first item contains `ClassName` key (new auto-detect)

Both routes per-item `JsonToObject`. `UnityJsonSerializableObject` element type is excluded from both — handled by `DataToObject`'s dedicated branch instead.

## 7. Related

- 📋 [SerializeReference_Symmetry_Plan](../Plan/SerializeReference_Symmetry_Plan.md)
- 📖 [DevLog 00005](../../../DevLogs~/00005_2026-05-06.md)
- 🤖 [Cmd_DiagnoseAssetReflection](../API/UCL_AgentCommand/Cmd_DiagnoseAssetReflection.md)
- 🤖 [Cmd_FindAssetUsages](../API/UCL_AgentCommand/Cmd_FindAssetUsages.md)

---

## Other Languages

- 🇬🇧 English (this document)
- 🇯🇵 [日本語](../../ja/Architecture/Polymorphism_In_UCL.md)
- 🇨🇳 [简体中文](../../zh-Hans/Architecture/Polymorphism_In_UCL.md)
- 🇹🇼 [繁體中文](../../zh-Hant/Architecture/Polymorphism_In_UCL.md)
