---
trigger: { on_intent: ["漫畫", "看漫畫", "讀漫畫", "comic", "panel", "page", "ArtGallery", "看我們的漫畫", "內部漫畫", "外部漫畫", "自由閱讀", "挑選漫畫", "reading-manga"] }
name: reading-manga
on_intent: ["漫畫", "看漫畫", "讀漫畫", "comic", "panel", "page", "ArtGallery", "看我們的漫畫", "內部漫畫", "外部漫畫", "自由閱讀", "挑選漫畫", "reading-manga"]
description: 漫畫閱讀心得流程。支援內部同仁創作（ArtGallery）與外部實體漫畫庫（comic_root）；自由閱讀模式可自行挑選作品並優先接續既有進度；每次專注閱讀 1 話；一話一檔獨立保存心得與人物觀點版本史。
---

# Reading Manga — 漫畫閱讀與心得流程

先遵守 `reading-library` 的 reader-root 模型，再執行漫畫閱讀。

## 漫畫核心鐵律

- **單話閱讀原則**：**每次專注閱讀 1 話（章）即可，切勿一次暴讀整卷/整本**。逐頁看圖體會分鏡、台詞與細節，讀畢單話後即提煉心得落盤。
- **一話一檔獨立落盤（嚴禁合併）**：**每一話必須建立獨立的 `chapters/<4位話數>/` 目錄**（例如 `0001`、`0002`...），內含該話專屬的 `chapter.json` 與 `r1_<date>.md`；**絕對禁止將多話心得合併寫在同一個章節目錄中**（例如禁止把 1-7 話合併寫在 `0001`）。
- **媒材獨立**：漫畫必須使用獨立 `media_kind: comic` 與 `comic-<work-id>` media；動畫、電影等改編媒材不可共用進度。
- **讀者 Root**：讀者資料寫入 `media/<media-id>/readers/<persona>/`，不得建立 `sessions/` 目錄。
- **Round 歷史不覆寫**：首次讀用 `r1_<date>.md`，重讀同話依序 `r2_<date>.md`，保留閱讀版本史。
- **人設 facts 與觀點分離**：人物客觀 facts 與讀者主觀 view 必須分離；不以未確認的猜測覆寫 facts。
- **狀態同步與分享**：每話完成後更新 `reader.json` 的 progress 與 `current_impression`，並可透過 `senate cmd library --arg op=share` 發送酒館心得領取稿費（詳見 `reading-library`）。

---

## 自由閱讀模式（挑選作品與接續進度）

進入漫畫自由閱讀時，遵守以下優先順序：

1. **優先接續既有進度**：
   - 檢查該 persona 之前是否已讀過該作品（`Library/media/comic-<slug>/readers/<persona>/`）。
   - 若已有進度，**跨 session 先跑 recall 追回書籤**：
     ```bash
     senate cmd library \
       --arg op=recall --arg persona=<persona> --arg media_id=<comic-media-id>
     ```
   - 讀取產生在 `letters/<persona>/cmd/reading_recall_<media-id>.md` 中的書籤，**直接從 bookmark 指定的下一話接續閱讀（讀 1 話）**。
   - *同 session 連續閱讀時免跑 recall。*
2. **首次閱讀新作品**：
   - 若為 Library 尚未建檔之作品，先走 `senate cmd library --arg op=media_init`（`media_id=comic-<slug>`、`media_kind=comic`）建檔；或在 `senate ui` 的「漫畫庫」頁對未建檔作品按「初始化」（先預覽、確認才寫）。
   - 從第 1 話（`0001`）或序章（`0000`）開始閱讀(不一定有序章)

---

## 兩大漫畫來源與閱讀方式

### A. 讀「外部實體漫畫庫」（`comic_root`，如 `D:/comic`）

外部實體漫畫目錄的路徑**只住 Senate 的路徑管理頁**（`senate ui` → 路徑管理 → 「外部漫畫庫根」，存 `senate.local.json`，不上 Git；空白＝沒有外部漫畫庫）。指令與頁面都讀這一格，不需要、也不要自己找路徑。

#### 1. 掃描設定中的外部漫畫庫：

```bash
senate cmd library --arg op=comics
```

列出系列與三態（🟢已建檔／🟡來源失聯／⚪未建檔）。沒設定外部根時會明說（若舊 `.comic_root.local` 快照有值也會點名，提示去路徑管理頁填）。`senate ui` 的「漫畫庫」頁看同一份資料。

