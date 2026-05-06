---
title: Cmd_ExportDocsCatalog API
description: 扫描指定 markdown 文件夹，解析每份 .md 的 YAML frontmatter，输出单一可 Ctrl+F 搜索的索引文件；通过 frontmatter 的 aliases 字段实现「物品 ↔ 道具」这类同义词模糊搜索
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_ExportDocsCatalog.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [docs catalog, 文件索引, 文档目录, document index, 文档搜索, fuzzy search, aliases, 同义词]
tags: [agent_commands, docs, search]
---

# Cmd_ExportDocsCatalog

## 1. 概览

`Cmd_ExportDocsCatalog` 解决「项目 docs 数量爆炸（200+ 篇）→ agent / 人类无法靠单纯 grep 定位」的问题。它递归扫描指定 markdown root，把每份文件的 YAML frontmatter 解析后**汇总为单一表格**，agent 在 IDE 内 `Ctrl+F` 一次扫过即可定位候选清单，再去读原档。

| 特性 | 说明 |
|---|---|
| **不依赖 embedding / LLM** | 纯 keyword + alias 比对，离线即时、零成本 |
| **模糊搜索（同义词）** | 由各 doc 的 frontmatter `aliases:` 字段提供（无中央同义词表）|
| **静态快照** | catalog 是 frozen output；要刷新得重跑此 Cmd |

## 2. 参数格式 (Args Schema)

| 参数 | 必填 | 默认 | 说明 |
|---|:-:|---|---|
| `roots` | ❌ | `Docs;CardGame/Assets/UCL/UCL_Core/Docs~` | 分号或逗号分隔的目录清单（**git-root 相对**） |
| `outputPath` | ❌ | `Docs/_catalog.md` | 输出文件路径（**git-root 相对**） |
| `format` | ❌ | `md` | `md`（人类读）或 `json`（程序读）|
| `excludeDirs` | ❌ | `node_modules;.git;_Drafts` | 路径片段命中即略过 |
| `includeArchived` | ❌ | `false` | 是否列出 frontmatter 含 `archived: true` 的文件 |

> [!IMPORTANT]
> 与多数 Cmd 不同，本 Cmd 的 `outputPath` 与 `roots` 是相对 **git root**（不是 Unity project root），因为 `Docs/` 通常住在 git root 而非 `CardGame/`。

## 3. 解析的 frontmatter 字段

```yaml
---
title:           # 标题（缺则 fallback 至文件名 / 第一个 H1）
description:     # 一句话摘要
last_updated:    # YYYY-MM-DD
target_audience: # [Designer, AI_Agent, ...]
tags:            # [battle, status, ...]    — 受控词汇做分类
aliases:         # [物品, item, 道具]         — 自由同义词做模糊搜索
archived:        # true 表示已过时，默认不列入
---
```

## 4. 输出格式

### 4.1 Markdown（默认）— 依 top-dir 分组

```markdown
## `Docs/Workflows` (25)

| Path | Title | Description | Tags | Aliases | Audience | Updated |
|---|---|---|---|---|---|---|
| [`Docs/Workflows/...`](...) | 道具目录 | ... | item, catalog | 物品, item, 道具 | Designer | 2026-05-04 |
```

### 4.2 JSON（`format=json`）

参考英文 / 繁中版完整结构。

## 5. queue.json 调用

```json
{
  "Id": "20260506-export-docs-catalog",
  "Type": "ExportDocsCatalog",
  "Mode": "OneShot",
  "Args": {
    "roots": "Docs;CardGame/Assets/UCL/UCL_Core/Docs~",
    "outputPath": "Docs/_catalog.md",
    "format": "md"
  }
}
```

## 6. 模糊搜索 — Aliases

- **痛点**：搜「物品」找不到标题写「道具系统」的文件
- **解法**：在该文件 frontmatter 加 `aliases: [物品, item]`，下次跑 catalog 后命中

四类推荐同义词：中英对照 / 同概念别名 / 子系统别称 / 常用缩写。

## 7. 限制

- 只支持单行 frontmatter scalar 与行内 list（多行 list 写成 `[a, b]`）
- aliases 维护全靠人类自律
- catalog 是静态快照，加新 alias 后须重跑

## 8. 关联文档

- [DocsCatalog_Workflow](eov_docs:zh-Hans/Workflows/DocsCatalog_Workflow.md)
- [Cmd_ExportCommandCatalog](Cmd_ExportCommandCatalog.md)
- [UCL_AgentCommand_Architecture](UCL_AgentCommand_Architecture.md)

---

## 其他语系

- 🇬🇧 [English](../../../en/API/UCL_AgentCommand/Cmd_ExportDocsCatalog.md)
- 🇯🇵 [日本語](../../../ja/API/UCL_AgentCommand/Cmd_ExportDocsCatalog.md)
- 🇨🇳 简体中文（本档）
- 🇹🇼 [繁體中文](../../../zh-Hant/API/UCL_AgentCommand/Cmd_ExportDocsCatalog.md)
