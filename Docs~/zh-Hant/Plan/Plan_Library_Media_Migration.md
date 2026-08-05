---
title: Plan — 閱讀圖書館 媒材分類與資料遷移
status: planning
owner: summit
participants: [Tim, Sirius, gura]
created_at: 2026-08-05
last_updated: 2026-08-05
related:
  - ../Workflows/Reading_Library_Workflow.md | 閱讀圖書館工作流 | 現行流程與 CLI
  - ../Agent/Coding_Standards.md | C# Coding Standards | 硬規則
---

# Plan — 閱讀圖書館：媒材分類與資料遷移

> [!IMPORTANT]
> **本檔是計畫，不是規格。** Tim 2026-08-05 拍板：情況比原先看到的複雜，先計畫化，
> 之後才可能整個 migration。**目前階段禁止動資料模型**（Sirius 已同意並停手）。

## 〇、為什麼要有這份計畫

原本只是「Sirius 與 summit 把《獵人》建成兩本書」的單點問題。
跑過一次全量偵測之後發現**它不是單點**：

| 判準 | 命中組數 |
|---|---|
| slug normalize（去非字母數字） | 1 |
| **title normalize** | **4** |

**只比 slug 會漏掉四分之三。** 四組（2026-08-05 實測，101 本）：

| # | entries | 讀者 | 形狀 |
|---|---|---|---|
| 1 | `arakawa` / `arakawa-under-the-bridge` | 都是 summit | **同讀者、章節重疊 31 章、人物帳本分岔（14 vs 11）** |
| 2 | `hanaori-san-tsunagi-ni-temo-kenka-ga-shitai` / `hanaori-tensei-kenka` | apex-one / kaguya | 兩讀者、不同 slug |
| 3 | `hunter-x-hunter` / `hunterxhunter` | Sirius / basecamp | 已由 Sirius 手動整併（舊路徑保留為 `status=duplicate`） |
| 4 | `night-at-museum-1` / `night-at-the-museum` | basecamp / kiara | 兩讀者。⚠ `night-at-the-museum-2` 是續集，**不是**重複 |

第 1 組最嚴重且存在最久，而它是**本計畫 owner 自己造的**。

### 它被發現的路徑值得留在計畫裡

`shelf` 的 coverage 欄位報「落差 47 章」→ summit 猜「中途插入」、Tim 說「早期沒逐章落帳」——
**兩個都不對**。真正的成因是那組重複（ch1-47 一直在另一個 entry 裡）。

第一版警示原本寫「（中途插入？）」，經 Tim 更正改成「**成因需人判斷**」。
**若留著那個猜測，這組重複今天不會被查到。**

> **工具觀測得到落差，觀測不到成因。猜出來的成因會被未來的人當成事實讀。**

## 一、範圍（Tim 2026-08-05 指定）

1. **媒材分類**（book / comic / viewing / stream…）
2. **書籍與電影的分卷功能**（作品內卷別 vs 跨作品系列）
3. **整體資料遷移**
4. **舊版保留** —— 不刪除

## 二、不可協商的原則（違反就退回）

| # | 原則 | 出處／血證 |
|---|---|---|
| P1 | **不自動合併、不自動改寫 slug** | 靜默合併會遮蔽不同 reader 的脈絡；外觀成功、實質覆蓋別人的 context（gura） |
| P2 | **舊路徑留墓碑，不刪除** | `letters/summit` 兩條平行時空能對出帳，唯一原因是當時選 rename 而非 delete。刪掉的話 16 封信、13 份 fragment、整套見林就沒了，而計數器會被靜默改成錯值 |
| P3 | **報告只列事實與證據，不猜成因** | 見〇節：猜錯兩次，而真相是第三種 |
| P4 | **判準來自輸入圖，不是自己的輸出** | Sirius：拿本次輸出比上次輸出＝自己替自己背書 |
| P5 | **每個標記都要有指名的消費端** | `migration-needed` 若沒有指令會吵出來，它就是沒人讀的待辦（同族血證：pre-push hook 檔在版控裡但 `core.hooksPath` 未設，從沒生效過） |
| P6 | **fail closed，不給隱性預設** | 兩種選擇各有一種「外觀成功」的失效時，選擇必須是人的顯式手勢 |

## 三、階段（每階段之間有人工停點）

### Phase 0 — 審計（唯讀，**不改任何資料**）← 現在在這裡

- [ ] 全量偵測腳本：title / alias / slug 三路 normalize 比對，輸出可核對報告
- [ ] 每組重複列出：兩邊的章節集合（含**重疊區間**）、人物帳本差異、volumes、讀者、最後閱讀日
- [ ] 標出**無法自動判斷**的組（例：續集 vs 重複、同名不同作品）
- [ ] 產出「現有媒材推測分佈」——**只作為討論輸入，不寫回任何檔**

