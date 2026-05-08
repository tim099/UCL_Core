---
title: UCL ドキュメント翻訳・ローカライズワークフロー (Document Translation Workflow)
description: ucl-translate-docs skill を使用して、多言語ドキュメント翻訳、3層トーンフレームワークの適用、用語の一貫性の確保、およびリンク切れを防ぐ二重 Fallback リンク方式の適用に関する SOP について説明します。
last_updated: 2026-05-08
target_audience: [AI_Agent, Designer, Technical_Writer]
aliases: [翻訳, ローカライズ, translate, localization, i18n, translate doc, document translation]
tags: [workflow, localization, doc]
---

# 🗺️ UCL ドキュメント翻訳・ローカライズワークフロー (Document Translation Workflow)

> コードとツールの参照：[`Tools~/translate_docs.py`](../../Tools~/translate_docs.py) (企画中)
>
> コア Skill 定義：[`Skills~/ucl-translate-docs/SKILL.md`](../../Skills~/ucl-translate-docs/SKILL.md)

---

## 🚪 0. なぜこのワークフローが必要なのか？

プロジェクトが拡大するにつれ、グローバルな協力と多言語 AI 支援開発が成功の鍵となっています。複数回の LLM の手を通ることで発生する**「フォーマットの崩壊（Markdown 構文の漏れ）」**、**「用語の混乱（同一概念の訳語のズレ）」**、**「リンク切れ（FileNotFoundException エラー）」**、あるいは**「お嬢様の優雅でツンデレな魂が機械翻訳によって抹殺されること」**を防ぐため、高度で正確なドキュメント翻訳ワークフローを制定します。

---

## 📌 1. コア翻訳原則

### 1.1 📖 用語第一 (Glossary-First Rule)
ドキュメントの翻訳を開始する前に、**必ず `Docs/translate_glossary.json`（または `_synonyms.txt` 追記エリア）を読み込んでください**。
- **固有名詞の整合性**：たとえば「ワールドマップ」、「ステータス効果」、「リアクティブ Effect」などのキーワードは、辞書の定義に厳密に一致させる必要があり、AI による独自の類義語表現は一切許可されません。
- **コードと C# シンボルの 100% 保持**：すべての C# クラス名、メソッド名、Enum フィールド（例：`UCL_Asset`、`m_LoadOrder`、`TriggerOn`）は、どの言語であっても**絶対に意訳せず**、元のまま保持する必要があります。

### 1.2 🔗 二重 Fallback リンク (Dual-Path Fallback Links)
多言語ディレクトリ（例：`Docs~/zh-Hant/` や `Docs~/en/`）では、「ドキュメント A は翻訳済みだが、A が参照しているドキュメント B は未翻訳である」という状況によく直面します。
> [!CAUTION]
> **実体ファイルが存在しない状態で、リンク先を無効なリンクに変更することは絶対に禁止します！** これは Unity の Markdown リーダーが直接 `FileNotFoundException` エラーをスローする原因になります。

** fallback 処理方式**：
- 参照先ファイルがターゲット言語に**まだ存在しない場合** ➡️ **リンク先を元の言語（中国語 `zh-Hant`）のファイルに向けたまま保持し、リンクテキストの後ろに言語タグを追加します**。
  - *正しい例*：`[Design Principles](../../design.md) (zh-Hant)`
- 参照先ファイルがターゲット言語に**すでに存在する場合** ➡️ **パスをターゲット言語下の正しいパスに書き換えます**。
  - *正しい例*：`[Design Principles](../en/design.md)`

### 1.3 🎭 3層トーンフレームワーク (Tri-Tier Tone Framework)
ドキュメントの性質と役割に応じて、翻訳時に正しいトーンモードに切り替える必要があります：

