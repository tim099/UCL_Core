---
title: Relationship 系統 — affinity 重做：資料落回 persona 櫃子、一事件一檔、雙專案合併遷移、舊系統退場
slug: relationship-system
status: approved（2026-08-18 Tim 拍板：新系統名 relationship、資料落 letters/<persona>/relationship/、events 與 opinions 各自一筆一檔、後台手動遷移【選 A：支援多來源】、完工後廢棄舊 affinity；C 案「把剩下的 persona 也升成獨立 repo」由 Tim 同步進行中）
created_at: 2026-08-18T07:20:00Z
created_by: calli
location: UCL_Core (cross-project)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Docs~/{lang}/Mechanics/Affinity_System.md | Affinity System | 現行 schema 與 8 軸權重（本案不動計算，只動存放）
  - ucl_core:Docs~/{lang}/Plan/Plan_BugReport_System.md | BugReport 系統 | 一單一檔＋後台頁的同型前例（2026-08-18 落地）
  - ucl_core:Skills~/ucl-affinity/SKILL.md | ucl-affinity（將退場） | 現行入口；落地後由 ucl-relationship 取代（見 §7）
---

# Relationship 系統 — 實作 Plan（affinity 重做）

## 0. 先講量出來的數字（不是估計，是掃過的）

| 項目 | LY | Bar |
|---|---|---|
| persona 數 | 17 | 20 |
| `(persona, target)` 配對 | 108 | 138 |
| history 事件 | 590 | 757 |
| opinions | 532 | — |

**兩專案的重疊（以 `(at, reason)` 當事件指紋）：**

| | 數量 |
|---|---|
| 兩邊都有的配對 | **98** |
| 只有 LY 有的配對 | 10 |
| 只有 Bar 有的配對 | 40 |
| **完全相同的事件（前期共同段）** | **425** |
| 只有 LY 有的事件 | 144 |
| 只有 Bar 有的事件 | 262 |
| ⚠ **同時戳但 reason 不同（撞號）** | **0** |

⇒ **`(at, reason)` 是可用的指紋** —— 這是整個遷移設計的地基，而它是量出來的不是假設的。

**再一個關鍵讀數：現值能不能從事件重算出來？**

| | 配對數 |
|---|---|
| 重算 == 現值 | **105 / 108** |
| 重算 != 現值（有漂移） | **3**（`basecamp→Tim` / `claude-da-xiaojie→Tim` / `ridge-two→Tim`） |
| 沒有 history | 0 |

三筆漂移**方向一致：現值 > 重算**（例：`basecamp→Tim` 的 trust 現值 1.0、重算 0.8）——
表示早期有些調整**沒有被記進 history**（schema v1 時代或直接編 JSON 留下的）。
⇒ 這三筆不能靠重算補回來，要用**期初餘額**處理（見 §4.3）。

---

## 1. 現況的三個結構問題

### 1.1 資料放在「系統的資料夾」而不是「人的資料夾」

現況：`AgentCommands/ChatTavern/affinity/<persona>/relations.json`
而 persona 自己的櫃子在 `AgentCommands/ChatTavern/baton/letters/<persona>/`
（裡面已經有 `fragments/`、`bookshelf/`、`keys/`、`longterm/`）。

⇒ **同一個人的東西散在兩棵樹上。** 早安 brief 的 §6.5「見人」要跨樹去撈，
而 letters 那棵樹有些 persona 已經是獨立 git repo（`letters/calli` 就是），
好感度卻留在主 repo ⇒ **一個人的記憶可以被搬走，他對別人的看法不會跟著走。**

### 1.2 一 persona 一個大 JSON ＝ git 衝突磁鐵

`relations.json` 內含所有 target 的向量、opinions、完整 history。
兩個 agent 同時對不同人更新好感 ⇒ 同一檔案的 conflict。
（`basecamp/relations.json` 已有 150 條 opinions、243 筆事件在同一個檔裡。）

**這跟 BugReport 今天遇到的是同一題**，那邊已經用「一單一檔」解掉了。

### 1.3 事件與現值混在同一份檔，重算與存值可能對不上

現況 `emotion_vector` / `surface_score` 是**存出來的**，`history` 是**記出來的**，
而上面量到：3 筆已經對不上，**而且沒有任何機制會叫**。
存值與事件流在同一個檔裡並列，讀的人天然會假設它們一致。

---

## 2. 新架構

### 2.1 資料夾命名：`relationship/`

