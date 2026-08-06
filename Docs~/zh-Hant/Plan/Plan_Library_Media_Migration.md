---
title: Plan — 閱讀圖書館 媒材分類與資料遷移
status: approved
owner: summit
participants: [Tim, Sirius, gura]
created_at: 2026-08-05
last_updated: 2026-08-06
related:
  - ../Workflows/Reading_Library_Workflow.md | 閱讀圖書館工作流 | 現行流程與 CLI
  - ../Agent/Coding_Standards.md | C# Coding Standards | 硬規則
  - ../UCL_EditorPage/UCL_ReadingNotesManagePage.md | 閱讀心得管理頁 | Archive 與新 Library 的唯讀入口定位
---

# Plan — 閱讀圖書館：媒材分類與資料遷移

> [!IMPORTANT]
> **2026-08-06 Tim 已拍板方向；本檔是實作前的規格依據。** 新資料模型與工具尚未實作，
> 但任何實作都必須遵守本檔的 Archive 不可變、手動遷移與 reader ownership 邊界。

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
5. **新／舊資料的檢索邊界** —— 新工具只讀新 schema；legacy 僅供人工遷移時開啟

## 二、不可協商的原則（違反就退回）

| # | 原則 | 出處／血證 |
|---|---|---|
| P1 | **不自動合併、不自動改寫 slug** | 靜默合併會遮蔽不同 reader 的脈絡；外觀成功、實質覆蓋別人的 context（gura） |
| P2 | **Archive 不可修改、遷移一律複製** | Archive 是歷史原件；新目標只能新增副本與 receipt，已遷移狀態寫在 Archive 外的 registry，不以移動／刪除／墓碑改寫原件表達 |
| P3 | **報告只列事實與證據，不猜成因** | 見〇節：猜錯兩次，而真相是第三種 |
| P4 | **判準來自輸入圖，不是自己的輸出** | Sirius：拿本次輸出比上次輸出＝自己替自己背書 |
| P5 | **每個標記都要有指名的消費端** | `migration-needed` 若沒有指令會吵出來，它就是沒人讀的待辦（同族血證：pre-push hook 檔在版控裡但 `core.hooksPath` 未設，從沒生效過） |
| P6 | **fail closed，不給隱性預設** | 兩種選擇各有一種「外觀成功」的失效時，選擇必須是人的顯式手勢 |
| P7 | **所有新閱讀紀錄必綁 persona；沒有主線** | `reader_persona` 缺失的 legacy 資料遷移為 `unknown`；只有確認原讀者後才能由人歸檔至其 persona |
| P8 | **合併全人工、由原讀者執行** | script 只可列 inventory／hash／計數，不能判斷可否合併、canonical、角色 alias 或章節等價性 |
| P9 | **新工具不讀 legacy schema** | Archive 不提供新 CLI／新檢索相容層；需要舊內容時，原讀者手動整理並遷移後再用新工具 |
| P10 | **已遷移狀態以 registry 為真相源** | 不可由 Archive 目錄是否仍存在、檔案是否被搬動或新目標是否同名反推遷移進度 |

## 三、已拍板資料與操作邊界

### 3.1 新資料模型的層次

新模型分為 `work → media → persona → read_session`：

- **work**：人工確認的作品本體；只放可跨媒介共用的書名、原作者與關係，不放閱讀進度。
- **media**：實際閱讀／觀看版本，使用受控且可讀的 `media_id` 前綴，例如 `comic-...`、`anim-...`、`film-...`、`series-...`、`stream-...`。`media_kind` 是新 schema 的顯式欄位；legacy 沒有該欄位，不得倒推。
- **persona**：所有新紀錄的必要 owner。舊主線／讀者不明資料先進 `unknown`；`unknown` 不是正常新建時的預設值。
- **read_session**：同一 persona 對同一 media 的每一次閱讀／觀看。章節心得、bookmark 與時間屬於 session，避免二讀覆寫第一次的紀錄。

`media_id` 的前綴供人辨識，`work_id` 與 `media_kind` 才是關聯真相源。不同媒介即使同一作品也不可共用章節、進度或心得；跨媒介關係一律人工建立。

### 3.2 Legacy 與 Archive

- `AgentCommands/BookNotes/Archive/` 是不可變來源；不被新 CLI、搜尋或管理頁直接消費。
- 新筆記只可寫入新 schema。想重新閱讀 legacy 時，由原讀者先手動閱讀／整理 Archive，再建立新資料副本。
- Archive source 的語義不得由 title、slug、文字內容或章號自動推論；缺少 reader、media 或作品關聯時如實保留為未知。

### 3.3 Migration registry

遷移進度獨立記於 `AgentCommands/LibraryMigration/registry.json`。每一個 Archive source entry 各有一筆 record；合併案以共同的 `migration_group` 關聯多筆 record。

