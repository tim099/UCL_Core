---
name: reading-library
on_intent: ["讀書", "閱讀", "閱讀心得", "書架", "library"]
description: 新閱讀心得系統。新資料一律採 work → media → persona reader root；每位讀者在同一媒材下有一份進度、期待度與閱讀看法，章節 round 保存重讀歷史。Archive 僅供人工遷移參考。
---

# Reading Library

所有日常閱讀資料只可寫入 `AgentCommands/BookNotes/Library/`。`Archive/` 唯讀，不得由新 CLI、頁面或日常流程消費。

## 資料模型

```text
work → media → reader_persona
```

每個 `media × persona` 的唯一 reader root：

```text
Library/media/<media-id>/readers/<persona>/
  reader.json                 # 程式真相源
  bookshelf.md                # 人可讀投影
  chapters/<chapter-id>/
    chapter.json              # rounds 索引
    r<round>_<YYYY-MM-DD>.md  # 不覆寫的心得歷史
  characters/<character-id>/
    profile.json              # 已確認 facts
    vN_<date>.md              # 主觀 view 的版本史
```

`read_session` 是一次閱讀／重讀的**概念**，由章節 `round` 表示；不得再建立 `sessions/` 路徑或獨立 session 資料夾。

## 硬規則

- 寫入前確認 `work_id`、`media_id`、`reader_persona`；沒有 persona 不得建立新紀錄。`unknown` 僅用於來源讀者無法判定的 legacy 遷移。
- `reader.json` 是 status、anticipation、bookmark、current impression 與日期的唯一真相源；`bookshelf.md` 必須由工具同步，是可讀投影，不可成為第二真相源。
- 同一章重讀時新增 `r2_...md` 並加入 `chapter.json.rounds`；不可覆寫既有 round。
- 人物已確認的客觀資料寫 `profile.json`；讀者感受與推測寫版本化 `vN_<date>.md`。
- 媒材必須獨立：`comic-`、`anim-`、`film-`、`series-`、`stream-` 等 media 不共用進度。
- 新工具寫入時驗證 `reader.json.reader_persona` 與路徑 `<persona>` 相同，避免資料放錯讀者根目錄。

## 追回既有閱讀進度

要在隔一段時間後接回閱讀，先以**當前 persona 與 media id**生成完整追回檔：

```powershell
python <UCL_Core>/Tools~/AgentCommands/library.py reading-recall --persona <persona> --media-id <comic-|anim-|film-|... media-id>
```

`--book-id` 可作為 `--media-id` 的相容別名。工具只讀
`Library/media/<media-id>/readers/<persona>/`，會驗證 JSON 身分與路徑相同；絕不讀 Archive 或其他
persona。產物為 `AgentCommands/ChatTavern/baton/letters/<persona>/_reading_recall_<media-id>.md`，
會在下次生成時覆寫，內含目前狀態、書架投影、所有已讀章節 round 原文，以及角色 facts／view
版本。它是追讀用機械視圖，不能取代或手改原始筆記。

## 每次閱讀完成後

1. 新增本章 round 與更新 `chapter.json`。
2. 更新 `reader.json` 的 bookmark、`last_read`、status、anticipation（若變動）與 `current_impression`。
3. 同步 `bookshelf.md` 的 frontmatter、進度與最新看法。
4. 漫畫依 `reading-manga` 發出 `reading-reflection`。

> 新 Library API 尚在實作；在 API 就緒前，可手動依此結構寫入，但禁止呼叫 legacy `library.py --book` 或 branches 流程。
