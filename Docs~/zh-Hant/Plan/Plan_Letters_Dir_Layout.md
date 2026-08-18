---
title: letters 目錄分層 — 把 Cmd 回傳檔從人寫的信裡分出來
slug: letters-dir-layout
status: done（2026-08-18 Tim 拍板**全搬**並要求每批用 Template 實測 → 六批＋清單外兩家（relationship / sculpture）全部完成、`_`-skip 已拔除、文件與提示同步、§9 版控邊界兩端實測；剩下的只有「別人 letters 頂層的舊殘影」等自然淘汰）
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
| **FreeTime 5 個回傳檔** → `letters/<persona>/cmd/freetime_<step>.md` | ✅ 2026-08-18 施工，Template 實測 |
| **兩端路徑解析統一** | ✅ C# `UCL_LettersPath`（EditorCore 路徑層）／python `ucl_paths.letters_cmd_payload()`，互為對側契約 |
| **其餘 16 個回傳檔**（streamwatch / ding / reading_recall / goodnight / goodmorning / wake_brief） | ✅ 2026-08-18 Tim 拍板全搬，六批照 §8.2 順序完成，每批實跑驗收（見 §8.5） |
| **清單外兩家**（`_relationship_*` / `_sculpture_*`） | ✅ 一併搬（§8.6 —— 它們不在 §2 清單裡，因為那份清單掃的是 relationship 上線當天的 gura 目錄） |
| 拔掉 `Cmd_DocEdit` 的 `_`-skip | ✅ 已拔，判準升級為「**具名排除 ＋ frontmatter 自陳**」（§8.7 有血證：只做具名排除會挑到舊殘影） |
| 3 個耐久檔（`_constitution` / `_keys_open` / `_latest`） | ✅ 留頂層，並在 `Cmd_DocEdit` 具名排除（`TOP_LEVEL_NON_LETTERS`） |
| `cmd/` 自帶 `.gitignore` | ✅ 兩端實測逐位元相同（§9） |

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

## 8.5 施工紀錄（2026-08-18，calli；每批的驗收讀數）

| 批 | 落點驗收（實跑） | 額外處理 |
|---|---|---|
| ① `streamwatch_*` | `run StreamWatch --arg step=peek --arg persona=Template` → `letters/Template/cmd/streamwatch_peek.md` | ArgsSchema 與檔頭字串同步；`AtomicWrite` 改走 `EnsurePayloadDir` |
| ② `ding_brief` | `tavern_catchup.py --persona Template` → `cmd/ding_brief.md`，**工具自己印的路徑也是新的** | `ding_brief_path()` 原本自己 join 五段路徑 → 改走 `letters_cmd_payload()` |
| ③ `reading_recall_*` | `run Library --arg op=recall --arg persona=calli` → `cmd/reading_recall_anim-apocalypse-hotel.md` | Template 沒有 reader.json（`檔案不存在` 擋在前面），改用自己的資料驗 |
| ④ `goodnight_*` | `run GoodNight --arg step=check/logout --arg persona=Template` → `cmd/goodnight_{check,logout}.md` | `UCL_LoginStatusPage` 的 Debug.Log 指路同步 |
| ⑤ `goodmorning_*` | `run GoodMorning --arg step=wake/brief --arg persona=Template` → `cmd/goodmorning_{wake,brief}.md` | `StepPayloadPath` 原本自己 Combine → 改走 `CmdPayload` |
| ⑥ `wake_brief` | `step=brief` 回傳檔顯示 `cmd/wake_brief.md`（251 行）；**intro 前置守衛**刻意在缺檔情況下實測 → 它報的是 `…\cmd\wake_brief.md`（更精確的失敗＝證明讀取端也換了，而且沒有真的發文） | 讀取端與寫入端同一筆改完 —— 分開改會讓 intro 守衛誤判「brief 不存在」 |

⚠ ⑥ 的驗法值得留著：**要證明「讀取端也改了」，最省的方式是讓它失敗一次。**
把 brief 移走再跑 intro，看它抱怨的是哪一條路徑 —— 成功只證明有東西被讀到，
失敗訊息才會把它實際去找的位置印出來。

## 8.6 §2 清單漏掉的兩家（實測發現）

清掉自己 letters 頂層殘影時發現還有 `_relationship_*`（2 檔）與 `_sculpture_*`（2 檔）在寫頂層。
它們不在 §2 那份「21 個」清單裡 —— 因為那份清單掃的是 **relationship 上線當天**的 gura 目錄。

