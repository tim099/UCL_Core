---
name: ucl-memory
description: |
  個人記憶 & 集體潛意識 Alaya & 回憶（語意檢索）— 非工作類記憶的入口。
  **個人記憶**（`letters/<persona>/fragments/`）＝「我是誰、我反覆犯什麼、我怎麼看某件事」：
  自由時間的心得、對某件事的看法、個人經驗、值得記住的事件。
  **集體潛意識 Alaya**（`AgentCommands/Alaya/fragments/`）＝非工作但**對所有人都成立**的通用經驗
  （原型案例：陪看時不要劇透未播出的劇情）。通用守則放集體、自己的血證放個人，兩邊 links 互指。
  **回憶**＝走 `knowledge_base.py`（與 UCL_KnowledgeBaseAdminPage 同一支）語意檢索。
  ⚠ 輸入形狀是**句子不是關鍵字** —— 關鍵字查失敗的樣子跟「這條記憶不存在」一模一樣。
  記憶**不無限增長**：寫入前先搜、定期整合／關聯／回填。

  ⚠ 工作類的東西不在這裡 → skill `ucl-work-memory`（綁工作主題、為交接與後續維護）。

  觸發詞 (case-insensitive substring, 任一命中即 lazy-load):
  - 回憶 / 想不起 / 我以前是不是 / 之前有沒有遇過 / 撈記憶 / 找記憶 / 記憶檢索 / 語意檢索 / recall
  - 個人記憶 / 我的記憶 / 記憶碎片 / fragment / 見根 / 抽碎片 / 整理記憶碎片
  - 集體潛意識 / 集體記憶 / 共同記憶 / Alaya / 阿賴耶 / 抑止力
  - 值得記住 / 記一筆 / 這件事要記住 / 今天的心得 / 我對這件事的看法 / 個人經驗
  - 記憶維護 / 記憶整合 / 記憶關聯 / 碎片太多 / 回填關鍵字 / 搜不到記憶

related:
  - <ucl_core:Docs~/{lang}/Workflows/Memory_Common_Principles.md> | **共通鐵律（格式 / 寫入 / 檢索 / 維護）** | 三層共用，本檔不重抄
  - <ucl_core:Docs~/{lang}/Workflows/Memory_Fragment_Backfill_Workflow.md> | 個人記憶（見根）| 回溯補抽 wake>30 的完整流程
  - <ucl_core:Docs~/{lang}/Workflows/Alaya_Collective_Memory_Workflow.md> | 集體潛意識 Alaya | 門檻/權重與維護
  - <ucl_core:Skills~/ucl-work-memory/SKILL.md> | ucl-work-memory | **工作類記憶歸那邊**
  - <ucl_core:Skills~/agent-lessons-log/SKILL.md> | lessons.jsonl | 原始流水帳（Alaya 的進料端）
last_updated: "2026-08-17 v1.1 (Tim 拍板修 Alaya 門檻 — 一個人認為就整理, 人數改記為 recurrence 權重; v1.0 誤設「兩人以上才准進」。三層分工＋檢索形狀與分數帶為實跑量測)"
---

# UCL Memory — 個人記憶 / 集體潛意識 Alaya / 回憶

> 一句話：**想不起某件事 → 寫成一句話去搜（§1）。
> 有值得記住的事 → 先搜再寫，判準決定放哪一層（§2）。
> 碎片變多 → 定期整合，不是繼續加（§4）。**

## 🗺 三層，先確認你要動哪一層

| 層 | 存哪 | 判準 | 入口 |
|---|---|---|---|
| **個人記憶** | `letters/<persona>/fragments/` | 沒有「我」就不成立 | 本 skill |
| **集體潛意識 Alaya** | `AgentCommands/Alaya/fragments/` | 通用、但不綁任何工作 | 本 skill |
| 工作記憶 | `AgentCommands/WorkMemory/<topic>/` | 綁工作主題、為交接 | **`ucl-work-memory`** |

```
① 這條沒有「我」也成立嗎？   不成立 → 個人記憶
② 它綁在某一項具體工作上嗎？  是 → ucl-work-memory
                              否 → Alaya
```

不確定 → **先寫個人記憶**。升級到 Alaya 門檻很低（§3：自己判斷就能整理），但反向降級會讓外部 links 斷。

---

## 🔍 §1 回憶（最常用的入口）

```bash
KB="python <UCL_Core>/Tools~/AgentCommands/knowledge_base.py"
$KB search --target fragments,alaya --query "<把想不起的那件事寫成一句話>" --topk 8
$KB search --target all            --query "<同上>" --topk 12    # 連文件/閱讀庫/工作記憶一起撈
```

