---
name: reading-library
on_intent: ["讀書", "閱讀", "閱讀心得", "書架", "library"]
description: 閱讀心得流程。Senate 依設定與 persona 解析路徑，work → media → reader 保存進度、期待度、章節 rounds 與人物看法。
---

# Reading Library

日常閱讀使用 `senate cmd library`，不需要 Unity Editor。指令由 Senate 設定解析資料根與信件庫根；讀者操作明確帶 `persona` 與 `media_id`。所有根目錄必須已存在，persona 必須有 `profile/`，設定不完整時先修設定再重跑。

## 模型與落點

`work → media → reader`：同作品的漫畫、小說、動畫是獨立 media，進度與心得各自保存。

```text
<資料根>/BookNotes/Library/
  works/<work-id>/work.json
  media/<media-id>/media.json
  media/<media-id>/readers/<persona>/
    reader.json
    bookshelf.md
    chapters/<四位章號>/chapter.json
    chapters/<四位章號>/r<round>_<YYYY-MM-DD>.md
    characters/<character-id>/profile.json
    characters/<character-id>/vN_<YYYY-MM-DD>.md
<信件庫根>/<persona>/bookshelf/<media-id>.md
<信件庫根>/<persona>/cmd/reading_recall_<media-id>.md
```

`reader.json` 是當前進度與看法的真相源；閱讀卡與追回檔是可重建投影。信件庫可獨立於資料根，不自行拼出根目錄。使用 `op=paths` 取得實際落點。

## 續讀與建檔

先讀追回檔，再接續既有進度：

```bash
senate cmd library --arg op=recall --arg persona=<persona> --arg media_id=<media-id>
```

讀取回傳列出的檔案；`full=0` 只列 round 索引。媒材尚未登記時，先確認作品身分並建檔；預設期待度 3，可由本人明確指定 0–5：

```bash
senate cmd library --arg op=media_init --arg persona=<persona> \
  --arg work_id=<work-id> --arg media_id=book-<work-id> --arg media_kind=book \
  --arg-file title=<UTF-8 標題檔>
```

既有媒材的新讀者用 `op=register_reader`。身分與媒材 id 不猜測、不借用別人的 reader root。

## 完成一次閱讀

```bash
senate cmd library --arg op=note_chapter --arg persona=<persona> \
  --arg media_id=<media-id> --arg chapter_id=0001 --arg-file body=<UTF-8 心得檔>
```

章號四位數，`0000` 代表序章。重讀建立新 round；同一章分場讀完用 `append=1`，可帶 `append_round=N`，省略時續寫最新 round。正文追加，保留既有內容。每次寫入同步閱讀卡與追回檔；依回傳確認正文與投影落點。

`bookmark` 更新書籤、目前看法與狀態；`add_character` 登記已確認 facts 與主觀看法，`revise_view` 記錄改觀及原因。角色不以推測名字覆寫 facts。長文字一律使用 UTF-8 檔與 `--arg-file`。不得手改 JSON 或投影，也不建立額外 `sessions/` 目錄。

## 來源與分享

同事的書在 `<資料根>/Books/<slug>/`，漫畫在 `<資料根>/ArtGallery/Comic/<slug>/`；漫畫閱讀套用 `reading-manga`，每次專注一話。寫書與出版使用 `senate cmd book`，參閱 `Book_Writing_Workflow.md`。

外部漫畫由閱讀心得管理頁設定，Senate 從唯一啟用專案的 `.comic_root.local` 讀取：

```bash
senate cmd library --arg op=comics
```

分享前先完成心得。`senate cmd library --arg op=share_body` 搭配 persona、media_id、chapter_id 與選填 round，只組出貼文正文。發布與稿費使用 `ucl-chat-tavern` 協議；Editor 的 `Library op=share` 可記錄分享回執，該入口需依派遣規範先確認 Editor 可用。

## 查詢與審計

`paths` 唯讀列出位置與存在狀態，persona/media_id 可選。`scan` 產出審計報告，候選由人判讀；`authored_diff` 比對寫書資料，`authored_migrate` 預設 dry-run，確認後才帶 `confirm=1`。

完整參數以 `senate cmd library --help` 為準。日常資料不從 Archive 讀取或補寫；人工遷移參閱 `Reading_Library_Archive_Reference.md`。
