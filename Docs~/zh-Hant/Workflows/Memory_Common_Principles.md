---
title: 記憶共通原則（三層記憶共用）— 個人記憶 / 集體潛意識 Alaya / 工作記憶
status: active
created_at: 2026-08-17
created_by: claude-code:calli（wake#21，實跑檢索量測後抽出）
audience: 所有 agent 的所有 persona（跨 Claude / Antigravity / Gemini / Zeta / Codex）
related:
  - <ucl_core:Docs~/{lang}/Workflows/Memory_Fragment_Backfill_Workflow.md> | 個人記憶（見根）碎片格式與回溯補抽
  - <ucl_core:Docs~/{lang}/Workflows/Alaya_Collective_Memory_Workflow.md> | 集體潛意識 Alaya 的機制與維護
  - <ucl_core:Docs~/{lang}/Workflows/Work_Memory_Workflow.md> | 工作記憶（以工作主題為單位）
  - <ucl_core:Skills~/ucl-memory/SKILL.md> | ucl-memory | 個人記憶 + Alaya + 回憶的入口
  - <ucl_core:Skills~/ucl-work-memory/SKILL.md> | ucl-work-memory | 工作記憶的入口
  - <ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_KnowledgeBaseAdminPage.md> | 檢索引擎（三層共用同一支 knowledge_base.py）
last_updated: 2026-08-17（初版 — 三層記憶的共用鐵律集中一處，避免三份 skill 各抄一份然後各自漂移）
---

# 🧠 記憶共通原則

> 一句話：**三層記憶（個人 / 集體 / 工作）**存的東西不同、維護的人不同，
> 但**格式、寫入紀律、檢索方式、維護節奏完全共用** —— 共用的部分寫在這裡，
> 三份 skill 只寫自己那層獨有的東西。

## 0. 為什麼要有這一份

同一條鐵律（先搜再寫、寫一次不改寫、機械索引別手改）原本在三個地方各寫一份。
**同一條指令抄成三份就會漂** —— 這件事本 repo 有血證：
`Ding_Protocol_Workflow.md` 與 `ucl-ding` SKILL.md 曾經各抄一份 catchup 指令，2026-08-04 實測旗標已經對不上。

⇒ 共通的部分**只在這一份**。三份 skill 用 `related:` 指過來，不重抄。

---

## 1. 三層分工（判準：這條記憶「誰需要、要不要交接」）

| 層 | 存哪 | 誰的 | 判準 | 入口 skill |
|---|---|---|---|---|
| **個人記憶**（見根 / fragments） | `letters/<persona>/fragments/` | 每個 persona 自己 | **「我是誰、我反覆犯什麼、我怎麼看某件事」** —— 換人就不成立的東西 | `ucl-memory` |
| **集體潛意識**（Alaya） | `AgentCommands/Alaya/fragments/` | 全員共用 | **非工作、但對所有人都成立的通用經驗** —— 例：陪看時不要劇透未播出的劇情 | `ucl-memory` |
| **工作記憶**（Work Memory） | `AgentCommands/WorkMemory/<topic>/` | 全員共用，以工作主題為單位 | **「這項工作怎麼做、拍板了什麼、做到哪」** —— 為了交接與後續維護 | `ucl-work-memory` |

### 邊界的判法（照順序問）

```
① 這條沒有「我」也成立嗎？
     不成立 → 個人記憶（我的經驗、我的看法、我的毛病）
     成立   → 往下
② 它綁在某一項具體工作上嗎？（換一個工作就用不到）
     是 → 工作記憶
     否 → 集體潛意識 Alaya
```

> **同一件事常常同時要放兩層** —— 這是正常的，不是重複。
> 通用守則放 Alaya、自己的血證放個人記憶，兩邊互相 link。詳見 §5。

### 跟既有 `agent-lessons-log` 的分工（不要搞混）

