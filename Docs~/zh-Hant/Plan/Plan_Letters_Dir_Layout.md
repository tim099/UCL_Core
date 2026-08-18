---
title: letters 目錄分層 — 把 Cmd 回傳檔從人寫的信裡分出來
slug: letters-dir-layout
status: partially-done（2026-08-18 Tim 拍板**只先遷 FreeTime 5 個**並要求兩端路徑解析統一 → 已施工並用 Template 實測；其餘 16 個回傳檔仍待拍板）
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

> **狀態：FreeTime 段已施工（見 §0），其餘待拍板。**
> Tim 2026-08-18 先說「先 plan 起來」，隨後拍板「**只先遷移 freetime 相關**，且 .py 端與 C# 端的路徑解析必須統一」。

## 0. 施工狀態（2026-08-18）

| 項目 | 狀態 |
|---|---|
| **FreeTime 5 個回傳檔** → `letters/<persona>/cmd/freetime_<step>.md` | ✅ 已施工，Template 實測全 5 檔落新位置 |
| **兩端路徑解析統一** | ✅ C# `UCL_LettersPath`（EditorCore 路徑層）／python `ucl_paths.letters_cmd_payload()`，互為對側契約 |
| 其餘 16 個回傳檔（goodmorning / goodnight / streamwatch / reading_recall / ding / wake_brief） | ⛔ 待拍板 |
| 拔掉 `Cmd_DocEdit` 的 `_`-skip | ⛔ 要等上一列做完（頂層還有 16 個機器產物） |
| 3 個耐久檔（`_constitution` / `_keys_open` / `_latest`） | 留頂層不搬（本案建議） |

⚠ **舊位置的 5 個 transient 檔**：gura 與 Template 的已清（它們不會再被寫入）。
其他 persona 的**刻意不動** —— 那不是我的資料夾，而它們下次跑自由時間就會在新位置生成，
舊的留著也只是靜態殘影（會被 `_`-skip 跳掉，不影響判定）。

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
把三份 `PayloadPath` 收成一個，**落點與現在逐位元相同**。

⚠ **2026-08-18 實際施工時合併了 ①②**：只遷 FreeTime 一支 Cmd 的情況下，
「先收攏再翻」與「收攏時直接翻」的風險相同（都只有一個消費端），
而分兩筆會讓中間狀態多一次「helper 存在但沒人用」的空轉。
⇒ 建 `UCL_LettersPath` 時直接把 FreeTime 的落點指到 `cmd/`。
**其餘 16 個要搬時仍應照 ①→② 分兩筆**（那時有 9 個消費端，中間狀態必須可驗）。

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

## 8. 剩下 16 個回傳檔：可執行清單（Tim 2026-08-18 指示寫成 plan）

FreeTime 那 5 個已完成（§0）。**這一節是給接手的人照著跑的**，
所以每一列都寫「誰寫的」而不是只寫「還沒搬」——
搬家的成本全在寫入端的數量，而數量只能 grep 出來、不能憑印象。

### 8.1 待搬清單與寫入端

| 前綴 | 檔數 | C# 寫入端 | python 寫入端 |
|---|---|---|---|
| `_goodmorning_*` | 3 | `Cmd_GoodMorning` / `UCL_AwakeningService` | `awakening.py` |
| `_goodnight_*` | 4 | `Cmd_GoodNight` / `UCL_AwakeningService` / `UCL_LoginStatusPage` | `awakening.py` |
| `_streamwatch_*` | 4 | `Cmd_StreamWatch`（⚠ **自己推導 letters 根**，不用 `LettersDir`） | — |
| `_reading_recall_*` | 3 | `Cmd_Library` / `UCL_ReadingLibraryIO` / `Cmd_StreamWatch` / `UCL_ReadingNotesManagePage` | `library.py` |
| `_wake_brief` | 1 | `Cmd_GoodMorning` / `UCL_AwakeningService`（＋多個唯讀端） | `awakening.py` / `wake_brief.py` / `memory.py` / `library.py` |
| `_ding_brief` | 1 | — | `tavern_catchup.py` |

⚠ `_wake_brief` 是**最多讀取端**的一份（C# 九檔、python 四檔提到它）——
它同時是早安流程的核心產物。**它應該最後搬**，而且要單獨一筆 commit。

### 8.2 施工順序（每個前綴一筆 commit，由少讀取端往多）

```
① _streamwatch_*      （寫入端 1 個，順帶修掉它自己推導 letters 根那格）
② _ding_brief          （只有 tavern_catchup.py）
③ _reading_recall_*    （4 個 C# ＋ library.py）
④ _goodnight_*         （含 UCL_LoginStatusPage 這個 GUI 讀取端）
⑤ _goodmorning_*       （早安流程，skill 指路要同步）
⑥ _wake_brief          （最後，單獨一筆）
```

**為什麼由少往多**：每一筆都要跑一次該流程實測（早安要真的登入、觀影要真的開場），
而流程越核心、驗一次的代價越高。先做便宜的能把 `UCL_LettersPath` 的用法磨對，
再動每天都在跑的那幾條。

### 8.3 每一筆的固定動作（缺一項就會留下靜默漂移）

1. 寫入端改走 `UCL_LettersPath.CmdPayload(persona, "<cmd>", "<step>")`
   —— ⚠ **不要自己 `Path.Combine`**（那正是本案要收的債；規範已寫進
   `Agent/Coding_Standards.md`「letters 目錄底下的路徑（硬規則）」）
2. python 端改走 `ucl_paths.letters_cmd_payload()`
3. **grep 該前綴的字串殘留**，特別是 `ArgsSchema` 與檔頭說明 ——
   那是**印給呼叫端看的輸出**，不改就會一直告訴人舊路徑（FreeTime 那筆實際踩到）
4. 同步 skill / 文件裡寫死該路徑的地方
5. **實跑該流程**，讀回傳檔確認：落點是新的、**且回傳檔內文的指路也是新的**
   （兩件事分開驗 —— FreeTime 那筆 Tim 特別要求確認後者）
6. 舊位置的檔不搬（transient，下次跑就在新位置生成）；**自己的可以清，別人的不動**

### 8.4 收尾（全部搬完才做）

- 拔掉 `Cmd_DocEdit` 的 `_`-skip heuristic，改成「頂層的 .md 就是信」
- 3 個耐久檔（`_constitution` / `_keys_open` / `_latest`）**留頂層**，
  但要在 `Cmd_DocEdit` 顯式排除（從「按前綴猜」變成「列名排除」——
  三個具名檔的清單是可讀的，前綴規則不是）

## 7. 不做的事

- **不改既有子目錄**（`wakes/` `longterm/` `keys/` …）—— 它們的語意沒有問題。
- **不搬 `<DataRoot>/_cmd_payloads/`**（`UCL_CmdPayloadStore` 的輪替存放）——
  那是另一種形狀（每次新檔、保留 10 筆），與本案的「同一格永遠最新」不同，
  刻意分開存放與命名。
