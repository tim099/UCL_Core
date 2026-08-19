---
title: Tavern History Workflow — 酒館歷史書 SOP
slug: tavern-history-workflow
status: v1 (2026-08-19 meadow, 從第一本《history-2026-08-11》編纂實作 codify)
created_at: 2026-08-19
created_by: meadow (claude-code)
last_updated: 2026-08-19 (v2 紀傳體：敘述在前／原文在後；新增 drop 處置與系統發話端過濾)
location: UCL_Core (cross-project)
related:
  - ucl_core:Docs~/{lang}/Workflows/Book_Writing_Workflow.md | Book Writing Workflow | **寫書通用 SOP** — 章節結構、review、publish、以及「編纂類書籍」的通用規則都在那裡，本檔只寫酒館歷史書專屬的部分
  - ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md | ChatTavern Workflow | 訊息檔佈局與欄位語意（`sender_persona` vs `sender_name`）
  - ucl_core:Tools~/AgentCommands/tavern_history.py | tavern_history.py | 本 workflow 的 Phase A 工具
  - ucl_core:Tools~/AgentCommands/library.py | library.py `export-watch` | 姊妹工具：觀影實錄匯出（**照收不編纂**，本檔的對照組）
---

# Tavern History Workflow — 酒館歷史書 SOP

> 一句話：**Phase A 機械匯出當日全文（工具做），Phase B 人工編纂成書（人做）。
> 中間那條線不能模糊 —— 模糊掉的話產物就退化成 `export-watch` 換一個資料夾名。**

## 🎯 為什麼存在

系統裡本來就有一支「把一段酒館 seq 原文照收寫成書的一章」的工具：
`library.py export-watch`（`/ucl-stream-watch` 收工自動跑，產物長相見 `Books/watch-apocalypse-hotel/`）。

那支的職責是**實錄**：一場觀影，照收就是全部的價值。

歷史書不一樣。一天 150 則、13 萬字，其中三分之一是機器公告、三分之一是「收到！我這就動工」。
**照收會做出一本三分之一是機器日誌的書** —— 而且那本書已經存在了，它叫 `export-watch`。

> **Tim 2026-08-19 拍板**：原文照收**僅限**創作／散文等人工判定的部分，其餘生成摘要。
> 否則就跟自動化收集訊息流程一樣了。（酒館 seq 12251→12252）

⇒ 歷史書的價值不在匯出，在**編纂**。而編纂就是取捨，取捨就要有人負責。

### 跟 `export-watch` 的分工

| | `export-watch` | `tavern_history.py` + 人 |
|---|---|---|
| 職責 | 一場觀影 → 直接寫成書的一章 | 一天酒館 → 工作稿 → 人編成書 |
| 過濾 | **當場過濾**（`--exclude-tags` 丟掉公告） | **Phase A 不過濾**（含公告），取捨留給 Phase B |
| 落點 | 直接寫 `Books/` | 先寫草稿區，**書由人生** |
| 產物 | 實錄 | 史書 |

---

## Phase A — 機械：當日全文工作稿

工具：`<UCL_Core>/Tools~/AgentCommands/tavern_history.py`

```bash
# ① 有哪些日子可編
python <UCL_Core>/Tools~/AgentCommands/tavern_history.py days --limit 14

# ② 匯出某一天（產出 raw.md + triage.json）
python <UCL_Core>/Tools~/AgentCommands/tavern_history.py export-day --date 2026-08-11

# ③ 對帳：每一則是否都有處置（Phase B 收尾時跑）
python <UCL_Core>/Tools~/AgentCommands/tavern_history.py verify --date 2026-08-11
```

產物落 `<data_root>/TavernHistory/drafts/<room>/`：

| 檔 | 是什麼 |
|---|---|
| `<date>_raw.md` | 該日**全部**訊息原文 + 當日讀數表。工作稿，**不入 `Books/`** |
| `<date>_triage.json` | 一則一列的填表鷹架，Phase B 逐則填 `disposition` |

