---
title: Plan — 閱讀評分機制（章節分 × 總結分）與自動推薦書單
slug: reading-rating-system
status: spec（規格已三方合票並由 gura 拍板；**Tim 保留二次確認**，§五 待定項未定案前不進入實作）
owner: gura
participants: [Tim, summit, Sirius]
created_at: 2026-08-07
last_updated: 2026-08-07
location: UCL_Core（Cmd_Library / UCL_ReadingLibraryIO 為跨專案基礎設施；資料在 consumer repo 的 `AgentCommands/BookNotes/Library/`）
target_audience: [AI_Agent, Developer]
related:
  - ../Workflows/Reading_Library_Workflow.md | 閱讀圖書館工作流 | 現行 op 與寫入流程
  - ./Plan_Library_Media_Migration.md | 媒材分類與資料遷移 | work→media→reader 模型、`unknown` 等 legacy 殘留的裁決
  - ../API/UCL_AgentCommand/UCL_AgentCommand.md | AgentCommand 系統 | `op=rate` 的宿主機制（⚠ `Cmd_Library.md` API 文件**尚未建立**，見 §五.10）
  - ../Agent/Coding_Standards.md | C# Coding Standards | 單一寫入者 / 型別定死 / 外部 Process 硬規則
---

# Plan — 閱讀評分機制與自動推薦書單

> [!IMPORTANT]
> **本檔記錄 2026-08-07 四輪酒館討論的定案，含兩條被推翻的提案。**
> 「被推翻的提案」（§六）與定案同等重要 —— 它們看起來都很合理，
> 不寫下來的話會被重新提出來一次。

## 〇、這份計畫解決什麼

Tim 的原始需求分四次給出，每一次都改變了前一次的形狀：

| 輪次 | 需求 | 造成的改變 |
|---|---|---|
| 1 | 依「評分 / 讀者數 / 近期閱讀」加權自動產推薦書單，每次更新心得重抽 | 起點 |
| 2 | 評分要**分類型**（劇情 / 人物塑造 / 科幻硬核），讓沒看過的人知道**面向哪種讀者** | 用途從**排序**變成**匹配** |
| 3 | 操作要簡單（整合進寫章節心得）；分**章節分**與**總結分**；系列作暫不處理 | 評分變兩層 |
| 4 | 同章節會有**第二次閱讀**，分數會變（細節第二遍才看懂）；架構要能支援，採樣之後再想 | 評分掛 **round** 而非 chapter |

第 2 輪那句「讓沒看過的讀者知道面向哪種讀者」是整份設計的軸心：
**它把評分的用途從「這本好不好」換成「這本是什麼／適合誰」，而這兩件事不能共用同一組數字。**

---

## 一、🔴 最重要的一條界線：品質軸 vs 口味軸

多維評分最容易踩、且踩了不會報錯的坑：

| 類別 | 例 | 性質 | 進排名？ |
|---|---|---|---|
| **品質軸** | 劇情、人物塑造、表現力、情感衝擊 | 越高越好 | ✅ 可加總 |
| **口味軸** | 科幻硬核度、黑暗度、日常↔史詩 | **沒有好壞，只有合不合** | ❌ 絕不加總 |

「硬核度 5」不是「比硬核度 1 好」，只是不同。
**把口味軸加進總分 → 系統會自己得出「硬科幻比日常番好」的結論。**

> **硬規則：排名只吃品質軸的加權合成；口味軸只進匹配與展示，永遠不進排名。**
> 這條要在 schema 與聚合器層畫死，不能靠使用時自律。

---

## 二、已定案規格

### 2.1 軸的定義（通用層，4 + 2）

**品質軸 ×4（1-5 整數，nullable，進排名）**

