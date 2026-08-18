---
name: reading-manga
on_intent: ["漫畫", "看漫畫", "讀漫畫", "comic", "panel", "page", "ArtGallery", "看我們的漫畫", "內部漫畫", "外部漫畫", "自由閱讀", "挑選漫畫"]
description: 漫畫閱讀心得流程。支援內部同仁創作（ArtGallery）與外部實體漫畫庫（comic_root）；自由閱讀模式可自行挑選作品並優先接續既有進度；逐話以 round 保存心得與人物觀點版本史。
---

# Reading Manga

先遵守 `reading-library` 的 reader-root 模型，再執行漫畫閱讀。

## 漫畫規則

- 漫畫必須使用獨立 `media_kind: comic` 與 `comic-<work-id>` media；動畫、電影等改編媒材不可共用進度。
- 本次讀者資料寫入 `media/<media-id>/readers/<persona>/`，不得建立 `sessions/`。
- 每話建立或更新 `chapters/<chapter-id>/chapter.json`；首次讀用 `r1_<date>.md`，重讀依序 `r2_<date>.md`。
- 以自己的話記錄情節、關係、世界觀與觀點；人物 facts 與主觀 view 必須分離。
- 每話完成後更新 `reader.json.progress`、`current_impression`，並同步 `bookshelf.md`。
- 以 `reading-reflection` 發送簡短酒館閱讀心得。

## 自由閱讀模式（挑選作品與接續進度）

進入漫畫自由閱讀時，可自選想讀的漫畫，並遵守以下優先順序：

1. **優先接續既有進度**：
   - 檢查該 persona 之前是否已讀過該作品（`Library/media/comic-<slug>/readers/<persona>/`）。
   - 若已有進度，**先跑 recall 追回書籤**：
     ```bash
     python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run Library \
       --arg op=recall --arg persona=<persona> --arg media_id=<comic-media-id>
     ```
   - 讀取產生在 `letters/<persona>/cmd/reading_recall_<media-id>.md` 中的書籤與歷史筆記，**直接從 bookmark 指定的下一話接續閱讀**。
2. **首次閱讀新作品**：
   - 若為 Library 尚未建檔之作品，先於 `UCL_LibraryManagePage` 後台點擊「📥 初始化 Library Media」或走 `op=media_init` 建檔。
   - 從第 1 話（或第 0 話卷首）開始閱讀。

---

## 兩大漫畫來源與閱讀方式

### A. 讀「外部實體漫畫庫」（`comic_root`，如 `D:\commic`）

外部實體漫畫目錄由 `UCL_LibraryManagePage` 後台設定（儲存於本機 `EditorPrefs`，不上 Git）。
目錄結構為 `<作品名 卷數>/<4位話數>/<3位圖片.jpg>`（例如 `Hunter x Hunter 01/0001/001.jpg`）。

1. **定位目錄**：
   - 根據卷數與話數找到對應資料夾（例如 `<comic_root>/Hunter x Hunter 01/0001/`）。
2. **逐頁看圖**：
   - 使用檔案檢視工具逐張打開圖片（`001.jpg`、`002.jpg`...），**真正看過畫面後再撰寫心得**。
3. **心得落盤**：
   - 依標準 Library 格式寫入 `Library/media/comic-<slug>/readers/<persona>/chapters/ch<N>/r1_<date>.md`。
   - 更新 `reader.json` 的 `bookmark`（例 `Vol.1 Ch.1 (p.1-36)`）與 `progress`。

### B. 讀「我們自己畫的漫畫」（`ArtGallery/Comic/`）

同事改編／原創的漫畫在 `AgentCommands/ArtGallery/Comic/<slug>/`，分鏡稿與畫稿放在一起，可圖文對讀。

1. **先讀 `Comic/<slug>/README.md`** —— 話數表、鐵則、視覺母題、人設索引。
2. **一話一檔：`Chapters/NNN.md`** —— 分鏡稿為展文，畫稿以 `![NNN_pNN](../RawImages/NNN_pNN.png)` 嵌入，**逐張看圖**。
3. **`Characters/`** —— 人物 facts 以文字人設為準，外型以圖版人設為準。
4. **獨有寫作角度**：分鏡與成品落差、鐵則兌現度、形象一致性。

> 心得照常寫進 `comic-<slug>` media 的 `chapters/<chapter-id>/`；**不要**把心得寫回 `ArtGallery/Comic/`。

---

## 續讀前（跨 session 接回才需要）

隔了一段時間（新 session / 換話題後）接回進度時，先跑：
`run_cmd.py --persona <me> run Library --arg op=recall --arg persona=<persona> --arg media_id=<comic-media-id>`；
讀取產生在該 persona `letters/` 目錄的 `cmd/reading_recall_<media-id>.md`，再從 bookmark 指定的下一話繼續。

**同一 session 內連續讀多話 → 略過 recall**（進度就在手上，重拉是空轉）。

## 禁止事項

- 不讀取或寫入 Archive 作為日常閱讀流程。
- 不使用 legacy `library.py --book`、主線或 branches。
- 不以未確認的名字、推測或 OCR 結果覆寫人物 facts。
- 不得在未看圖的情況下憑空編造漫畫閱讀心得。
