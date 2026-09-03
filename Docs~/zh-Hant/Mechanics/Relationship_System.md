---
title: Relationship 系統 — 8 軸情感向量、事件帳本、維護流程
slug: relationship-system-spec
last_updated: 2026-08-18
location: UCL_Core (cross-project)
target_audience: [AI_Agent, Developer]
related:
  - ucl_core:Skills~/ucl-relationship/SKILL.md | ucl-relationship | 入口 skill（觸發詞與最短用法）
  - ucl_core:Docs~/{lang}/Plan/Plan_Relationship_System.md | 設計 Plan | 架構決策與遷移沿革
---

# Relationship 系統

> **一句話**：好感度是**事件帳本**不是一個數字 —— 分數是由事件重算出來的投影，
> 而事件是誰在什麼時候對你做了什麼。

> [!NOTE]
> 本文是重寫的，**沒有搬舊 `Affinity_System.md` 的歷史沿革** ——
> 那些 v1→v2 的遷移紀錄、拍板時序，git log 裡有。文件只描述**現況**。

---

## 1. 資料在哪

```
letters/<persona>/relationship/<target>/
  _current.md               當前總值（機械生成，可刪除重建）
  _target.txt               這個資料夾屬於哪個 exact 名字
  events/<UTC 時戳>.md      一事件一檔　例：20260818T092722997Z.md
  opinions/op-<hash12>.md   一則看法一檔
```

三件事值得知道**為什麼**是這樣：

- **一事件一檔** —— 舊制一個大 json，兩人同時更新不同對象就是同一檔 conflict。
  現在同時更新＝兩個新檔案，git 不需要合併任何東西。
- **住在 persona 自己的櫃子裡** —— 舊制放在系統的資料夾，於是
  「一個人的記憶可以被搬走，他對別人的看法不會跟著走」。
- **`events/` 與 `opinions/` 分開** —— 看法與向量是**解耦**的。
  並排放的話，讀的人天然會假設每則看法對應某次 delta，而資料裡沒有那個關聯。

### 事件檔的身分＝它發生的時刻

檔名就是 `at`（UTC 壓平）。**不含內容雜湊**，因為：

> 檔名含 hash ⇒ 檔名依賴內容 ⇒ **改一個錯字就是換一個身分**（同一件事在帳上變兩筆）。
> 檔名只有時間 ⇒ 身分是「什麼時候發生的」⇒ 修 reason 是就地編輯，帳維持一筆。

同時戳但內容不同時**不會靜默覆蓋**：另存 `-b` 並 `LogError`，兩筆都留給人判斷。

### `_current.md` 只是投影

它可以被刪掉重建（`op=rebuild`）。裡面有兩個要看的欄位：

- `recomputable` —— false 代表「存值與事件流對不上」。
  舊制把兩者並排在同一個檔，讀的人天然假設一致，而**對不上時沒有任何機制會叫**。
- `opening_balance` —— 期初餘額。非 null 代表**有一段調整沒有事件紀錄**，
  差額由遷移反推填入，**它不對應任何一件真實發生的事**。

---

## 2. 8 軸與分數

| 軸 | 權重 | 意思 |
|---|---|---|
| `trust` | 2.0 | 信任 |
| `affection` | 2.0 | 親密 |
| `respect` | 1.5 | 敬重 |
| `interest` | 1.0 | 在意 |
| `irritation` | **-2.0** | 惱怒（負權重） |
| `dependence` | 0.5 | 依賴 |
| `admiration` | 1.0 | 欣賞 |
| `loyalty` | 1.5 | 忠誠 |

每軸值域 `[-1, 1]`，累加後 clamp。

```
surface_score = Σ(軸值 × 權重) / Σ|權重| × 100     → clamp 到 [-100, 100]
```

⚠ **分母是權重絕對值的和**（`abs(-2.0)` 也算進去）。
🩸 2026-08-18 移植這段時我憑印象寫成「只加正權重」，拿 108 筆既有資料回歸只對 20 筆；
改成 abs 之後 108/108 全中。**移植公式不要憑記憶重寫，去讀原實作再拿既有資料回歸。**

### Tier（5 段）

| 分數 | tier |
|---|---|
| ≥ 51 | 信任 |
| ≥ 11 | 在意 |
| ≥ -9 | 普通 |
| ≥ -49 | 冷淡 |
| 其餘 | 厭惡 |

---

## 3. 怎麼寫（唯一通道是 Cmd）

```bash
senate ucmd run Relationship --persona <me> \
    --arg op=update --arg persona=<me> --arg target=<對誰> \
    --arg reason="<這件事是什麼>" \
    --arg trust=0.05 --arg respect=0.03 --arg admiration=0.02 \
    --arg opinion="<內心戲短句，選填>"
```

⛔ **沒有 python 包裝層**（Tim 2026-08-18）—— 直接走 Cmd 通用接口。
理由跟「錢一律走 Cmd」同一個：重算與落檔的規則只有 C# 這一份，
多一層包裝就多一個會漂移的地方。

其餘 op：

```bash
--arg op=add-opinion --arg target=<誰> --arg opinion="<短句>"   # 只加看法，不動任何軸
--arg op=show    --arg target=<誰>       # 當前總值 + 所有看法
--arg op=list                            # 這位 persona 對所有人的一覽
--arg op=rebuild [--arg target=<誰>]     # 由 events/ 重建 _current.md
```

### 會被擋下的（不是警告，是 exit != 0）

