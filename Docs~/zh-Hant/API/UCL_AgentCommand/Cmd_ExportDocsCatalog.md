---
title: Cmd_ExportDocsCatalog API
description: 掃描指定 markdown 資料夾，解析每份 .md 的 YAML frontmatter，輸出單一可 Ctrl+F 搜尋的索引檔；透過 frontmatter 的 aliases 欄位實現「物品 ↔ 道具」這類同義詞模糊搜尋
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_ExportDocsCatalog.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [docs catalog, 文件索引, 文件目錄, document index, 文件搜尋, fuzzy search, aliases, 同義詞]
tags: [agent_commands, docs, search]
---

# Cmd_ExportDocsCatalog

## 1. 概覽

`Cmd_ExportDocsCatalog` 解決「專案 docs 數量爆炸（200+ 篇）→ agent / 人類無法靠單純 grep 定位」的問題。它遞迴掃指定 markdown root，把每份檔案的 YAML frontmatter 解析後**集中成一張單一表格**，agent 在 IDE 內 `Ctrl+F` 一次掃過就能找到候選清單，再去讀原檔。

| 特性 | 說明 |
|---|---|
| **不依賴 embedding / LLM** | 純 keyword + alias 比對，離線即時、零成本 |
| **模糊搜尋（同義詞）** | 由各 doc 的 frontmatter `aliases:` 欄位提供（無中央同義詞表）|
| **靜態快照** | catalog 是 frozen output；每次想刷新得重跑此 Cmd |

典型用途：
- agent 進新專案需快速定位「哪份文件講戰鬥配置」 → 搜「battle」/「戰鬥」
- 想找「裝備」相關但作者寫成「飾品」 → 在該 doc frontmatter 補 `aliases: [裝備]` 後重跑
- 設計師在 200 篇中找特定 SOP → 用 Tags 欄位過濾

## 2. 參數格式 (Args Schema)

| 參數 | 必填 | 預設 | 說明 |
|---|:-:|---|---|
| `roots` | ❌ | `Docs;CardGame/Assets/UCL/UCL_Core/Docs~` | 分號或逗號分隔的資料夾清單（**git-root 相對**） |
| `outputPath` | ❌ | `Docs/_catalog.md` | 輸出檔路徑（**git-root 相對**） |
| `format` | ❌ | `md` | `md`（人類讀）或 `json`（程式讀）|
| `excludeDirs` | ❌ | `node_modules;.git;_Drafts` | 路徑片段命中即略過 |
| `includeArchived` | ❌ | `false` | 是否列出 frontmatter 含 `archived: true` 的文件 |

> [!IMPORTANT]
> 與多數 Cmd 不同，本 Cmd 的 `outputPath` 與 `roots` 是相對 **git root**（不是 Unity project root），因為文件目錄 `Docs/` 通常住在 git root 而非 `CardGame/`。

## 3. 解析的 frontmatter 欄位

```yaml
---
title:           # 標題（缺則 fallback 至檔名 / 第一個 H1）
description:     # 一句話摘要
last_updated:    # YYYY-MM-DD
target_audience: # [Designer, AI_Agent, ...]
tags:            # [battle, status, ...]    — 受控詞彙做分類
aliases:         # [物品, item, 道具]         — 自由同義詞做模糊搜尋
archived:        # true 表示已過時，預設不列入
---
```

> [!NOTE]
> Parser 是 hand-rolled，**只支援單行 scalar 與行內 `[a, b]` list**。多行 list（`- a\n- b`）暫不支援。

## 4. 輸出格式

### 4.1 Markdown（預設）

依 top-dir 分群，每群一張表格：

```markdown
## `Docs/Workflows` (25)

| Path | Title | Description | Tags | Aliases | Audience | Updated |
|---|---|---|---|---|---|---|
| [`Docs/Workflows/...`](...) | 道具目錄 | ... | item, catalog | 物品, item, 道具, 消耗品 | Designer, AI_Agent | 2026-05-04 |
```

