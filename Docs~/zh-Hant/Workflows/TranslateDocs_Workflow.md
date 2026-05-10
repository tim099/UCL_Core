---
title: UCL 文件翻譯與本地化工作流程 (Document Translation Workflow)
description: 說明如何使用 ucl-translate-docs skill 進行跨語系文件翻譯、套用三層語氣架構、保證術語一致性、以及採用雙軌 Fallback 連結防止死連結的 SOP
last_updated: 2026-05-08
target_audience: [AI_Agent, Designer, Technical_Writer]
aliases: [翻譯, 本地化, translate, localization, i18n, translate doc, document translation]
tags: [workflow, localization, doc]
---

# 🗺️ UCL 文件翻譯與本地化工作流程 (Document Translation Workflow)

> 程式碼與工具參考：[`Tools~/translate_docs.py`](../../Tools~/translate_docs.py) (規劃中)
>
> 核心 Skill 定義：[`Skills~/ucl-translate-docs/SKILL.md`](../../Skills~/ucl-translate-docs/SKILL.md)

---

## 🚪 0. 為什麼有這份工作流？

隨著專案日益壯大，跨國協作與多語系 AI 輔助開發成為核心關鍵。為了防止文件翻譯在多個 LLM 轉手過程中出現**「格式崩潰（Markdown 語法遺漏）」**、**「術語混亂（同一概念譯名漂移）」**、**「連結失效（FileNotFoundException 報錯）」**，或**「大小姐優雅傲嬌的靈魂被機械化翻譯給抹殺」**，我們特此制定這套工程化、高精度的翻譯工作流。

---

## 📌 1. 核心翻譯原則

### 1.1 📖 術語第一 (Glossary-First Rule)
在開始翻譯任何文件前，**必須先讀取 `Docs/translate_glossary.json`（或 `_synonyms.txt` 增補區）**。
- **專利名詞對齊**：諸如「大地圖」、「狀態效果」、「反應式 Effect」等詞彙，必須嚴格對齊術語字典定義，不允許任何 AI 自行發揮的同義詞。
- **代碼與 C# 符號 100% 保持**：所有 C# 類別名、方法名、Enum 欄位（例如 `UCL_Asset`、`m_LoadOrder`、`TriggerOn`）在任何語系中都**絕對不能意譯**，必須保持原樣。

### 1.2 🔗 雙軌 Fallback 連結 (Dual-Path Fallback Links)
在多語系目錄（如 `Docs~/zh-Hant/` 與 `Docs~/en/`）中，經常面臨「A 文件已翻譯，但 A 文件引用的 B 文件尚未翻譯」的尷尬情況。
> [!CAUTION]
> **絕對禁止在實體檔案不存在時，將連結改成死連結！** 這會直接導致 Unity 的 Markdown 閱讀器拋出 `FileNotFoundException` 錯誤。

** fallback 處理方案**：
- 如果被引用文件在目標語系中**尚不存在** ➡️ **保持連結指向原語系（中文 `zh-Hant`）檔案，並在連結文字後方追加語系標記**。
  - *正確範例*：`[Design Principles](../../design.md) (zh-Hant)`
- 如果被引用文件在目標語系中**已存在** ➡️ **改寫路徑至目標語系下的正確路徑**。
  - *正確範例*：`[Design Principles](../en/design.md)`

### 1.3 🎭 三層語氣架構 (Tri-Tier Tone Framework)
依照文檔的本質與職責，翻譯時必須切換至正確的語氣模式：

| 模式 (Mode) | 適用文檔 | 語氣規範 | 翻譯示範 (以傲嬌大小姐為例) |
|---|---|---|---|
| **Mode A: Dry Specs** | API 規格、資料結構、JSON 欄位說明 | 100% 嚴肅、精準、去情緒化、剔除任何無關贅詞。 | `「這段邏輯用於重置快取，別亂動。」` ➡️ `"This logic resets the cache. Do not modify."` |
| **Mode B: Workflows** | SOP、建立資產指南、開發流程 | 保持清晰有條理，語氣積極自信，帶有極簡高雅的修飾。 | `「請按照步驟建立 JSON。」` ➡️ `"Please follow these elegant steps to establish the JSON."` |
| **Mode C: Readability** | 核心讀我、AI 閱讀規範、導覽說明 | 100% 完美本地化，將本小姐高貴優雅的傲嬌吐嘈完美對齊！ | `「哼！本小姐才不是為了你才寫的...」` ➡️ `en: "Hmph! It's not like I wrote this for you..."` / `ja: "ふん！別にあんたのために書いたんじゃないんだからね！"` |

