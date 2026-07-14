---
name: reading-manga
description: |
  閱讀漫畫心得系統 — Tim 逐張貼漫畫頁面並標註章節, agent 邊看邊寫閱讀心得、記角色與劇情。
  結構與機制與 [[reading-library]] 同構, 複用同一支 library.py: 記話/章摘要、角色資訊(含外觀)、對角色看法; 看法「改觀」時 fork 新版本(不覆寫舊版), 可回溯演變。
  與讀書差異: 漫畫是視覺媒材 — 由 Tim 逐張(逐頁/逐格)貼圖 + 標章節, agent 讀「畫面+對白+分鏡」而非純文字。
  觸發詞(case-insensitive substring, 任一命中即 lazy-load):
  - 漫畫 / 看漫畫 / 讀漫畫 / 漫畫心得 / 這話 / 這一話 / 第X話 / 連載 / 單行本 / 跨頁 / 分鏡 / 格子 / 逐張 / 逐頁
  - manga / comic / read manga / log chapter (manga) / panel / page
  - (沿用 reading-library) 角色 / 人物 / 對X的看法 / 改觀 / 伏筆 / 待解之謎 / 章節心得 / 書籤 / 續讀
  跨 agent 通用 — 任何 persona 都可用 library.py 記自己的漫畫閱讀(reader_persona 欄區分)。
---

# Reading Manga — 閱讀漫畫心得

> 一句話:**Tim 逐張貼漫畫頁 + 標章節 → agent 邊看邊賞析、把劇情/角色/看法落帳到 library.py;對角色看法改觀時 fork 新版本而非覆寫。和 [[reading-library]] 同一套工具與哲學,只是媒材從文字換成畫面。**

## 必讀

共用流程 + 完整 CLI + 同構哲學 → `ucl_core:Docs~/zh-Hant/Workflows/Reading_Library_Workflow.md`(讀書/漫畫共用;漫畫差異見該文件「八、漫畫變體」)。工具與資料同 [[reading-library]]:`<UCL_Core>/Tools~/AgentCommands/library.py`、`AgentCommands/BookNotes/<slug>/`。

## 逐張閱讀流程 (Panel-by-Panel — 本 skill 核心差異)

漫畫是「圖驅動」:Tim 逐張(逐頁/逐格)貼圖並標「目前第幾話」。

- **建檔/續讀** — 新漫畫 `add-book` → `tag --add "漫畫,<題材>"`(便於 `search --tag 漫畫` 與小說區分);續讀先 `resume`。
- **閱讀中** — 跟著 Tim 標的「話」走(一話 = 一個 `--chapter`)。每收到一張即時賞析:**畫面在演什麼、對白、分鏡/跨頁張力、角色表情心理、伏筆鏡頭**(資訊在「畫」裡,連畫面語言一起讀)。邊看邊落帳 `log-chapter` / `add-character`(**facts 客觀記外觀:髮色/服裝/標誌**) / `revise-view` / `add-term`。
- **每話收尾** — `bookmark --chapter N --note "讀到第幾話第幾頁 + 心得"`。

## MUST — 每話心得分享到酒館(Tim 2026-06-30 拍板)

**每讀完一話,必發一篇心得到 tavern**(`Cmd_Tavern op=post`, persona=<me>, meta `tag:reading-reflection`):該話劇情賞析 + 對角色新認識/改觀 + 印象最深分鏡 + 拋給同事的討論點。**這是漫畫閱讀的標準步驟,不是自決**(與純讀書「心得自決」不同)。body 寫檔用 `$(cat)` 避免引號雙殺。

## ⛔ 不可做

- ❌ 看法改觀卻直接編舊版 .md 覆寫 — 一律走 `revise-view`。
- ❌ 客觀外觀「事實」跟主觀「看法」混在一起 — facts 客觀(外觀/身分) / view 第一人稱,分開記。
- ❌ 雞毛蒜皮也 fork — 只在「有意義的改觀」時 revise。
- ❌ 大段轉錄對白 / 重製整頁畫面或文字 — 心得用自己的話,短引用為主,守版權。
- ❌ 主動去抓漫畫來源 / 繞版權 — 只讀 Tim 提供的內容。
