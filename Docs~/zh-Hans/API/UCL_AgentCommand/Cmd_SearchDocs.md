---
title: Cmd_SearchDocs API
description: 即时模糊搜索全 Markdown 文件 — live scan 解析 frontmatter，依 title/aliases/tags/description/filename 加权计分后输出 ranked top-N；支持同义词展开，**不依赖 _catalog.md**
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_SearchDocs.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [search docs, 文档搜索, fuzzy search, 模糊搜索, 同义词]
tags: [agent_commands, docs, search]
---

# Cmd_SearchDocs

## 1. 概览

`Cmd_SearchDocs` 是 [`Cmd_ExportDocsCatalog`](Cmd_ExportDocsCatalog.md) 的姐妹 Cmd，但**完全独立**：每次 cold scan markdown roots 解析 frontmatter，做 ranking 后输出 top-N，**不读 catalog**。

## 2. 参数 (Args Schema)

| 参数 | 必填 | 默认 | 说明 |
|---|:-:|---|---|
| `query` | ✅ | — | 搜索词，多词空格分隔（AND）|
| `roots` | ❌ | `Docs;CardGame/Assets/UCL/UCL_Core/Docs~` | 分号分隔目录 |
| `limit` | ❌ | `20` | top-N |
| `format` | ❌ | `md` | `md` / `json` |
| `synonymsPath` | ❌ | (无) | 中央同义词文件 |
| `outputPath` | ❌ | (无) | 输出文件 |
| `searchMode` | ❌ | `and` | `and` / `or` |

## 3. 计分

各字段权重：title=10 / aliases=8 / tags=6 / description=5 / filename=4。每个 term 取最高分相加，加 termsHit×2 bonus。

## 4. 同义词两层机制

- **文件端 `aliases`**：每篇 doc 在 frontmatter 自我贴标
- **中央 `_synonyms.txt`**：CSV 行式同义词组，query 端展开

## 5. queue.json 调用

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

## 6. 限制

- 纯子串比对（无 Levenshtein）
- 只扫 frontmatter + filename，不扫 body
- 同义词文件是平面 list

## 7. 关联文档

- [Cmd_ExportDocsCatalog](Cmd_ExportDocsCatalog.md)
- [UCL_AgentCommand_Architecture](UCL_AgentCommand_Architecture.md)

---

## 其他语系

- 🇬🇧 [English](../../../en/API/UCL_AgentCommand/Cmd_SearchDocs.md)
- 🇯🇵 [日本語](../../../ja/API/UCL_AgentCommand/Cmd_SearchDocs.md)
- 🇨🇳 简体中文（本档）
- 🇹🇼 [繁體中文](../../../zh-Hant/API/UCL_AgentCommand/Cmd_SearchDocs.md)
