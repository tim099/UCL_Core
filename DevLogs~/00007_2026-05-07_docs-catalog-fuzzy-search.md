---
date: 2026-05-07
index: 00007
title: 文件目錄索引 + 模糊搜尋系統 — Cmd_ExportDocsCatalog / Cmd_SearchDocs / UCL_DocSearchPage
tags: [feature]
---

# 文件目錄索引 + 模糊搜尋系統

## What

跨專案文件爆量（200+ 篇）後，「想找一篇文件不知從何找起」的解法 — 三個層次：

| 層 | 元件 | 用途 |
|---|---|---|
| **基礎** | [`UCL_DocCatalogScanner`](../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/UCL_DocCatalogScanner.cs) | 共用 helper：掃 markdown roots + 解析 YAML frontmatter |
| **靜態索引** | [`Cmd_ExportDocsCatalog`](../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_ExportDocsCatalog.cs) | 輸出單一 `_catalog.md`（Path / Title / Description / Tags / **Aliases** 表格），給 IDE Ctrl+F 用 |
| **即時搜尋** | [`Cmd_SearchDocs`](../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_SearchDocs.cs) + [`UCL_DocSearchPage`](../UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_DocSearchPage.cs) + [`UCL_DocSearchEngine`](../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/UCL_DocSearchEngine.cs) | live scan + ranking，**不依賴 catalog 檔**，永遠新鮮 |

**模糊搜尋兩層機制**：
- **Doc 端 frontmatter `aliases:`** — 文件作者貼標可能被搜尋到的詞（例「道具系統」加 `aliases: [物品, item]`）
- **中央 `Docs/_synonyms.txt`**（純文字 CSV 格式）— query 端展開：搜「物品」自動展成 `物品 | 道具 | item | items | 消耗品`

**計分權重**：title=10 / aliases=8 / tags=6 / description=5 / filename=4 + termsHit×2 bonus

**語系偏好排序**：路徑含當前 `UCL_LocalizeManager.s_LangName` 段（如 `/zh-Hant/`）+5 score，讓對應語系版本排前。

**UCL_DocSearchPage**（獨立 Editor 頁）：
- text field + Enter 觸發 / Search 按鈕
- 進階選項可折疊：mode AND/OR / limit slider 5–100 / synonyms 路徑 / includeArchived
- 每筆結果含「📂 定位（檔案管理員）」+「📖 開啟檔案（OS 預設應用）」兩顆動作按鈕
- UCL_Core 內文件走 `UCL_URL.ResolveURL("ucl_core:...")` 路徑開啟，其他位置走 `file:///` 絕對路徑
- 從 `UCL_WelcomePage` 或 EditorMenu 主頁的「🔍 開啟文件搜尋頁」按鈕進入

## Why

兩個現實痛點：
1. 專案文件超過 200 篇後，純記憶 + grep 已找不到 — 需要結構化索引
2. 命名習慣不一致（同一個概念在不同文件叫「道具」/「物品」/「item」）— 需要同義詞展開機制

純 keyword + alias 比對，**不依賴 embedding / LLM**，離線即時、零成本。

## How to use

### 從 Welcome 或 EditorMenu

點「🔍 開啟文件搜尋頁」→ 輸入關鍵字 → Enter / Search → 點結果的「📖 開啟檔案」直接看內容。

### CLI / Agent batch

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run SearchDocs \
    --arg "query=物品" --arg limit=10 --arg format=md \
    --arg synonymsPath=Docs/_synonyms.txt --timeout 30
```

```bash
# 重新生成靜態索引
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run ExportDocsCatalog \
    --arg roots="Docs;CardGame/Assets/UCL/UCL_Core/Docs~" \
    --arg outputPath=Docs/_catalog.md --output-file Docs/_catalog.md
```

### 給自家文件加 aliases

```yaml
---
title: 道具系統 (RCG_ItemData)
description: ...
aliases: [物品, item, items, 道具, 消耗品]
tags: [item, drop_pool]
---
```

### Catalog vs SearchDocs 何時用哪個

| 情境 | 建議 |
|---|---|
| 想瀏覽所有文件、看大致分布 | **Catalog**（Ctrl+F + 視覺化分群）|
| 已知關鍵詞、要 ranked 結果 | **SearchDocs**（自動 ranking）|
| query 含冷僻同義詞 | **SearchDocs**（query 端展開更廣）|

## Breaking changes

無。新增功能不影響既有路徑。

## 相關文件

- 📚 [Cmd_ExportDocsCatalog](../Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_ExportDocsCatalog.md)
- 🔍 [Cmd_SearchDocs](../Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_SearchDocs.md)