檔尾附「統計」區塊：總文件數 / 含 frontmatter 數 / 含 tags 數 / 含 aliases 數。

### 4.2 JSON（`format=json`）

```json
{
  "command": "ExportDocsCatalog",
  "generated": "2026-05-06T22:43:00",
  "scan_roots": ["Docs", "CardGame/Assets/UCL/UCL_Core/Docs~"],
  "total": 204,
  "docs": [
    {
      "path": "Docs/Catalogs/Item_Catalog.md",
      "title": "道具目錄 (Item Catalog)",
      "description": "...",
      "tags": ["item", "catalog"],
      "aliases": ["物品", "item", "items", "道具"],
      "target_audience": ["Designer", "Game_Balancer", "AI_Agent"],
      "last_updated": "2026-05-04",
      "has_frontmatter": true
    }
  ]
}
```

## 5. 在 queue.json 中呼叫

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
  "Description": "重新生成文件總目錄"
}
```

或 Python wrapper：

```bash
python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py run ExportDocsCatalog \
    --arg roots="Docs;CardGame/Assets/UCL/UCL_Core/Docs~" \
    --arg outputPath=Docs/_catalog.md \
    --arg format=md \
    --output-file Docs/_catalog.md \
    --timeout 60
```

## 6. 模糊搜尋 — Aliases 用法

### 6.1 痛點與解法

- **痛點**：搜「物品」找不到標題寫「道具系統」的文件
- **解法**：在該文件 frontmatter 加 `aliases: [物品, item]`，下次跑 catalog 後該行的 Aliases 欄位就含「物品」→ `Ctrl+F` 命中

### 6.2 推薦四類同義詞

1. 中英對照：`[物品, item]` / `[狀態, status]`
2. 同概念別名：`[道具, 物品]` / `[Buff, 增益]`
3. 子系統別稱：`[召喚, summon, 從屬]`
4. 常用縮寫：`[CMD, Agent Command]` / `[SP, Status Power]`

### 6.3 與 tags 的差異

| 欄位 | 用途 | 詞彙性質 |
|---|---|---|
| `tags` | 分類 / 主題 | **受控詞彙**（固定枚舉）|
| `aliases` | 搜尋變體 | **自由詞**（任作者填）|

同一個詞兩邊都放沒副作用。

## 7. 限制

| 限制 | 解法 |
|---|---|
| 只支援單行 frontmatter scalar 與行內 list | 多行 list 寫成 `[a, b]` 形式 |
| Aliases 維護全靠人類自律 | PR review 時提醒；或之後加 `Cmd_LintDocsAliases` 統計覆蓋率 |
| catalog 是靜態快照 | 加新 alias 後須手動重跑 Cmd |
| 不能搜「概念」（無語意搜尋） | 後續可加 `Cmd_SearchDocs` + embedding；目前夠用 |

## 8. 關聯文件

- [DocsCatalog_Workflow](eov_docs:zh-Hant/Workflows/DocsCatalog_Workflow.md) — EOV 端的完整 SOP 與設計理念
- [Cmd_ExportCommandCatalog](Cmd_ExportCommandCatalog.md) — 姐妹 Cmd（匯出 Cmd 目錄而非 docs 目錄）
- [UCL_AgentCommand_Architecture](UCL_AgentCommand_Architecture.md) — Agent Command 系統總體架構

---

## 其他語系

- 🇬🇧 [English](../../../en/API/UCL_AgentCommand/Cmd_ExportDocsCatalog.md)
- 🇯🇵 [日本語](../../../ja/API/UCL_AgentCommand/Cmd_ExportDocsCatalog.md)
- 🇨🇳 [简体中文](../../../zh-Hans/API/UCL_AgentCommand/Cmd_ExportDocsCatalog.md)
- 🇹🇼 繁體中文（本檔）
