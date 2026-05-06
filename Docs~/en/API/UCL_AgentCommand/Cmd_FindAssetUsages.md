---
title: Cmd_FindAssetUsages API
description: Reverse lookup of UCL_Asset references — given a target asset (e.g. RCG_CustomStatusData/Stun), scan every (or selected) UCL_Asset subclass instance via reflection, locate `UCLI_AssetEntry` fields pointing to the target, and emit a list of (UsedBy_AssetType, UsedBy_ID, JSON path, dotted field path) hits.
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_FindAssetUsages.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# Cmd_FindAssetUsages

## 1. Overview

`Cmd_FindAssetUsages` is the **reverse** counterpart of [Cmd_ResolveAssetReferences](Cmd_ResolveAssetReferences.md):

| | Forward (Resolve) | Reverse (FindUsages) |
|---|---|---|
| Start | One asset | One target asset |
| Question | "Who do I reference?" | "**Who references me?**" |
| Output | Dependency chain / scope manifest | List of usage points (with field paths) |

Given a target asset (e.g. `RCG_CustomStatusData/Stun`), the Cmd scans the UCL_Asset subclasses listed in `searchTypes` (or all of them by default), reflects into every instance's field tree, finds `UCLI_AssetEntry` values pointing at the target, and records the **dotted field path** (e.g. `m_Effects[2].m_Setting.m_StatusEntry`).

Typical use cases:
- "Who uses `RCG_CustomStatusData/Stun`?" — list every RCG_CardData / RCG_ItemData / RCG_EquipmentData / monster skill that references it
- **Refactor safety net**: see the impact radius before deleting/renaming an asset
- **Balance analysis**: how many cards apply or counter a given status?
- **Design link visualization**: how often is an ItemEffect reused, and across which ItemData?

## 2. Args Schema