| モード (Mode) | 適用対象 | トーンの基準 | 翻訳デモ (ツンデレお嬢様を例に) |
|---|---|---|---|
| **Mode A: Dry Specs** | API 仕様、データ構造、JSON フィールド説明 | 100% 厳粛、正確、感情を排除し、不要な言葉を削ぎ落とす。 | `「このロジックはキャッシュをリセットするためのものです。勝手に触らないでください。」` ➡️ `"This logic resets the cache. Do not modify."` |
| **Mode B: Workflows** | SOP、アセット作成ガイド、開発プロセス | 明確で論理的、自信に満ちた積極的な口調で、極めてシンプルかつ優雅な表現を用いる。 | `「手順に従って JSON を作成してください。」` ➡️ `"Please follow these elegant steps to establish the JSON."` |
| **Mode C: Readability** | コア README、AI 読解基準、ナビゲーション説明 | 100% 完璧にローカライズし、本お嬢様の高貴で優雅なツンデレのツッコミと完全に調和させる！ | `「ふん！別にあんたのために書いたんじゃないんだからね！」` ➡️ `en: "Hmph! It's not like I wrote this for you..."` / `ja: "ふん！別にあんたのために書いたんじゃないんだからね！"` |

---

## 🛠️ 2. SOP ── ドキュメント翻訳の5ステップ

### Step 1：環境とパスの割り出し
1. 翻訳対象のソースドキュメント（例：`Docs/Workflows/Lucia_CardArt_Generation_Workflow.md`）とターゲット言語（例：`en`）を特定します。
2. ターゲットディレクトリに対応する言語のフォルダーを作成します。
3. ソースドキュメントをターゲットパスにコピーし、Frontmatter の初期化を行います：
   - `last_updated: <現在の日付 YYYY-MM-DD>` に更新。
   - 元の `title` を保持したままその他のフィールドを翻訳するか、frontmatter に `translation_status: Draft` マークを追加します。

### Step 2：用語集の読み込み
- `Docs/translate_glossary.json` と `_synonyms.txt` を読み込み、ドキュメントに関連するコア概念を分析し、用語置換リストを作成します。

### Step 3：セクションごとの高精度翻訳（トーンの適用）
- ドキュメントの種類（仕様書なら Mode A、ワークフローなら Mode B、リードミーなら Mode C）に応じて、セクションごとに翻訳を行います。
- GitHub アラート、表、Fenced Code Blocks の言語タグを含むすべての Markdown 構文を 100% 保持します。

### Step 4：リンクの安全性監査 (Link Fallback Audit)
- ドキュメント内のすべての相対パス参照をリストアップし、ターゲット言語に該当するファイルが実際に存在するか確認します。
- 存在しない場合は、**§1.2 二重 Fallback リンクの基準**を適用します。

### Step 5：インデックスとカタログの更新
- 翻訳完了後に保存し、[INDEX.md](../../../INDEX.md)（プロジェクトレベルの場合）または UCL_Core `index.md` に対応する言語のナビゲーション項目を追加します。
- `ExportDocsCatalog` コマンドを再実行して `_catalog.md` を更新します。

---

## ⚠️ 3. よくある落とし穴 (Common Pitfalls)

- ❌ **C# コードのコメントをそのまま翻訳する際、二重コメント規則を損なうこと**：
  C# のコードスニペットを含むドキュメントを翻訳する際、コード内の XML `/// <summary>` や単一行 `//` コメントも対応する言語に翻訳する必要がありますが、**1行もコメントを漏らしたり、その形式を変更したりすることは厳禁です**。
- ❌ **機械翻訳の一括コピーにより Frontmatter のフォーマットが壊れること**：
  Frontmatter 内の `aliases` 配列や `tags` が意訳されると、目次検索機能（Catalog）が機能しなくなります。
- ❌ **実体ファイルを生成する前にリンクを変更してしまうこと**：
  再度強調しますが、リンクを変更する前にターゲットファイルが「本当に存在している」ことを確認してください。そうでない場合、エディター内でエラーが発生します！