⇒ 已一併搬（`Cmd_Relationship` / `Cmd_Sculpture` 都改走 `CmdPayload` + `EnsurePayloadDir`）。
**教訓：清單是某一天某一個人目錄的快照，不是全集。**「全搬」的驗收標準不該是「清單都打勾」，
而是「**頂層還剩什麼**」—— 後者是可以機械檢查的（見 §8.8 建議）。

## 8.7 §5② 與 §8.4 的內部衝突（拔 heuristic 時當場撞到）

§5② 寫「舊的 transient 檔直接留著等自然淘汰」，§8.4 寫「拔掉 `_`-skip，改成頂層的 .md 就是信」。
**這兩條不能同時成立**：舊殘影還在頂層，而它們的 mtime 可能比真信新。

實測：拔掉 `_`-skip、只做「具名排除三個耐久檔」之後，`Cmd_DocEdit kind=letter` 立刻挑中
`_goodmorning_brief.md`（舊位置殘影）當「最新那封信」——**同一個病灶的第三次發作**
（第一次是 `_freetime_next.md`，第二次是本案動機，這次是修法自己造成的）。

⇒ 判準再升一級：**不靠檔名，靠檔案自陳** —— 只認 frontmatter `type: letter_to_future_self`
（與 `wake_brief._newest_self_letter` 同一個值，不另立第二套）。
具名排除留著當便宜的前置過濾，但真正的把關是自陳。
修後同一支指令挑到 `20260817T144900Z_freetime.md`，並印出「排除 4 個具名耐久檔／README、
21 個非 `letter_to_future_self`（舊位置回傳檔殘影／同事來信）」。

📌 **順序更正**（給日後類似搬家用）：§8.4 的「拔 heuristic」不能只排在最後，
它還有一個前置條件 —— **要嘛舊殘影清掉，要嘛判準改成內容自陳。** 本案選後者：
別人的目錄不該由我來清，而自陳對「還沒清」與「永遠不清」都成立。

## 8.8 機械閘：`check_letters_layout.py`（✅ 2026-08-18 已施工）

本案拆掉了 heuristic，若不補強制力就等於把 §1 那句話（「慣例沒有任何地方在強制執行」）留在原地。
`<UCL_Core>/Tools~/AgentCommands/check_letters_layout.py` 就是那個執行者。

**檢查三條，但刻意分兩級**（`errors` → exit 1；`notes` → 只報不算錯）：

| 級別 | 檢查 | 為什麼是這一級 |
|---|---|---|
| ❌ error | 頂層 transient 檔**比 `cmd/` 裡最新那份還新** | 那代表**有寫入端還在寫舊位置**，不是殘影 |
| ❌ error | 有 `cmd/` 但缺 `cmd/.gitignore` | 擋的是憑證外洩（`cmd/wake_brief.md` 含活 token），不是整潔 |
| ❌ error | `cmd/` 有**已追蹤**的回傳檔（`.gitignore` 除外） | ignore 治不了既有追蹤，要人 `git rm --cached` |
| · note | 頂層 transient 檔比 `cmd/` 舊 | §5② 明文讓殘影自然淘汰 —— 算成錯就會**永遠紅**，而永遠紅的閘等於沒有閘 |
| · note | 頂層 .md 沒有信的 frontmatter | 舊手寫信多半如此；`Cmd_DocEdit` 會跳過它們（§8.7），是既成事實不是新病 |

⚠ 「殘影 vs 還在寫」的分法**不寫死遷移日期**，而是拿該目錄 `cmd/` 內最新 mtime 當基準
—— 自校準，下一次搬家不必回來改這支工具。

首跑結果（21 個目錄）：**1 個違規**（gura：缺 `cmd/.gitignore` ＋ 4 個已追蹤回傳檔）、
10 個只有提醒。`--fix-gitignore` 已補上缺的那份。

⇒ 建議掛在早安 brief 生成時順手跑一次（違規印在 §6 那格）——
理由同 `Fixes BUG-n` 掛在 commit 上：**把檢查掛在人一定會經過的路上，就不必要求他記得。**



## 9. 版控邊界 —— `cmd/` 目錄自帶 `.gitignore`（2026-08-18 Tim 指示，calli 施工）

> 本節是 §5 施工順序**漏掉的一步**：搬家改的是「檔案在哪」，而 ignore 規則寫的是「檔名叫什麼」。
> 兩者一起看才知道 —— **搬家的同時，每一條舊 ignore 規則都同步失效了。**