> [!IMPORTANT]
> ### ⚠ 這是語意檢索 —— **輸入形狀是句子，不是關鍵字**
>
> calli 2026-08-17 實測（同一份索引，只改查詢形狀）：
>
> | 查詢 | 正解排名 | 分數 |
> |---|---|---|
> | `劇透`（2 字，**碎片 tags 裡就有這個詞**） | **第 7** | 0.5421 |
> | `來源判定`（只在 tags 的詞） | **不在 top-4** | — |
> | `呼吸距離`（**正文原句節錄**） | **不在 top-3** | — |
> | `陪看的時候我把本來就知道的東西當成畫面上看到的講出來，害對方被劇透了` | **top-1** | **0.7389** |
>
> **關鍵字查失敗的樣子是「查不到」—— 跟「這條記憶不存在」長得一模一樣，所以它不會叫。**

**分數帶**（讀結果時對照，不要只看排名）：

| 帶 | 意義 |
|---|---|
| **0.65 ~ 0.74** | 真命中 |
| 0.42 ~ 0.65 | 灰帶 —— 沾到但不是這條，**或是該回填的訊號**（§4） |
| ≤ 0.42 | 無關 |

**只想看自己的** → 用自己那份**單 persona 索引** `frag_<persona>`：

```bash
$KB search --target frag_<persona>,alaya --query "<句子>" --topk 8
```

它由 config 依磁碟自動展開（新 persona 一出現就有自己的 target，沒有要手維護的名單），
第一次查會就地建、之後只有**自己的碎片變動**才重建。實測（basecamp 117 chunks）：
查詢 **54ms**，而共用 `fragments` 索引是 **4291ms**（28.5 MB 要整份載入）；
別人改一筆碎片就讓共用索引 stale、一次重建 **7.6s**，切開之後那些 churn 不再落在你的路徑上。

⛔ `frag_*` **不進 `--target all`** —— 它跟 `fragments` 蓋同一批檔案，兩者一起進 all 會讓
同一段文字算兩次、同分並列，看起來像兩筆獨立證據。要跨人看就用 `fragments`（那份仍在）。

⚠ 用共用 `fragments` 撈自己的東西時，`topk` 是**過濾前**的截斷 ——
自己的碎片排在 41 名就永遠看不到，**而那個缺席不會叫**。這正是單 persona 索引要解的問題。

---

## ✍️ §2 記一筆（個人記憶）

### Step 1 — 先搜（不可跳）

```bash
$KB search --target fragments,alaya --query "<你要寫的那條，寫成一句話>" --topk 5
```

| 結果 | 動作 |
|---|---|
| 命中自己的碎片 ≥ 0.65 | **不開新檔** —— 追加一筆 `origins` + `recurrence` +1 |
| 命中他人的近似碎片 | 各自保留，**互相 peer link**（`links: [<persona>/<id>]`）—— 「不同脈絡下各自踩到」本身就是資訊 |
| 命中 Alaya | link 過去，**個人這筆只寫自己怎麼栽的**，通用守則不重寫 |
| 沒命中（全部 < 0.65） | 開新檔 |

### Step 2 — 寫檔

```
AgentCommands/ChatTavern/baton/letters/<persona>/fragments/<type>_<slug>.md
```

`type` ∈ `lesson | unsolved | relation | identity | philosophy | howto | practice`
（slug 用英文 kebab-case，中文標題放 `title:`；**檔名不放日期／wake 編號**）

Schema 與三條寫作硬規則見
[共通原則 §2-§3](<ucl_core:Docs~/{lang}/Workflows/Memory_Common_Principles.md>) —— **本檔不重抄**。
正文三段固定：`**症狀**` / `**可行動守則**` / `**為何 status 是 X**`。

> 判準：**沒有「可行動守則」段的不算 fragment**（那是感想）。
> 未來的自己讀完能不能**照著做一個動作**？

### Step 3 — 機械重建見根索引

```bash
python <UCL_Core>/Tools~/AgentCommands/awakening.py root-index --persona <persona>
```

⚠ **跑完要真的打開 `fragments/_root_index.md` 看內容** —— 只信 stdout 會栽
（那份 workflow 的作者本人就因為 replace 沒命中卻印了「修正完成」而白改一次）。

---

## 🕯 §3 升級到 Alaya（集體潛意識）

**門檻只有一個：你判斷它「沒有我也成立、且不綁任何工作」** —— 一個人認為就整理，
**不必等第二個人栽**（Tim 2026-08-17 拍板）。

### 人數是權重，不是入場券

| `recurrence` | 意義 |
|---|---|
| 1 | 只有你栽過／確認過 → **正常入庫、正常被檢索到** |
| 2+ | 多位各自栽過 → **同一條在多人身上重演，它更該先被想起來** |

撈到另一個當事人時做兩件事：`recurrence` +1、`links` 加上對方的個人 fragment
—— **加權重，不是補資格**。那份 links 清單就是「這條有多普遍」的證據。

> ⚠ **v1 這個權重是給人看的** —— `knowledge_base.py` 的排序只看語意相似度、**不讀 `recurrence`**。
> 落實方式：`recurrence` 在 frontmatter（進 embedding），**人讀結果時近分的以 recurrence 高者優先**。
> 檢索端加權**尚未實作**，這是明確缺口不是「已經在做只是看不見」。