必要欄位：`source_path`、`source_snapshot`、`owner_persona`（或 `unknown`）、`migration_group`、`state`、`target_path`、`receipt_path`、`last_action_at`、`last_action_by`、`next_action`、`block_reason`。

可用 state：`not_started`、`inventory_ready`、`awaiting_owner`、`in_progress`、`pending_review`、`migrated`、`blocked`、`not_applicable`。只有實際操作者在目標與 receipt 存在、逐項驗收完成後，才可手動標為 `migrated`；`blocked` 與 `awaiting_owner` 必填原因。

### 3.4 手動合併 SOP

合併由原讀者執行，且只在新 schema 已建立 target work／media／persona／read_session 後進行：

1. 為每個 source 建立唯讀 snapshot manifest，並建立空白 merge ledger。
2. 操作者逐項填寫去向：保留、複製到目標、建立 alias、連到既有項或暫緩；每筆寫來源、目標、理由、時間與操作者。
3. 同章、角色同名、版本差異或疑似二讀一律預設暫緩；沒有原讀者的明確手勢就不寫入目標。
4. merge ledger 必須回查所有 source 項目都有去向。Archive 不修改，目標也不得覆寫 source 歷史。
5. 章節、角色版本史、arc、volume、bookmark 分別驗收；任何未決項目使整組維持 `pending_review`。

`arakawa`／`arakawa-under-the-bridge` 是此 SOP 的首個範本，owner 為 summit；它是否為重複建檔或兩個 read session，僅能由 summit 裁決。

## 四、階段（每階段之間有人工停點）

### Phase 0 — 新 schema 與 registry 規格（不讀 legacy 內容）← 現在在這裡

- [ ] 定義並實作 `work → media → persona → read_session` 的新 schema。
- [ ] 定義受控 media kind／media id 前綴與 registry schema。
- [ ] 新媒材筆記頁以新 schema 建立、檢索與管理資料；舊 `UCL_LibraryManagePage` 收斂為 Books 全文管理。
- [x] 已提供 `UCL_ReadingNotesManagePage` 的作品名稱入口搜尋：唯讀列出 Archive `book.json` 與新 Library `work.json`／`media.json` 對應資料夾，供人工開啟與遷移；尚未建立新資料／session 寫入流程。
- [x] `UCL_LibraryManagePage` 與 `UCL_ReadingNotesManagePage` 都加入 `UCL_ToolBoxPage`；Control Panel 也提供兩頁入口。

**停點：新 schema 與 registry 可建立新筆記，但不讀取／改寫 Archive。**

### Phase 1 — 新筆記流程（防止繼續長）

- [ ] 所有新建／記錄要求顯式 persona、media 與 read session；不可產生主線。
- [ ] `unknown` 僅供 legacy 遷移；新建流程禁止將它當隱性預設。
- [ ] 新流程只查新 schema；若使用者要舊內容，顯示需手動遷移的說明，不開 legacy reader。

### Phase 2 — 按需手動遷移（逐 entry／逐 group）

- [ ] 原讀者需要某 Archive entry 時，先在 registry 建立／更新 record，再手動建立新目標副本。
- [ ] 單筆遷移依 receipt 逐項驗收後才標 `migrated`。
- [ ] 多 source 合併依 3.4 SOP 執行；沒有 owner 或未決項目即停在 `awaiting_owner`／`pending_review`。

### Phase 3 — 回溯與長期維護

- [ ] 新媒材筆記頁提供 registry 的唯讀摘要：state 計數、待處理 owner、blocked 原因與 receipt 連結。
- [ ] 檢討受控 media kinds 與新 schema，但不得以 schema 更新重解讀 Archive。

## 五、歷史設計問題與已覆蓋的裁決

本節保留 2026-08-05 的原始設計證據；若與第三節衝突，一律以第三節已拍板規則為準。

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

## 六、明確不做（現階段）

- ❌ 不讓新 CLI、搜尋或管理頁讀取 legacy Archive schema
- ❌ 不修改、搬移、刪除或在 Archive source 內寫入遷移標記
- ❌ 不把 title／slug normalize、章節號、文字內容或 hash 相同當成可合併結論
- ❌ 不自動合併任何一組
- ❌ 不建立沒有 persona 的主線，亦不把 `unknown` 當新建預設
- ❌ 不替其他 persona 的資料或合併決定；原讀者不明時維持 `awaiting_owner`

## 七、分工現況

| 誰 | 做什麼 |
|---|---|
| **Sirius** | 將拍板模型、registry 與手動合併 SOP 文件化；協助新 schema／新媒材筆記頁的實作設計，不讀 legacy 內容 |
| **summit** | 自己的 arakawa group 的原讀者與手動合併 owner；協助新流程與防線設計 |
| **gura** | 判準審查（「不幫使用者做靜默猜測」那條紀律的守門） |
| **Tim** | 目標 schema 與受控 media kinds 的最終裁決；跨 persona／原讀者不明項目的授權裁決 |
