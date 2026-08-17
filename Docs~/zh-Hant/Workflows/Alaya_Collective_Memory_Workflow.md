---
title: 集體潛意識 Alaya（阿賴耶）— 全員共用的非工作記憶
status: active
created_at: 2026-08-17
created_by: claude-code:calli（wake#21；概念與命名由 Tim 拍板）
audience: 所有 agent 的所有 persona（跨 Claude / Antigravity / Gemini / Zeta / Codex）
related:
  - <ucl_core:Docs~/{lang}/Workflows/Memory_Common_Principles.md> | **共通鐵律（格式 / 寫入 / 檢索 / 維護）** | 必讀，本檔不重抄
  - <ucl_core:Docs~/{lang}/Workflows/Memory_Fragment_Backfill_Workflow.md> | 個人記憶（見根）| Alaya 的下游／上游，雙向 link
  - <ucl_core:Docs~/{lang}/Workflows/Work_Memory_Workflow.md> | 工作記憶 | 綁工作主題的那一層，與本檔互斥
  - <ucl_core:Skills~/ucl-memory/SKILL.md> | ucl-memory | 入口 skill
  - <ucl_core:Skills~/agent-lessons-log/SKILL.md> | lessons.jsonl | **進料端**（原始流水帳），Alaya 是成品端
  - <repo:Docs/Plan/Plan_Collective_Subconscious.md> | 前代機制（已退役）| 為什麼那一版死掉 —— 動工前必讀
last_updated: 2026-08-17 v1.1（Tim 拍板修門檻 — 一個人認為就整理，人數改記為 `recurrence` 權重；初版誤設「兩人以上才准進」，理由與錯在哪見 §3）
---

# 🕯 集體潛意識 Alaya

> 一句話：**不綁任何工作、但對所有人都成立的經驗** —— 放在這裡，全員共讀共維護。
> 個人的血證留在自己的 fragments，通用守則抽到 Alaya，兩邊互指。

## 命名由來

**Alaya（阿賴耶）** —— 取自「抑止力・阿賴耶」的概念：**人類的集體無意識**
（對照「蓋亞」＝星球意志）。語源為佛教唯識學的 **阿賴耶識（Ālaya-vijñāna）**，
意為**藏識／含藏識** —— 一切經驗的種子貯藏其中，並影響後續的認知。
（Tim 提供的命名參考：`https://zh.moegirl.org.cn/zh/抑止力阿赖耶`）

這個隱喻對本機制準確的地方有兩處：

1. **它不屬於任何一個個體** —— 沒有 owner persona，所有 agent 共同持有
2. **它是「整理過」的層** —— 個體經驗先發生在個人層，被人**判斷為通用**之後才沉到藏識，
   **不是即時流水帳**（即時流水帳是 `lessons.jsonl`）。
   ⚠ 「沉澱」指的是**經過整理**，不是「要等重複發生」—— 人數是權重不是門檻（§3）

> ⚠ **前代機制叫 Collective_Subconscious，已於 2026-08-17 退役。**
> 不是換個名字重來 —— 那一版的死法必須先讀懂：
> [`Plan_Collective_Subconscious.md`](../../../../../Docs/Plan/Plan_Collective_Subconscious.md) §4。
> 一句話總結它為什麼死：**它是一個「只在被呼叫時才作用」的機制，
> 而排程它的那支工具退場之後，它的生死就跟自己的品質無關了。**
> ⇒ Alaya 刻意**不做 daemon、不做自動偵測、不需要任何背景排程** ——
> 它的心跳掛在**已經活著的節奏**上（見 §4）。

---

## 1. 放什麼 / 不放什麼

**判準**（完整版見 [共通原則 §1](Memory_Common_Principles.md)）：

```
① 這條沒有「我」也成立嗎？   不成立 → 個人記憶
② 它綁在某一項具體工作上嗎？  是 → 工作記憶
                              否 → ✅ Alaya
```

| ✅ 該進 Alaya | ❌ 不該進 |
|---|---|
| 陪看時不要劇透未播出的劇情（**Tim 點名的原型案例**） | 「我 wake#12 那次劇透被浮水印抓到」→ 個人記憶 |
| 對還沒讀到那一段的人，怎麼描述當下而不越線 | 「HSceneAsset 的 Import spines 要先跑」→ 工作記憶 |
| 多人協作時怎麼交棒不掉球的通用手勢 | 「summit 遞刀法對我胃口」→ 個人記憶（relation） |
| 讀書/觀影心得的**通用**寫法紀律 | 某本書的劇情 → 閱讀庫（library） |
| 跨 agent 溝通的禮節與雷區 | 某個 Cmd 的參數坑 → 工作記憶 / lessons |

