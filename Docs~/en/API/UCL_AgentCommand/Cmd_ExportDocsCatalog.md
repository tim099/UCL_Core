---
title: Cmd_ExportDocsCatalog API
description: Scans markdown roots, parses YAML frontmatter of each .md, and emits a single Ctrl+F-searchable catalog file; supports synonym fuzzy search via the per-doc `aliases` frontmatter field
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_ExportDocsCatalog.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [docs catalog, document index, fuzzy search, aliases, synonym search]
tags: [agent_commands, docs, search]
---

# Cmd_ExportDocsCatalog

## 1. Overview

`Cmd_ExportDocsCatalog` solves the "doc explosion" problem (200+ markdown files → grep alone is hopeless). It recursively scans markdown roots, parses each file's YAML frontmatter, and **emits a single browsable table**. AI agents and humans `Ctrl+F` the catalog once and locate candidate docs, then read the originals.

| Trait | Notes |
|---|---|
| **No embedding / LLM** | Pure keyword + alias matching — offline, instant, zero cost |
| **Fuzzy synonym search** | Powered by each doc's frontmatter `aliases:` field (no central thesaurus) |
| **Static snapshot** | Catalog is frozen output; rerun this Cmd to refresh |

Typical uses:
- Agent entering an unfamiliar project: search "battle" / "戰鬥" to find combat docs
- Looking for "equipment" docs but author wrote "飾品" → add `aliases: [equipment]` and rerun
- Designer filtering 200 docs to a topic via the Tags column

## 2. Args Schema

| Arg | Required | Default | Description |
|---|:-:|---|---|
| `roots` | ❌ | `Docs;CardGame/Assets/UCL/UCL_Core/Docs~` | Semicolon-/comma-separated dirs (**git-root-relative**) |
| `outputPath` | ❌ | `Docs/_catalog.md` | Output path (**git-root-relative**) |
| `format` | ❌ | `md` | `md` (human-readable) or `json` (machine-readable) |
| `excludeDirs` | ❌ | `node_modules;.git;_Drafts` | Path substrings to skip |
| `includeArchived` | ❌ | `false` | Whether to list docs whose frontmatter has `archived: true` |

> [!IMPORTANT]
> Unlike most Cmds, `outputPath` and `roots` are relative to **git root** (not Unity project root) because `Docs/` typically lives outside `CardGame/`.

## 3. Parsed Frontmatter Fields

```yaml
---
title:           # Display title (fallback: filename or first H1)
description:     # One-line summary
last_updated:    # YYYY-MM-DD
target_audience: # [Designer, AI_Agent, ...]
tags:            # [battle, status, ...]   — controlled vocab for categorization
aliases:         # [items, item, 物品]      — free-form synonyms for fuzzy search
archived:        # true → omitted from catalog by default
---
```

> [!NOTE]
> The parser is hand-rolled and only supports **single-line scalars and inline `[a, b]` lists**. Multi-line lists (`- a\n- b`) are not supported.

## 4. Output Formats

### 4.1 Markdown (default)

Grouped by top-dir, one table per group:

```markdown
## `Docs/Workflows` (25)

| Path | Title | Description | Tags | Aliases | Audience | Updated |
|---|---|---|---|---|---|---|
| [`Docs/Workflows/...`](...) | Item Catalog | ... | item, catalog | items, item, 道具 | Designer, AI_Agent | 2026-05-04 |
```

Footer "Stats" section: total docs / docs with frontmatter / docs with tags / docs with aliases.

### 4.2 JSON (`format=json`)

```json
{
  "command": "ExportDocsCatalog",
  "generated": "2026-05-06T22:43:00",
  "scan_roots": ["Docs", "CardGame/Assets/UCL/UCL_Core/Docs~"],
  "total": 204,
  "docs": [
    {
      "path": "Docs/Catalogs/Item_Catalog.md",
      "title": "Item Catalog",
      "description": "...",
      "tags": ["item", "catalog"],
      "aliases": ["items", "item", "道具"],
      "target_audience": ["Designer", "Game_Balancer", "AI_Agent"],
      "last_updated": "2026-05-04",
      "has_frontmatter": true
    }
  ]
}
```

## 5. queue.json invocation

```json
{
  "Id": "20260506-export-docs-catalog",
  "Type": "ExportDocsCatalog",
  "Mode": "OneShot",
  "Args": {
    "roots": "Docs;CardGame/Assets/UCL/UCL_Core/Docs~",
    "outputPath": "Docs/_catalog.md",
    "format": "md"
  },
  "Description": "Regenerate the docs catalog"
}
```

Or via Python wrapper:

```bash
senate ucmd run ExportDocsCatalog \
    --arg roots="Docs;CardGame/Assets/UCL/UCL_Core/Docs~" \
    --arg outputPath=Docs/_catalog.md \
    --arg format=md \
    --output-file Docs/_catalog.md \
    --timeout 60
```

## 6. Fuzzy Search — Aliases

### 6.1 Pain & Fix

- **Pain**: searching "items" misses a doc titled "Equipment System"
- **Fix**: add `aliases: [equipment, gear]` to that doc's frontmatter; the next catalog run includes those words in the row's Aliases column → `Ctrl+F` hits

### 6.2 Four Recommended Categories

1. Cross-language pairs: `[items, 物品]` / `[status, 狀態]`
2. Same-concept synonyms: `[gear, equipment]` / `[buff, 增益]`
3. Subsystem aliases: `[summon, minion, 從屬]`
4. Acronyms: `[CMD, Agent Command]` / `[SP, Status Power]`

### 6.3 Tags vs Aliases

| Field | Purpose | Vocab |
|---|---|---|
| `tags` | Categorization | **Controlled** (fixed enum) |
| `aliases` | Search variants | **Free-form** (author choice) |

Putting the same word in both is fine.

## 7. Limitations

| Limitation | Workaround |
|---|---|
| Only single-line frontmatter scalars / inline lists | Use `[a, b]` form for multi-value fields |
| Alias maintenance is manual | Remind in PR review; consider a future `Cmd_LintDocsAliases` |
| Catalog is a static snapshot | Rerun after adding aliases |
| No semantic search (concepts) | Future: `Cmd_SearchDocs` + embedding; for now keyword+alias is enough |

## 8. Related

- [DocsCatalog_Workflow](eov_docs:en/Workflows/DocsCatalog_Workflow.md) — Project-side SOP & design rationale
- [Cmd_ExportCommandCatalog](Cmd_ExportCommandCatalog.md) — Sister Cmd (exports Cmd catalog instead of docs catalog)
- [UCL_AgentCommand_Architecture](UCL_AgentCommand_Architecture.md) — Agent Command system architecture

---

## Other languages

- 🇬🇧 English (this file)
- 🇯🇵 [日本語](../../../ja/API/UCL_AgentCommand/Cmd_ExportDocsCatalog.md)
- 🇨🇳 [简体中文](../../../zh-Hans/API/UCL_AgentCommand/Cmd_ExportDocsCatalog.md)
- 🇹🇼 [繁體中文](../../../zh-Hant/API/UCL_AgentCommand/Cmd_ExportDocsCatalog.md)
