---
title: Cmd_ExportDocsCatalog API
description: 指定された markdown ルートを走査し、各 .md の YAML frontmatter を解析して単一の Ctrl+F 検索可能なカタログファイルを出力する；frontmatter の aliases 欄により「アイテム ↔ 道具」のような同義語ファジー検索を実現
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_ExportDocsCatalog.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [docs catalog, ドキュメント索引, document index, ファジー検索, fuzzy search, aliases, 同義語]
tags: [agent_commands, docs, search]
---

# Cmd_ExportDocsCatalog

## 1. 概要

`Cmd_ExportDocsCatalog` は「プロジェクトの docs が爆発的に増え（200+ 篇）→ agent / 人間が単純な grep で定位できない」問題を解決します。指定された markdown root を再帰的に走査し、各ファイルの YAML frontmatter を解析して**単一テーブルに集約**します。agent は IDE 内で `Ctrl+F` 一発で候補ドキュメントを特定し、原本を読みに行けます。

| 特性 | 説明 |
|---|---|
| **embedding / LLM 非依存** | 純粋な keyword + alias マッチング — オフライン即時、コストゼロ |
| **同義語ファジー検索** | 各 doc の frontmatter `aliases:` 欄により実現（中央辞書なし）|
| **静的スナップショット** | catalog は frozen output；更新には再実行が必要 |

## 2. 引数フォーマット (Args Schema)

| 引数 | 必須 | デフォルト | 説明 |
|---|:-:|---|---|
| `roots` | ❌ | `Docs;CardGame/Assets/UCL/UCL_Core/Docs~` | セミコロン/カンマ区切りのフォルダ一覧（**git-root 相対**） |
| `outputPath` | ❌ | `Docs/_catalog.md` | 出力ファイルパス（**git-root 相対**） |
| `format` | ❌ | `md` | `md`（人間用）または `json`（プログラム用）|
| `excludeDirs` | ❌ | `node_modules;.git;_Drafts` | 一致したらスキップ |
| `includeArchived` | ❌ | `false` | `archived: true` のドキュメントを含めるか |

> [!IMPORTANT]
> 多くの Cmd と異なり、本 Cmd の `outputPath` と `roots` は **git root** 相対（Unity project root ではない）。`Docs/` は通常 `CardGame/` の外にあるためです。

## 3. 解析される frontmatter フィールド

```yaml
---
title:           # 表示タイトル（無い場合はファイル名 / 最初の H1）
description:     # 一行要約
last_updated:    # YYYY-MM-DD
target_audience: # [Designer, AI_Agent, ...]
tags:            # [battle, status, ...]    — 統制語彙による分類
aliases:         # [アイテム, item, 道具]    — 自由形式の同義語によるファジー検索
archived:        # true なら catalog から除外
---
```

## 4. 出力形式

### 4.1 Markdown（既定）— top-dir でグルーピング

```markdown
## `Docs/Workflows` (25)

| Path | Title | Description | Tags | Aliases | Audience | Updated |
|---|---|---|---|---|---|---|
| [`Docs/Workflows/...`](...) | アイテム目録 | ... | item, catalog | アイテム, item, 道具 | Designer | 2026-05-04 |
```

### 4.2 JSON（`format=json`）

英語 / 繁体中文版の完全な構造を参照。

## 5. queue.json 呼び出し

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

## 6. ファジー検索 — Aliases

- **痛点**：「アイテム」で検索しても「道具システム」のドキュメントがヒットしない
- **解決法**：当該ドキュメントの frontmatter に `aliases: [アイテム, item]` を追加 → 次回 catalog 実行で Aliases 欄に含まれヒット

4 種類の推奨同義語：言語横断ペア / 同概念別名 / サブシステム別称 / 略語。

## 7. 制限事項

- 単行 scalar と行内 `[a, b]` list のみサポート（複数行 list は不可）
- aliases メンテナンスは作者の自律に依存
- catalog は静的スナップショット、新 alias 追加後は再実行が必要

## 8. 関連ドキュメント

- [DocsCatalog_Workflow](eov_docs:ja/Workflows/DocsCatalog_Workflow.md)
- [Cmd_ExportCommandCatalog](Cmd_ExportCommandCatalog.md)
- [UCL_AgentCommand_Architecture](UCL_AgentCommand_Architecture.md)

---

## 他言語

- 🇬🇧 [English](../../../en/API/UCL_AgentCommand/Cmd_ExportDocsCatalog.md)
- 🇯🇵 日本語（本ファイル）
- 🇨🇳 [简体中文](../../../zh-Hans/API/UCL_AgentCommand/Cmd_ExportDocsCatalog.md)
- 🇹🇼 [繁體中文](../../../zh-Hant/API/UCL_AgentCommand/Cmd_ExportDocsCatalog.md)
