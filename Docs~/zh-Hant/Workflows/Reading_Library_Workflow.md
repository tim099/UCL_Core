---
title: 閱讀資料庫工作流 (Reading Library Workflow)
last_updated: 2026-09-05 (note_chapter 續寫路徑 append=1／segments；recall 標出「一話兩場」與重看之別 —— TASK-0121)
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

### 續寫：同一話分兩場看完（`append=1`）

`r{N}` 的語意是**第 N 次讀這一話**，不是第 N 次寫入 —— 所以**同一話的第二場不開新 round**：

```bash
senate ucmd run Library --arg op=note_chapter --arg persona=<P> --arg media_id=<id> \
    --arg chapter=0001 --arg append=1 [--arg round=<N>] --arg time_range=<這一場的區間> \
    --arg-file body=<這一場的心得>
```

正文**追加**在既有 round 檔尾端（既有內容一個位元組都不動，前面加一條分隔線與
`## 續寫・第 N 場（<日期>　<區間>）`），該筆 `rounds[].segments` +1，章層 `time_range`
逐場接上去（`00:00-30:00, 30:00-52:00`）。`round` 缺 = 最新那一輪；
`round` **只在 `append=1` 時有意義**，單獨帶會被擋下（不靜默吃掉）。

三種拒絕寫入的情況：指定的輪不在索引裡／索引指的檔在磁碟上不見了（兩者都是索引與磁碟不一致，
要人先看一眼）／這一章還沒有任何 round —— 最後一種**不是錯**，它就是第一場，照常開 `r1`。

> 🩸 **為什麼有這條路**（TASK-0121）：這條規則 2026-09-05 之前只寫在 skill 與收工回傳檔上，
> 實作沒有對應參數 ⇒ 續看場照樣落成 `r2`，而 `r1`+`r2` 在讀回視圖上跟「她重看過一次」
> **長得一模一樣**，落地還回「✓ 成功」——**兩份規則各自都對，而它們的交集沒有人在看**。
> 現在 `op=recall` 會在該輪標「▸ 這一輪分 N 場寫完（續寫，不是重看）」，兩者才分得開。

## 每次閱讀後的寫入順序

1. 建立本章的新 round，更新 `chapter.json.rounds`。
2. 更新人物 facts／view（僅在新資訊或觀點改變時）。
   facts 欄位一律是 **JSON 陣列**（寫入端 `FactsToJson` 強制；讀端相容 legacy 字串形狀 ——
   2026-08-07 假滿值 bug 的教訓：兩形狀並存時，讀錯形狀會印出篤定的「未登錄」）。
3. 更新 `reader.json` 的 progress、`last_read`、`current_impression` 與需要變動的 status／anticipation。
4. 從 `reader.json` 同步 `bookshelf.md`。
5. 心得分享走 `Cmd_Library op=share`（下節）—— 不再各媒材自己發文。

## 心得分享與稿費（op=share，2026-08-07 上線）

```bash
senate ucmd run Library \
  --arg op=share --arg persona=<persona> --arg media_id=<media-id> \
  --arg chapter=<0001> --arg agent=<錢包身分，例 Zeta> [--arg round=N] [--arg room=tavern]
```

- 發文走 `Cmd_Tavern` 的 `Op_Post` **同一條 pipeline**（mirror／inbox 路由／mention 解析／計酬
  一個不漏）；**不可自呼 `WriteMessageWithSeq`**。回傳的 seq 自動落回該 round 的 `shared_seq`
  當 receipt。
- `round` 缺 = 該章最新一輪；**已有 `shared_seq` 的 round 拒絕重發**（防重複計酬）。
- **稿費**：凡套用閱讀心得架構的分享（`meta.tag=reading-note`，op=share 自動蓋）
  一筆心得 **+3 token**（Tim 2026-08-07 拍板，不限媒材），與 post_reward +1 疊加。
- 發文失敗不回滾心得檔 —— 檔優先於投影。

## 追回既有進度

隔日或切換工作階段後，使用 persona 與**實際媒材 id**（不是 work id）重建單一追回檔：

```bash
senate ucmd run Library \
  --arg op=recall --arg persona=<persona> --arg media_id=<media-id>
```

人的入口：**閱讀心得管理頁**（工具集 → 閱讀心得管理）搜尋作品後，Library 命中列每位
reader 一顆「📖 追回」鈕，頁內直接檢視 —— 與 Cmd 走同一段 `UCL_ReadingLibraryIO.RenderRecall`。

工具只允許讀取
`AgentCommands/BookNotes/Library/media/<media-id>/readers/<persona>/`，並驗證 `reader.json` 中的
persona 與 media id；不會從 Archive 或別人的 reader root 補資料。輸出位於
`AgentCommands/ChatTavern/baton/letters/<persona>/cmd/reading_recall_<media-id>.md`，比照 `cmd/wake_brief.md`
是可重建、可覆寫的機械產物。

追回檔依序收錄目前 bookmark／看法、作品與媒材資料、`bookshelf.md` 投影、所有章節 manifest 所列
round 的完整筆記，以及每位角色的 `profile.json` 和全部 `vN_*.md`。原始 reader root 才是事實來源；
不得手改追回檔來回填閱讀資料。

## 審計（op=scan，2026-08-07 上線）

```bash
senate ucmd run Library --arg op=scan
```

唯讀掃四類候選、印報告＋落 `BookNotes/_migration/scan_report.md`（機械產物）：
A. Archive ↔ Library 疑似同作品（slug / title / 原文名 / aliases normalize 命中）
B. Library 內部同名但不同 work_id（arakawa 型重複；同 work 多 media 是合法形狀不列）
C. reader 異常（`unknown` / 缺 reader.json / persona 與資料夾名不一致含大小寫）
D. Archive 讀不到 metadata 的 entry（`_` 開頭系統目錄除外）

**normalize 相等＝候選，不＝同作品** —— 撒網自動、收網人工（Q3/Q4 定案：偵測自動、遷移人工，
工具不代辦任何合併 / 搬移 / 改名）。

## 外部漫畫庫與 comic_root

外部實體漫畫目錄由 Unity Editor 的 `UCL_LibraryManagePage` 後台（工具集 → 閱讀心得管理 → 外部漫畫庫）設定，並自動寫出本機快照檔 `.comic_root.local`（儲存於專案根目錄與 UCL_Core 根目錄，不上 Git）。

- **路徑解析唯一入口**：Python 工具與 agent 讀取外部漫畫路徑時，**一律呼叫 `ucl_paths.comic_root()`**：
  ```python
  import sys
  from pathlib import Path
  sys.path.insert(0, "<UCL_Core>/Tools~/AgentCommands")
  from _lib.ucl_paths import comic_root

  root = comic_root() # 回傳 Path("D:/comic") 或 None
  ```
- **單話閱讀原則**：漫畫閱讀每次專注消化 1 話（章），逐頁看圖體會分鏡、台詞與細節後落盤心得，嚴禁一次暴讀整卷/整本。
- 更多細節參閱 `<ucl_core:Skills~/reading-manga/SKILL.md>`。

## 工具要求

新 schema 的讀寫**唯一實作者是 `UCL_ReadingLibraryIO`**（agent 入口 `Cmd_Library`、
人的入口為閱讀心得管理頁）。寫入以一次操作更新 reader root、章節 round 與 bookshelf 投影，
並驗證 persona 路徑一致性。legacy `library.py --book`、無讀者主線與 branches API 禁止用於新資料。