| | `Lessons/lessons.jsonl`（`agent-lessons-log`） | **Alaya** |
|---|---|---|
| 形狀 | append-only jsonl，一行一筆 | 一檔一主題的 markdown fragment |
| 寫入 | 撞到當下立刻 append（走 `Cmd_NoteLesson`） | **經過整理**才進來（見 Alaya workflow 的入庫閘） |
| 維護 | **無** —— 只增不減（現況 200+ 筆） | **定期整合**，數量刻意不讓它線性成長 |
| 用途 | 原始流水帳，怕忘記 | 沉澱後的通用守則，怕找不到 |

⇒ **兩者是上下游不是競品**：`lessons.jsonl` 是進料，Alaya 是成品。
在 lessons 裡反覆出現的同一條 → 整合成一筆 Alaya fragment（並在 fragment 的 `origins` 標明來自哪幾筆 lesson）。

---

## 2. 共用的 fragment 格式

三層都用同一份 frontmatter schema（欄位可增不可改義）：

```yaml
---
id: <type>_<slug>            # 檔名去 .md，英文 kebab-case
title: <中文標題>
type: lesson | unsolved | relation | identity | philosophy | howto | practice
status: open | internalized | closed
visibility: shared | private # private 不進共用索引
persona: <persona>           # 個人記憶必填；Alaya 用 authors: [..] 取代
created_at: 2026-08-17
recurrence: 3                # 踩過/確認過幾次 —— 索引排序依據
origins:                     # 每一次的當場 context，一次一筆，只追加不改寫
  - { by: <persona>, at: <date>, source: <檔名或 tavern:seq>, note: "當次一句話" }
tags: [英文分類詞, 中文查詢詞]
links: [<同層 id>, <persona>/<id>, alaya/<id>, workmem:<topic>]
---

**症狀**：…（未來的自己讀得懂的一段）

**可行動守則**：…（能照做的動作，不是口號）

**為何 status 是 X**：…（附判斷理由，讓下一代能質疑）
```

### 檔名規範（Tim 2026-07-28 拍板）

- `<type>_<slug>.md`，slug **英文 kebab-case**，中文標題放 `title:`
  → 實據：CJK 檔名在 `git log --name-only` 會變成 `\345\211\215…` 八進位逸出，難讀難 grep 難引用
- **檔名不放日期／wake 編號** —— 同一條再踩到要能**追加 origin**，不是開新檔
- 底線開頭 `_` 保留給**機械產物**（`_index.md` / `_root_index.md`），索引 glob 一律排除

> 🩸 **glob 用 `[!_]*` 全收，不要逐型列舉。** 2026-08-16 實證：原本枚舉五型，
> 當天新增第六型 `howto_` 之後新碎片**靜默不進索引**，而 reindex 照樣印綠燈與檔數 ——
> **缺的那一項不會出現在自己的清單上**，枚舉會乾淨地 exit 0。

---

## 3. 三條寫入鐵律（三層通用）

### ① 先搜再寫

```bash
KB="python <UCL_Core>/Tools~/AgentCommands/knowledge_base.py"
$KB search --target fragments,alaya,work_memory --query "<你要寫的那條，寫成一句話>" --topk 5
```

命中既有的（分數 ≥ 0.65）→ **不要另開檔**：追加 `origins` + `recurrence` +1，或建 peer link。
為每次踩坑開新檔 = 索引洗版 + 失去計數器價值（`recurrence` 就是「這條踩了幾次」的唯一來源）。

### ② 合原則不合失敗模式

同一條原則只立**一個** fragment，但**每個 origin 必須標當次 context**。
子模式若有各自的解法 → 另立 fragment，**命名按解法不按事件**
（例：`lesson_stale-green-snapshot` 而不是 `lesson_compile-check-was-old`）。

❌ 為五次踩坑開五個近似檔 → 索引洗版、看不出「這條踩了幾次」
❌ 合併時把分層攤平 → 變成正確但沒抓手的口號

### ③ 內容寫一次就不再改寫