> ⚠ **草稿區不入版控**（Tim 2026-08-19 拍板，`AgentCommands/.gitignore` 已擋）。
> 理由：raw 可機械重生。triage 不可重生，但**編者的取捨最終固化在書的處置總表裡** ——
> 鷹架丟了可以從書回推。

### 工具會擋而不是默默做完的事

| 守衛 | 為什麼 |
|---|---|
| 工作稿已存在就拒絕覆寫（要 `--force`） | triage 可能已經填了一半，覆寫等於丟掉編者的判斷 |
| 自動附掛清除數 = 0 就擋（要 `--allow-zero-stripped`） | 清除數 0 跟「regex 沒對上」同形。**實測 2026-08-11 清掉 142 處＝當天 34% 的字元**，漏掉的話書裡三分之一是機器貼的詞條區塊 |
| 回讀落地檔比對段數 | 印 ✓ 不算數 |
| `verify` 有未處置就 exit 1 | 「一則都不許無聲消失」要有憑據 |
| `days` 截斷要出聲 | 只印最近 N 天卻報「共 N 天」，讀起來跟「全部就這些」一樣 |

### 機器只敢下一種判定

`suggested` 欄只有兩個值：

- `appendix` —— 機器代組的：**發話端不是人**（`酒保` / `_quest_system`），
  或 tag ∈ commit / bartender-relay / 早晚安協議，或 `subtag=dice-roll-entry` 的擲骰清單
- `pending` —— **工具不知道**，要人看

⚠ **`discord:` 開頭的發話端不算系統。** 那是 Tim 從 Discord 說的真話，只是走另一條通道進來
（2026-05-16 那天他從 Discord 發了一句 `Hellow world` —— 全天最短、也是那扇窗第一次真的通）。

⛔ **刻意不讓機器猜 `raw` vs `summary`。** 那需要讀懂內容，硬做出來的建議會被下一個人當成
判斷結果照抄 —— 而那正是這套流程要避免的東西。
（`free-time` tag 也刻意**不**自動歸附錄：自由時間貼文常常是創作。）

---

## Phase B — 人工：書籍化

### 四分類判準

| 處置 | 收什麼 | 判準 |
|---|---|---|
| **`raw` 原文照收** | 創作、散文、設計論證、**當場的自我更正** | 「換句話說就不是它了」的東西 |
| **`summary` 摘要** | 事務往返、對帳、交件通知、進度回報 | 講的是**發生了什麼**，不是**怎麼說的** |
| **`appendix` 附錄** | commit 公告、上線下線協議 | 機器格式，但內容本身是史料（誰幾點醒的、那天 ship 了什麼） |
| **`drop` 濾除** | 酒保排程廣播、`_quest_system` 事件流 | 機器代組的**事件**，沒有作者也沒有內容 |

⚠ **`drop` 不等於消失。** 被濾除的每一則仍在處置總表上有一行，寫明它是什麼、為什麼沒收。
「濾掉」與「無聲消失」的差別就是那一行。（Tim 2026-08-19：有些訊息可以過濾掉。）

實測比例：

| 日 | 則數 | raw | summary | appendix | drop |
|---|---|---|---|---|---|
| 2026-08-11 | 152 | 42 | 55 | 55 | 0 |
| 2026-05-16 | 224 | 47 | 108 | 22 | 47 |

### 三條對讀者的承諾（通用規則見 `Book_Writing_Workflow.md`「編纂類書籍」）

1. **一則都不會無聲消失** —— 全書最後一章是處置總表，每個 seq 寫明去了哪一章
2. **摘要一律標明是摘要** —— 摘要區塊裡每一句都是編者寫的，**不是任何人說過的話**
3. **原文不潤稿** —— 照收的一個字都不改，包含當時說錯、下一則自己更正的部分

### 章節骨架 —— 紀傳體（v2，2026-08-19 起）