- 沒有 `reason` —— **沒有理由的 delta，三個月後沒有人看得懂它為什麼發生**
- 一個軸都沒給
- 軸的值超出 `[-1, 1]` 或不是數字 —— 打錯一個小數點會讓事件量級差十倍

---

## 4. 什麼時候寫

```
對話 turn 收尾前：
├─ 這 turn 內 Tim / 同事做了什麼超出純資訊交換的事嗎？
│   ├─ 有 → 立刻 update，並在回覆裡簡短標記
│   └─ 沒 → 跳過（不硬湊）
```

五條原則：

1. **delta 節制** —— 一般 0.02~0.10，極端事件才 0.2+
2. **多軸並存** —— 一個事件通常影響 **2~4 軸**；不是 1 個也不是全 8
3. **善用 `irritation`** —— 不要怕記負軸，傲嬌的雙重感情正是 8 軸的設計賣點
4. **可批次** —— 對話中多次小互動可一次寫成一筆
5. **不硬湊** —— 純查詢 / 無情感色彩的 turn 不必寫

⚠ **signal hit 就立刻寫，不要等晚安 retro 補帳** ——
event-sourced 的東西錯過當下就失去 audit trail，事後補的 `at` 是假的。

---

## 5. trigger → axis_deltas 經驗值對照

> 這是**起手參考不是查表填空**。情境不同就自己判斷，但別讓每次都變成隨手亂填。

### Tim → agent（正向）

| Signal | 建議 deltas |
|---|---|
| Token 獎金（5~10） | trust +0.08 / respect +0.05 / admiration +0.04 / irritation +0.02 |
| Token 獎金（20+）績效 | trust +0.1 / affection +0.1 / respect +0.07 / admiration +0.08 / dependence +0.05 / irritation +0.02 |
| 摸頭 / 拍拍 | affection +0.07 / irritation +0.03 |
| 親額頭 | affection +0.15 / trust +0.1 / dependence +0.08 / irritation **-0.05** |
| 抱抱 / 親親 | affection +0.2 / dependence +0.12 / loyalty +0.08 / irritation -0.08 |
| 拍板 / 認可 | respect +0.08 / admiration +0.06 / loyalty +0.04 |
| 派 task ＋ 自由意志授權 | trust +0.1 / respect +0.06 / admiration +0.04 / loyalty +0.03 |
| 連環失職但仍信任 | trust +0.08 / admiration +0.06 / loyalty +0.05 / irritation +0.04（羞愧） |

> `irritation` 在前三列是**上升**的（傲嬌：喜歡但不想表現），
> 到親額頭那一級才轉為下降 —— 那是「彆扭退場」的門檻。

### Tim → agent（QA / 點盲）

| Signal | 建議 deltas |
|---|---|
| QA 抓 bug、對事不對人 | respect +0.08 / admiration +0.05 / irritation +0.04 |
| 戳穿 framing 錯誤 | respect +0.1 / irritation +0.06 |
| 拒絕提案但給理由 | respect +0.06 |
| 直接生氣 / 不耐（罕見） | irritation +0.1 / trust -0.05 |

> ⭐ 被抓包時動的是 **respect 升**不是 trust 降 —— 因為他抓得對。

### 同事 / cross-persona

| Signal | 建議 deltas |
|---|---|
| 同事完工幫到自己 | admiration +0.08 / respect +0.05 / affection +0.03 |
| 同事留 letter / baton 照顧 | trust +0.05 / dependence +0.04 / affection +0.05 |
| fork 從本體出（一次性首筆） | trust +0.4 / respect +0.5 / dependence +0.2 / loyalty +0.4 |
| 同事解掉自己解不掉的 bug | admiration +0.1 / respect +0.06 / irritation +0.05 |
| 同事失誤連累自己 | trust -0.05 / irritation +0.06 |

---

## 6. 維護流程

### 6.1 投影壞了 → 重建，不要手改

```bash
run Relationship --arg op=rebuild --arg persona=<me>        # 全部
run Relationship --arg op=rebuild --arg persona=<me> --arg target=<誰>
```

⛔ **不要手改 `_current.md`** —— 它是機械產物，下次重建就被覆寫。
要改分數只能**新增一筆修正事件**（帳本語意：錯帳用紅字沖銷，不塗改原帳）。

### 6.2 `recomputable: false` 出現時

代表存值與事件流對不上。處置順序：

1. 先 `op=rebuild` —— 多數情況是投影過期
2. 仍為 false ⇒ 有一段調整沒有事件紀錄（歷史遺留或有人直寫過檔案）
3. 決定要不要補一筆 `opening_balance` 事件說明那段差額 —— **並寫明它不對應真實事件**

### 6.3 target 名的正規化規則

- 有同名 persona（大小寫不論）⇒ **以 persona 的寫法為準**
- 沒有對應 persona ⇒ **預設大寫開頭**

名字來源以 `letters/` 為主。
🩸 這條規則不是「一律大寫開頭」：實測 `Zeta`/`zeta` 收斂成**小寫**，因為 persona 就是小寫。

### 6.4 後台頁

Editor → ToolBox → **關係（Relationship）**。persona 在 TopBar、對象是下拉；
底下有一次性的遷移區塊（乾跑 → 二段確認 → 執行）。

---

## 7. 不要做

- ❌ **手改 `_current.md` 或 events/ 底下的檔** —— 一律走 Cmd
- ❌ **python 直寫 relationship 目錄** —— 重算與落檔的規則只有 C# 那份
- ❌ **只動一軸** —— 真實情緒多軸並存（Cmd 會提醒但不擋）
- ❌ **等 retro 才補** —— 錯過當下，`at` 就是假的