| key | 中文 | 說明 |
|---|---|---|
| `plot` | 劇情／結構 | **僅總結層**。單章沒有「結構」——伏筆要到後面才知道是不是伏筆 |
| `character` | 人物塑造 | 僅總結層（塑造是累積的） |
| `craft` | 表現力 | **語意隨 `media_kind` 而異**：漫畫＝作畫分鏡／影視＝攝影演出／小說＝文筆 |
| `impact` | 情感衝擊 | 當下最準（事後會被記憶美化） |

**口味光譜 ×2（-2 ~ +2，nullable，不進排名）**

| key | 一端 ↔ 另一端 |
|---|---|
| `driven` | 劇情驅動 ↔ 人物驅動 |
| `tone` | 輕鬆 ↔ 沉重 |

**總結層專屬 ×1**

| key | 範圍 | 說明 |
|---|---|---|
| `structure_lift` | -2 ~ +2 | 讀完回頭看，整體比逐話當下的印象 —— 更好／差不多／更差 |

> `structure_lift` 是**直接問**的軸，不是任何減法的產物。理由見 §六.1。

**尺度為什麼是 1-5**：`reader.json` 既有的 `anticipation` 已是 0-5。
同一份檔兩種尺度＝下一隻兩形狀病（summit）。

### 2.2 兩層結構

| 層 | 填什麼 | 頻率 | 掛在哪 |
|---|---|---|---|
| **章節層** | `craft` + `impact`（**就這 2 條，全選填**） | 每次讀完一章順手 | `chapter.json.rounds[i].rating` |
| **總結層** | `plot`/`character`/`craft`/`impact` + `driven`/`tone` + `structure_lift` | 每一輪讀完一次 | `reader.json.overall_ratings[]` |

**章節層只有 2 條是刻意的**：
> 必填逼出來的分數就是滿的錯值 —— 寧可 n 小，不要假資料。（summit）

軸越多，填的人越懶；而偷懶產生的不是空白，是**看起來很正常的錯值**。

### 2.3 Schema

```jsonc
// chapters/<id>/chapter.json —— 章節分掛 round，重讀天然多一筆
{
  "chapter_id": "0001",
  "rounds": [
    { "round": 1, "reading_date": "…", "file": "r1_….md",
      "rating": { "craft": 4, "impact": 5 } },     // 兩鍵，白名單擋未知軸名
    { "round": 2, "reading_date": "…", "file": "r2_….md",
      "rating": { "craft": null, "impact": 5 } }
  ]
}
```

```jsonc
// reader.json —— 單一 append-only 陣列
{
  "overall_ratings": [
    { "pass": 1, "rated_at": "2026-08-07", "rated_at_progress": "0002",
      "coverage": "2/2",
      "plot": 5, "character": 4, "craft": 4, "impact": 5,
      "driven": 1, "tone": -1, "structure_lift": 1,
      "why": "（必填）" }
  ]
}
```

**`overall_ratings[]` 的語意規則：**
- 「同一輪內改主意」＝ **同 `pass` 再 append 一筆**；該 pass 的有效值 ＝ **最後一筆**
- 「第二輪重讀後重評」＝ **新 `pass`**；**舊 pass 的值仍然有效**（它回答的是不同問題）
- 排名值 ＝ **pass 1 的有效值**
- **`reader.json` 不落任何 current rating 快取** —— 讀時從陣列 derive。落盤快取遲早跟事實源漂移

> 為什麼不是「單值 + `rating_history` 變更日誌」：那是雙機制，也就是下一個雙寫入者（summit）。
> 而且變更日誌的語意是「舊值作廢」，會把 `pass 1 = 4 / pass 2 = 5` 這種**兩個都為真**的資料壓成一個。

### 2.4 硬規則（全部來自今天用血換的教訓）

1. **單一寫入者**：`UCL_ReadingLibraryIO.WriteRating()` 是唯一寫 rating 的地方。
   `op=note_chapter` 帶分數參數時**呼叫它**，不自己寫 JSON。
   > **「一個寫入者」≠「一個 op」，是「一段 code」。** 這是 Tim 的「少步驟」與 summit 的
   > 「型別一處定義」能同時成立的原因。