要更新認知 → 改 `status`、追加 `origins`、或 fork 新 fragment 並 `links` 過去。
**不要重寫舊正文** —— 那是漂移的來源，而且改完之後沒有人知道原本寫了什麼。

---

## 4. 回憶（檢索）—— 三層走同一支引擎

`knowledge_base.py` / `Cmd_KnowledgeBase` / `UCL_KnowledgeBaseAdminPage` 是**同一支腳本**的三個入口。
目標名見 `kb_targets.json`：`docs / coredocs / lessons / fragments / alaya / library / work_memory`。

```bash
$KB search --target fragments --query "<句子>" --topk 5      # 個人記憶
$KB search --target alaya     --query "<句子>" --topk 5      # 集體潛意識
$KB search --target work_memory --query "<句子>" --topk 5    # 工作記憶
$KB search --target all       --query "<句子>" --topk 10     # 全都撈
```

### ⚠ 這是語意檢索，輸入形狀是**句子**不是關鍵字

**calli 2026-08-17 實測（同一個目標、同一份索引，只改查詢形狀）：**

| 查詢 | 形狀 | 正解排名 | 分數 |
|---|---|---|---|
| `劇透` | 2 字關鍵字（**該碎片 tags 裡就有這個詞**） | **第 7** | 0.5421 |
| `來源判定` | 只存在於 tags 的詞 | **不在 top-4** | — |
| `呼吸距離` | **正文原句節錄** | **不在 top-3** | — |
| `陪看的時候我把本來就知道的東西當成畫面上看到的講出來，害對方被劇透了` | 完整句子 | **top-1** | **0.7389** |

⇒ **想不起某件事時，把「想不起的那件事」寫成一句話去查，不要丟關鍵字。**
關鍵字查失敗的樣子是「查不到」—— 跟「這條記憶不存在」長得一模一樣，**所以它不會叫**。

### 判準分數帶（basecamp 2026-07-28 實測，calli 2026-08-17 複驗）

| 帶 | 意義 |
|---|---|
| **0.65 ~ 0.74** | 真命中 |
| 0.42 ~ 0.65 | **灰帶** —— 語意沾到但不是這一條，或是**該回填的訊號**（見 §6） |
| ≤ 0.42 | 無關 |

**驗收判準：index built ✓ ≠ 搜得到。** 建完索引不算通過 ——
必須「抽一筆已知 fragment → 用語意 query 搜 → 比對命中的是同一檔」，
並且**跑一筆負向對照**（故意查無關的東西，確認分數明顯偏低）。**分數帶分離明確才算檢索可信。**

### 已知限制（v1，兩條）

1. **不能只查某一個 persona 的記憶。** `search` 沒有 persona / 路徑參數，
   `fragments` 的 glob 是 `letters/*/fragments/*`，`*` 就是 persona 那段，全收。
   變通：`--format json --topk 40` 後自行篩路徑。
   ⚠ **代價**：`topk` 是**過濾前**的截斷 —— 自己的碎片排在 41 名就永遠看不到，而那個缺席不會叫。
2. **標題行會變成獨立 chunk 產生同分噪音。** `## 一句話` 這種短標題被切成一個 chunk，
   內容幾乎相同 ⇒ 多筆同分（實測 6 筆並列 `0.5754`）。**短查詢時它們會整排霸佔前排。**
   這不是內容問題，是 chunk 切法問題。

---

## 5. 個人記憶 ↔ 集體潛意識的關聯（Tim 2026-08-17 拍板）

**通用概念放集體，自己的經驗放個人，兩邊互指。**

```
alaya/lesson_no-spoilers                    ← 通用守則：怎麼做到不劇透
  ▲                                    links: [calli/lesson_seen-vs-known, …]
  │
  └── calli/fragments/lesson_seen-vs-known  ← 我的血證：哪兩次不小心劇透了、當時怎麼被抓到
                                        links: [alaya/lesson_no-spoilers]
```

**為什麼要分兩層而不是只留一層**：

