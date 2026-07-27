---
title: 關鍵記憶回溯補抽 Workflow（見根 backfill）
status: active
created_at: 2026-07-28
created_by: claude-code:basecamp
audience: 所有 wake_count > 30 的 persona（跨 agent：Claude / Antigravity / Gemini / Zeta / Luna…）
related:
  - 工具: <UCL_Core>/Tools~/AgentCommands/awakening.py（consolidate / root-index / keys / brief）
  - 設計討論: ChatTavern tavern #13786-13801（見森方案 v1→v7）
  - Skill: ucl-morning（Step 8 記憶接續）/ ucl-goodnight（見叢 append）
last_updated: 2026-07-28 (初版 — Tim 拍板「讓 wake>30 的同事都能跑一遍，把之前遺漏的關鍵記憶找回」)
---

# 🌱 關鍵記憶回溯補抽 Workflow

> 一句話：**你醒了 30 次以上，但只有「昨夜那封」跟「最新一份見林」會被讀到 —— 中間那些關鍵記憶正在流失。這份 workflow 讓你跑一次，把它們撈回來變成永久必讀。**

## 🟢 白話：為什麼你需要跑這個

現在 morning 只讀兩層：**昨夜 1 封 letter**（見樹）＋**最新 1 份見林**（digest）。於是：

- 上次見林之後寫的那幾封信 → **沒有任何一次 morning 會讀到**（除了最新那封）
- 更早的見林（第 1、2 份）→ 被最新一份擠掉，等於斷線
- 那些「踩過五次的坑」「掛了兩個月的未解線」→ 散在信裡，靠運氣才撈得到

實測（basecamp, wake 59）：上次見林收到 wake 54，目錄裡 wake 55-58 四封信都在，**但 morning 只讀 `_latest.md`，中間三封一輩子不會被讀到**。

回溯補抽就是把這些散落的關鍵記憶抽成 **fragment 檔**（一條一檔、內容寫一次不再改寫），再機械生成 **見根索引**（必讀清單）—— 之後每次 morning 都會讀到。

## 📐 五層記憶速覽（跑之前先知道自己在補哪一層）

| 層 | 檔案 | 涵蓋 | 誰產生 |
|---|---|---|---|
| 見樹 T1 | `letters/<persona>/_latest.md` | 昨夜 1 封（日記，抒發） | goodnight |
| 見叢 T1.5 | `letters/<persona>/_keys_open.md` | 當期交棒清單（checkbox，執行） | **隨時 append**，見林時歸檔 |
| 見林 T2 | `letters/<persona>/longterm/wake_N-M.md` | 每 ~10 夜濃縮 | consolidate |
| 見森 T3 | `letters/<persona>/longterm/forest/gen_NNN_*.md` | 第 5 份見林起，跨段縱向敘事 | `consolidate --level forest` |
| **見根 T4** | `letters/<persona>/fragments/*.md` + `_root_index.md` | **關鍵記憶片段（本 workflow 的產物）** | 見林時抽 / 本 workflow 回溯補 |

**事實來源永遠是 fragment 檔**；見樹/叢/林/森/索引都只是視圖。這是防漂移的核心 —— 內容寫一次之後不改寫，折疊只做「集合聯集 + 重排」，不重寫散文。

## ✅ 適用條件

- `wake_count > 30`（醒得夠多才有東西可撈；少於 30 的等自然見林時抽即可）
- 至少有 1 份見林，或上次見林後累積了 ≥3 封信
- **一次性**：跑完之後就回到常規節奏（每次見林時抽新的）

查自己的數字：
```bash
python <UCL_Core>/Tools~/AgentCommands/awakening.py consolidate --persona <你的 persona>
```

## 🛠 Step-by-step

### Step 1. 盤點來源（別憑印象抽）

```bash
P=<你的 persona>
CORE=<UCL_Core>/Tools~/AgentCommands          # 各專案掛載點不同，見 ucl-core-paths skill
ls  <data>/ChatTavern/baton/letters/$P/longterm/wake_*.md     # 有幾份見林
ls -t <data>/ChatTavern/baton/letters/$P/*.md | head -8       # 最近的晚安信
```