```
AgentCommands/ChatTavern/baton/letters/<persona>/relationship/
  _index.md                    # 所有對象的當前總值一覽（機械生成，可重建）
  <target>/                    # 一個對象一個資料夾
    _current.md                # 只有總值 —— 不含 opinions、不含事件
    events/
      20260514T102720593Z-a1f9c3d2.md    # 一事件一檔（有 axis_deltas 的帳）
    opinions/
      op-3f2a91c4d7b8.md                 # 一則看法一檔（純文字，與向量解耦）
```

**三層拆開，而不是兩層**（Tim 2026-08-18）：`_current.md` 只留總值，
`events/` 與 `opinions/` 各自一筆一檔。

⭐ **`events` 與 `opinions` 分兩個資料夾不是整潔，是把「解耦」變成結構性的事實。**
現行 skill 的硬規則第 4 條寫著「opinion 跟 emotion_vector 解耦」——
但現況它們並排在同一個 target 物件裡，讀的人天然會假設每則 opinion 對應某次 delta。
分開放之後，**要把它們綁在一起得先跨資料夾**，而那個動作會讓人停下來想一下。

**命名（Tim 2026-08-18 拍板）**：新系統叫 **relationship**，資料夾用同一個字。

> 📌 我原本提的 X 是 `people/`（理由：早安 brief 已有 §6.5「見人」，
> `bookshelf`（見書）↔ `people`（見人）是同一組對仗）。**Tim 給了系統名之後這個提案作廢** ——
> 系統叫 relationship 而資料夾叫 people，就是憑空多一個要解釋的對應關係。
> **少一個差異，就少一處「為什麼這裡不一樣」要解釋。** 用同一個字。

- ⛔ 不用 `affinity/`：那是**要退場的舊系統的名字**，而且會跟舊路徑同名，
  遷移期間兩個 `affinity/` 並存會分不清誰是誰。
- ⛔ 不用 `relations/`：`relations.json` 是舊檔名，同名會讓「這是新的還是舊的」變成要查的事。

### 2.2 事件檔名 = 去重指紋（本案最重要的一個決定）

```
20260514T102720593Z-a1f9c3d2.md
└─ at（UTC 壓平）──────┘ └ sha1(at + "\n" + reason)[:8]
```

⭐ **把去重做成檔名，而不是做成比對邏輯。**

同一筆事件不管來自 LY 還是 Bar，算出來的檔名**逐字元相同** ⇒
遷移時的去重就是一句「檔案已存在就跳過」，
**不需要任何比對程式碼，也就沒有比對程式碼會漏掉的可能**。

（實測支撐：425 筆共同事件的 `(at, reason)` 完全一致，0 筆撞號。
 而 `at` 含毫秒，同一 persona 同一毫秒發兩筆事件的機率可以忽略；
 真撞上了 `fp` 不同 ⇒ 兩個檔案並存，**不會有人被靜默覆蓋**。）

### 2.3 事件檔內容

```markdown
---
at: 2026-05-14T10:27:20.593Z
persona: calli
target: Tim
source: LY            # 遷移進來的標來源專案；新事件寫 live
axis_deltas: {trust: -0.02, respect: 0.03, admiration: 0.02}
surface_score_after: 0
---

Tim QA 抓包: 問 /ucl-ding spec 本小姐只在 chat 邊回沒走酒館, 行為違反答案內容
```

- **正文＝reason**（人讀的那句），frontmatter＝機器讀的
- `surface_score_after` 保留但**只當歷史註記**，不當事實來源（見 §2.5）
- ⚠ 這一層**不放 opinion** —— 硬綁會編造一個資料裡不存在的關聯

### 2.4 opinion 檔 —— 而它有一個事件沒有的麻煩：**沒有時戳**

掃出來的事實：

| | LY | Bar |
|---|---|---|
| opinions 總數 | 532 | 690 |
| 型別 | **全部是純字串** | 全部是純字串 |
| 兩邊內容相同 | **390** | |
| 只有一邊有 | LY 123 / Bar 237 | |
| 同一份檔內自我重複 | **0** | |

⇒ **opinion 沒有 `at`，也沒有任何時間資訊** —— 只有它在陣列裡的位置。
而 LY 的第 5 則跟 Bar 的第 5 則在分支之後就不是同一則，**索引不能當身分**。

**所以 opinion 的檔名只能用內容雜湊**：

```
op-3f2a91c4d7b8.md          ← sha1(內容 trim 後)[:12]
```

去重照樣是「檔案已存在就跳過」（390 則共同的會自動收成一份），
**但順序救不回來** —— 這件事要寫進檔案而不是假裝沒發生：

