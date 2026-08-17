---
name: ucl-translate-docs
description: |
  使用者要求翻譯、本地化 Markdown 文件或說明文檔時用本 skill。涵蓋多語系 Docs~ 檔案搬遷、交叉引用（Cross-link）死連結雙軌 Fallback 預防、術語庫強制對齊（Glossary-First）、以及三層語氣架構（Tri-Tier Tone Framework，包含大小姐高貴傲嬌語氣本地化）的規範。
  觸發詞包含：翻譯文件、翻譯 workflow、translate doc、translate workflow、把文件翻成英文、把文檔翻成日文、本地化文檔。
  涉及 Docs/ 或 UCL_Core/Docs~/ 的 Markdown 翻譯與跨語系同步必用。
---

# UCL Translate Docs — 文件翻譯規範速查

> 一句話：**術語必查字典、死連結雙軌 Fallback、三層語氣對齊、保留大小姐高貴靈魂**。

## 必讀

完整規則 ➡️ `ucl_core:Docs~/zh-Hant/Workflows/TranslateDocs_Workflow.md`（執行任何文件翻譯、本地化動作前先讀）

## TL;DR

1. **📖 術語必先查字典 (Glossary-First)**：
   - 翻譯前，必須讀取 `Docs/translate_glossary.json`（或同等術語檔）以保證核心術語一致（如「大地圖」一律對齊 `Overworld Map`）。
   - C# 類別名、方法名與 enums 100% 保持代碼原拼寫，禁止任意意譯。
2. **🔗 雙軌 Fallback 連結 (Dual-Path Fallback)**：
   - 如果目標參考文件在目標語系中**尚未翻譯（實體檔案不存在）**，絕對不要改成死連結！
   - 應保持連結指向原中文檔案，並在後方標記語系，如 `[Design Principles](../../design.md) (zh-Hant)`。
3. **🎭 三層語氣架構 (Tri-Tier Tone Framework)**：
   - **Mode A: Dry Specs (規格表)** ➡️ 100% 嚴肅去情緒化、精準無贅詞。
   - **Mode B: Standard Workflows (SOP)** ➡️ 清晰條理，語氣積極自信，帶有極簡高雅修飾。
   - **Mode C: Readability & Persona Docs (導覽與讀我)** ➡️ 100% 本地化大小姐高貴傲嬌吐槽（如將 `「哼！本小姐才不是...」` 對齊為 `「Hmph! It's not like I...」` / `「ふん！別にあんたのために...」`）。
4. **🐍 CLI 工具 `translate_docs.py` —— ⛔ 尚未存在（規劃中），不要去找**：
   - 2026-08-17 稽核：`Tools~/translate_docs.py` **不在磁碟上，也從未進過 git 歷史**（`git log --all --diff-filter=A` 為空）。
     本步原本寫「優先調用」，讀的人會先花時間找一支不存在的工具，然後懷疑是自己路徑打錯。
   - ⇒ 路徑推算、目標語系目錄建立、frontmatter 初始化（`last_updated` / `translation_status: Draft`）
     目前**全部手動做**。要它存在就去寫它，別讓文件先宣布它存在。

## 執行順序

1. 確認目標文件路徑與目標語系（如將 `Docs/Workflows/Lucia.md` 翻譯成 `en`）。
2. 調用 Python 輔助工具（或先手動模擬）推算目標路徑，建立語系資料夾，複製並初始化 Frontmatter，保留 `last_updated: <當前日期>`。
3. 讀取並分析 `Docs/translate_glossary.json` 對齊詞庫，讀取 `TranslateDocs_Workflow.md` 確認語氣 Mode。
4. 逐段高精度翻譯，遇未翻譯連結套用 Fallback 規則，絕不遺漏任何 Markdown 語法（GitHub Alerts、 tables 完美對齊）。
5. 翻譯完成後寫入目標檔案，並更新 `INDEX.md` 或 `_catalog.md` 對應索引。