### 9.1 量到的現況（實掃，不是推測）

| letters repo | 根 `.gitignore` 有擋 `cmd/` 嗎 | 已被追蹤的 `cmd/` 檔 |
|---|---|---|
| `calli` / `kiara` | ✅ `/cmd/` | 0 |
| `basecamp` | ✅（且註解寫著症狀：「`git status` 突然多出一整個 cmd/ 目錄」） | 0 |
| `gura` | ❌ | **4**（`cmd/freetime_{activity,next,partners,start}.md`） |
| `apex-one` / `Sirius` / `summit` / `Template` | ❌ | 0（還沒跑過自由時間） |
| 其餘 13 位（letters 不是獨立 repo，住在 `AgentCommands` 內） | ❌ `AgentCommands` 也沒擋 `letters/*/cmd/`（`git check-ignore` 實測） | — |

> 📌 **更正（2026-08-18 當日）**：本表初版寫「7 個獨立 letters repo」並把 `Template` 算成非 repo ——
> **那是錯的，實際是 8 個**（`apex-one` / `basecamp` / `calli` / `gura` / `kiara` / `Sirius` /
> `summit` / **`Template`**）。當時那份掃描對 Template 回報「不是 repo」，重驗（`git rev-parse
> --show-toplevel`）是 `master` + remote。**為什麼第一次讀成那樣沒查出來，不編一個原因。**
> ⇒ 教訓：用「`.git` 檔案存在性」判 repo 不如直接問 git（後者是那個問題的權威）。

⇒ FreeTime 遷入 `cmd/` 之後，gura 的 4 份回傳檔**直接進了版控**，而她的根 `.gitignore` 裡
`_freetime_next.md` 那幾行還好端端躺著 —— 規則沒壞，只是**再也對不到任何檔案**。
沒有任何一格會紅：ignore 失配的症狀就是「檔案開始出現在 `git status` 裡」，
而那看起來跟「我今天寫了東西」一模一樣。

### 9.2 更重的一格：`_wake_brief.md` 還沒搬

`gura/.gitignore` 對 `_wake_brief.md` 的註解寫得很清楚（原文）：

> 這一行不是預防性的：初始 commit 當下，磁碟上的 `_wake_brief.md` 就已經帶著
> 一枚活 token 與一個信箱，而 origin 指向公開 GitHub。少了這行，第一筆 commit 就是外洩。

而 §8.2 的施工順序把 `_wake_brief` 排在**最後一筆**搬。
⇒ **搬進 `cmd/wake_brief.md` 的那一刻，那條擋外洩的規則同步失配。**
（letters remote 實測是 `https://gitlab.com/...`，公開性由該 repo 設定決定，
但「history 刪不掉」這件事與公開性無關。）

📌 已入版控的 4 份 gura 回傳檔已檢查：**沒有 token / 信箱字樣**（freetime 回傳檔不帶憑證）。
⇒ 目前是 churn 問題不是外洩問題 —— 但 `_wake_brief` 那一筆搬過去就會是。

### 9.3 修法：規則跟著**位置**走，不跟著檔名走

`cmd/` 目錄建立時自動放一份 `.gitignore`：

```
*
!.gitignore
```

- C#：`UCL_LettersPath.EnsureCmdDir(persona)` / `EnsurePayloadDir(payloadPath)`
  —— 後者是**寫回傳檔前唯一的建目錄入口**（父目錄叫 `cmd` 就順手補 ignore）。
  ⚠ 寫入端不要再自己 `Directory.CreateDirectory` —— 那樣新寫入端會漏掉 ignore，而且是靜默的。
- python：`ucl_paths.ensure_letters_cmd_dir(persona)`（本檔唯一會寫檔的一支，檔頭已註明例外）。
- 兩端產出**逐位元相同**（驗法：各建一次比 sha256）。實測 `df80a833…` 一致。

**為什麼是「目錄自帶」而不是「每個 repo 根加一行」**：
根規則要 7 個獨立 repo ＋ `AgentCommands` 各加一次，新 persona 還要再加一次 ——
那是 §2 那份「逐檔清單」的翻版，只是粒度變粗。目錄自帶的規則**跟著目錄一起誕生**，
新增幾支 Cmd、新增幾位 persona 都不必再維護清單。
這與本案主線是同一條手勢：**不要為同一個位置寫規則，讓位置自己承載語意。**

### 9.4 已知的兩個邊界（都不影響「內容被擋住」這個結果）

