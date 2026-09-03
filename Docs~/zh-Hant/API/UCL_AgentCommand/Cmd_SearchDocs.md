---
title: Cmd_SearchDocs API
description: 即時模糊搜尋全 Markdown 文件 — live scan 解析 frontmatter，依 title/aliases/tags/description/filename 加權計分後輸出 ranked top-N；支援同義詞展開，**不依賴 _catalog.md**
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_SearchDocs.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [search docs, 文件搜尋, fuzzy search, 模糊搜尋, query expansion, 同義詞, ranked, scoring]
tags: [agent_commands, docs, search]
---

# Cmd_SearchDocs

## 1. 概覽

`Cmd_SearchDocs` 是 [`Cmd_ExportDocsCatalog`](Cmd_ExportDocsCatalog.md) 的姐妹 Cmd，但**完全獨立**：

| | ExportDocsCatalog | **SearchDocs** |
|---|---|---|
| 目的 | 產**靜態索引**給 Ctrl+F 用 | **即時搜尋**並 ranking |
| 依賴 catalog 檔？ | 自己就是輸出 catalog | ❌ **不讀 catalog**，每次 cold scan |
| 輸出 | `Docs/_catalog.md` | top-N 命中表（含 score / 命中欄位）|
| 同義詞展開 | doc 端的 frontmatter aliases | doc aliases + 中央 `_synonyms.txt`（query 端展開）|

設計取捨：每次都重新 scan 200 篇 markdown 在 SSD 上 <200ms，遠低於 Ctrl+F 翻 catalog 的成本，且**永遠新鮮**（catalog 可能漏 regen）。

## 2. 參數格式 (Args Schema)

| 參數 | 必填 | 預設 | 說明 |
|---|:-:|---|---|
| `query` | ✅ | — | 搜尋詞，多詞以空白分隔；預設 AND（全部都得命中）|
| `roots` | ❌ | `Docs;CardGame/Assets/UCL/UCL_Core/Docs~` | 分號分隔資料夾（git-root 相對）|
| `limit` | ❌ | `20` | 回傳 top-N 命中 |
| `format` | ❌ | `md` | `md`（人類讀）/ `json`（程式讀）|
| `excludeDirs` | ❌ | `node_modules;.git;_Drafts` | 路徑片段命中即略過 |
| `synonymsPath` | ❌ | （無）| 中央同義詞檔（git-root 相對）— 例 `Docs/_synonyms.txt` |
| `outputPath` | ❌ | （無）| 輸出檔；不指定則只印 Console |
| `searchMode` | ❌ | `and` | `and` / `or` |

## 3. 計分機制

對每個 entry 對每個 query term（含同義詞展開後）做 case-insensitive 子字串比對，採用**最高分制**（多欄位命中只取最高權重），最後 sum 所有 term：

| 命中欄位 | 權重 |
|---|:-:|
| `title` | **10** |
| `aliases` | **8** |
| `tags` | **6** |
| `description` | **5** |
| `filename` | **4** |

加上 `termsHit × 2` bonus。AND 模式下任一 term 全部欄位都 miss → score=0（被過濾）。

## 4. 同義詞兩層機制

### 4.1 文件端：frontmatter `aliases`
每篇 doc 自己貼標可能被搜到的詞：
```yaml
aliases: [物品, item, items, 道具, 消耗品]
```

### 4.2 Query 端：中央 `_synonyms.txt`（選用）
純文字格式（避免 YAML lib 依賴），每行一組逗號分隔的同義詞集合：
```text
# 註解行以 # 起始
物品, 道具, item, items, 消耗品
狀態, status, buff, debuff
```

當搜尋 `query=物品` 且該 query 在某行內，wrapper 自動把該行其他詞也當作搜尋變體，與每篇 doc 的 aliases 同時比對。

兩層的差異：
- **doc aliases**：「我這篇可能被怎麼搜到」 → 作者貼標
- **中央 synonyms**：「使用者可能輸入的變體」 → 集中維護

## 5. 輸出範例

### 5.1 Markdown

```markdown
# 🔍 SearchDocs — "物品"

> Mode: **AND** · Scanned **208** docs · Found **5** hit(s)
>
> Terms expanded via synonyms:
>  - `物品 | 道具 | item | items | 消耗品 | consumable`

| # | Score | Path | Title | Matched | Description |
|---|---|---|---|---|---|
| 1 | 12 | [`Docs/Architecture/ItemEffect_Model.md`](...) | 道具效果強度模型 | aliases, description, filename, tags, title | ... |
| ...
```

### 5.2 JSON（`format=json`）

```json
{
  "command": "SearchDocs",
  "query": "物品",
  "scanned": 208,
  "hitCount": 5,
  "expandedTerms": [["物品", "道具", "item", "items", "消耗品", "consumable"]],
  "hits": [
    {
      "rank": 1, "score": 12,
      "path": "Docs/Architecture/ItemEffect_Model.md",
      "title": "道具效果強度模型 (ItemEffect Strength Model)",
      "description": "...",
      "matched_fields": ["aliases", "description", "filename", "tags", "title"],
      "tags": ["item", "balance", "model"],
      "aliases": ["物品", "item", "items", "道具", "消耗品", "consumable", "物品效果", "..."]
    }
  ]
}
```

## 6. 在 queue.json 中呼叫

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
  },
  "Description": "搜尋全部與『物品』相關的文件"
}
```

或 Python wrapper：

```bash
senate ucmd run SearchDocs \
    --arg "query=物品" \
    --arg limit=10 \
    --arg format=md \
    --arg synonymsPath=Docs/_synonyms.txt \
    --timeout 30
```

> [!TIP]
> 不指定 `outputPath` 時 Cmd 直接把命中表印到 Unity Console（Debug.Log）— agent 用 Python wrapper 會看到 stdout。指定 `outputPath` 才會寫檔。

## 7. 限制

| 限制 | 解法 |
|---|---|
| 純子字串比對，無 fuzzy editing distance | 拼字錯誤要靠 aliases / synonyms 補；未來可加 Levenshtein |
| 不搜 body（只搜 frontmatter + filename）| 預設行為，避免大檔拖慢；如需 body 搜尋自行寫 `Cmd_GrepDocs` |
| 同義詞檔是平面 list，不支援階層 | 簡單夠用；複雜需求可換 yaml lib |
| 同 query 重複展開，沒 dedup query side | 微秒級，可忽略 |

## 8. 關聯文件

- [Cmd_ExportDocsCatalog](Cmd_ExportDocsCatalog.md) — 姐妹 Cmd（產靜態索引）
- [DocsCatalog_Workflow](eov_docs:zh-Hant/Workflows/DocsCatalog_Workflow.md) — EOV 端的完整 SOP
- [UCL_AgentCommand_Architecture](UCL_AgentCommand_Architecture.md)

---

## 其他語系

- 🇬🇧 [English](../../../en/API/UCL_AgentCommand/Cmd_SearchDocs.md)
- 🇯🇵 [日本語](../../../ja/API/UCL_AgentCommand/Cmd_SearchDocs.md)
- 🇨🇳 [简体中文](../../../zh-Hans/API/UCL_AgentCommand/Cmd_SearchDocs.md)
- 🇹🇼 繁體中文（本檔）