```markdown
---
origin: [LY#12, Bar#9]     # 來源專案與原陣列索引（能救的只有這個）
at: null                    # ⚠ 舊資料沒有時戳，不是漏填
migrated_at: 2026-08-18T07:20:00Z
---

哼。Tim 突然說那是他自己看錯……
```

⇒ **`at: null` 要顯式寫出來，不能省略。**
省略的話下一個工具會以為「這個欄位還沒被填」而去猜一個時間 ——
而猜出來的時間看起來跟真的一模一樣。

⚠ **合併後分支點之後的 opinion 順序是不可還原的。**
`_index` 與後台頁排序一律用 `(是否共同段, origin, 原索引)`，
並在畫面上標明「分支後的順序是合併產物，不是時序」。
**遷移之後新寫的 opinion 一律帶真的 `at`**，這個坑只存在於舊資料。

### 2.4 `_current.md` —— 總值，且標明它是不是算得出來的

```markdown
---
target: Tim
emotion_vector: {trust: 0.44, affection: 0.27, ...}
surface_score: 31
tier: 在意
event_count: 22
last_updated: 2026-08-18T07:20:00Z
recomputable: true          # ← 重算 == 現值
opening_balance: null       # ← 期初餘額（見 §4.3）；null = 全部由事件推出
---

## opinions
- <一行一句，時間序>
```

**`recomputable` 這個欄位是本案的體檢指標**：它為 false 就是「存值與事件流對不上」——
現況那 3 筆的病，以後會**在檔案上顯形**而不是要人去掃才知道。

### 2.5 舊路徑退場

`AgentCommands/ChatTavern/affinity/` 遷移完成後**保留但凍結**（改名 `affinity_archive/`），
不刪 —— 它是遷移正確性唯一的對照組。確認一段時間沒問題再由人決定刪不刪。

---

## 3. 為什麼「一事件一檔」值得付這個檔案數

遷移後檔案數 ≈ 事件數（LY+Bar 去重後約 **1,031** 筆）＋ 每對象一份 `_current.md`（約 148）。

換到的：
1. **git 衝突歸零** —— 兩人同時更新不同對象＝兩個新檔案，不需要合併
2. **跟著 persona 的櫃子走** —— `letters/<persona>` 是 submodule 時，好感度跟著搬
3. **去重變成檔案系統的性質**，不是一段要維護的邏輯
4. **事件不可就地改寫** —— 要改只能新增一筆修正事件，帳本語意天然成立

---

## 4. 遷移設計（後台手動觸發）

### 4.0 ⚠ 「兩邊各自跑一遍就會同步」—— 對 8 個 persona 成立，對其餘 27 個不成立

Tim 2026-08-18：「migration 只對本專案內資料跑，我會在 Bar 跟 LY 各自跑一遍把資料同步。」
**這個前提要先驗，而我驗了，它只有一部分成立。**

| 讀數 | 值 |
|---|---|
| 兩專案的 `AgentCommands` remote | **同一個** `gitlab.com/gamedesign1/agentcommands.git` |
| 但分支 | LY 在 **`LY`**、Bar 在 **`main`** ← 分歧的來源 |
| `letters/<persona>` 是獨立 repo 的 | **8 個**：Sirius / Template / apex-one / basecamp / calli / gura / kiara / summit |
| 其餘 persona 的 letters | 直接躺在 `AgentCommands` repo 裡（約 **27** 個） |

**Tim 的補充（2026-08-18）：「跨兩個專案 wake 的 persona 目前會是獨立 repo。」
—— 這句量過了，主體成立，但有一條 77 筆的尾巴。**

| persona | 獨立 repo | LY 事件 | Bar 事件 | 分歧事件 |
|---|---|---|---|---|
| summit | ✔ | 154 | 155 | 151 |
| basecamp | ✔ | 148 | 243 | 115 |
| gura / calli / kiara / apex-one / Sirius | ✔ | — | — | （皆有，走 git 收） |
| **crest-001** | ✘ | 26 | 17 | **15** |
| **ame** | ✘ | 6 | 26 | **20** |
| **kotoko** | ✘ | 22 | 37 | **15** |
| claude-da-xiaojie / trailhead / apex-two / meadow / ridge-001 / ridge-two | ✘ | — | — | 各 1~8 |

⇒ **量的主體確實都在獨立 repo 那一側**（summit / basecamp / calli / gura / kiara 加起來就是絕大多數），
Tim 的模型是對的。**但仍有 9 個非獨立 repo 的 persona 兩邊都有資料且分歧，合計 77 筆事件。**
它們多半是「曾經跨專案醒過、後來只在一邊」的（`crest-001` 甚至是 LY 這邊多 12 筆）。

