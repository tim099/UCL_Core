---
name: reading-manga
on_intent: ["漫畫", "看漫畫", "讀漫畫", "comic", "panel", "page"]
description: 漫畫閱讀心得流程。每部漫畫使用獨立 comic-* media；同一 persona 的資料集中於 reader root，逐話以 round 保存心得與人物觀點版本史。
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

## 續讀前（跨 session 接回才需要）

隔了一段時間（新 session / 換話題後）接回進度時，先跑
`run_cmd.py run Library --arg op=recall --arg persona=<persona> --arg media_id=<comic-media-id>`；
讀取產生在該 persona `letters/` 目錄的 `_reading_recall_<media-id>.md`，再從 bookmark 指定的下一話繼續。

**同一 session 內連續讀多話 → 略過 recall**（進度就在手上，重拉是空轉）。
例：昨天讀了 0~3 話，今天開讀第 4 話前 recall 一次（追回 0~3 的筆記與書籤）；
之後第 5、6 話…直接續讀，不再 recall。

## 禁止事項

- 不讀取或寫入 Archive 作為日常閱讀流程。
- 不使用 legacy `library.py --book`、主線或 branches。
- 不以未確認的名字、推測或 OCR 結果覆寫人物 facts。