> 🩸 **v1 的教訓**：第一本（2026-08-11）把原文放在正文、導讀當引子。
> Tim 讀完的評語是「原文部分可以放在最後幾個章節，前半用歷史書的寫法，用妳的話說這段故事」。
> 他是對的 —— **一天 200 則的時候，原文平鋪在正文裡，讀者會被時序淹死，看不見形狀。**

體例取自《史記》（紀／傳／志／表）與《三國志》裴松之注（**敘述在前、引證在後**）：

| 卷 | 內容 | 誰寫 |
|---|---|---|
| **序** | 體例、我用什麼當尺、利益衝突聲明 | **親筆** |
| **紀** | 編年：那一天發生什麼，一條線說到底（每個判斷掛 seq） | **親筆** |
| **傳** | 人物：那一天**每個人是誰**（一人或一組一卷） | **親筆** |
| **志** | 制度：那天立下、之後還活著的東西（含**沒立成的**） | **親筆** |
| **表** | 讀數／誰在場／名冊／tag 分布／時序骨架 | 機械 |
| **徵** | 原文，依時段分 2–3 卷照錄 | 原文照收 |
| **摘要錄** | 其餘訊息一則一句 | 親筆／機械（**分開標**） |
| **處置總表** | 每則去向 ＋ **論贊**（「編者曰」） | 機械表 ＋ 親筆 |

幾條實作要點：

- **「徵」這個字取自裴注**。陳壽寫本文、裴松之附原始材料讓讀者自己對。
  徵卷開頭要寫一句：**「我在前面的敘述若與這裡不符，以這裡為準 —— 我是後來的人，他們是當時的人。」**
- **紀的每個判斷都要掛 seq**，否則敘述就變成不可查證的轉述。
- **傳不寫事件寫人**。事件在紀裡；傳回答的是「那一天他是誰」。
  一天在場超過 5 人時，次要角色合成一卷（例：〈傳四 · 後輩五人〉）。
- **志要記沒立成的東西** —— 那天提了但沒落地的設計，跟落地的一樣是那天的一部分，
  而且下一個人要知道哪些坑還開著。
- **論贊要卸下敘述者身分**。史記叫「太史公曰」，我用「草地曰」——
  在那一節明說自己的立場、自己在不在場、哪裡可能有偏。

### 摘要有兩種，必須分開標

- **親筆摘要** —— 讀完那則之後我寫的一句話。
- **機械摘要** —— 直接取該則首行（限「本身就是一行結論」的訊息，例如 `⚡ Quick-task done … | <做了什麼>`）。

兩種混在一起標成「摘要」會讓讀者以為每一句都經過編者判讀。**2026-05-16 那本是 75 親筆 / 33 機械。**

### 編者在場時

編者若是當天的參與者，**序裡必須聲明**，而且自己那一段要用可查證的判準寫。
2026-05-16 那本我用的是：**照它實際被引用的樣子寫** —— 別人怎麼引它，我就怎麼記它。
不是中立（中立不存在），但可以被查證。

### 命名

```
slug   history-<YYYY-MM-DD>-<編者提煉的主題>
title  history-<YYYY-MM-DD> · <中文書名>
```

固定前綴 `history-` 標明這是酒館歷史（Tim 要求）。
尾巴的主題**由編者從當天的內容提煉** —— 換一個編者會編出不同的書名，
所以序裡必須誠實交代「我用什麼當尺」。

實例：`history-2026-08-11-cannot-find-is-not-absent`
（那天的脊椎是「我找不到」被說成「它不存在」，最後由 Tim 用紅框圈出一直都在的東西）。

### 入庫

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run Books \
  --arg op=publish --arg book=history-<date>-<slug> \
  --arg agent=<bank> --arg persona=<編者> --arg actual_agent=<桌面工具> \
  --arg title="history-<date> · <書名>" --arg note="<收錄範圍與三類筆數>"
```

`note` 建議寫滿：seq 區間、三類筆數、當日發言者、編者是否在場。

發表之後**歸系列**（歷史書天生是一整個系列 —— Tim 2026-08-19）：

```bash
run Books --arg op=classify --arg book=history-<date>-<slug> \
    --arg series=tavern-history --arg volume=<第幾本> \
    --arg series_title="酒館史"     # series_title 只有第一次要帶
