---
title: 閱讀圖書館 Archive 歷史格式參考
last_updated: 2026-08-06
status: archive-reference-only
summary: 僅供原讀者人工閱讀與遷移 BookNotes Archive 時辨識舊格式；不是日常閱讀流程、不是新 CLI 規格。
audience: Tim / agent
related:
  - <ucl_core:Docs~/zh-Hant/Workflows/Reading_Library_Workflow.md> | 新閱讀圖書館工作流 | 日常唯一流程
  - <ucl_core:Docs~/zh-Hant/Plan/Plan_Library_Media_Migration.md> | 遷移計畫 | registry 與人工 merge SOP
---

# 閱讀圖書館 Archive 歷史格式參考

> [!CAUTION]
> **只有在原讀者需要人工閱讀 `AgentCommands/BookNotes/Archive/`、準備遷移時才可參考本文件。**
> 不可用本文件操作新紀錄，不可讓新工具讀 Archive，也不可對 Archive 執行舊 `library.py` 的寫入命令。

## 舊格式辨識

```text
AgentCommands/BookNotes/Archive/<legacy-slug>/
  book.json
  chapters/chNN_<slug>.md
  characters/<character-id>/_profile.json
  characters/<character-id>/vN_<date>.md
  arcs/
  branches/<reader>/        # 舊分支模型；不等同新 persona/read_session
```

舊 `book.json` 的 `reader_persona` 可能缺失；遷移時如實標為 `unknown`，不得據 title、文字內容或歷史路徑猜測原讀者。`chapter: N` 也可能不是唯一鍵（壓卷／特別篇會重複），不可直接當新 schema 的章節 ID。

## 人工遷移時的讀法

1. 從 `UCL_ReadingNotesManagePage` 取得 Archive 入口；先確認操作者是該筆記的原讀者。
2. 只讀原件，建立 Archive snapshot manifest；不要調整檔名、frontmatter 或加標記。
3. 對章節、人物版本、arc、volume、bookmark 分別寫 merge ledger 去向：保留、複製、alias、連到既有項或暫緩。
4. 所有來源項目都有去向、receipt 存在且驗收後，才在 registry 標為 `migrated`。

## 舊命令的地位

舊 `library.py add-book`、`log-chapter`、`resume --book`、`branches`、`bookmark` 等命令只描述歷史 schema，**不得用於 Archive 或新的閱讀紀錄**。它們不是遷移工具；使用它們會把舊格式重新長回來或改動歷史原件。
