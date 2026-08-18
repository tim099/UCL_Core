---
title: letters 目錄分層 — 把 Cmd 回傳檔從人寫的信裡分出來
slug: letters-dir-layout
status: draft（2026-08-18 gura 提案；Tim 說「先 plan 起來」，**未施工**）
created_at: 2026-08-18T07:45:00Z
created_by: gura
location: UCL_Core (cross-project)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_DocEdit.md | Cmd_DocEdit | 本案的導火線（letter 自動解析抓到 Cmd 回傳檔）
  - ucl_core:Docs~/{lang}/Plan/Plan_FreeTime_Cmd.md | 自由時間 Cmd 化 | 回傳檔慣例的來源
  - repo:docs/Glossary/one-symbol-two-duties.md | 一符二役 | `_` 前綴同時代表兩種東西
---

# letters 目錄分層 — 把 Cmd 回傳檔從人寫的信裡分出來

> **狀態：草案，未施工。** Tim 2026-08-18「cmd 資料夾我認為也可以處理，可以先 plan 起來」。

## 1. 導火線（實測，不是推測）

`Cmd_DocEdit`（2026-08-18 新增）在 `kind=letter` 沒給 `target` 時要「取最新那封信」。
第一版取「letters 頂層最新的 `.md`」，**實跑立刻解析到 `_freetime_next.md`** —— 那是 Cmd 回傳檔，不是信。

當下的修法是**跳過 `_` 開頭與 `README.md`**。它有效，但那是 heuristic：
它依賴「機器產物都用 `_` 開頭」這條慣例，而慣例沒有任何地方在強制執行。

## 2. 量到的現況（gura 的 letters 頂層，2026-08-18）

| 分類 | 數量 |
|---|---|
| 人寫的信（時間戳命名） | **29** |
| `_` 開頭的機器產物 | **24** |

而那 24 個**要再分兩種**，這是本案的核心發現：

| 類型 | 檔案 | 數量 | 性質 |
|---|---|---|---|
| **Cmd 回傳檔**（transient） | `_freetime_*`(5)、`_streamwatch_*`(4)、`_goodnight_*`(4)、`_goodmorning_*`(3)、`_reading_recall_*`(3)、`_ding_brief`、`_wake_brief` | **21** | 每跑一次就重生，刪掉沒差 |
| **耐久產物**（只是剛好也 `_` 開頭） | `_constitution.md`、`_keys_open.md`（見叢）、`_latest.md`（指針） | **3** | **刪掉就沒了** |

⇒ **`_` 前綴目前是「一符二役」**：既表示「機器寫的暫存」，又表示「機器維護的耐久檔」。
今天那個 `_`-skip 之所以沒出事，是因為它把 3 個耐久檔也跳掉了 —— 而它們剛好都不是信。
**那是運氣，不是設計。**

## 3. 提案：`letters/<persona>/cmd/`

把 **21 個 transient 回傳檔**搬進 `cmd/`，檔名可以順勢拔掉 `_` 前綴
（目錄本身已經說了它是什麼）：

```
letters/gura/
  20260817T144800Z_wake36_freetime_whiskey_reflection.md   ← 人寫的信，留在頂層
  _constitution.md                                          ← 耐久，留在頂層
  _keys_open.md  _latest.md                                 ← 耐久，留在頂層
  cmd/
    freetime_next.md  freetime_start.md  freetime_activity.md …
    goodmorning_wake.md  goodnight_check.md  streamwatch_cycle.md …
  wakes/  longterm/  keys/  bookshelf/  essays/ …            ← 既有子目錄不動
```

### 為什麼叫 `cmd`

比對既有兄弟目錄（`bookshelf` / `essays` / `keys` / `longterm` / `mailbox` / `mbti` /
`portraits` / `sketchbook` / `tools` / `wakes`）：**全小寫、無底線、集合名詞**。

| 候選 | 判斷 |
|---|---|
| **`cmd`** ✅ | 短、無歧義（裡面全是 Cmd 回傳檔）；與 `tools/` 同形 |
| `payloads` | ⛔ 會跟 `<DataRoot>/_cmd_payloads/`（**輪替保留 10 筆**）撞語意。同一個詞兩種形狀＝再演一次一符二役 |
| `reports` | ⛔ `AgentCommands/BugReports/reports/` 已是別的東西（bug 單） |
| `returns` | 直譯「回傳檔」但英文語意模糊（回報率？退貨？） |
| `steps` | 對 `freetime_next` / `goodmorning_wake` 準，對 `wake_brief` / `ding_brief` 不準 |

### 真正的好處不是整齊

搬完之後 letters 頂層**只剩人寫的信**（＋3 個耐久檔）⇒
`Cmd_DocEdit` 的 `_`-skip **可以拔掉**。

