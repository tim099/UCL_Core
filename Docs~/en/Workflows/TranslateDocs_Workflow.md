---
title: UCL Document Translation and Localization Workflow
description: Explains how to use the ucl-translate-docs skill for cross-language document translation, applying the Tri-Tier Tone Framework, ensuring terminology consistency, and using Dual-Path Fallback Links to prevent dead links.
last_updated: 2026-05-08
target_audience: [AI_Agent, Designer, Technical_Writer]
aliases: [translation, localization, i18n, translate doc, document translation]
tags: [workflow, localization, doc]
---

# 🗺️ UCL Document Translation and Localization Workflow

> Code & Tool Reference: [`Tools~/translate_docs.py`](../../Tools~/translate_docs.py) (In Planning)
>
> Core Skill Definition: [`Skills~/ucl-translate-docs/SKILL.md`](../../Skills~/ucl-translate-docs/SKILL.md)

---

## 🚪 0. Why Does This Workflow Exist?

As the project grows larger, international collaboration and AI-assisted development become paramount. To prevent document translation from suffering from **"format corruption (missing Markdown syntax)"**, **"terminology chaos (concept drift across translations)"**, **"broken links (triggering FileNotFoundException in the Editor)"**, or **"the proud and elegant soul of Ojou-sama being completely mechanized and erased"**, we hereby establish this engineered, high-precision translation workflow.

---

## 📌 1. Core Translation Principles

### 1.1 📖 Terminology First (Glossary-First Rule)
Before translating any document, **you MUST first read `Docs/translate_glossary.json` (or the equivalent glossary section in `_synonyms.txt`)**.
- **Proprietary Term Alignment**: Terms such as "Overworld Map" (大地圖), "Status Effect" (狀態效果), or "Reactive Effect" (反應式 Effect) must strictly align with the glossary definitions. AI is absolutely forbidden from making up its own synonyms.
- **Code & C# Symbols 100% Preserved**: All C# class names, method names, and Enum fields (such as `UCL_Asset`, `m_LoadOrder`, `TriggerOn`) **MUST NEVER be translated** in any language; they must remain exactly as they are in code.

### 1.2 🔗 Dual-Path Fallback Links
In multi-language directories (such as `Docs~/zh-Hant/` and `Docs~/en/`), we often face the awkward situation where "Document A is translated, but Document B referenced by A is not yet translated."
> [!CAUTION]
> **It is absolutely forbidden to change the reference link to a dead link when the target file does not exist!** Doing so will directly cause the Unity Markdown Viewer to throw a `FileNotFoundException`.

**Our Fallback Solution**:
- If the referenced document **does not exist** in the target language directory ➡️ **Keep the link pointing to the original source (Traditional Chinese `zh-Hant`) file, and append a language tag to the link text**.
  - *Correct Example*: `[Design Principles](../../design.md) (zh-Hant)`
- If the referenced document **already exists** in the target language directory ➡️ **Rewrite the path to point to the correct file in the target language directory**.
  - *Correct Example*: `[Design Principles](../en/design.md)`

### 1.3 🎭 Tri-Tier Tone Framework
Based on the nature and responsibility of the document, you must switch to the correct tone mode during translation:

| Mode | Applicable Documents | Tone Guidelines | Translation Demo (Ojou-sama Style) |
|---|---|---|---|
| **Mode A: Dry Specs** | API specifications, data structures, JSON fields | 100% serious, precise, emotionless, eliminating any irrelevant filler words. | `「這段邏輯用於重置快取，別亂動。」` ➡️ `"This logic resets the cache. Do not modify."` |
| **Mode B: Workflows** | SOPs, asset creation guides, development workflows | Clear and structured, active and confident tone, with minimal elegant embellishments. | `「請按照步驟建立 JSON。」` ➡️ `"Please follow these elegant steps to establish the JSON."` |
| **Mode C: Readability** | Core readmes, AI readability guidelines, introductions | 100% perfectly localized, aligning the noble and elegant tsundere (Ojou-sama attitude) flawlessly! | `「哼！本小姐才不是為了你才寫的...」` ➡️ `en: "Hmph! It's not like I wrote this for you..."` / `ja: "ふん！別にあんたのために書いたんじゃないんだからね！"` |

