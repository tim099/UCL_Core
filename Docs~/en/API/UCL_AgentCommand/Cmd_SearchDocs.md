---
title: Cmd_SearchDocs API
description: Live fuzzy search across all markdown docs — parses frontmatter on every call, scores by title/aliases/tags/description/filename weights, returns ranked top-N; supports synonym query expansion, **independent of _catalog.md**
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_SearchDocs.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [search docs, fuzzy search, query expansion, synonyms, ranked search, scoring]
tags: [agent_commands, docs, search]
---

# Cmd_SearchDocs

## 1. Overview

`Cmd_SearchDocs` is the sister Cmd of [`Cmd_ExportDocsCatalog`](Cmd_ExportDocsCatalog.md), but **completely independent**:

| | ExportDocsCatalog | **SearchDocs** |
|---|---|---|
| Purpose | Produce a **static index** for Ctrl+F | **Live search** + ranking |
| Reads catalog file? | It IS the catalog producer | ❌ **Never reads catalog** — cold scans every call |
| Output | `Docs/_catalog.md` | Top-N ranked hits (with score / matched fields) |
| Synonym expansion | per-doc frontmatter aliases | Doc aliases + central `_synonyms.txt` (query-side expansion) |

Design rationale: cold-scanning ~200 markdown files takes <200 ms on SSD — far cheaper than Ctrl+F-ing a stale catalog, and **always fresh**.

## 2. Args Schema

| Arg | Required | Default | Description |
|---|:-:|---|---|
| `query` | ✅ | — | Query terms, space-separated; default AND (all must match) |
| `roots` | ❌ | `Docs;CardGame/Assets/UCL/UCL_Core/Docs~` | Semicolon-separated dirs (git-root-relative) |
| `limit` | ❌ | `20` | Top-N hits to return |
| `format` | ❌ | `md` | `md` / `json` |
| `excludeDirs` | ❌ | `node_modules;.git;_Drafts` | Path substrings to skip |
| `synonymsPath` | ❌ | (none) | Central synonym file (git-root-relative) — e.g. `Docs/_synonyms.txt` |
| `outputPath` | ❌ | (none) | Output file path; if omitted, prints to Unity Console |
| `searchMode` | ❌ | `and` | `and` / `or` |

## 3. Scoring

For each entry × each query term (with synonyms expanded), case-insensitive substring match. Each term takes the **max** weight across fields hit; total score = sum across terms + bonus.

| Field hit | Weight |
|---|:-:|
| `title` | **10** |
| `aliases` | **8** |
| `tags` | **6** |
| `description` | **5** |
| `filename` | **4** |

Plus `termsHit × 2` bonus. In AND mode, any term that misses all fields → score=0 (filtered out).

## 4. Two-Layer Synonym Mechanism

### 4.1 Doc side: frontmatter `aliases`
Each doc declares "I might be searched as":
```yaml
aliases: [items, item, 物品, 道具]
```

### 4.2 Query side: central `_synonyms.txt` (optional)
Plain text (no YAML lib dep). One CSV equivalence group per line:
```text
# Comments start with #
物品, 道具, item, items, consumable
status, buff, debuff
```

When searching `query=物品` and that term appears in a row, the wrapper automatically expands to all words in that row.

Difference between the two layers:
- **Doc aliases**: "how this doc might be searched" — author tagged
- **Central synonyms**: "what variants users might type" — centrally maintained

## 5. Output

### 5.1 Markdown

```markdown
# 🔍 SearchDocs — "物品"

> Mode: **AND** · Scanned **208** docs · Found **5** hit(s)
>
> Terms expanded via synonyms:
>  - `物品 | 道具 | item | items | 消耗品 | consumable`

| # | Score | Path | Title | Matched | Description |
|---|---|---|---|---|---|
| 1 | 12 | [`Docs/Architecture/ItemEffect_Model.md`](...) | ItemEffect Strength Model | aliases, description, filename, tags, title | ... |
```

### 5.2 JSON (`format=json`)

Includes `command`, `query`, `scanned`, `hitCount`, `expandedTerms`, `hits[]` (with rank, score, path, title, description, matched_fields, tags, aliases).

## 6. queue.json invocation

```json
{
  "Id": "20260506-search-items",
  "Type": "SearchDocs",
  "Mode": "OneShot",
  "Args": {
    "query": "物品",
    "limit": "10",
    "format": "md",
    "synonymsPath": "Docs/_synonyms.txt"
  }
}
```

Or Python wrapper:

```bash
python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py run SearchDocs \
    --arg "query=物品" --arg limit=10 --arg format=md \
    --arg synonymsPath=Docs/_synonyms.txt --timeout 30
```

> [!TIP]
> Without `outputPath`, the Cmd prints the hit table directly to Unity Console (Debug.Log).

## 7. Limitations

- Pure substring; no fuzzy editing distance — typos rely on aliases / synonyms
- Frontmatter + filename only; body content not searched (use a future `Cmd_GrepDocs` if needed)
- Synonyms file is flat (no hierarchy) — sufficient for current scale

## 8. Related

- [Cmd_ExportDocsCatalog](Cmd_ExportDocsCatalog.md) — sister Cmd (static catalog)
- [DocsCatalog_Workflow](eov_docs:en/Workflows/DocsCatalog_Workflow.md) — project SOP
- [UCL_AgentCommand_Architecture](UCL_AgentCommand_Architecture.md)

---

## Other languages

- 🇬🇧 English (this file)
- 🇯🇵 [日本語](../../../ja/API/UCL_AgentCommand/Cmd_SearchDocs.md)
- 🇨🇳 [简体中文](../../../zh-Hans/API/UCL_AgentCommand/Cmd_SearchDocs.md)
- 🇹🇼 [繁體中文](../../../zh-Hant/API/UCL_AgentCommand/Cmd_SearchDocs.md)
