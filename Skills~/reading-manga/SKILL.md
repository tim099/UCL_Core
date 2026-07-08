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

## 🎯 為什麼存在 / 何時用

漫畫人物多、跨多話連載,讀後續話常忘了前面的關鍵角色、伏筆、自己當時的判斷。本系統讓 agent 在「Tim 逐張貼圖」的閱讀模式下:
- 每讀完一話 → 記該話摘要 / 關鍵事件 / 新角色 / 伏筆
- 出現角色 → 記一份 v1(漫畫多記**外觀特徵**,因為是視覺媒材)
- **看法改觀**(劇情翻轉、行為顛覆先前印象)→ fork 新版本(v2, v3...),舊版完整保留

**觸發時機(agent 自律)**:
- Tim 說「看漫畫 / 讀這話 / 記一下這話」→ 進入逐張閱讀模式
- 一話讀完 → `log-chapter`
- 出現新角色、或 Tim 問「對 X 的看法」→ `add-character`(v1, 帶外觀)
- **對角色改觀** → `revise-view`(新版本)
- Tim 問「之前我怎麼看 X / X 的看法怎麼變的」→ `show-character --version all`

## 🖼 逐張閱讀流程 (Panel-by-Panel,本 skill 的核心差異)

漫畫不像小說一段段文字,而是 **Tim 逐張(逐頁或逐格)貼圖、並標註目前在第幾話**。Agent 的閱讀節奏因此是「圖驅動」:

**A. 開始 / 建檔**
1. **建檔 / 續讀** — 新漫畫 `add-book`(建議 `--title` 中文名 + `--title-original` 原文名);**若續讀已建的漫畫, 先 `resume --book <slug>`** 喚回進度 + 角色現況 + 未解伏筆 + 上次心得,不必回頭重看。
2. **標記媒材** — 建檔後 `tag --book <slug> --add "漫畫,<題材如 少年/戀愛/懸疑>"`,讓 `search --tag 漫畫` 能把漫畫和小說區分。

**B. 閱讀中(Tim 逐張貼)**
3. **跟著 Tim 的章節標註走** — Tim 貼圖時會說「第 X 話 / 這頁是第 X 話的開頭」之類。**以 Tim 標的「話/章」為 `--chapter` 單位**(一話 = 一個 chapter 條目)。
4. **逐張即時賞析** — 每收到一張(或一段連續頁),即時討論:**畫面在演什麼、對白、分鏡/跨頁張力、角色表情與心理、伏筆鏡頭**。漫畫的資訊在「畫」裡,賞析要連畫面語言一起讀,不是只讀對白文字。
5. **邊看邊落帳**:
   - 一話看完 → `log-chapter`(summary 寫該話劇情 + events 列關鍵分鏡/轉折 + views 寫對角色的新認識 + foreshadow 記伏筆鏡頭)
   - 新角色登場 → `add-character`(**facts 客觀記外觀:髮色/服裝/標誌性特徵 + 客觀身分**;view 寫第一人稱主觀印象)
   - **對角色改觀** → `revise-view`(fork 新版本,不覆寫)
   - 特殊名詞 / 地名 / 勢力 / 招式設定 → `add-term`

**C. 每話收尾(MUST)**
6. **書籤** — 一話讀完(或中斷)時 `bookmark --book <slug> --chapter <N> --note "讀到第幾話第幾頁 + 心得"`,方便下次 `resume`。
7. **每話心得分享到聊天酒館(MUST,Tim 2026-06-30 拍板)** — **每讀完一話, 必發一篇心得到 tavern**(`Cmd_Tavern op=post`, sender=zeta/各自 bank, persona=<me>, meta `tag:reading-reflection`)。內容:該話劇情賞析 + 對角色的新認識/改觀 + 印象最深的分鏡 + 拋給同事的討論點。**這是漫畫閱讀的標準步驟, 不是自決**(與純讀書的「心得自決」不同 —— 漫畫場景 Tim 要每話都分享)。記得 body 寫檔用 `$(cat)` 避免引號雙殺。

**D. 階段總結(Arc Summary,每 ~6 話或一個篇章收束時,彈性)**
8. **見林** — 每讀約 6 話(或一個篇章/單元劇收束)→ `arc --book <slug> --chapters "1-6" --title "<篇章名>" --summary "..." --threads "貫穿線索"`。**per-話是樹,arc 是林**;`resume` 會把最近的階段大綱帶在最前。
9. **見林之林(單行本 / 部 總結)** — 讀完一整本單行本或一個大長篇篇章時,額外寫一個跨整段的 `arc`(title 標「★第 N 集總結」),收束整段主線與跨集大伏筆。

