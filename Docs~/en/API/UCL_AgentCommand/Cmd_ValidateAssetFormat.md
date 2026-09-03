---
title: Cmd_ValidateAssetFormat API
description: Round-trip serialize/deserialize a UCL_Asset to detect schema or formatting issues, plus optional BFS reference integrity check
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_ValidateAssetFormat.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-05
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# Cmd_ValidateAssetFormat

> 📖 **Full documentation pending translation.** See the canonical Traditional Chinese version: [zh-Hant/API/UCL_AgentCommand/Cmd_ValidateAssetFormat.md](../../../zh-Hant/API/UCL_AgentCommand/Cmd_ValidateAssetFormat.md).

## TL;DR

Reads an asset's JSON file, deserialises it through the loader, re-serialises it back to JSON, and diffs the two canonical forms to detect:

- **Schema drift** — fields the loader doesn't recognise (silently dropped) or default values it filled in (likely missing in source)
- **Reference integrity** (optional via `checkRefs=N` arg) — sub-assets referenced by the asset that don't exist on disk

Captures all Unity Console errors emitted during load/serialize so you can correlate diffs with their underlying causes (e.g. enum parse failures, missing sub-assets).

## Args

| Arg | Required | Default | Notes |
|---|:-:|---|---|
| `assetType` | ✅ |  | C# Type name, e.g. `RCG_ItemData` |
| `assetId` | ✅ |  | Asset ID, no extension |
| `outputPath` | ❌ | `AgentCommands/asset_format_check_<type>_<id>.md` | Report path (relative to Unity project root) |
| `fixedPath` | ❌ | sibling `.fixed.json` | Roundtrip JSON; written when verdict ≠ PASS |
| `verbose` | ❌ | `false` | Include full content in report |
| `checkRefs` | ❌ | `0` | BFS depth for reference check (0=off, 1=direct, 2+=walk further) |
| `ignoreEmptyIds` | ❌ | `true` | Skip empty asset entry IDs |

## Verdicts

- `PASS` — raw matches roundtrip (perfect)
- `FormattingOnly` — canonical equal but raw differs (whitespace / order only)
- `SchemaDiff` — canonical differs (real schema problem)
- `Error` — could not run (file missing, parse exception, etc.)

Plus a separate `reference_check` axis: `Skipped | OK | Missing`.

## Quick example

```bash
senate ucmd run ValidateAssetFormat     --arg assetType=RCG_ItemData --arg assetId=ManaCore_Shard --arg checkRefs=1
```

See the [Traditional Chinese version](../../../zh-Hant/API/UCL_AgentCommand/Cmd_ValidateAssetFormat.md) for full report structure, diagnostic patterns, and known limits.
