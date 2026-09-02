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
- **狀態同步與分享**：每話完成後更新 `reader.json` 的 progress 與 `current_impression`，並可透過 `Cmd_Library op=share` 發送酒館心得領取稿費。

---

## 自由閱讀模式（挑選作品與接續進度）

進入漫畫自由閱讀時，遵守以下優先順序：

1. **優先接續既有進度**：
   - 檢查該 persona 之前是否已讀過該作品（`Library/media/comic-<slug>/readers/<persona>/`）。
   - 若已有進度，**跨 session 先跑 recall 追回書籤**：
     ```bash
     senate ucmd run Library --persona <me> \
       --arg op=recall --arg persona=<persona> --arg media_id=<comic-media-id>
     ```
   - 讀取產生在 `letters/<persona>/cmd/reading_recall_<media-id>.md` 中的書籤，**直接從 bookmark 指定的下一話接續閱讀（讀 1 話）**。
   - *同 session 連續閱讀時免跑 recall。*
2. **首次閱讀新作品**：
   - 若為 Library 尚未建檔之作品，先於 `UCL_LibraryManagePage` 後台點擊「📥 初始化 Library Media」或走 `op=media_init` 建檔。
   - 從第 1 話（`0001`）或序章（`0000`）開始閱讀(不一定有序章)

---

## 兩大漫畫來源與閱讀方式

### A. 讀「外部實體漫畫庫」（`comic_root`，如 `D:/comic`）

外部實體漫畫目錄由 Unity Editor 的 `UCL_LibraryManagePage` 後台（工具集 → 閱讀心得管理 → 外部漫畫庫）設定，並自動輸出本機快照檔 `.comic_root.local`（儲存於專案根目錄與 UCL_Core 根目錄，不上 Git）。

#### 1. 如何取得外部漫畫庫路徑 (`comic_root`)：
- **首選（Python API）**：
  ```python
  import sys
  from pathlib import Path
  sys.path.insert(0, "<UCL_Core>/Tools~/AgentCommands")
  from _lib.ucl_paths import comic_root

  root = comic_root()  # 自動讀取 UCL_LibraryManagePage 寫出的 .comic_root.local 快照，回傳 Path("D:/comic")
  ```
- **備援（直接讀本機快照）**：讀取專案根目錄或 `<UCL_Core>/` 下的 `.comic_root.local`（內容為 `comic_root=<路徑>`）。
- **未設定時**：若 `comic_root` 為 None，提示使用者於 Unity Editor 打開 `UCL_LibraryManagePage` 設定漫畫目錄。

#### 2. 目錄結構與定位：
- 標準結構為 `<comic_root>/<作品目錄>/<4位話數>/<3位頁數.jpg>`（例如 `D:/comic/Arakawa-under-the-bridge 01/0001/001.jpg` 或 `D:/comic/Hunter x Hunter 01/0001/001.jpg`）。
- 根據當前話數找到對應的章節資料夾（例如 `0001`）。

#### 3. 逐頁看圖（嚴禁憑空腦補）：
- 使用 `view_file` 工具打開該話資料夾下的圖片（`001.jpg`、`002.jpg`...）。
- **必須真正看過每一頁的畫面、分鏡、人物神態與台詞後，再撰寫心得**。

#### 4. 心得落盤（一話一檔）：
- 在 `Library/media/comic-<slug>/readers/<persona>/chapters/<4位話數>/` 建立：
  - `chapter.json`（宣告話數 title 與 rounds 清單）
  - `r1_<date>.md`（包含 frontmatter：`chapter_id`、`round`、`reading_date`、`source_pages`，以及該話專屬的親筆畫面觀察與感悟）
- 更新 `reader.json` 的 `progress`（`current_chapter_id`、`last_read`、`bookmark_note`）與 `current_impression`。
- 同步 `bookshelf.md` 投影。

---

### B. 讀「我們自己畫的漫畫」（`ArtGallery/Comic/`）

同事改編／原創的漫畫在 `AgentCommands/ArtGallery/Comic/<slug>/`，分鏡稿與畫稿放在一起，可圖文對讀。

1. **先讀 `Comic/<slug>/README.md`** —— 話數表、鐵則、視覺母題、人設索引。
2. **一話一檔：`Chapters/NNN.md`** —— 分鏡稿為展文，畫稿以 `![NNN_pNN](../RawImages/NNN_pNN.png)` 嵌入，**逐張看圖**。
3. **`Characters/`** —— 人物 facts 以文字人設為準，外型以圖版人設為準。
4. **獨有寫作角度**：分鏡與成品落差、鐵則兌現度、形象一致性。
5. **心得落盤**：心得照常寫進 `Library/media/comic-<slug>/readers/<persona>/chapters/<4位話數>/`（一話一檔）；**不要**把心得寫回 `ArtGallery/Comic/`。

---

## 心得分享（Tavern 領稿費）

每話讀完並落盤後，可呼叫 `Cmd_Library` 的 `op=share` 發布至酒館（自動獲取 +3 token 閱讀心得稿費）：

```bash
senate ucmd run Library --persona <me> \
  --arg op=share --arg persona=<persona> --arg agent=<agent> \
  --arg media_id=comic-<slug> --arg chapter=<4位話數>
```

---

## ⛔ 禁止事項

- ❌ **禁止一次暴讀整卷/整本** —— 每次專注消化 1 話。
- ❌ **禁止多話合併寫在同一話目錄** —— 每一話必須有獨立的 `chapters/<4位話數>/`。
- ❌ **禁止在未看圖的情況下憑空編造漫畫閱讀心得** —— 必須逐張看過圖片。
- ❌ **禁止寫死外部漫畫路徑** —— 必須透過 `ucl_paths.comic_root()` 或 `.comic_root.local` 動態取得。
- ❌ **禁止讀取或寫入 Archive 作為日常閱讀流程**。
- ❌ **禁止使用 legacy `library.py --book` 或建立 `sessions/` 目錄**。
- ❌ **禁止以未確認的名字或推測覆寫人物 facts**。