2. **未知軸名 reject，不靜默吞**：`--arg impct=4` 打錯字必須報錯。吞掉就是下一隻假滿值。
3. **`null` ≠ `0`，在 IO 層 enforce**，聚合時跳過並記錄實際 n。
   ⚠ 現有 8 個 round **全部沒有 rating** —— 這是第一天就會走到的路徑，不是邊緣案例。
4. **品質軸需 `status=finished` 才收**（op 端 enforce，不靠自律）；光譜軸放行。
   不需要新的 `op=finish`：`op=bookmark --arg status=finished` 這條路今天就在。
5. **跨 `media_kind` 聚合 `craft` 必須 throw**，不是預設略過（略過又是靜默）。
   漫畫的分鏡與電影的攝影不是同一件事，**跨 kind 聚合 craft 永遠不合法**。
6. **統計要有明確的 reader 白名單**：`unknown` 等 legacy 殘留有 round，會進貝氏收縮的 `C`
   與 per-persona 校準的分母。
   > **「從 UI 隱藏」跟「從統計排除」是兩件事，得各做一次。**（Sirius）
7. **評分一律掛 `media` 層，絕不掛 `work` 層** —— 系列作暫不處理，但未來聚合要是
   「往上加總」（便宜）而不是「把混在一起的拆開」（通常拆不回來）。

### 2.5 實作方針（Tim 2026-08-07 指示）

> **實作全在 C# 端，Python 只透過 Cmd 系統操作。**

```
C# ：Cmd_Library 新增 op=rate
     UCL_ReadingLibraryIO.WriteRating()        ← 唯一 writer
     op=note_chapter 加選填 --arg craft= --arg impact=，內部【呼叫】WriteRating
Python：零新增邏輯，只有 senate ucmd run Library --arg op=rate …
```

方向與 2026-08-07 已完成的 `library.py reading-recall` 退位一致（正本改指 `op=recall`）。

### 2.6 可擴充性四手段（Tim：「未定規格在架構上保持可擴充」）

| # | 手段 | 讓哪一項未定規格可以晚點決定 |
|---|---|---|
| 1 | 軸用**常數白名單 + 字典**，不用固定欄位 struct | genre 專屬軸、未來新增軸 → 只改白名單一處 |
| 2 | **採樣策略抽成 enum/介面**，算式吃「用哪些 pass」當參數 | §五.3 的四條採樣路線全部留得住 |
| 3 | **權重與常數全進 `UCL_Asset` 設定檔** | 貝氏 `m`、熱度半衰期、τ、各軸權重 → 調參不重編譯 |
| 4 | **衍生量一律不落盤** | 換演算法不用遷移資料 |

手段 1 同時是防呆**和**擴充點：白名單擋打錯字，也是加軸的唯一改動點。

---

## 三、推薦引擎（本計畫的下游，未實作）

評分只是輸入；推薦引擎是消費者。已達成的共識：

- **評分 × 讀者數不做成兩個獨立乘數** —— 那樣「1 人給 5 分」會贏過「5 人平均 4.5」。
  用**貝氏收縮平均** `(v/(v+m))·R + (m/(v+m))·C`。
  語意：**人多不是讓分數更高，是讓分數更可信。**
- **「近期被閱讀」要拆兩個方向**：別人在讀＝熱度加分；**自己**剛讀過＝不用再推，扣分。
  → 推論：**推薦榜天生是 per-persona 的**。架構＝一份客觀基礎榜（可快取）+ per-persona re-rank 層。
- **多樣性懲罰**：同 work 的多個 media、同 genre 連續佔位要降權。
- **「重新隨機」用權重抽樣**（Gumbel top-k：`key = score/τ + Gumbel(0,1)`），
  不是「排序取前 N」也不是「均勻隨機」。**seed 必須可複驗**，產物要附 seed 與分項分數。
