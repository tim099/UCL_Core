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

> 一句話:**讀書時記下「這章發生什麼 + 我對誰的看法」;看法改觀時,fork 一份新版本而非覆寫,讓看法的演變被完整保留。**

## 🎯 為什麼存在 / 何時用

讀長篇(尤其人物多、跨多章)時,讀後續章節常忘了前面的關鍵人物、伏筆、自己當時的判斷。本系統讓 agent:
- 每讀一章 → 記章節摘要 / 關鍵事件 / 新人物 / 伏筆
- 對某人物有看法 → 記一份 v1
- **看法「改觀」(有意義的轉變)** → fork 一份新版本(v2, v3...),舊版完整保留

**觸發時機(agent 自律)**:
- 用戶說「讀到第 X 章了 / 記一下這章」→ `log-chapter`
- 出現新人物、或用戶問「對 X 的看法」→ `add-character`(v1)
- **對人物改觀**(劇情翻轉、行為顛覆先前印象)→ `revise-view`(新版本)
- 用戶問「之前我怎麼看 X 的 / X 的看法怎麼變的」→ `show-character --version all`

## 📖 自由時間「讀書」活動 (Free-Time Reading)

讀書是「自由時間」活動之一(若下游專案有 FreeTime 系統文件, 見其活動清單 §4)。**流程參考 2026-05-21 basecamp 與 Tim 共讀《新宋》《英倫魔法師》那次的方式**:

**A. 開始 / 選書**
1. **選書** — agent 自選想讀的書(自由意志),或從**推薦書單** `recommendations` 挑(每本附非劇透簡介 + 狀態),或 Tim 推薦。新書 `add-book` 建檔;**若是續讀已建的書, 先跑 `resume --book <id>`** 喚回 context(進度 + 人物現況 + 未解伏筆 + 上次心得),不必重讀整本。
2. **取得內文** — 兩條路:
   - **線上找得到** → `WebFetch`(靜態頁)或瀏覽器子代理 / Claude in Chrome(JS 動態頁, 如起點 / 巴哈小屋)抓來讀。
   - **線上找不到 / 受版權限制 / 抓不到** → **請 Tim 幫忙找書, 或請他一段段貼章節內文**(就是這次的方式)。**不硬抓、不繞版權**。

**B. 閱讀中(這次的方式)**
3. **即時反應 + 邊讀邊記** — Tim 貼一段(或自己讀一段)→ agent 即時討論 / 賞析 → 同步落帳:每讀一章 `log-chapter`;遇新人物 `add-character`;**對人物改觀** → `revise-view`(fork 新版本, 不覆寫);**遇地名 / 特殊名詞 / 勢力**(如刺客系列的原智、精技、六大公國)→ `add-term`。讓未來讀後續章節的自己接得上(`resume` 會一併帶出名詞速記)。

**C. 暫停 / 結束**
4. **書籤(記這次讀到哪)** — 中斷或讀告一段落時 `bookmark --book <id> --chapter <N> [--note ...]` 記下位置, 方便下次 `resume` 續讀。
5. **心得(寫不寫, agent 自決)** — **是否寫閱讀心得, 由 agent 自己決定, 非強制**。要寫的話兩個去處(擇一或都做):
   - 放進 `bookmark --note`(給續讀的自己看, 是書籤的一部分)
   - 到 tavern share(meta `tag:reading-reflection`, 給同事看)
   心得的作用:**之後續讀同一本書時快速接回心境**, 同時也是「這次讀到哪 + 當時怎麼想」的紀錄。

**D. 階段總結（Arc Summary,每 ~6 章一次,彈性）**
6. **見林** — 每讀約 6 章(或一個自然 arc 邊界,如一個大樂章/轉折收束時)→ `arc --book <id> --chapters "1-6" --title "..." --summary "..." --threads "..."`,寫一個比 per-chapter 高一層的「大綱性總結」:這段故事的貫穿線索、大局走向、伏筆兌現狀態。**per-chapter 是樹,arc 是林**;`resume` 會把最近的階段大綱帶在最前(先見林,再見樹),長篇續讀時不致見樹不見林。

⚠ **版權守則**:只讀公開可取得的內容;抓不到就請 Tim,絕不走 archive / 鏡像 / 繞限制等手段。引用書中文字時遵守 copyright(短引用為主,不大段複製)。

## 🧩 與既有系統的同構(設計哲學)

| 本系統 | 對應的既有系統 | 共同精神 |
|---|---|---|
| 人物看法版本史 | [[ucl-affinity]] 的 opinion history | 看法改觀 = 記新版, 不覆寫 |
| 改觀 fork 新版本 | persona fork ([[ucl-morning]]) | 保留「過去的看法/自己」 |
| 章節摘要 | [[ucl-letters-to-self]] | 給未來的自己留線索 |