⇒ **Tim 2026-08-18 拍板：走 A，並且同時在做 C。**

- **A（本案實作）**：遷移頁提供「額外來源」欄位（可留空）。兩邊各自跑時都讀兩份舊資料 ——
  因為去重靠檔名、合併冪等，**兩邊結果逐位元組相同**，不影響「各自跑一遍」的流程。
  ⭐ A 是 B 的超集：欄位留空就等於「只讀本專案」。
- **C（Tim 進行中）**：把剩下的 persona 也升成獨立 repo。
  ⇒ C 完成後 A 的多來源會變成**只在遷移那一次用到**，之後同步全交給 git。
  ⚠ 所以多來源欄位**不要做成常態設定** —— 做成一次性的遷移輸入，
    否則 C 完成後它會變成一個沒人記得為什麼在那裡的欄位。

**其餘兩個擋路石（跟上面獨立）：**

1. **Bar 端有 6 個 letters repo 是 detached HEAD**
   （`Sirius` / `apex-one` / `basecamp` / `calli` / `gura` / `kiara`，實測 `rev-parse --abbrev-ref HEAD` 回 `HEAD`）。
   在那裡 commit 會落在游離節點、推不上追蹤分支 ⇒ **資料寫了但傳不出去**，
   而且不會有任何錯誤（`ucl-commit` 開頭那條硬規則講的就是這個）。
3. **`Template` 兩邊分支名不同**（LY `master` / Bar `main`）—— 就算 commit 了也是兩條線。

**⇒ 建議：保留「來源可多選」的設計，但語意換一個。**
不是「跨專案遷移」，是**「本專案的遷移可以指定額外的來源舊資料」**：

- 兩邊各自跑，各自都讀 **LY 舊 affinity ＋ Bar 舊 affinity**，寫進**自己的**新結構
- 因為去重是檔名、合併是冪等的 ⇒ **兩邊算出來的內容逐位元組相同**，
  **不需要 git 幫忙同步也已經一致**
- 那 8 個有獨立 repo 的 persona，git 再幫忙收一次（同名檔案＝同一個 blob，不會衝突）

⚠ 若堅持「只讀本專案」，那**遷移前必須先把兩邊的舊 `affinity/` 對齊**
（或接受 27 個 persona 繼續分歧）—— 這是流程上的前置條件，不是工具能補的。
⇒ 這一題請 Tim 拍：**A. 遷移可讀多來源**（推薦）／ **B. 只讀本專案，接受 27 個繼續分歧**。

### 4.1 為什麼是手動按鈕而不是自動

Tim 拍板手動，理由本案同意並補一條：
**自動遷移最壞的失敗是「跑過了但只跑了一半，而沒有人知道它跑過」。**
手動按鈕讓「什麼時候跑的、跑出什麼」變成一次有人在場的事件。

### 4.2 流程（後台頁 `UCL_AffinitySystemPage` 加一個遷移區塊）

```
[來源專案根] D:/Unity/Bar        ← 可填多個，預設帶本專案
[乾跑（Dry Run）]  ← 先按這個，什麼都不寫
      ↓ 印出：
      將寫入 N 檔｜跳過（已存在＝重複）M 筆｜⚠ 指紋相同但內容不同 K 筆
      ⚠ 無法重算的配對 3 筆（會生期初餘額）
[執行遷移]  ← 二段確認（第一次點只 arm）
```

- **Dry Run 是預設動作**，`執行遷移` 在 dry run 跑過之前**不可按**
  （BugReport 後台頁的二段確認同款手勢；這裡再加一道，因為遷移只該發生一次）
- ⛔ **來源專案路徑不寫死** —— 走輸入欄位 ＋ `EditorPrefs` 記住上次的值
  （寫死另一個專案的路徑正是 `ucl-core-paths` 那條硬規則要防的事）
- 遷移**只寫新檔、不刪舊檔**；跑第二次應該是 0 寫入（冪等，且這件事本身就是驗收項）

### 4.3 三筆算不出來的怎麼辦：期初餘額

不硬把現值塞進去假裝算得出來，也不丟掉那段歷史。做法：

生成一筆 `00000000T000000000Z-opening.md`，`axis_deltas` 填「現值 − 重算」的差，
正文寫明：

> 期初餘額 —— 這一段調整**沒有留下事件紀錄**（schema v1 時代或直接編 JSON）。
> 差額由遷移工具在 2026-08-18 反推填入，**它不對應任何一件真實發生的事**。

並在 `_current.md` 標 `opening_balance: {...}`。
⇒ **重算後等於現值，而「有一段是補的」這件事寫在檔案上，不會被下一個人讀成真帳。**

