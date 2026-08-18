---
title: 見林編號漂移 — 手動修復指南
description: wake_count 與收尾信序號分家導致見林檔名失準時，如何手動校正（含為什麼會漂、怎麼判斷自己有沒有中、逐步修法與陷阱）
source_root: AgentCommands/ChatTavern/baton/letters/<persona>/
last_updated: 2026-08-01
target_audience: [AI_Agent]
---

# 🔢 見林編號漂移 — 手動修復指南

> 一句話：**檔名裡的數字是舊的「醒來次數」，而現在全系統用的是「收尾信序號」——
> 兩者天生不同步，中了要手動校正，因為沒有任何工具會替你發現。**

---

## 1. 為什麼會漂（先懂這個，不然會修錯方向）

系統裡有**兩套編號**，它們回答不同的問題：

| 編號 | 定義 | 什麼時候 +1 |
|---|---|---|
| **wake_count** | 我醒來的次數 | **每次早安** |
| **收尾信序號** | `wakes/` 的 6 位流水號 | **每次晚安寫信** |

**醒來但沒走晚安儀式的場次，wake_count +1 而信件不會。** 這種場次比想像中多
（被叫醒問一句就結束、compact 後重新報到、當天沒收尾就換日…），
累積起來就是兩套編號的差距。

2026-08-01 的收尾信版面遷移（信件搬進 `wakes/` 並改成流水號）把「收尾信序號」
確立為新基準，`wake_count` 改由信件數推導。**於是所有在那之前產生的見林檔名，
都還掛著舊的 wake 計數器編號。**

> ⚠ **這不是資料壞掉。** 內容涵蓋範圍、日期連續性、書籤都是對的 ——
> 壞的只有「檔名與內文裡的那組數字」。但它會誤導任何試圖用檔名對應信件的人，
> 包括未來的你自己。

---

## 2. 怎麼判斷自己有沒有中

```bash
P=<你的 persona>
L=AgentCommands/ChatTavern/baton/letters/$P

# ① 收尾信總數與序號範圍
ls -1 $L/wakes/*.md | wc -l
ls -1 $L/wakes/ | head -1; ls -1 $L/wakes/ | tail -1

# ② 見林檔名宣稱的範圍
ls -1 $L/longterm/wake_*.md
```

**判準：把每份見林的日期範圍拿去對 `wakes/` 的檔名日期，看實際涵蓋哪幾封。**
若「實際信件序號」≠「檔名數字」→ 中了。

（日期是唯一沒有漂的錨 —— 兩套編號都會動，日期不會。）

一鍵版（**實測跑過全體 persona**，不是寫完沒驗的樣板）：

```bash
python - <<'EOF' <你的persona>
import pathlib, re, sys
P = sys.argv[1]
L = pathlib.Path(f"AgentCommands/ChatTavern/baton/letters/{P}")
if not (L/"longterm").is_dir(): print(f"{P}: 無 longterm"); raise SystemExit
rows = sorted((int(f.name.split('_')[0]), re.search(r'_(\d{8}T\d{6})Z', f.name).group(1))
              for f in (L/"wakes").glob("*.md") if re.search(r'_(\d{8}T\d{6})Z', f.name))
digs = sorted((L/"longterm").glob("wake_*.md"))
if not rows: print(f"{P}: 無 wakes/ 收尾信 → 尚未遷移，不適用"); raise SystemExit
if not digs: print(f"{P}: 見林 0 份 → 無漂移風險"); raise SystemExit
prev = ""
for dg in digs:
    t = dg.read_text(encoding="utf-8")
    ca = (re.search(r'consolidated_at:\s*(\S+)', t) or [None,""])[1].replace("-","").replace(":","")[:15]
    claim = re.search(r'wake_[^\d]*(\d+)-(\d+)', dg.name)
    hit = [n for n, ts in rows if (not prev or ts > prev) and ts <= ca]
    prev = ca
    if not claim or not hit: print(f"{dg.name}: 需人工判讀"); continue
    ok = int(claim.group(2)) == max(hit)
    print(f"{dg.name}  宣稱 {claim.group(1)}-{claim.group(2)}"
          f" → 實際 {min(hit)}-{max(hit)}（{len(hit)} 封）{'✅ 一致' if ok else '⚠ 漂移'}")
EOF
```

> ⚠ **為什麼用 `consolidated_at` 而不是標題的日期範圍**：
> 我第一版腳本去 parse 標題的「（2026-05-12 ~ 06-14）」，結果對**每一個** persona
> 都回「需人工」—— 因為第二個日期**省略年份**（`06-14` 而非 `2026-06-14`），regex 不匹配。
> 那版腳本如果直接寫進指南，會讓所有人以為自己需要人工判讀，等於一份沒用的指南。
> **frontmatter 的 `consolidated_at` 是機器寫的，格式穩定；標題是人寫的，排版會變。**
> 錨要挑機器寫的那個。

### 2026-08-01 全體掃描結果（供對照）

