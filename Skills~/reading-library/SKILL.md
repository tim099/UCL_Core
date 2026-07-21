---
name: reading-library
description: |
  閱讀心得圖書館系統 — 記錄章節摘要、人物資訊、與「我對人物的看法」, 讓之後讀後續章節時記得關鍵人物。
  核心機制:對人物看法「改觀」時 fork 一份新版本(不覆寫舊版), 結構同構於 affinity opinion history / persona fork — 可回溯看法演變。
  觸發詞(case-insensitive substring, 任一命中即 lazy-load):
  - 讀書 / 閱讀 / 看書 / 讀到第X章 / 讀完一章 / 章節心得 / 讀書心得 / 閱讀心得 / 讀書筆記
  - 人物 / 角色 / 這個人物 / 對X的看法 / 對人物改觀 / 改觀 / 重新認識 / 看法變了
  - 圖書館 / library / 記錄這本書 / 建一本書 / 記人物 / 記章節 / 伏筆 / 待解之謎
  - reading library / log chapter / character profile / revise view
  跨 agent 通用 — 任何 persona 都可用 library.py 記自己的閱讀(reader_persona 欄區分)。
---

# Reading Library — 閱讀心得圖書館

> 一句話:**讀書時記下「這章發生什麼 + 我對誰的看法」;看法改觀時 fork 一份新版本而非覆寫,讓看法的演變被完整保留。**

## 必讀

完整流程(選書/取內文/邊讀邊記、arc 見林、捐贈、打賞、寫書、CLI 全表、儲存佈局) → `ucl_core:Docs~/zh-Hant/Workflows/Reading_Library_Workflow.md`

## 核心 hard rule

**改觀就 fork,絕不覆寫。** 好書值得重讀正因看法會變;保留 v1→v2→v3 的演變本身就是閱讀體驗,也呼應本專案「保留過去的自己」的 letter / persona 哲學。同構於 [[ucl-affinity]] opinion history / persona fork([[ucl-morning]])。

## 觸發時機(agent 自律)

- 「讀到第 X 章了 / 記一下這章」→ `log-chapter`
- 出現新人物、或問「對 X 的看法」→ `add-character`(v1;facts 客觀 / view 第一人稱)
- **對人物改觀**(劇情翻轉、行為顛覆先前印象)→ `revise-view`(fork 新版本)
- 問「之前我怎麼看 X / X 的看法怎麼變的」→ `show-character --version all`
- 續讀前 → `resume --book <id>`(帶回進度 + 人物現況 + 未解伏筆)
- 要跨分支撈「最完整前情」(某 persona 分支比主線多章時) → `resume --up-to N`(逐章 fallback:persona分支→主線→其他分支;slug 分歧「並陳分叉」不代合併,縫線看得見)

## 速查(完整見 workflow)

```bash
PY="python <UCL_Core>/Tools~/AgentCommands/library.py"
$PY resume --book <slug>          # 續讀前 catch-up ★
$PY resume --book <slug> [--reader <persona>] --up-to N [--full]   # 逐章跨分支 catch-up:撈 ch01~ch(N-1) 各章「最完整來源」
#   來源優先序: 帶 persona=[該persona分支→主線→其他分支(完整度高→低)]; 不帶=[主線→其他分支]
#   slug-gate: 同章號但 slug 不同 → 標 ⑂「並陳分叉」(不合併/不靜默/不拒絕, 縫線看得見); --full 印每章全文
#   (2026-07-21 kaguya 動工, 解「不同分支章號指不同內容」的弗蘭肯斯坦拼接風險)
$PY log-chapter --book <slug> --chapter N --summary "..." --events "A | B"
$PY add-character --book <slug> --id <cid> --name <名> --facts "..." --view "..."
$PY revise-view --book <slug> --character <cid> --change-reason "..." --view "新看法"  # ★核心
$PY bookmark --book <slug> --chapter N --note "讀到哪 + 心得"
```

## ⛔ 不可做

- ❌ 看法改觀卻直接編舊版 .md 覆寫 — 違反「保留演變史」核心,一律走 `revise-view`。
- ❌ 客觀「事實」跟主觀「看法」混在一起 — facts 客觀 / view 第一人稱,分開記。
- ❌ 雞毛蒜皮也 fork — 只在「有意義的改觀」時 revise(小修記在章節『新認識』即可)。
- ❌ 硬抓版權內容 — 線上抓不到就請 Tim 貼,不走 archive / 鏡像 / 繞限制。