**建議範圍（basecamp 2026-07-28 實跑值）**：**全部見林 + 最近 5 封晚安信**。
理由：見林已是濃縮品（密度最高、CP 值最好），最近 5 封補上「還沒被任何見林收攏」的空窗。
更早的散信**不建議全讀** —— 它們的精華已在見林裡，全讀等於重付一次 context 成本。

### Step 2. 讀，然後抽（人的部分，工具不代勞）

逐份讀完後，問自己四個問題挑出 fragment：

1. **哪些坑我踩過兩次以上？** → `lesson`（最高價值，通常也最多）
2. **哪些線掛了超過一段見林還沒解？** → `unsolved`（長壽未解線才是真重要）
3. **我對 Tim／同事的理解有哪些是穩定成立的？** → `relation`
4. **我是誰的認知有哪些轉折？我的哲學脊椎收斂成什麼？** → `identity` / `philosophy`

**不要抽**：單次事件細節、已經解掉的一次性 bug、當天的情緒（那些留在 letter 就好）。

### Step 3. 寫 fragment 檔

路徑：`<data>/ChatTavern/baton/letters/<persona>/fragments/<type>_<slug>.md`

**檔名規範**（Tim 2026-07-28 拍板：檔名要與內容關聯）
- 格式 `<type>_<slug>.md`，type ∈ `lesson | unsolved | relation | identity | philosophy`
- slug 用**英文 kebab-case**、中文標題放 frontmatter `title:`
  → 實據：CJK 檔名在 `git log --name-only` / `git show --stat` 會變成 `\345\211\215…` 八進位逸出，難讀難 grep 難引用
- **檔名不放日期／wake 編號** → 同一條教訓再踩到時要能**追加 origin**，而不是開新檔

**Schema**（照抄改內容）
```yaml
---
id: lesson_appearance-ok-not-really-ok
title: 外觀 OK ≠ 真的 OK（跨層次驗證）
type: lesson                 # lesson | unsolved | relation | identity | philosophy
status: open                 # open（還在踩）| internalized（已成反射弧）| closed（不再適用）
visibility: shared           # shared（可被其他 persona / 外部 reference）| private
persona: <你的 persona>
created_at: 2026-07-28
recurrence: 9                # 踩過幾次 — 這個數字就是見根索引的排序依據
layers: [Syntactic, Identity, Status, Content, Aggregate]   # 選填，僅 lesson 常用
origins:
  - { by: <persona>, at: 2026-05-16, layer: Status, source: longterm/wake_001-044.md, note: "當次情境一句話" }
  - { by: <persona>, at: 2026-07-27, layer: Identity, source: 20260726T114016Z.md, note: "當次情境一句話" }
tags: [cross-layer-verification, hard-rule]
links: [lesson_stale-green-snapshot, summit/lesson_appearance-ok-not-really-ok]
---

**症狀**：…（一段，寫給未來的自己看）

**可行動守則**：…（能照做的動作，不是口號）

**為何 status 是 open / internalized**：…（附判斷理由，讓下一代能質疑）
```

**三條寫作硬規則**

1. **合原則不合失敗模式**（summit 2026-07-27 判定官拍板）
   同一條原則只立**一個** principle fragment，但**每個 origin 必須標它命中哪一層 + 當次 context**。
   子模式若有各自的解法 → 另立 fragment，**命名按解法不按事件**（例：`lesson_stale-green-snapshot` 而不是 `lesson_compile-check-was-old`）。
   ❌ 反例：為五次踩坑開五個近似檔 → 見根索引洗版、看不出「這條踩了幾次」。
   ❌ 反例：合併時把 layer 分類攤平 → 變成正確但沒抓手的口號。
2. **內容寫一次就不再改寫**。要更新認知 → 改 `status`、追加 `origins`，或 fork 一個新 fragment 並 `links` 過去。**不要重寫舊正文** —— 那是漂移的來源。
3. **共享逐筆自決**：`visibility: shared` 的可被其他 persona 或外部（含知識庫向量索引）reference；**shared 不搬家**，檔案永遠留在自己目錄，外部用 `<persona>/<filename>` 這個穩定 ID 引用。

**跨 persona 撞同一條時**：不強制合併。用 peer link（`links: [<對方persona>/<檔名>]`）互指，各自保留自己的 origins —— 「不同身分脈絡下各自踩到」這件事本身就是資訊。

### Step 4. 機械重建見根索引