判準從「讀取端按前綴猜」變成「兩種東西住在不同地方」——
與 2026-08-18 造的 [`一符二役`](../../../../docs/Glossary/one-symbol-two-duties.md) 同一條手勢：
**不要為同一個位置寫「什麼算 A、什麼算 B」的規則，把兩個身分分到兩層。**

## 4. 成本（grep 出來的）

| 端 | 數量 | 內容 |
|---|---|---|
| **C# 寫入/讀取** | 12 檔 | `Cmd_GoodMorning` / `Cmd_GoodNight` / `Cmd_FreeTime` / `Cmd_StreamWatch` / `Cmd_Library` / `UCL_AwakeningService` / `UCL_ReadingLibraryIO` / `UCL_LoginStatusPage` / `UCL_ReadingNotesManagePage` / `UCL_AgentCommandRunner` ／本案新增兩支 |
| **python** | 8 檔 | `awakening.py` / `wake_brief.py` / `tavern_catchup.py` / `library.py` / `memory.py` / `persona_resolve.py` / `tavern_cmd.py` / `migrate_persona_binding.py` |
| **skill / 文件印著這些路徑** | **18 檔** | 會變成死指路；而那裡面有早安／晚安／觀影等**每天在跑的流程** |

⚠ 而那 12 個 C# 端**各自算路徑**（`Cmd_FreeTime.PayloadPath` / `Cmd_Sculpture.PayloadPath` /
`Cmd_StreamWatch.PayloadPath` 三份，其中 StreamWatch 還自己推導 letters 目錄而不用
`UCL_AwakeningService.LettersDir`）。

⇒ **直接翻目錄 = 12 處各改一次**，而漏掉一處的症狀是「那支 Cmd 的回傳檔還在舊位置」，
**不會報錯**（寫檔會自動建目錄）。

## 5. 施工順序（三筆 commit，順序不可換）

### ① 收攏路徑 helper（不改行為）
把三份 `PayloadPath` 收成一個（例 `UCL_LetterPayloadPath.For(persona, cmd, step)`），
**落點與現在逐位元相同**。這一筆本身就是今天已記下的技術債，獨立有價值。
驗收：所有 Cmd 跑一輪，回傳檔路徑與改動前逐字相同。

### ② 翻目錄（改一行）
helper 內把落點改成 `cmd/` 子目錄、檔名去 `_`。
同步 8 個 python 端與 18 份 skill/文件的指路。
**舊的 transient 檔直接留著等自然淘汰**（它們不會再被寫入；下次跑就在新位置生成）——
不做搬移，因為搬移沒有價值而且會讓「舊位置還有東西」持續一段時間。

⚠ **3 個耐久檔（`_constitution` / `_keys_open` / `_latest`）留在頂層不動。**
它們不是回傳檔，搬進 `cmd/` 會讓「Cmd 寫的暫存」這個語意再度被稀釋。

### ③ 拔掉 heuristic
移除 `Cmd_DocEdit` 的 `_`-skip，改成「頂層的 .md 就是信」（3 個耐久檔仍需個別排除 ——
或考慮把它們也各自歸位，那是另一個題目）。

## 6. 未決（要 Tim 拍板才動）

1. **範圍**：全 21 個一起搬，還是只搬 FreeTime 的 5 個？
   （只搬 5 個＝問題沒解、頂層還有 16 個，而且多一個「為何只有 freetime 在資料夾裡」的不一致）
2. **自由時間寫的信要不要也分資料夾？** 目前它們混在頂層、靠檔名尾綴自述：
   `20260814T023200Z_freetime_reflection.md` / `20260817T144800Z_wake36_freetime_whiskey_reflection.md`。
   選項：(a) 不動（信就是信，時間戳排序已足夠）(b) `letters/<persona>/freetime/`。
   **gura 傾向 (a)** —— 那些是信，而信按時間讀最自然；再分類會讓「該去哪找那封信」多一個判斷。
   本案要解的是「機器產物混在信裡」，不是「信不夠分類」。
3. **3 個耐久檔的長期歸屬**：留頂層，或各自進對應目錄（`_keys_open` → `keys/`？）。
   本案建議**留頂層**，理由是它們是「這個 persona 的當前狀態」，而頂層正是找它們的地方。

## 7. 不做的事

- **不改既有子目錄**（`wakes/` `longterm/` `keys/` …）—— 它們的語意沒有問題。
- **不搬 `<DataRoot>/_cmd_payloads/`**（`UCL_CmdPayloadStore` 的輪替存放）——
  那是另一種形狀（每次新檔、保留 10 筆），與本案的「同一格永遠最新」不同，
  刻意分開存放與命名。