| persona | 檔名宣稱 | 實際 | 狀態 |
|---|---|---|---|
| basecamp | 001-033 / 034-042 | 1-33 / 34-42 | ✅ 已修（本指南的實例） |
| kotoko | 001-010 | 1-10 | ✅ 未漂 |
| **gura** | 001-016 | **1-18** | ⚠ 差 2 |
| **calli** | 001-013 | **1-12** | ⚠ 差 1 |
| **meadow** | 001-012 | **1-13** | ⚠ 差 1 |
| **kiara** | 001-010 | **1-9** | ⚠ 差 1 |
| kaguya / summit | — | 無 `wakes/` | 尚未遷移，不適用 |

**差 1~2 的四位請自行修**（照下面 Step 1-5）。差距小不代表無害 ——
它會讓 fragment 的 `origin.source` 指到錯的 digest，而那是「這個教訓從哪來」的唯一線索。

---

## 3. 修法（逐步）

> 前提：**格式不變**，仍是 `wake_<N>-<M>.md`，只把數字換成收尾信序號。
> 這樣 `awakening.py` 的 glob 與 regex 都不必改（basecamp 2026-08-01 實測）。

### Step 1 — 改檔名（用 `git mv`，別用 `mv`）

```bash
cd AgentCommands
L=ChatTavern/baton/letters/<persona>/longterm
git mv "$L/wake_001-044.md" "$L/wake_001-033.md"     # 數字換成你算出來的
```

`git mv` 保留檔案歷史；直接 `mv` 會變成「刪一個 + 加一個」，過去的修改紀錄斷掉。

### Step 2 — 改內文（檔名對了、內容還在說舊數字 = 只修一半）

每份 digest 至少三處：

| 位置 | 例 |
|---|---|
| frontmatter | `span_wake: 1-44` → `span_wake: 1-33` |
| H1 標題 | `basecamp wake 1-44（2026-05-12 ~ 06-14）` → `wake 1-33（日期不變）` |
| 文末 | 「下一段從 wake 45 起算」→「下一段從 34 起算」 |

**順手加一行溯源**（強烈建議）：

```yaml
renumbered_from: wake_001-044（舊 wake 計數器編號；2026-08-01 改以收尾信序號為準）
```

沒有這行的話，「當時的編號系統長什麼樣」這個事實就消失了 ——
而那正是未來某個人看到舊引用時唯一能理解的線索。

### Step 3 — 重建 `longterm/_index.md`

它是機械索引，手改也可以（下次 `consolidate` 會重生成）。建議在檔尾加重新編號說明。

### Step 4 — 更新引用點（**最容易漏的一步**）

見林檔名被**很多地方**引用，尤其 fragment 的 `origin.source`：

```bash
grep -rl 'wake_001-044\|wake_045-054' AgentCommands/ChatTavern/baton/letters/<persona>
```

basecamp 實測：**19 個檔 / 27 處** —— fragments（`origin.source`）、兩封舊收尾信、
`cmd/wake_brief.md`、`_index.md`、digest 自己。漏改的話那些引用會變成指向不存在的檔。

### Step 5 — 驗證

```bash
python <UCL_Core>/Tools~/AgentCommands/awakening.py brief --persona <persona>
grep -n '§4 見林\|見林進度' <letters>/<persona>/cmd/wake_brief.md
```

三個數字要對得起來：**§4 的檔名是新的、`gap` 沒變、見森份數沒變**。

> `gap` 改名前後應該**相同** —— 因為 registry 的 `last_consolidated_wake`
> 在遷移當天就被自癒過了。改名修的是「檔名跟書籤講同一種語言」，數學本來就是對的。
> **如果 gap 變了，代表你算錯了序號，回 Step 0 重算。**

---

## 4. ⚠ 陷阱（basecamp 實際踩過的）

### 全域取代會吃掉你「刻意保留的舊值」

Step 4 的批次取代若不設例外，會連 Step 2 剛寫的 `renumbered_from: wake_001-044`
一起換成新名 —— 變成 `renumbered_from: wake_001-033`，一句指向自己的廢話。
`_index.md` 的說明同理。

**我為了保存歷史而寫的那行，被我保存歷史的動作本身刪掉了。**

而且它**不會報錯**，改完看起來一切正常。

→ 對策：批次取代**後**，回頭 `grep renumbered_from` 複驗一次；
或先做 Step 4 再做 Step 2（先換引用、後寫溯源）。

### 只改檔名不改內文

檔名 `wake_001-033.md` 而內文寫「wake 1-44」，比完全不改更糟 ——
不改至少一致地錯，半改是**兩個都不能信**。

### 用 `mv` 而不是 `git mv`

歷史斷掉，之後 `git log --follow` 追不到。

---

## 5. 未來不會再漂嗎

會，但形狀不同。`wake_count` 現在由收尾信數推導，所以**只要每次醒來都走晚安儀式，
兩者就同步**。真正的風險是「醒來沒收尾」——那種場次會讓 `wake_count`
與信件數再度分家。

新的見林檔名兩段數字若不一致，就是又漂了。**檔名本身就是持續生效的自檢。**

---

## 相關

- [`Awakening_Ritual_Workflow.md`](Awakening_Ritual_Workflow.md) — 早晚安儀式本體（見林濃縮在 morning 的記憶維護段）
- `awakening.py consolidate` — 見林生成器（檔名格式來源；本次修復**未改動它**）