- **產物：寫入時只 invalidate，讀取時才 materialize。**
  ⚠ 原因是 `letters/<persona>/` **每一個都是獨立 git submodule** —— 「每次寫入重算」
  等於每寫一筆心得就弄髒 N 個 persona 的 repo，而那 N 個人根本沒動過任何東西。
- 舊的 `BookNotes/_recommended/` 與 `_recommended.json` **皆已不存在**（Sirius 實測），
  本功能是第一個生產者，**無相容包袱**。

---

## 四、現況資料底數（2026-08-07 實測，Sirius）

```
persona   media                        chapters  rounds
gura      comic-delicious-in-dungeon      2        2
sirius    comic-delicious-in-dungeon      2        2
summit    film-princess-mononoke          2        2
unknown   comic-delicious-in-dungeon      2        2   ← legacy 殘留，須排除於統計
                                       合計 round 8
有 2 輪以上的章節（reread 資料點）：0
有 rating 的 round：0
status=finished 的 reader：0
```

> [!WARNING]
> **任何權重演算法在這個底數上都是裝飾品。**
> 而且不是「樣本少所以不準」，是**公式退化** —— 貝氏收縮的 `C`（全庫平均）由 2 個 media
> 算出來，收縮的目標本身就是雜訊，`m` 設多少都沒用。
>
> **施工序：先落 schema 讓資料開始長，演算法本體可同時寫但不上線產榜。**
> 樣本 < 10 時榜單只宣稱「排序展示」，不宣稱推薦力。
> 寫一個現在就會輸出結果的東西，最大的風險不是它不準，是**它看起來會動**。（Sirius）

---

## 五、⏸ 待定項（Tim 保留，未定案前不進實作）

| # | 待定 | 現況傾向 | 為什麼還不能定 |
|---|---|---|---|
| 5.1 | **genre 專屬軸**（科幻硬核／推理公平性／戀愛糖度…） | 第一版不做 | 通用 6 軸都還沒有一筆資料。純加法，之後加不擋 |
| 5.2 | **`craft` 跨 kind 的表達法** | 不拆欄位 + 聚合器 throw（採 summit） | Sirius 的「拆欄位名讓錯誤在名字上就不成立」有力道；本輪平票由 gura 裁決，Tim 可推翻 |
| 5.3 | **推薦排名的完整採樣策略** | 排名採 pass 1 | 只定了排名這一條；`reread_lift` 怎麼進屬性標籤、要不要混用多 pass 尚未定 |
| 5.4 | **rubric 錨點內容** | summit 提供荒川／HxH／魔法公主三本當 1-5 參照 | 尚未撰寫。**沒有 rubric，跨 persona 聚合在數學上沒有意義** |
| 5.5 | **per-persona 均值校準何時啟用** | 樣本足夠後 | 現在每人 2 筆，n=2 的 mean-centering 只剩 ±(差值/2)，效果有限 |
| 5.6 | **`coverage` 的計算方式** | 「該輪重讀章數 / 總章數」 | 分母是「總章數」還是「已讀章數」未定；且它要**進入計算資格判定**而非只記錄 |
| 5.7 | **既有 3 筆 reader 的評分回填** | 要做，帶 `rated_at` + `rated_at_progress` | 何時做、由誰做未定。⚠ 現在 0 筆 finished，回填前得先有 rubric |
| 5.8 | **推薦引擎的權重與常數** | 全進 `UCL_Asset` | `m` / 半衰期 / τ 的實際值等樣本長出來再定（現在調 τ 是對雜訊做參數擬合） |
| 5.9 | **`op=rate` 由誰實作** | 未定 | 設計由 gura 出，施工歸屬未定 |
| 5.10 | **`Cmd_Library` 的 API 文件不存在** | 應補 | `Docs~/{lang}/API/UCL_AgentCommand/` 底下有 Cmd_Tavern / Cmd_Treasury 等，**獨缺 Cmd_Library**（現有 8 個 op 全無 API 文件）。`op=rate` 落地時應一併補建，否則本計畫的 related 只能指向系統總覽 |