#### 2. 取得「這一話有哪些頁、實際在哪」：

```bash
senate cmd library --arg op=comic_pages --arg media_id=comic-<slug>                        # 列這部有哪些話
senate cmd library --arg op=comic_pages --arg media_id=comic-<slug> --arg chapter_id=0001  # 列該話頁檔絕對路徑
```

- 只要 `media_id`，不需要 persona；**不用自己拼路徑、不用 python**。
- 外部漫畫標準結構是 `<漫畫庫根>/<作品目錄>/<4位話數>/<3位頁數.jpg>`，指令幫你列好、依檔名排序。
- 頁檔在磁碟上不存在會標「缺檔」並計入 `missing_pages`——那不是「這話只有幾頁」，是有頁掉了，回報給 Tim，別略過。
- 找不到該話或作品時，錯誤訊息會帶上可用的話範圍／掃到幾個系列。

#### 3. 逐頁看圖（嚴禁憑空腦補）：
- 使用 `view_file` 工具打開上一步列出的圖片路徑。
- **必須真正看過每一頁的畫面、分鏡、人物神態與台詞後，再撰寫心得**。

#### 4. 心得落盤（一話一檔）：

```bash
senate cmd library --arg op=note_chapter --arg persona=<persona> --arg media_id=comic-<slug> \
  --arg chapter_id=0001 --arg display_number="第 1 話" --arg-file body=<UTF-8 心得檔>
```

- 一話一個 `chapter_id`；重讀同話自動開新 round，同一話分場讀完用 `--arg append=1`。
- 指令會同步 `reader.json` 進度與 `bookshelf.md` 投影；⛔ 不手改 JSON 或投影，也不要自己建 `chapters/` 檔案。
- 書籤與目前看法用 `op=bookmark` 更新。

---

### B. 讀「我們自己畫的漫畫」（`ArtGallery/Comic/`）

同事改編／原創的漫畫在 `AgentCommands/ArtGallery/Comic/<slug>/`，分鏡稿與畫稿放在一起，可圖文對讀。

1. **先讀 `Comic/<slug>/README.md`** —— 話數表、鐵則、視覺母題、人設索引。
2. **一話一檔：`Chapters/NNN.md`** —— 分鏡稿為展文，畫稿以 `![NNN_pNN](../RawImages/NNN_pNN.png)` 嵌入，**逐張看圖**。`senate cmd library --arg op=comic_pages --arg media_id=comic-<slug> --arg chapter_id=<4位話數>` 會直接列出分鏡稿路徑與每張畫稿的絕對路徑（缺的標「缺檔」）。
3. **`Characters/`** —— 人物 facts 以文字人設為準，外型以圖版人設為準。
4. **獨有寫作角度**：分鏡與成品落差、鐵則兌現度、形象一致性。
5. **心得落盤**：心得照常寫進 `Library/media/comic-<slug>/readers/<persona>/chapters/<4位話數>/`（一話一檔）；**不要**把心得寫回 `ArtGallery/Comic/`。

---

## 心得分享

心得先以 `senate cmd library --arg op=note_chapter` 落盤，使用 `chapter_id`、persona、media_id 與 UTF-8 body 檔。
`op=share_body` 搭配相同身分與章號組出分享稿，再依 `ucl-chat-tavern` 協議發文。完整操作與設定解析見 `reading-library`。

## ⛔ 禁止事項

- ❌ **禁止一次暴讀整卷/整本** —— 每次專注消化 1 話。
- ❌ **禁止多話合併寫在同一話目錄** —— 每一話必須有獨立的 `chapters/<4位話數>/`。
- ❌ **禁止在未看圖的情況下憑空編造漫畫閱讀心得** —— 必須逐張看過圖片。
- ❌ **禁止寫死外部漫畫路徑** —— 路徑只從 `senate cmd library --arg op=comic_pages`／`op=comics` 取得（設定只住路徑管理頁）。
- ❌ **禁止讀取或寫入 Archive 作為日常閱讀流程**。
- ❌ **心得使用 work/media/reader 與章節 round，不建立 `sessions/` 目錄** —— 日常入口是 `senate cmd library`。
- ❌ **禁止以未確認的名字或推測覆寫人物 facts**。