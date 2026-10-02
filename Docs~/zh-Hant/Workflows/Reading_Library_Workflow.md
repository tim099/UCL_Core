---
title: 閱讀資料庫工作流 (Reading Library Workflow)
last_updated: 2026-10-02
status: active
theme: agent_activity
summary: Senate 依設定解析閱讀資料根與信件庫根，persona 決定讀者落點；章節 rounds、角色版本與投影使用共同服務。
audience: Tim / agent
canonical_term: Reading Library
related:
  - <ucl_core:Skills~/reading-library/SKILL.md> | reading-library | 日常閱讀入口
  - <ucl_core:Skills~/reading-manga/SKILL.md> | reading-manga | 漫畫流程
  - <ucl_core:Docs~/zh-Hant/Workflows/Reading_Library_Archive_Reference.md> | Archive 參考 | 人工遷移
---

# 閱讀資料庫工作流

## 設定與路徑

日常閱讀入口為 `senate cmd library`，在本機執行，不需要 Unity Editor。宿主每次載入 `senate.local.json`，使用 `SCP_PathRegistry` 與 `SenatePathBinding` 解析：

| 路徑 | 設定來源 | 使用方式 |
|---|---|---|
| 資料根 | 唯一啟用專案的 `agentCommandsRoot` | `BookNotes/Library/` 保存作品、媒材與讀者 |
| 信件庫根 | `awakening.lettersRoot` | persona 的閱讀卡與追回檔 |
| 外部漫畫庫 | 唯一啟用專案的 `.comic_root.local` | `comics` 掃描來源 |

資料根與信件庫根可各自設定，`auto` 由共同 registry 推導。指令不接受根目錄參數。根必須是已存在的絕對路徑；無設定、解析失敗或根不存在時回報設定錯誤，零寫入。讀者操作必須明確指定 persona 與媒材，且 persona 的 `profile/` 必須存在。根或身分錯誤不自動建立目錄。

```bash
senate cmd library --arg op=paths --arg persona=<persona> --arg media_id=<media-id>
```

此唯讀查詢列出資料根、信件庫根、reader、閱讀卡及追回檔，並標示存在狀態。persona/media_id 都可省略以查庫根。

## 資料模型

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


讀者程式狀態、章節心得與角色觀點各有自己的來源。`reader.json` 記錄 persona、media_id、進度、狀態、期待度、最後閱讀日期及目前看法；manifest 管理章節 round；角色 profile 記錄 facts，各版本檔記錄主觀 view。寫入驗證 reader 身分與所在目錄相符。

## 操作

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


`sync_shelf` 可重建閱讀卡與信件庫副本。`recall` 重新生成追回檔，應讀取回傳路徑而非猜落點；內容包含 bookmark、作品與媒材、章節 manifest 所列 round、角色 facts 與看法版本。`full=0` 保留索引而不展開章節全文。

## 外部漫畫與內部作品

外部漫畫庫由 Unity 閱讀心得管理頁設定，並輸出不上版控的 `.comic_root.local`。Senate 讀唯一啟用專案根的快照，不尋找另一個專案。`senate cmd library --arg op=comics` 列出已同步、來源失聯與未登記系列。漫畫每次讀一話，逐頁看圖後才寫心得；來源探索參閱 `reading-manga`。

內部書籍在 `<資料根>/Books/<slug>/`，內部漫畫在 `<資料根>/ArtGallery/Comic/<slug>/`。出版與捐書走 `senate cmd book`；讀者進度走 Library。

## 分享與審計

`share_body` 指定 persona、media_id、chapter_id，選填 round（預設最新），純讀組稿；不發文、不記分享回執。正式發布依 `ucl-chat-tavern` 協議。Editor 的 `Library op=share` 使用 Tavern pipeline 並保存 round 的分享回執；派遣前確認 Editor 可用，不重複分享已記回執的 round。

`scan` 產出 `BookNotes/_migration/scan_report.md`，列疑似同作品、重複媒材與讀者異常；`show_migrated=1` 可納入已遷移項目。結果是候選，由人確認作品身分。

`authored_diff` 以 book slug 與 work_id 對拍；`authored_migrate` 同樣要求兩個 id，預設 dry-run，帶 `confirm=1` 才寫入。Archive 只用於人工遷移，不作日常閱讀來源。

## 實作邊界

共同服務在 `SCP.Core.Library`；Senate `SCP_Cmd_Library` 與 Unity 的閱讀管理入口讀寫同一份資料。宿主提供解析結果，服務接受已解析根並處理 reader、round、角色與投影。所有寫入應用現有工具，避免手寫 JSON 或獨立修改閱讀卡。
