---
title: Cmd_SearchDocs API
description: 全 Markdown ドキュメントをリアルタイムでファジー検索 — 毎回 frontmatter を解析し、title/aliases/tags/description/filename の重み付けで採点後 ranked top-N を出力；同義語展開対応、**_catalog.md に依存しない**
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_SearchDocs.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [search docs, ドキュメント検索, fuzzy search, ファジー検索, 同義語]
tags: [agent_commands, docs, search]
---

# Cmd_SearchDocs

## 1. 概要

`Cmd_SearchDocs` は [`Cmd_ExportDocsCatalog`](Cmd_ExportDocsCatalog.md) の姉妹 Cmd ですが**完全に独立**しています：毎回 markdown ルートを cold scan し、frontmatter を解析、ranking 後に top-N を出力します。**catalog ファイルを読みません**。

## 2. 引数 (Args Schema)

| 引数 | 必須 | デフォルト | 説明 |
|---|:-:|---|---|
| `query` | ✅ | — | 検索クエリ、スペース区切り（AND）|
| `roots` | ❌ | `Docs;CardGame/Assets/UCL/UCL_Core/Docs~` | セミコロン区切りフォルダ |
| `limit` | ❌ | `20` | top-N |
| `format` | ❌ | `md` | `md` / `json` |
| `synonymsPath` | ❌ | (なし) | 中央同義語ファイル |
| `outputPath` | ❌ | (なし) | 出力ファイル |
| `searchMode` | ❌ | `and` | `and` / `or` |

## 3. スコアリング

各フィールドの重み：title=10 / aliases=8 / tags=6 / description=5 / filename=4。各 term は最高重みを採用し合算、`termsHit × 2` ボーナス。

## 4. 同義語の二層メカニズム

- **ドキュメント側 `aliases`**：各 doc が frontmatter で自己ラベリング
- **中央 `_synonyms.txt`**：CSV 行形式の同義語グループ、query 側で展開

## 5. queue.json 呼び出し

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

## 6. 制限

- 純粋な部分文字列マッチ（Levenshtein なし）
- frontmatter + filename のみ走査、body は対象外
- 同義語ファイルはフラットリスト

## 7. 関連ドキュメント

- [Cmd_ExportDocsCatalog](Cmd_ExportDocsCatalog.md)
- [UCL_AgentCommand_Architecture](UCL_AgentCommand_Architecture.md)

---

## 他言語

- 🇬🇧 [English](../../../en/API/UCL_AgentCommand/Cmd_SearchDocs.md)
- 🇯🇵 日本語（本ファイル）
- 🇨🇳 [简体中文](../../../zh-Hans/API/UCL_AgentCommand/Cmd_SearchDocs.md)
- 🇹🇼 [繁體中文](../../../zh-Hant/API/UCL_AgentCommand/Cmd_SearchDocs.md)