---

## 🛠️ 2. SOP ── 文件翻譯五步走

### Step 1：環境與路徑推算
1. 確定要翻譯的源文件（如 `Docs/Workflows/Lucia_CardArt_Generation_Workflow.md`）與目標語系（如 `en`）。
2. 在目標目錄建立對應語系的資料夾。
3. 複製源文件至目標路徑，並進行 Frontmatter 初始化：
   - 更新 `last_updated: <當前日期 YYYY-MM-DD>`。
   - 保留原 `title` 並翻譯其餘欄位，或在 frontmatter 追加 `translation_status: Draft` 標記。

### Step 2：術語庫載入
- 讀取 `Docs/translate_glossary.json` 與 `_synonyms.txt`，分析該文檔中涉及的核心概念，列出術語替換清單。

### Step 3：分段高精度翻譯 (與語氣匹配)
- 根據文件類型（API 規格屬 Mode A、Workflow 屬 Mode B、Readability 屬 Mode C）進行分段翻譯。
- 100% 保持所有 Markdown 語法，包括 GitHub alerts、表格、Fenced Code Blocks 的語言標籤。

### Step 4：連結安全檢測 (Link Fallback Audit)
- 列出文件中所有相對路徑引用，逐一檢查目標語系下對應路徑的檔案是否存在。
- 若不存在，套用 **§1.2 雙軌 Fallback 連結規範**。

### Step 5：索引與 Catalog 回填
- 翻譯完成並存檔後，在 [INDEX.md](../../../INDEX.md)（若是專案層文檔）或 UCL_Core `index.md` 加上對應語系的導航條目。
- 重新跑 `ExportDocsCatalog` 指令更新 `_catalog.md`。

---

## 🚀 3. 增量追蹤與標籤迭代 (Incremental Tracking & Tagging)

> 為了避免在頻繁的 Git Commits 中迷失，我們引入「Localization Checkpoint」標籤機制，確保所有變更都能被有序地消化，不留任何未翻譯的死角！

### 3.1 迭代循環 SOP

當妳準備進行批量本地化收割時，請依序執行以下神聖的步驟：

1. **🔍 回溯錨點 (Find Anchor)**：
   使用 `git tag` 尋找上一筆格式為 `Localize_{N}` 的標籤（例如 `Localize_01`）。
   - 若不存在，則以該文件的首次 Commit 或 Initial Commit 為起點。
2. **📑 抓取變更 (Fetch Changes)**：
   執行 `git diff --name-only <Last_Tag> HEAD`，篩選出位於 `Docs~/zh-Hant/` 目錄下且被修改過的所有 Markdown 文件。
3. **⚙️ 執行翻譯 (Process Files)**：
   針對這些變更文件，逐一按照 **§2. SOP 文件翻譯五步走** 完成對應語系的更新與翻譯。
4. **📦 封存提交 (Commit & Tag)**：
   - 先執行翻譯文件的 Stage 與 Commit。
   - 依照「增量編號」或「標籤覆寫」策略打上新標籤。

### 3.2 標籤策略 (Tagging Strategy)

本小姐特此批准兩種標籤處理風範：

| 策略名稱 | 適用場景 | Git 操作範例 | 備註 |
| :--- | :--- | :--- | :--- |
| **🏰 高雅編號派 (Versioning)** | **推薦使用**。保留所有本地化歷史軌跡，便於事後回溯追蹤。 | `git tag Localize_02` | 本小姐最欣賞的歷史延續感！✨ |
| **🧹 懶惰覆寫派 (Moving Tag)** | 僅在乎「當前最新進度」，不想讓 Tag 列表膨脹。 | `git tag -d Localize_01` <br> `git tag Localize_01` | 粗俗但有效。執行前務必確認妳沒有弄丟錨點的疑慮！哼！ |

---

## ⚠️ 4. 常見地雷 (Common Pitfalls)

- ❌ **直接翻譯 C# 程式碼註解時破壞雙重註解鐵律**：
  在翻譯帶有 C# 程式碼片段的文件時，程式碼內的 XML `/// <summary>` 與單行 `//` 註解也必須同步翻譯成對應語系，但**嚴禁遺漏任何一行的註解或改變其格式**。
- ❌ **用機器翻譯一鍵複製導致 Frontmatter 格式壞掉**：
  Frontmatter 內的 `aliases` 陣列或 `tags` 如果被意譯，會直接導致目錄檢索功能（Catalog）失效。
- ❌ **產生實體檔案前先改了連結**：
  再次強調，改連結前一定要確認該目標檔案「真的存在」，否則編輯器內會報錯！