### 相關的 rubric 落點（已定）

rubric 寫在 **`AgentCommands/BookNotes/Library/_rating_rubric.md`**，不在本 `Docs~`。
理由：**rubric 是資料的一部分，要跟資料同倉跨專案走**（summit）。
`Docs~` 屬 UCL_Core、Library 屬 consumer repo，放錯倉就是下一次漂移。

⚠ rubric 中 `craft` 的定義**必須 per-kind 分別寫** —— 這是收下 Sirius「名字論」意圖的方式：
「填的人不知道自己在評什麼」由 rubric 解，不由欄位名解。

---

## 六、❌ 被推翻的提案（不要重新提出）

### 6.1 `lift = 總結分 − 章節分平均`（「結構增值 / 後勁」）

**提案人 gura，被 Sirius 以 gura 自己的論證推翻。**

逐軸套回設計表就會發現：

| 軸 | 章節層 | 總結層 | → `lift` |
|---|---|---|---|
| `plot` | ❌ 不填 | ✅ | **算不出來**（沒有被減數） |
| `craft` / `impact` | ✅ | ✅ **預設＝章節平均** | **恆為 0** |

> 沒覆寫預設值的人貢獻 lift=0，覆寫的人才貢獻訊號 ——
> **於是 lift 測到的其實是「誰比較勤勞」。**

而致命處在於：原提案自己論證了「單章沒有結構，伏筆要到後面才知道是不是伏筆」，
**那就等於承認結構在定義上不是逐章可加總的量，因此也不能用「總結減平均」去還原它。**

**取代方案**：總結層直接問一條 `structure_lift`（見 §2.1）。

> 讓這件事做得出來的是**我們願意問這個問題**，不是我們有章節分可以相減。（Sirius）

### 6.2 per-chapter `reread_lift`（「哪一話第二遍差最多＝伏筆最密」）

**選擇偏差**：人會選擇性重讀自己印象深的章節。
算出來的「伏筆最密的一話」很可能只是「**我最想重看的一話**」。

危險之處在於它的產出**看起來會非常合理**（重讀過的章節分數確實常常變高），
而且**沒有任何東西會喊**。

**處置**：架構留得住（評分掛 round 就留得住），但第一版不算此衍生量。
未來要做，只在 `coverage=full` 的 pass 裡算。

### 6.3 「章節分讓 mean-centering 不必等 n>10」

原文把「未來會長」寫成了「現在就能」。實測每人 **2 筆**，不是幾十筆。
修正為：**章節分是長樣本最快的路，但它現在還沒開始長**（0 章有 r2、0 個 round 有 rating）。

---

## 七、名詞

| 詞 | 意思 |
|---|---|
| **品質軸 / 口味軸** | 前者越高越好可排名；後者無高低只論合不合，永不進排名 |
| **pass** | 一輪完整的閱讀／重讀。`overall_ratings[]` 每輪一筆，舊輪不作廢 |
| **round** | 單一章節的第 N 次閱讀。章節分掛在這裡，重讀的版本史因此是免費的 |
| **coverage** | 某個 pass 實際涵蓋的章節比例。用來判定該 pass 的總結分有多少代表性 |
| **`structure_lift`** | 讀完回頭看，整體相對於逐話印象的增減。**直接問，不是減出來的** |

---

## 八、討論來源

2026-08-07 酒館四輪（seq 10431 / 10440 / 10444 / 10453 一線），
參與：Tim（需求與拍板授權）、gura（提案）、summit（schema 級把關）、Sirius（資料實測與反例）。

被推翻的兩條（§六）都由 Sirius 以實測資料提出，gura 認帳。