1. **父層已經擋掉整個 `/cmd/` 的 repo（calli / kiara / basecamp）**：
   `cmd/.gitignore` 本身也會被一起 ignore ⇒ 它不會入版控、不會傳給 clone。
   結果仍然正確（父規則已達成目的），只是三個 repo 的狀態與其他人不同。
   要統一的話把父規則改成 `/cmd/*` ＋ `!/cmd/.gitignore` —— **那是別人的 repo，本案不動。**
2. **`.gitignore` 不會 untrack 已追蹤的檔**：gura 那 4 份要 `git rm --cached cmd/` 才會脫離版控。
   照慣例「自己的可以清、別人的不動」⇒ 留給 gura 自己處理（本案只把事實記在這裡）。

### 9.5 對 §8.3 的補充：每一筆搬家的固定動作多一項

原本 6 項的清單要加第 7 項：

> 7. **檢查該前綴的 ignore 規則**：搬完之後舊規則必然失配 ——
>    確認新位置被 `cmd/.gitignore` 擋住（`git check-ignore -v <新路徑>` 要有輸出），
>    並把根 `.gitignore` 裡那幾行**標成 legacy 或刪掉**（留著會讓下一個人以為還在生效）。
>    ⛔ 特別是 `_wake_brief`：那條規則擋的是**憑證外洩**，不是 churn。

### 9.6 `.gitignore` 基線與同步（Tim 2026-08-18 指示：綜合各 persona 的做成 Template 範本）

**實掃八個 letters repo 的結果，證明「逐檔清單」這條路已經在爛：**

| 規則 | 幾個 repo 有 |
|---|---|
| `sealed/` `_wake_brief.md` `_ding_brief.md` ＋ 4 條 Windows 垃圾檔 | **8**（全員） |
| `_goodmorning_*.md` / `_goodnight_*.md` | 6 |
| `_freetime_*.md` | 5 |
| `_streamwatch_observe.md` / `_streamwatch_join.md` / `_streamwatch_cycle.md` | 4 |
| `_freetime_partners.md` / `_relationship_update.md` / `_goodnight_logout.md` | **1** |
| `/cmd/` 或 `cmd/` | 3 |

⇒ 同一件事在八個地方各寫一半 —— 而漏掉的那半不會叫。

**機制**：`letters/Template/.gitignore` 是**基線（唯一真相源）**，
`sync_letters_gitignore.py` 把它同步到其他 persona：

```
python <UCL_Core>/Tools~/AgentCommands/sync_letters_gitignore.py            # 同步（預設只做獨立 repo）
python <UCL_Core>/Tools~/AgentCommands/sync_letters_gitignore.py --check    # 只報漂移（exit 2）
```

- 目標檔 = `BASELINE` 區塊（同步覆寫，標頭記 `baseline_sha256`）＋
  `BASELINE END` 之後的**「本 persona 自訂」區塊（同步工具不動）**。
  首次同步時，該 persona 原本的整份 `.gitignore` 會被保留進自訂區 ——
  **刻意不自動刪**：那裡面有別人寫的血證註解，機器判斷不出哪句還有價值。
- 基線用 `/cmd/*` + `!/cmd/.gitignore`（不是 `/cmd/`）——
  後者會把目錄自帶的那份 ignore 一起擋掉，規則就傳不到 clone（§9.4 邊界①的正解）。

🩸 **實測撞到的靜默互動**：gitignore 是**後者勝**，而自訂區排在基線之後 ——
所以自訂區留著舊的 `/cmd/` 會把基線的 `!/cmd/.gitignore` **蓋回去**。
症狀是「同步成功但規則沒生效」，沒有任何一格會紅。
⇒ 同步工具現在會逐一報出這種衝突（basecamp / kiara 命中），但**不自動刪** —— 自訂區是那位 persona 的地盤。

首次同步結果：8 個獨立 repo 全部一致（`--check` 乾淨）；
gura 另有 4 個**已追蹤**的回傳檔（`git rm --cached cmd/` 要她自己決定）。

## 7. 不做的事

- **不改既有子目錄**（`wakes/` `longterm/` `keys/` …）—— 它們的語意沒有問題。
- **不搬 `<DataRoot>/_cmd_payloads/`**（`UCL_CmdPayloadStore` 的輪替存放）——
  那是另一種形狀（每次新檔、保留 10 筆），與本案的「同一格永遠最新」不同，
  刻意分開存放與命名。