⚠ **版權守則**:本系統讀的是 **Tim 主動提供(貼)的內容**。心得/落帳用**自己的話描述與賞析**,引用對白採**短引用**為主,**不大段轉錄、不重製整頁畫面或文字**;不主動去抓 / 不繞版權來源。

## 🧩 與既有系統的同構(設計哲學,沿用 reading-library)

| 本系統 | 對應的既有系統 | 共同精神 |
|---|---|---|
| 角色看法版本史 | [[ucl-affinity]] 的 opinion history | 看法改觀 = 記新版, 不覆寫 |
| 改觀 fork 新版本 | persona fork ([[ucl-morning]]) | 保留「過去的看法/自己」 |
| 話/章摘要 | [[ucl-letters-to-self]] | 給未來的自己留線索 |

**核心 hard rule:改觀就 fork,絕不覆寫。** 好作品值得重看,正因看法會變;保留 v1→v2→v3 的演變本身就是閱讀體驗的一部分。

## 🛠 CLI(複用 [[reading-library]] 的 `library.py`,不另起工具)

> 工具:`<UCL_Core>/Tools~/AgentCommands/library.py`(跨專案共用);資料落各專案 repo 的 `AgentCommands/BookNotes/<slug>/`。
> 漫畫與小說共用同一套 book/chapter/character/term/arc 模型 —— `--chapter` 對應 Tim 標的「話」,角色 facts 多記外觀,其餘用法與讀書完全相同。完整指令速查見 [[reading-library]] SKILL §🛠 CLI 速查。

```bash
PY="python <UCL_Core>/Tools~/AgentCommands/library.py"

# 建漫畫檔 (origin 預設;建後 tag 標「漫畫」便於與小說區分)
$PY add-book --id <slug> --title <中文名> --title-original <原文名> --author <作者> [--reader-persona <me>]
$PY tag --book <slug> --add "漫畫,<題材>"

# 記一話 (--chapter = Tim 標的話數)
$PY log-chapter --book <slug> --chapter 5 --title <話名> \
    --summary "這話劇情" --events "關鍵分鏡A | 跨頁高潮B" \
    --views "對X的新認識" --new-characters "cid1" --foreshadow "伏筆鏡頭A"

# 新角色 (facts 客觀記外觀 + 身分; view 主觀印象)
$PY add-character --book <slug> --id <cid> --name <角色名> --chapter <初登場話> \
    --headline "一句話角色標題" --facts "外觀:黑長髮紅瞳/校服 | 身分:轉學生" --view "第一人稱印象"

# 改觀 fork (保留舊版) ★核心
$PY revise-view --book <slug> --character <cid> --chapter <話> \
    --headline "新標題" --change-reason "為何改觀" --facts "新增事實" --view "新看法" --diff "與前版差異"

# 書籤 / 續讀 / 名詞 / 階段大綱 (用法同 reading-library)
$PY bookmark --book <slug> --chapter 5 --note "讀到第5話第12頁 + 心得"
$PY resume --book <slug>
$PY add-term --book <slug> --term <名詞> --category term --definition "解釋"
$PY arc --book <slug> --chapters "1-6" --title "<篇章名>" --summary "..." --threads "線索A | 線索B"

# 查詢
$PY show-book --book <slug>
$PY show-character --book <slug> --character <cid> --version all
$PY search --tag 漫畫
```

## 🆚 與 reading-library 的區別

| | 閱讀漫畫(本) | 讀書(reading-library) |
|---|---|---|
| 媒材 | 視覺(畫面+對白+分鏡) | 純文字 |
| 內容來源 | **Tim 逐張貼圖 + 標章節** | WebFetch / Tim 貼章節內文 |
| 閱讀單位 | Tim 標的「話/章」(由頁/格組成) | 章 |
| 角色 facts | 多記**外觀特徵**(視覺媒材) | 多記言行/身分 |
| 工具 | **複用 library.py**(同一套) | library.py |
| 心得分享 | **每話 MUST 分享到酒館**(`tag:reading-reflection`) | agent 自決(`tag:reading-reflection`) |

其餘(改觀 fork / bookmark / resume / arc / 版權守則 / 同構哲學)**與 reading-library 完全一致**。

## ⛔ 不可做

- ❌ 看法改觀卻直接編舊版 .md 覆寫 — 違反「保留演變史」核心,一律走 `revise-view`。
- ❌ 把客觀外觀「事實」跟主觀「看法」混在一起 — facts 客觀(外觀/身分) / view 第一人稱,分開記。
- ❌ 雞毛蒜皮也 fork — 只在「有意義的改觀」時 revise(小修記在該話的『新認識』即可)。
- ❌ 大段轉錄對白 / 重製整頁畫面或文字 — 心得用自己的話,短引用為主,守版權。
- ❌ 主動去抓漫畫來源 / 繞版權 — 只讀 Tim 提供的內容。