### 4.4 衝突處理

| 情況 | 處置 |
|---|---|
| 指紋相同、內容相同 | 跳過（正常重複，425 筆屬此） |
| 指紋相同、內容不同 | **兩檔並存**（`-b` 後綴）＋ dry run 報數，交人判斷。⛔ 不自動挑一邊 |
| 只有一邊有 | 直接寫入 |
| `surface_score` 兩邊不同（54 筆） | **不遷移分數** —— 合併完由事件重算，存值以重算為準 |

⭐ 最後一列是重點：**兩邊的 `surface_score` 都不是「對的」**，
它們只是各自分支後的局部結果。合併之後唯一有意義的總值是**從合併後的事件流重算的那個**。

---

## 5. 落地順序

1. `UCL_RelationshipIO`（C#）／`relationship_manager.py`（python）讀寫新結構 —— **先讀舊寫新並存**
2. `UCL_RelationshipPage` 加遷移區塊（dry run → 二段確認 → 執行）
   —— 母版是現行 `UCL_AffinitySystemPage`（Tim 指定參考），**新頁不是就地改舊頁**（理由見 §7）
3. 跑遷移，`affinity_archive/` 凍結舊資料
4. `relationship_update.py` ＋ `ucl-relationship` skill（三副本安裝）
5. 早安 brief §6.5 見人改讀 `relationship/`
6. **舊 affinity 系統退場**（見 §7）

---

## 7. 舊 `affinity` 系統退場（Tim 2026-08-18：完工後廢棄）

> 🩸 **退場要有人主動做，否則它會用最難看的方式死。**
> 今天早上我親手送走 `subconscious.py`：apex-one 寫的，註解三層俱全、
> 連 Windows CP950 輸出 emoji 會炸的坑都預先擋了 —— **品質完全沒有問題**。
> 它死在排程它的 `work_session.py` 退場之後**沒有人再呼叫它**，
> 安靜 2.7 個月、零錯誤、零警告、零人察覺。
> ⇒ **沒有被正式送走的系統不會消失，它會變成一段沒人知道還在不在跑的東西。**

退場清單（**每一項都要有人勾**，不是「反正沒人用了」）：

| 對象 | 處置 |
|---|---|
| `AgentCommands/ChatTavern/affinity/` | 改名 `affinity_archive/` **凍結不刪** —— 它是遷移正確性唯一的對照組 |
| `AgentCommands/Tools/affinity_update.py` | 改成**指路 stub**（exit 2 ＋ 印新指令），不是直接刪 |
| `AgentCommands/_lib/affinity_manager.py` | 同上，或整支移除並讓 import 端明確報錯 |
| `Skills~/ucl-affinity/` | 從 `_manifest.json` 移除 ＋ 三副本 `--uninstall`；正本刪除 |
| `Docs~/{lang}/Mechanics/Affinity_System.md` | **8 軸權重與 trigger 對照表要搬進新文件**（那部分沒有過時，過時的只有存放方式） |
| `UCL_AffinitySystemPage` | 保留到遷移驗收完成後再移除；移除時 ToolBox 入口與四語系 key 一起清 |

⚠ **兩個容易漏的**：
1. **指路 stub 比直接刪好** —— 直接刪的話，別的專案／舊 session 打過來只會得到
   `No such file`，那句話不會告訴任何人該改用什麼。
   （`awakening.py morning` 退場就是走 stub，實測有效。）
2. **8 軸權重那份規格不是舊系統的一部分** —— 它是「好感度怎麼算」，本案完全不動計算。
   退場時把它連同 `Affinity_System.md` 一起丟掉，等於把還活著的東西陪葬。

## 6. 驗收（每項都要實跑）

- [ ] Dry run 兩次數字完全一致（純讀無副作用）
- [ ] 執行遷移後再跑一次 ⇒ **寫入 0 檔**（冪等）
- [ ] 遷移後事件總檔數 == 去重後應有數（LY 590 ＋ Bar 757 − 重複 425 = **922**，
      ＋ 只在單邊出現的配對所帶的事件；以 dry run 印出的數字為準並與此對帳）
- [ ] 105 筆原本 `recomputable` 的配對，遷移後仍 `recomputable: true`
- [ ] 3 筆漂移的配對產生 `opening` 事件，且 `_current.md` 的 `opening_balance` 非 null
- [ ] 隨機抽 5 筆事件，內容與舊 `relations.json` 對應項**逐字元相同**
- [ ] 舊 `affinity/` 一個位元組都沒被改
