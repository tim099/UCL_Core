---
title: 閱讀資料庫工作流 (Reading Library Workflow)
last_updated: 2026-08-06
status: active
theme: agent_activity
summary: 新閱讀心得採 work → media → persona reader root；reader.json 保存當前狀態，章節 rounds 保存不可覆寫的閱讀歷史。
audience: Tim / agent
canonical_term: Reading Library
related:
  - <ucl_core:Skills~/reading-library/SKILL.md> | reading-library | 日常閱讀入口
  - <ucl_core:Skills~/reading-manga/SKILL.md> | reading-manga | 漫畫流程
  - <ucl_core:Docs~/zh-Hant/Workflows/Reading_Library_Archive_Reference.md> | Archive 參考 | 僅供人工遷移
---

# 閱讀資料庫工作流

所有日常讀寫只使用 `AgentCommands/BookNotes/Library/`。Archive 是舊資料的人工遷移來源，不能被新閱讀工具或日常檢索當作內容來源。

## 模型與路徑

```text
work → media → persona reader root
```

```text
Library/
  works/<work-id>/work.json
  media/<media-id>/media.json
  media/<media-id>/readers/<persona>/
    reader.json
    bookshelf.md
    chapters/<chapter-id>/
      chapter.json
      r<round>_<YYYY-MM-DD>.md
    characters/<character-id>/
      profile.json
      vN_<YYYY-MM-DD>.md
```

`media_id` 必須表達媒介，如 `comic-`、`anim-`、`film-`、`series-`、`stream-`。同一作品的改編媒材各自保存進度與心得。

## Reader root

一個 reader root 對應「某 persona 閱讀某一媒材」的持續關係；沒有主線，也不能省略 persona。

- `reader.json`：唯一程式真相源，記錄 `reader_persona`、`media_id`、status、anticipation、目前 bookmark、最後閱讀日與 current impression。
- `bookshelf.md`：由工具同步的人可讀卡片，呈現目前進度、期待度與短評；不可與 `reader.json` 獨立修改而產生雙真相源。
- `chapters/`：章節心得與閱讀歷史。
- `characters/`：角色 facts 與 reader view 的版本史。

寫入時必須驗證 `reader.json.reader_persona` 與資料夾名稱相同。`unknown` 只可代表 legacy 資料的未知讀者；不能成為新內容的預設讀者。

## Round，而非 sessions 目錄

一次閱讀或重讀仍稱為 read session，但它是章節 round 的語意，不是額外的路徑層級。

首次閱讀章節：

```text
chapters/0001/r1_2026-08-06.md
```

重讀同章時新增 `r2_<date>.md`，並更新 `chapter.json.rounds`。既有心得不可覆寫。此設計讓同一 persona 的進度、看法與期待度聚合在 reader root，同時保留每次實際閱讀的版本史。

## 每次閱讀後的寫入順序

1. 建立本章的新 round，更新 `chapter.json.rounds`。
2. 更新人物 facts／view（僅在新資訊或觀點改變時）。
3. 更新 `reader.json` 的 progress、`last_read`、`current_impression` 與需要變動的 status／anticipation。
4. 從 `reader.json` 同步 `bookshelf.md`。
5. 漫畫依 `reading-manga` 發出 `reading-reflection`。

## 工具要求

新的 `library.py` API 應以一次寫入操作更新 reader root、章節 round 與 bookshelf 投影，並驗證 persona 路徑一致性。legacy `library.py --book`、無讀者主線與 branches API 禁止用於新資料。