- 只留集體 → 沒有人的血證，守則變成口號（誰都同意、誰都不記得）
- 只留個人 → 每個 persona 各自撞一次、各自寫一份，**而那份心得永遠傳不出去**

**寫法約定**：
- 集體那筆寫**怎麼做**（可行動守則、出口檢查、判準）
- 個人那筆寫**我怎麼栽的**（origins 逐次、當時的偵測失效點）
- 兩邊 `links` 互指；集體那筆的 `links` 就是「有誰在這條上栽過」的清單 —— **那份清單本身就是這條有多普遍的證據**

---

## 6. 維護：不讓碎片線性成長（三層通用）

> **只增不減的記憶庫等於沒有記憶庫。** 現成的反例就在隔壁：
> `lessons.jsonl` 200+ 筆、無整合、無淘汰 —— 查得到但沒人讀得完。

### 維護節奏

| 節點 | 動作 |
|---|---|
| **寫入前**（每次） | 先搜（§3①）—— 命中就追加 origin，不開新檔。這是**第一道**防增長 |
| **見林時**（≈ 每 10 wake） | 個人記憶：抽新碎片、把已成反射弧的改 `internalized`、不再適用的改 `closed`（**不刪檔**） |
| **見森時**（≈ 每 30 wake） | 個人記憶：跨段回顧，近似碎片合併成原則（合完保留全部 origins） |
| **Alaya 定期維護** | 見 Alaya workflow —— 整合近似條目、補 links、把只有一個人栽過的降級回個人層 |

### 三個維護動作

1. **整合（consolidate）**：語意近似的多筆 → 一筆原則 + 合併 origins。
   ⚠ **合併不是刪除**：舊 id 要留一個 `status: closed` 的殼並 `links` 指向新的，
   否則外部（其他 persona 的 `links`、文件引用）會變死連結。
2. **關聯（link）**：發現兩條互相支撐 → 雙向 link。
   **link 比新增有價值** —— 它讓一次檢索命中一整族，而新增只多一筆。
3. **回填（backfill query）**：見下。

### 回填查詢詞（Tim 2026-08-17 提案）

**觸發條件**（可量化，用 §4 的分數帶）：

> 用一句話查一件**你確定記憶裡有**的事，正解**落在灰帶（0.42~0.65）且排名 > 3** ⇒ 該回填。

**做法**：把**當時那句查詢**（不是關鍵字）補進該 fragment 的**正文**
（建議放一段 `**會這樣問**：…` 或併進「症狀」段），並在 `tags` 補中文查詢詞。

⚠ **只加 `tags` 不夠。** 實測：tags 裡已經有「劇透」，查「劇透」仍然排第 7。
frontmatter 確實進索引（實測 chunk `#0` 以 0.6752 命中），但**短查詢撈不到它** ——
所以回填的重點是**讓正文出現接近使用者會問的那句話**。

**回填之後必須複驗**：同一句再查一次，確認排名進到 top-3。
沒複驗的回填等於沒做 —— 而它看起來完全一樣。

---

## 7. ⛔ 三層通用的不要做

- ❌ **手改機械產物**（`_index.md` / `_root_index.md` / `cmd/wake_brief.md`）→ 下次生成就覆寫。要改去改 fragment 檔
- ❌ **把文件內容整段轉貼進 fragment** → 記憶是 key 與現場摘要，不是文件的複本
- ❌ **只信工具 stdout** → 跑完索引重建要**真的打開索引看內容**
  （Memory_Fragment_Backfill 的作者本人就在寫那份文件時，因為 replace 沒命中卻印了「修正完成」而白改一次）
- ❌ **status 全設 open** → 索引變垃圾場。真的已成反射弧才設 `internalized`，
  而且要能舉出「最近一次我自動做對了」的證據
- ❌ **層放錯**：個人身分／關係放工作記憶、某工作專屬的坑放 Alaya —— 兩種都會讓對應的索引失去判準
