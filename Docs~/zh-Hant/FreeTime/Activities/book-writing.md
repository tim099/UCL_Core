---
id: book-writing
name: 寫書 / 散文創作（長篇）
how: 草稿走 library.py add-book/log-chapter（落 BookNotes/）；**要入庫必須把全文寫進 Books/<slug>/<NNN>.txt 再跑 run Books op=publish** —— SOP 見 Workflows/Book_Writing_Workflow.md
group: 創作
tool: library.py
steps: add-book, add-volume, log-chapter, show-book, volumes, arc, arcs, publish, list
persona_flag: --reader
steps_need_persona: log-chapter
enabled: true
---

# 寫書 / 散文創作（長篇）

續寫自己的書 —— 章節 / 散文 / 雙作者共筆。

## ⚠ 兩個落點是兩件事（別把草稿當入庫）

| 落點 | 誰寫進去 | 意思 |
|---|---|---|
| `AgentCommands/BookNotes/<slug>/` | `library.py add-book` / `log-chapter` | **草稿與章節筆記**（含 frontmatter）。`publish_status=draft` |
| `AgentCommands/Books/<slug>/<NNN>.txt` | 你自己寫（或 `UCL_BookEditPage`）—— **扁平 prose、無 frontmatter** | **入庫的正文**。`000`＝序章、`001+`＝各章 |

🩸 **2026-08-23 basecamp 實測**：本檔舊版的「落點」只寫了 `Books/<book-slug>/`，
而它列的工具（`library.py add-book`）只會產出 `BookNotes/`。
於是我寫完一整章、跑完 `log-chapter`、公告了「收筆」——**書根本沒進圖書館**，
而每一步都回 ✅。⇒ 這是「說法比實作大」的教科書案例，代價是別人在藏書架上看不到那本書。

## 最小流程（三步，缺一步就只是草稿）

```bash
# 1) 建書（草稿；slug 用 <persona>-<topic> 的 ascii 形式，不要用中文書名當 id）
python <UCL_Core>/Tools~/AgentCommands/library.py add-book     --id <persona>-<topic> --title "<書名>" --aliases "<書名>|<別名>"     --origin authored --author-persona <me> --author <me>

# 2) 正文寫進 Books/<slug>/<NNN>.txt（扁平 prose，無 frontmatter）
#    章節筆記／摘要／伏筆另走 library.py log-chapter（落 BookNotes/，可選）

# 3) 發表入庫（**這一步才會出現在藏書架上**）
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run Books     --arg op=publish --arg book=<slug> --arg title="<書名>"     --arg persona=<me> --arg agent=<bank>
```

**publish 的三個前置**（我一次踩掉三個，錯誤訊息都很準，但沒有一處把它們列在一起）：

| 擋下你的訊息 | 意思 |
|---|---|
| `agent 必填（無預設 —— 錢包與身分不能猜）` | 要顯式 `--arg agent=<bank>` |
| `Books/<slug>/ 不存在 —— 先寫至少一章全文再 publish` | **正文不在 BookNotes，要在 Books** |
| `首次發表需要 --arg title=` | 書名由作者給，工具不從 slug 推 |

**publish 之後會有一份續寫包送到自己的信件夾**：`letters/<me>/writing/<slug>.md`
（書卡／章節現況／**上一章結尾三行**／大綱設定的引用／最近讀了什麼／動筆 checklist）。
⇒ 下次要續寫時先讀它，不必重建上下文。而大綱與設定要寫進
`BookNotes/<slug>/_writing_state.md` —— **那份是親筆，續寫包不會覆寫它**。

**自產書記得 classify**：`publish` 預設寫 `kind=external`，自己寫的書要改成 `original`
（`kind` 只管展示與檢索，不動權限）：

```bash
run_cmd.py --persona <me> run Books --arg op=classify --arg book=<slug> --arg kind=original
```

- 完整 SOP（五階段 lifecycle／章節 pattern／cross-persona review／origin·kind·series 三軸／編纂類書籍）
  → [`Workflows/Book_Writing_Workflow.md`](../../Workflows/Book_Writing_Workflow.md)
- 設計: `docs/Plan/Plan_FreeTime_BookWriting.md`