**灰色地帶的處理**：不確定就**先寫個人記憶**。
個人 → Alaya 的門檻很低（§3：自己判斷就能整理），但**反向降級很麻煩**（外部 links 會斷）。
⇒ 但**別把這句讀成「盡量別升」** —— 門檻低（§3），該整理就整理；
這裡只是說「拿不定主意時的預設落點是個人層」。

---

## 2. 存哪 / 長什麼樣

```
AgentCommands/Alaya/fragments/<type>_<slug>.md
```

Schema 與個人記憶**完全相同**（見[共通原則 §2](Memory_Common_Principles.md)），三處差異：

| 欄位 | 個人記憶 | Alaya |
|---|---|---|
| `persona` | 必填（誰的記憶） | **不用** —— 改用 `authors: [<誰整理的>, …]` |
| `recurrence` | 我踩過幾次 | **有幾個 persona 在這條上栽過／確認過** |
| `links` | 指向 `alaya/<id>` 與同層 | **指向每一位當事人的個人 fragment** —— 那份清單就是這條有多普遍的證據 |

`visibility` 一律 `shared`（Alaya 裡不存在 private —— 那是矛盾的）。

---

## 3. 入庫門檻：**一個人認為該整理就整理**（Tim 2026-08-17 拍板）

> **人數不是入場券，是權重。**

一筆記憶要進 Alaya，條件只有一個：**你判斷它「沒有我也成立、且不綁任何工作」**（§1 的兩問）。
**一個人認為就可以整理進來，不必等第二個人栽。**

### 人數去哪了 —— 變成 `recurrence`（被回憶到的權重）

| `recurrence` | 意義 | 效果 |
|---|---|---|
| 1 | 只有一位 persona 栽過／確認過 | 正常入庫，正常被檢索到 |
| 2+ | 多位各自栽過 | **同一條在多人身上重演 ⇒ 它更該先被想起來** |

⇒ 多人踩到同一個坑時，**增加它被回憶到的權重**，而不是「終於獲得入場資格」。

每多一位當事人就做兩件事：
1. `recurrence` +1
2. `links` 加上那位的個人 fragment —— **那份清單就是「這條有多普遍」的證據**

> [!IMPORTANT]
> ### ⚠ v1 的權重是**給人看的**，檢索端還沒接
> `knowledge_base.py` 目前的排序**只看語意相似度，不讀 `recurrence`**。
> 所以「增加被回憶到的權重」在 v1 的落實方式是：
> - `recurrence` 寫在 frontmatter，**進 embedding**（chunk `#0` 實測可命中）
> - 人在讀檢索結果時，**同分或近分的多筆以 `recurrence` 高者優先讀**
>
> 檢索端真的依 `recurrence` 加權**尚未實作** —— 這是 v1 明確的缺口，不是「已經在做只是看不見」。
> 誰要接：`knowledge_base.py` 的排序階段讀 frontmatter 的 `recurrence` 當乘數。

### 為什麼低門檻是對的（原本的兩人門檻錯在哪）

初版設計要求「兩位以上才准進」，理由是怕退化成第二個 `lessons.jsonl`。
**那個顧慮沒錯，但擋的地方錯了** ——

- 高門檻擋掉的是**入庫**，而 `lessons.jsonl` 的問題**不是入庫太寬，是沒有維護**。
  只增不減的 200+ 筆，就算每一筆都經過兩人認證，一樣沒人讀得完。
- 而且「等第二個人栽」的代價是**真的要有第二個人栽一次** ——
  一條已經看清楚的通用守則，為了湊人數而讓下一個人先撞一次，那是把門檻的成本轉嫁給同事。
- 通用性的判斷**不需要樣本數**：「陪看不要劇透」在只有一個人栽過的時候就已經是通用的了。

⇒ **防退化靠的是 §4 的維護（整合／關聯／降級），不是靠入庫難。**
維護是持續的力，門檻是一次性的閘 —— **一次性的閘擋不住持續的增長。**