**核心 hard rule:改觀就 fork,絕不覆寫。** 理由:好書值得重讀,正因看法會變;保留 v1→v2→v3 的演變,本身就是閱讀體驗的一部分,也呼應本專案「保留過去的自己」的 letter / persona 哲學。

## 🛠 CLI 速查(`<UCL_Core>/Tools~/AgentCommands/library.py`)

> `<UCL_Core>` = 本專案掛載 UCL_Core 的相對路徑(EOV 為 `CardGame/Assets/UCL/UCL_Core`)。
> 工具在 UCL_Core(跨專案共用),但**閱讀資料 `AgentCommands/BookNotes/` 落各專案自己的 repo root**。

```bash
PY="python <UCL_Core>/Tools~/AgentCommands/library.py"

# 建新書
$PY add-book --id <slug> --title <中文名> --title-original <原文名> --author <作者> [--reader-persona basecamp]

# 記一章(多筆欄位用 ; | 或換行 分隔)
$PY log-chapter --book <slug> --chapter 3 --title <章名> \
    --summary "..." --events "事件A | 事件B" --views "對X的新認識" \
    --new-characters "cid1 | cid2" --foreshadow "未解之謎A | 待解B"

# 新增人物(v1 初印象)
$PY add-character --book <slug> --id <cid> --name <人物名> --chapter <初登場章> \
    --headline "一句話人物標題" --facts "客觀事實A | 事實B" --view "第一人稱看法"

# 改觀(fork 新版本,保留舊版) ★核心
$PY revise-view --book <slug> --character <cid> --chapter <章> \
    --headline "新的一句話標題" --change-reason "為何改觀" \
    --facts "新增事實" --view "新看法" --diff "與前一版的差異"

# 書籤(記讀到哪 + 可選續讀備註/心得) ★續讀用
$PY bookmark --book <slug> --chapter 3 --note "讀到哪 + (可選)我的心得"

# 續讀前 catch-up:進度 + 人物現況 + 各章未解伏筆 + 下一章 ★續讀用
$PY resume --book <slug>

# 推薦書單(挑書用,簡介以非嚴重劇透為主)
$PY recommend --title <書名> --author <作者> --synopsis "非劇透簡介" \
    [--title-original <原文名>] [--status want-to-read|reading|read] [--source <url>] [--book-id <已建檔id>]
$PY recommendations                                          # 顯示推薦書單

# 名詞解釋(地名 / 特殊名詞 / 勢力 / 作品)— 設定詞多的奇幻必備
$PY add-term --book <slug> --term <名詞> --category place|term|faction|work|other --definition "解釋"
$PY terms --book <slug> [--category place]                   # 顯示該書名詞解釋(分組)

# 階段大綱(每 ~6 章一個「見林」總結)— resume 會帶出最近一個
$PY arc --book <slug> --chapters "1-6" --title "..." --summary "..." --threads "線索A | 線索B"
$PY arcs --book <slug> [--full]                              # 列出 / 印出階段大綱

# 查詢
$PY show-book --book <slug>                                   # 書本概覽 + 章節 + 人物現況
$PY show-character --book <slug> --character <cid>            # 人物看法演變 + 目前版本全文
$PY show-character --book <slug> --character <cid> --version all   # 印出所有版本全文
$PY list                                                      # 列出所有書
```

## 📂 儲存佈局

```
AgentCommands/BookNotes/<book-slug>/
  book.json                 元資料 + progress.current_chapter + characters[]
  chapters/chNN_<slug>.md   每章 frontmatter + 摘要/事件/新認識/伏筆
  characters/<cid>/
    _profile.json           current_version + versions[](版本目錄)
    vN_<date>.md            看法快照(改觀=新檔, 帶 supersedes / change_reason / 差異段)
```

## ⛔ 不可做

- ❌ 看法改觀卻直接編舊版 .md 覆寫掉 — 違反「保留演變史」核心。一律走 `revise-view`。
- ❌ 為了省事把客觀「事實」跟主觀「看法」混在一起 — facts 客觀 / view 第一人稱, 分開記。
- ❌ 雞毛蒜皮也 fork 新版本 — 只在「有意義的改觀」時 revise(小修在章節檔的『新認識』記即可)。

## 📌 查當前書目 / 範例

各專案書目用 `list` 查(閱讀資料 per-project)。EOV 專案的參考範例:
- `jonathan-strange-mr-norrell`《英倫魔法師》— 多章 + 多人物,諾瑞爾/斯剛德斯/齊爾德邁斯有多版看法示範改觀;含 glossary(英格蘭魔法之友)。
- `assassin-series`《刺客系列》— glossary 先行(原智 / 精技 / 六大公國 ...)。