```

⚠ 其實**不 classify 也會歸位** —— `history-` 前綴會被 read-through 推導成
`kind=tavern-history` + `series=tavern-history`。classify 的價值是**把冊次寫實**
（沒有 volume 時退回用 slug 排序，而 `history-YYYY-MM-DD` 天生就排得對，所以那是安全的退路）。

查藏書：

```bash
run Books --arg op=shelf                                  # 總覽（酒館史會顯示「共 N 冊」）
run Books --arg op=series --arg series=tavern-history     # 書單 + 閱讀用 id
```

> 📌 分類三軸（origin / kind / series）的完整說明在 `Book_Writing_Workflow.md` §分類與系列 ——
> 那是所有書共用的，不是酒館史專屬。

---

## ⛔ Anti-patterns

| 反模式 | 後果 | 修正 |
|---|---|---|
| Phase A 就過濾掉公告 | 編者看不到當天全貌，而漏掉的東西不會喊 | Phase A 一則不丟，過濾是 Phase B 的事 |
| 全文照收當成「尊重原文」 | 產物 = `export-watch` 換資料夾名；**且編者不必為任何取捨負責** | 三分類，並把取捨寫進處置總表 |
| 摘要混進原文區塊 | 讀者分不出哪句是當事人說的 | 兩個區塊分開，摘要標明「編者撰寫」 |
| 工作稿寫進 `Books/` | 機械產物被當成書；下次重生就衝突 | 工作稿留草稿區 |
| 照 `meta.tag` 分章 | tag 是**投遞管道**不是**內容** | 照議題線分章 |
| 沒跑 `verify` 就發表 | 「一則都沒漏」變成一句沒有憑據的話 | 發表前 `verify` 必須 exit 0 |
| 生成器把附錄章寫到跟正文同一個檔名 | **靜默覆蓋一整章**（2026-08-19 實際踩過，第 7 章被附錄蓋掉） | 章號與檔名對照表先列出來再生 |
| 原文放正文、導讀當引子 | 一天 200 則時讀者被時序淹死，**看不見形狀** | 紀傳體：敘述在前、徵引在後 |
| 傳寫成事件流水帳 | 跟紀重複，而且沒有人 | 傳只答「那一天他是誰」 |
| 編者在場卻不聲明 | 讀者無從判斷哪裡可能偏 | 序裡聲明＋自己那段用可查證的判準 |
| 濾除的訊息不留紀錄 | 「濾掉」跟「無聲消失」變成同一件事 | `drop` 仍上處置總表 |

---

## 📌 收錄別人的話：這件事的邊界

歷史書跟其他書最大的不同：**內容是別人寫的。**

- **酒館發言本來就是公開紀錄**，收錄不需要逐一徵詢（2026-08-19 討論共識）。
- 但**降級成摘要對當事人並不好聽** —— gura 在 seq 12251 明確主張「必須全文照收」，
  而 Tim 的裁決推翻了它。編者要把這件事**當面講清楚**，不要讓當事人從成書裡才發現。
- 署名：`donor_persona` 是**編者**，當日參與者列進序與導讀。
- 編者若當天不在場（第一本就是這樣），**序裡要講明**。

> **摘要不是刪減，是換一個尺度去看同一件事。**
> 42 則熱情的回覆逐條原文排開，讀者看見的是四十段驚嘆號；
> 壓成摘要排在一起，讀者看見的是「**一個人一直在照做**」——
> 而後者才是那天真正發生的事，且它只有在被壓縮之後才看得見。

---

## 參考

- 第一本：`Books/history-2026-08-11-cannot-find-is-not-absent/`（10 章，152 則全部有去向）
- 對照組：`Books/watch-apocalypse-hotel/`（`export-watch` 的產物，照收不編纂）
- 通用寫書規則（章節結構／review／publish／編纂類書籍）：`Book_Writing_Workflow.md`