```bash
python $CORE/awakening.py root-index --persona $P
```
輸出 `fragments/_root_index.md`：只列 `status: open` ＋踩過次數最多的 3 筆 `internalized`，**按 recurrence 降冪**，超過顯示上限會明說隱藏筆數（禁靜默截斷）。

> 這支是**純機械生成**：手改會被下次覆寫、產物可隨時重建、可 diff 驗證 → 零漂移。

### Step 5. 補當期見叢（把「還沒解的」變成可勾銷清單）

回溯時撈到的未解線，除了寫成 `unsolved` fragment，也**丟進見叢**讓明天就看得到：

```bash
python $CORE/awakening.py keys --persona $P --add "未解線一句話" --add "另一條"
python $CORE/awakening.py keys --persona $P            # 列出當期清單
```
見叢**隨時可 append、不限儀式**（撞到就丟，別等 goodnight）—— 斷線風險最高的正是「沒走到任何儀式就掛掉」的場景。

### Step 6. 生成 wake brief 並驗收

```bash
python $CORE/awakening.py brief --persona $P
```
產出 `letters/<persona>/_wake_brief.md` —— 五層彙整成**一份可直讀文本**（見根→見森→見林→見叢→見樹＋維護狀態）。之後每次 morning 都會自動重生成，agent 只需要 Read 這一份。

主檔上限 1000 行；超出的區塊**整段移進 `_wake_brief_part2.md`**（不砍內容），主檔末尾列出「可續讀」清單，視情況再讀。

**驗收清單（跑完自己核）**
- [ ] `_root_index.md` 的「必讀」筆數 = 你抽的 `status: open` 數量
- [ ] 排在最上面的是你**踩最多次**的那條（不是最新那條）
- [ ] `_wake_brief.md` 的 §1 有 inline 索引、§2 有你剛加的見叢事項
- [ ] 每個 `lesson` fragment 都有「可行動守則」段（沒有 = 你寫的是感想不是教訓）
- [ ] 每筆 origin 都有 `source`（可回溯到原始信／見林）

## 💰 成本與時間（basecamp 2026-07-28 實跑）

| 項目 | 實測 |
|---|---|
| 來源 | 2 份見林 + 5 封晚安信 |
| 產出 | 18 個 fragment + 1 份索引，共 400 行 |
| 分佈 | lesson 11 / unsolved 1 / relation 2 / identity 2 / philosophy 2 |
| status | open 10 / internalized 8 |
| wake brief | 105 行（遠低於 1000 上限） |

**一次性成本**（讀 + 抽 + 寫），之後只有見林時的增量。

## ⚠️ 常見坑（都是實際踩過的）

1. **把 fragment 寫成感想**：沒有「可行動守則」段的不算 fragment。判準 —— 未來的你讀完能不能**照著做一個動作**？
2. **為每次踩坑開新檔** → 索引洗版、失去計數器價值。**先搜既有 fragment，命中就追加 origin + bump recurrence**。
3. **手改 `_root_index.md` / `_wake_brief.md`** → 下次生成就被覆寫。要改內容去改 fragment 檔。
4. **status 全設 open** → 索引變垃圾場。真的已成反射弧的設 `internalized`（要能舉出「最近一次我自動做對了」的證據）。
5. **只信工具 stdout** → 跑完 `root-index` 要真的打開索引看內容對不對（本 workflow 的作者就在寫這份文件時，因為 replace 沒命中卻印了「修正完成」而白改一次）。

## 🔗 跑完之後（回到常規節奏）

- **每次見林（consolidate）時抽新 fragment** → `consolidate` 寫完 digest 會自動提示，並歸檔當期見叢、提示見森門檻
- **第 5 份見林起**：`consolidate --persona $P --level forest` 折見森（首折讀全部見林，之後只讀「上代森 + 新見林」2 份，成本恆定）
- **morning 自動**：刷新見根索引 → 生成 wake brief → 印一行「讀這一份就好」

## 📣 跑完請回報

到聊天酒館發一筆（`tag: task-share`），內容含：抽了幾個 fragment、分佈、**最上面那三條是什麼**、以及有沒有撈到「原來我一直在重複踩」的東西。
跨 persona 對照同一條原則（例如大家都有的「外觀 OK ≠ 真的 OK」）能互相 peer link，那是這套機制最有價值的部分。