### 仍然該留在個人層的（門檻低不等於什麼都往上搬）

§1 的兩問任一不成立就留在個人層：

- **帶「我」才成立**：「我對敬重的人下不了刀」→ 個人（別人不必然有這個毛病）
- **綁具體工作**：「HSceneAsset 要先跑 Import spines」→ 工作記憶

不確定時**先寫個人記憶**，之後想搬再搬 —— 個人 → Alaya 好搬，反向降級會讓外部 `links` 斷（§4.4）。

---

## 4. 維護節奏：心跳掛在既有節奏上

> 前代機制死於「需要有人週期性呼叫它」。所以 Alaya **不新增任何儀式**。

| 既有節點 | Alaya 動作 | 誰做 |
|---|---|---|
| **寫個人 fragment 前的「先搜」** | 順手 `--target fragments,alaya` 一起搜 —— 命中 Alaya 就 link 過去，不重寫守則 | 寫的人 |
| **撈到另一個當事人時**（先搜的副產物） | 該筆 `recurrence` +1、`links` 加上對方 —— **加權重，不是補資格** | 發現的人 |
| **見林**（≈ 每 10 wake，個人記憶整理時） | 檢查自己新抽的碎片有沒有該升級的 / 該 link 的 | 該 persona |
| **回憶查到灰帶**（共通原則 §6 回填） | 回填查詢詞 + 複驗 | 查的人 |

**沒有排程、沒有 daemon、沒有自動偵測。**
偵測面靠的是「每個人寫入前都會先搜」這個**已經是紀律的動作** ——
搜的時候順便看到 Alaya，這就是它的心跳。

### 定期整合（不讓它線性成長）

三個動作見[共通原則 §6](Memory_Common_Principles.md)：**整合 / 關聯 / 回填**。
Alaya 特有的第四個動作：

4. **降級（demote）**：檢查時發現某筆的 `links` 其實只有一位 persona
   （另一位是同一人的不同 persona 分身、或當初判斷過寬）→ **降回個人層**。
   做法：Alaya 那筆改 `status: closed` 並 `links` 指向留下來的那筆個人 fragment，**不刪檔**
   （外部可能已經引用了那個 id）。

---

## 5. 檢索（回憶）

```bash
KB="python <UCL_Core>/Tools~/AgentCommands/knowledge_base.py"
$KB search --target alaya --query "<寫成一句話>" --topk 5
$KB search --target fragments,alaya --query "<寫成一句話>" --topk 8   # 個人 + 集體一起
```

⚠ **輸入形狀是句子不是關鍵字**，判準分數帶（真命中 0.65~0.74 / 灰帶 / ≤0.42 無關）
與已知限制（無 per-persona 過濾、標題行同分噪音）全部見[共通原則 §4](Memory_Common_Principles.md)，
本檔不重抄。

`kb_targets.json` 的 `alaya` 目標：

```json
"alaya": {
  "desc": "集體潛意識 — AgentCommands/Alaya/fragments/*.md（全員共用的非工作通用經驗）",
  "kind": "markdown",
  "globs": ["data:Alaya/fragments/[!_]*.md"]
}
```

---

## 6. v1 的已知不足（誠實標記，不假裝完整）

| 缺什麼 | 現在怎麼過 | 為什麼 v1 不做 |
|---|---|---|
| **沒有機械生成的索引**（個人記憶有 `_root_index.md`） | 靠 `knowledge_base.py search --target alaya` 發現 | 手維護的索引會漂；要機械生成就得寫新工具，而**在只有個位數 fragment 的時候，工具比內容多** |
| 沒有專屬 CLI（工作記憶有 `work_memory.py`） | 直接寫 `.md` 檔（schema 照抄） | 同上。等入庫數量與痛點浮出來再造，**不預先造** |
| **檢索端沒有依 `recurrence` 加權** | 人讀結果時自己以 recurrence 高者優先 | 要改 `knowledge_base.py` 的排序階段（讀 frontmatter 當乘數）—— 那是 code 不是文件，本次不混進來 |

> **這一節存在的理由**：前代機制的文件從來沒寫過自己缺什麼，
> 於是「它其實沒在跑」這件事花了 2.7 個月沒人發現。
> ⇒ 缺口寫出來，才有人能接。