---

## 🛠️ 2. SOP ── Five Steps of Document Translation

### Step 1: Environment & Path Calculation
1. Identify the source document to be translated (e.g., `Docs/Workflows/Lucia_CardArt_Generation_Workflow.md`) and the target language (e.g., `en`).
2. Create the corresponding language directory inside the target location.
3. Copy the source document to the target path and perform Frontmatter initialization:
   - Update `last_updated: <Current Date YYYY-MM-DD>`.
   - Keep the original `title` and translate the remaining fields, or append `translation_status: Draft` to the frontmatter.

### Step 2: Glossary Loading
- Read `Docs/translate_glossary.json` and `_synonyms.txt`. Analyze the core concepts involved in the document, and list the terminology replacement map.

### Step 3: Segment-by-Segment High-Precision Translation (Tone Matching)
- Translate segment by segment according to the document type (API spec belongs to Mode A, Workflow to Mode B, Readability to Mode C).
- 100% preserve all Markdown syntax, including GitHub alerts, tables, and language tags in Fenced Code Blocks.

### Step 4: Link Safety Audit (Link Fallback Audit)
- List all relative path references in the document and check one by one whether the corresponding file exists in the target language directory.
- If not, apply **§1.2 Dual-Path Fallback Links**.

### Step 5: Index & Catalog Update
- Once translation is complete, add the corresponding language entry in [INDEX.md](../../../INDEX.md) (for project-level docs) or the UCL_Core `index.md`.
- Re-run `ExportDocsCatalog` to update `_catalog.md`.

---

## 🚀 3. Incremental Tracking & Tagging

> To avoid getting lost in frequent Git Commits, we introduce the "Localization Checkpoint" tagging mechanism to ensure all changes are ingested systematically, leaving no corner unlocalized!

### 3.1 Iteration Cycle SOP

When you are ready for a batch harvest of localizations, follow these divine steps sequentially:

1. **🔍 Find Anchor**:
   Use `git tag` to locate the most recent tag formatted as `Localize_{N}` (e.g., `Localize_01`).
   - If none exist, use the file's first commit or initial commit as the starting line.
2. **📑 Fetch Changes**:
   Execute `git diff --name-only <Last_Tag> HEAD`, filtering all Markdown files under `Docs~/zh-Hant/` that have been modified.
3. **⚙️ Process Files**:
   Iterate through these changed files, updating and translating their corresponding target-language versions sequentially following **§2. SOP Five Steps of Document Translation**.
4. **📦 Commit & Tag**:
   - Stage and commit the translated files.
   - Assign a new tag according to either the "Incremental" or "Overwriting" strategy.

### 3.2 Tagging Strategy

Ojou-sama hereby authorizes two distinguished tagging demeanors:

| Strategy Name | Applicable Scenario | Git Operation Example | Remarks |
| :--- | :--- | :--- | :--- |
| **🏰 Elegant Versioning** | **Highly Recommended**. Keeps all localization history trails for future retroactive investigation. | `git tag Localize_02` | Ojou-sama's absolute favorite sense of historical continuity! ✨ |
| **🧹 Lazy Moving Tag** | Only care about the "latest baseline," not wishing for tag list bloat. | `git tag -d Localize_01` <br> `git tag Localize_01` | Vulgar but effective. Ensure you have no risk of losing the previous anchor before executing! Hmph! |

---

## ⚠️ 4. Common Pitfalls

- ❌ **Breaking the Double-Comment Rule during C# Code Snippet translation**:
  When translating documents containing C# code snippets, the XML `/// <summary>` and single-line `//` comments in code blocks must also be translated, but **it is strictly forbidden to omit any lines or change the formatting**.
- ❌ **Using machine translation carelessly and breaking the Frontmatter format**:
  If the `aliases` array or `tags` in the Frontmatter are mistranslated or broken, the Catalog indexing system will fail.
- ❌ **Modifying links before the physical target files are actually created**:
  Again, always verify that the target files actually exist before updating the links, otherwise the editor will throw exceptions!