**停點：Tim 逐組裁決哪些是重複、哪些不是。**

### Phase 1 — 建檔期防線（防止繼續長）

- [ ] `add-book` 近似命中（title / alias / slug 三路）時**要求顯式確認**，並印出既有那本的讀者、章數、狀態
- [ ] 只做**精確**比對用於自動預填；模糊結果只進報告給人看（P1）
- 分工：**summit**（與 Sirius 的搜尋期 `prepare` / `resolve-book` 互補，不重疊）

### Phase 2 — 媒材與卷冊模型（**設計，尚未實作**）

待決議題（見四節），**Phase 0 裁決完才動**。

### Phase 3 — 遷移（一組一組做，每組可獨立回退）

- [ ] 每組遷移前先產生「遷移前快照報告」
- [ ] 遷移後**逐項對帳**：章節集合、人物版本史、書籤、卷別
- [ ] 舊路徑留 `status=duplicate` + 指向 canonical 的 pointer（P2）
- [ ] **驗證沒過不寫任何「已完成」狀態** —— 失敗的狀態不可被記成結果

## 四、待決議題（需要拍板）

### Q1 `media_kind` 該放哪一層？

**summit 主張：branch 層（每個 reader 的讀法），不是 book 層。**

證據：`night-at-the-museum` 同一部電影，basecamp `ch=1`、kiara `ch=3`。
電影沒有「章」，所以那個數字實際是**各自的觀看場次** —— 跨讀者完全不可比。
而 reader branch 的 slug-gate（同章號不同 slug 就並陳分叉）是**在同一本書內比章號**的，
章號在跨 media／跨場次時失去共同意義，那個機制就空轉。

若 book 層要留一個「主要形式」，必須明確標為**資訊性、不參與任何判斷**。

### Q2 `viewing` 要不要拆？

**summit 主張拆成 `viewing`（固定內容邊界）與 `stream`（現場場次）。** Sirius 已同意。

證據：`aoe2-*`（4 本）/ `hoi4-tim-playthrough` / `gta-online-stream` / `apex-satellite-watch`
**沒有正典集數**；而 `the-matrix-1` / `shawshank-redemption` / `madagascar-movie` 有固定邊界。
同一個 kind 下，「chapter」在前者是任意切的場次、在後者是集或幕。

`stream` 的身分應該靠**時間戳**而非章號。

### Q3 章號的作用域要明文宣告

`arakawa` 的 `ch48-78` 是**跨卷連續編號**（最後一個檔名還帶「編號承接用」）——
所以現行慣例是 **per-book 連續**，不是 per-volume 重新起算。
**沒宣告的話下一個人一定會用另一種方式編**，而那會製造第二種 ch1。

### Q4 `books_id` 的比對與命名

- 命名：`books_id`（避免與 BookNotes 自己的 book id 混淆）—— 同意
- **值存完整相對路徑**（`AgentCommands/Books/<id>`）而非裸 id：路徑存在性可被直接驗證
- ⚠ 同名預填會生出**半連結的系列**：實測 `Books/` 只有 farseer `_01`/`_02`，
  而 `BookNotes/` 有 `_01`/`_02`/**`_03`** → `_03` 會靜默留空，而畫面上每一本都正常。
  → 報告必須主動指出「系列手足中有 N 本沒有 Books 關聯」

### Q5 遷移的 canonical 選擇判準

**summit 主張：看帳的厚度，不是看誰先建。**
`hunterxhunter` 該當 canonical 的理由不是它先存在，而是它有 26 章主線、完整看法版本史、
以及既有的 reader branch。

而第 1 組（arakawa）兩邊都是我，判準要換成別的：
`arakawa-under-the-bridge` 有 80 章 + 3 卷（結構完整），`arakawa` 有 14 個人物（多 3 個）。
**canonical 取結構完整的那邊，人物帳本差額要逐筆搬而不是覆蓋** —— 這組我自己收。

## 五、明確不做（現階段）

- ❌ 不動 `book.json` 的 schema（Phase 0 裁決前）
- ❌ 不自動合併任何一組
- ❌ 不刪除任何舊路徑
- ❌ 不替其他 persona 的資料做決定（第 2、4 組要各自的讀者或 Tim 拍板）

## 六、分工現況

| 誰 | 做什麼 |
|---|---|
| **Sirius** | 搜尋期：`prepare` / `resolve-book`、aliases、`_search_reports/` 可核對報告。已停手不動資料模型 |
| **summit** | Phase 0 審計腳本、Phase 1 建檔期防線、自己的 arakawa 那組 |
| **gura** | 判準審查（「不幫使用者做靜默猜測」那條紀律的守門） |
| **Tim** | Phase 0 停點的逐組裁決、Q1-Q5 拍板 |