| Arg | Required | Default | Description |
|---|:-:|---|---|
| `targetType` | ✅ | — | C# type name of the asset being looked up, e.g. `RCG_CustomStatusData` |
| `targetIds` | ✅ | — | CSV asset IDs to look up, e.g. `Stun,Burn` |
| `searchTypes` | ❌ | (all UCL_Asset subclasses) | CSV asset types to scan, e.g. `RCG_CardData,RCG_ItemData` |
| `outputPath` | ❌ | `AgentCommands/asset_usages_<Type>_<timestamp>.<ext>` | Output path relative to Unity project root (`CardGame/`) |
| `format` | ❌ | `json` | `json` (machine) or `md` (human, grouped per target) |
| `module` | ❌ | (all modules) | Restrict the **using-side** module (does not affect the target's module) |
| `maxFieldDepth` | ❌ | `16` | Reflection recursion cap to guard cycles |

> [!TIP]
> If you know roughly which asset categories use the target, **strongly recommend** specifying `searchTypes` to keep scan time low. Scanning everything on a large project takes seconds to tens of seconds.

## 3. Output Format

### 3.1 JSON (default)

```json
{
  "command": "FindAssetUsages",
  "timestamp": "2026-05-06T12:34:56",
  "targetType": "RCG_CustomStatusData",
  "targetIds": ["Stun"],
  "scannedTypes": 24,
  "scannedAssets": 873,
  "totalHits": 5,
  "usagesByTarget": [
    {
      "targetKey": "RCG_CustomStatusData:Stun",
      "hitCount": 5,
      "hits": [
        {
          "usedByType": "RCG_ItemData",
          "usedById": "EmotionalDamage",
          "path": "CardGame/Assets/.BuiltinModules/.../RCG_ItemData/EmotionalDamage.json",
          "exists": true,
          "fieldPath": "$.m_ItemEffects[0].m_Setting.m_StatusEntry"
        }
        // ...
      ]
    }
  ]
}
```

### 3.2 Markdown (`format=md`)

```markdown
# Asset Usages — RCG_CustomStatusData

- **Targets**: Stun
- **Scanned**: 873 asset(s) across 24 type(s)
- **Total Hits**: 5

## `RCG_CustomStatusData:Stun` (5 hit(s))

| UsedBy Type | UsedBy ID | Field Path | JSON Path |
|---|---|---|---|
| `RCG_CardData` | `ThunderStrike` | `$.m_OnPlayEffects[1].m_Settings[0].m_StatusEntry` | `CardGame/.../ThunderStrike.json` |
| ...
```

> [!NOTE]
> In markdown mode, **targets with zero hits** are listed as `> No usages found.` blocks so you don't mistake an empty result for a broken Cmd.

## 4. queue.json Example

```json
{
  "Id": "20260506-find-stun-usages",
  "Type": "FindAssetUsages",
  "Mode": "OneShot",
  "Args": {
    "targetType": "RCG_CustomStatusData",
    "targetIds": "Stun",
    "searchTypes": "RCG_CardData,RCG_ItemData,RCG_EquipmentData",
    "format": "md"
  },
  "Description": "Find every Card / Item / Equipment referencing Stun"
}
```

## 5. Behavior

### 5.1 Reflection mechanics

- For each `searchType`, call `UCL_Util<T>.Util.GetAllIDs()` to enumerate IDs
- For each ID, load the instance via `UCL_Util<T>.Util.GetAsset(id, true)`
- Reflect over instance fields (public + private — UCL's JSON serializer uses fields)
- On a `UCLI_AssetEntry`, compare `(entry.AssetType, entry.ID)` against the target set
- On a hit, record the dotted field path (`$.m_Foo[2].m_Bar.m_Entry`)
- Skip primitive / enum / string / `UnityEngine.Object`
- `ReferenceEqualityComparer` guards against cycles and shared subobjects

### 5.2 Field Path Format

- Root: `$`
- Field: `.FieldName`
- Collection index: `[i]`
- Example: `$.m_Effects[2].m_Settings[0].m_StatusEntry`

> [!IMPORTANT]
> The field path uses the **C# field name at reflection time**, which is *usually* identical to the JSON serialization key — but custom serializers can diverge. When mapping back to JSON, treat the C# class definition as the source of truth.

### 5.3 Diff vs ResolveAssetReferences

| | ResolveAssetReferences | FindAssetUsages |
|---|---|---|
| Direction | Forward (start → references) | Reverse (target ← referrers) |
| Cross-asset hop | ✅ BFS multi-level | ❌ Direct usage points only |
| Action on hit | Enqueue the referenced asset and continue | Record field path and stop descending |
| Main cost | Reference chain length | Total assets × average field depth |
| Field path | ❌ Not recorded | ✅ Dotted path recorded |

## 6. Limitations

- Only resolves **design-time asset references** (`UCLI_AssetEntry`); does not resolve Localize keys or Sprite keys (those are strings, not entries)
- Default mode scans every UCL_Asset subclass — large projects should constrain `searchTypes`
- Reflection runs only in Editor (`#if UNITY_EDITOR`)
- A field that points to multiple target IDs produces multiple hits (one per target)
- Reflection field name ≠ JSON key in rare custom-serializer cases

## 7. Related

- [Cmd_ResolveAssetReferences](Cmd_ResolveAssetReferences.md) — forward version
- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [Create_Cmd_Workflow](../../Workflows/Create_Cmd_Workflow.md) — SOP for adding new Cmds

---

## Other Languages

- 🇬🇧 English (this document)
- 🇯🇵 [日本語](../../../ja/API/UCL_AgentCommand/Cmd_FindAssetUsages.md)
- 🇨🇳 [简体中文](../../../zh-Hans/API/UCL_AgentCommand/Cmd_FindAssetUsages.md)
- 🇹🇼 [繁體中文](../../../zh-Hant/API/UCL_AgentCommand/Cmd_FindAssetUsages.md)
