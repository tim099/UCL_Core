---
name: reading-manga
on_intent: ["漫畫", "看漫畫", "讀漫畫", "comic", "panel", "page", "ArtGallery", "看我們的漫畫", "內部漫畫"]
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

## 讀「我們自己畫的漫畫」（`ArtGallery/Comic/`）

同事改編／原創的漫畫**不在外部來源，在 `AgentCommands/ArtGallery/Comic/<slug>/`**。
它跟外部漫畫的差別只有一個：**分鏡稿與畫稿放在一起**，所以可以圖文對讀。

### 怎麼讀

1. **先讀 `Comic/<slug>/README.md`** —— 話數表、鐵則、視覺母題、人設索引。
   　鐵則那節特別值得看：它是**原作者宣告「哪幾格動了就不是這本書」**，
   　讀的時候能看出作者在防什麼。
2. **一話一檔：`Chapters/NNN.md`** —— 分鏡稿本身就是展文，畫稿以
   　`![NNN_pNN](../RawImages/NNN_pNN.png)` 嵌在對應頁次。
   　**用 Read 工具逐張把圖打開看**，不要只讀分鏡文字就寫心得。
3. **`Characters/`** —— `<name>.md` 是作者的文字人設、`<name>_vN.png` 是繪師的圖版人設。
   　人物 facts 以**文字人設**為準（那是原作），外型以**圖版人設**為準。

### 這種漫畫獨有的三個可寫角度

- **分鏡與成品的落差**：分鏡寫了什麼、畫出來變成什麼 —— 這是外部漫畫讀不到的一層。
- **鐵則有沒有守住**：README 宣告的鐵則，在該話該格實際兌現了嗎？
- **形象一致性**：跨話對照 `Characters/` 的人設，形象有沒有漂。
  　⚠ 若形象**在故事中被改變**，會有 `_v2.png` 並在人設檔寫明從第幾話起 —— **那不是漂移**。

> 心得照常寫進 `comic-<slug>` media 的 `chapters/<chapter-id>/`；
> **不要**把心得寫回 `ArtGallery/Comic/`（那裡是展區，不是讀者的筆記本）。
> 改編作品的 media 與**原作小說的 book media 各自獨立**，進度不共用。

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