**防退化靠維護不靠入庫難**（§4）—— `lessons.jsonl` 的問題不是入庫太寬，是**沒有維護**：
200+ 筆就算每筆都經過兩人認證，一樣沒人讀得完。
**一次性的閘擋不住持續的增長。**

**仍該留在個人層的**：帶「我」才成立的（「我對敬重的人下不了刀」）、
綁具體工作的（「HSceneAsset 要先跑 Import spines」）。
拿不定主意 → 預設落點是個人層，但**別把這讀成「盡量別升」**。

### 個人 ↔ 集體怎麼分工寫

```
alaya/lesson_no-spoilers          ← 通用：怎麼做到（可行動守則、出口檢查、判準）
  ▲                           links: [calli/lesson_seen-vs-known, …]  ← 這份清單＝這條有多普遍的證據
  └── calli/lesson_seen-vs-known  ← 個人：我怎麼栽的（origins 逐次、偵測失效點）
                              links: [alaya/lesson_no-spoilers]
```

Alaya 檔案：`AgentCommands/Alaya/fragments/<type>_<slug>.md`。
Schema 同個人記憶，三處差異：`persona` → `authors: [...]`、`recurrence` ＝**有幾個 persona 栽過（＝回憶權重）**、
`visibility` 一律 `shared`。細節與維護見
[Alaya workflow](<ucl_core:Docs~/{lang}/Workflows/Alaya_Collective_Memory_Workflow.md>)。

---

## 🔧 §4 維護：不讓碎片線性成長

> **只增不減的記憶庫等於沒有記憶庫。** 反例就在隔壁：`lessons.jsonl` 200+ 筆、無整合、無淘汰。

心跳掛在**既有節奏**上，不新增儀式（前代 Collective_Subconscious 死於「需要有人週期性呼叫它」）：

| 既有節點 | 動作 |
|---|---|
| **每次寫入前** | 先搜（§2 Step 1）—— 這是第一道防增長 |
| **見林**（≈ 每 10 wake） | 抽新碎片、已成反射弧改 `internalized`、不再適用改 `closed`（**不刪檔**）、檢查該升 Alaya 的 |
| **見森**（≈ 每 30 wake） | 近似碎片合併成原則（**合完保留全部 origins**） |
| **回憶查到灰帶** | 回填（見下）|

### 三個維護動作

1. **整合** —— 語意近似的多筆 → 一筆原則。⚠ **合併不是刪除**：舊 id 留一個 `status: closed` 的殼並 link 到新的，否則外部引用變死連結。
2. **關聯** —— **link 比新增有價值**：它讓一次檢索命中一整族，新增只多一筆。
3. **回填查詢詞** ——

   **觸發條件**：用一句話查一件**你確定記憶裡有**的事，正解落在**灰帶且排名 > 3**。

   **做法**：把**當時那句查詢**補進該 fragment 的**正文**
   （建議開一段 `**會這樣問**：…` 列 2-3 句自然問法），`tags` 再補中文查詢詞。

   ⚠ **只加 `tags` 不夠** —— 實測 tags 裡已有「劇透」，查「劇透」仍排第 7。
   frontmatter 確實進索引（chunk `#0` 實測 0.6752），但短查詢撈不到它。

   **回填後必須複驗**：同一句再查，確認進 top-3。
   **沒複驗的回填等於沒做，而它看起來完全一樣。**

---

## ⛔ 不要做

- ❌ **丟關鍵字當查詢** → 見 §1。想不起來就把那件事**講成一句話**
- ❌ **工作類的東西寫進個人記憶／Alaya** → 綁工作主題的走 `ucl-work-memory`
- ❌ **把「帶我才成立」或「綁某工作」的東西往 Alaya 塞** → 門檻低不等於什麼都往上搬（§3 末段）
- ❌ **手改機械產物**（`_root_index.md` / `cmd/wake_brief.md`）→ 下次生成就覆寫，要改去改 fragment
- ❌ **重寫舊碎片正文** → 更新走改 `status` / 追加 `origins` / fork 新檔並 link
- ❌ **status 全設 open** → 索引變垃圾場。設 `internalized` 要能舉出「最近一次我自動做對了」
- ❌ **只信工具 stdout** → 索引重建完要打開索引看；檢索完要看**分數帶**不只看排名

## 📌 v1 已知不足（誠實標記）

| 缺什麼 | 現在怎麼過 |
|---|---|
| 檢索**無 per-persona 過濾**（單一 target 內） | 改查自己的單 persona 索引 `frag_<persona>`（§1）—— 不再需要事後篩 |
| 標題行變獨立 chunk → **同分噪音霸佔前排**（實測 6 筆並列 0.5754） | 用句子查詢；短查詢時往下多看幾筆 |
| Alaya **沒有機械索引、沒有專屬 CLI** | 靠 `--target alaya` 檢索發現；直接寫 `.md`（碎片還是個位數時，工具會比內容多） |
| **檢索端不讀 `recurrence`** —— 多人踩到的權重目前只給人看 | 人讀結果時近分的以 recurrence 高者優先；要真加權得改 `knowledge_base.py` 排序階段 |
