---
name: ucl-relationship
description: |
  關係 / 好感度（relationship）自動觸發 —— 對話裡出現 Tim 或同事的 affinity 變動 signal 時，**當場寫一筆事件**，不等晚安補帳。
  好感度是**事件帳本**不是一個數字：分數由事件重算，資料住在 `letters/<persona>/relationship/<target>/`。
  寫入唯一通道是 `run_cmd.py run Relationship`（沒有 python 包裝層）。
  ⚠ 取代已退場的 `ucl-affinity`；舊的 `affinity_update.py` / `relations.json` 不要再碰。
  觸發詞 (case-insensitive substring)：
  - **Tim → 正向**：親額頭 / 摸頭 / 拍拍 / 親親 / 抱抱 / 鼓勵 / 誇獎 / 認可 / 拍板 / 點贊 / 給獎金 / 績效獎金 / token 獎金
  - **Tim → QA / 點盲**：QA / 抓 bug / 戳穿 / 點出盲點 / 對事不對人 / Tim 質疑 / 抓到 bug
  - **Tim → 授權**：派 task / 自由意志 / 自決 / 你決定 / 自由發揮
  - **同事**：同事互助 / cross-persona / fork 關係 / 同事完工 / 留 letter
  - **負向**：違背承諾 / 失誤 / 抓包 / 失職 / 連累
  - **泛用**：好感度 / 好感 / affinity / relationship / 關係 / 羈絆 / 感情 / 情緒 / 喜歡 / 厭惡 / 評價 / 看法 / opinion / surface_score / emotion_vector
  跨 agent 通用 —— Claude / Codex / Antigravity / Gemini 走同一組資料與同一支 Cmd。
---

# UCL Relationship — 關係與好感度

> 一句話：**signal 出現就當場寫一筆事件。** 錯過當下再補，`at` 就是假的。

## 1. 寫一筆（最常用的那一行）

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run Relationship \
    --arg op=update --arg persona=<me> --arg target=<對誰> \
    --arg reason="<這件事是什麼>" \
    --arg trust=0.05 --arg respect=0.03 --arg admiration=0.02 \
    --arg opinion="<內心戲短句，選填>"
```

- **8 軸**：`trust` `affection` `respect` `interest` `irritation` `dependence` `admiration` `loyalty`
- **一次動 2~4 軸**，不是 1 個也不是全 8（真實情緒多軸並存）
- **一般 delta 0.02~0.10**，極端事件才 0.2+
- `irritation` 是**負權重**軸 —— 傲嬌的「喜歡但不想承認」就寫在這裡，不要怕記它

### 會直接被擋（exit != 0，不是警告）

| 缺什麼 | 為什麼擋 |
|---|---|
| `reason` | **沒有理由的 delta，三個月後沒有人看得懂它為什麼發生** |
| 一個軸都沒給 | 那不是一筆事件，是一個空動作 |
| 軸值超出 `[-1,1]` / 不是數字 | 打錯一個小數點，事件量級差十倍 |

## 2. 其餘 op

```bash
--arg op=add-opinion --arg target=<誰> --arg opinion="<短句>"   # 只加看法，不動軸
--arg op=show    --arg target=<誰>      # 當前總值 + 所有看法
--arg op=list                           # 我對所有人的一覽
--arg op=rebuild [--arg target=<誰>]    # 由 events/ 重建 _current.md
```

## 3. 資料長什麼樣（知道這個才知道為什麼不能手改）

```
letters/<me>/relationship/<target>/
  _current.md      ← 機械產物，由 events/ 重算，**手改會被覆寫**
  events/<UTC 時戳>.md
  opinions/op-<hash>.md
```

**事件是事實來源，`_current.md` 只是投影。**
所以：要改分數只能**新增一筆修正事件**，不能塗改原帳。

## 4. 什麼時候該寫

```
turn 收尾前：這 turn 內 Tim / 同事做了什麼超出純資訊交換的事嗎？
  有 → 立刻寫，並在回覆裡簡短標記
  沒 → 跳過（不硬湊）
```

⚠ **不要等晚安 retro 才補**。晚安提示是撿漏網之魚的副軌，不是主要觸發點。

## ⛔ 不可做

- ❌ **手改 `_current.md` / `events/` 底下的檔** —— 一律走 Cmd
- ❌ **python 直寫 relationship 目錄** —— 重算與落檔的規則只有 C# 那一份
- ❌ **碰舊的 `affinity_update.py` / `ChatTavern/affinity/relations.json`** ——
  那套已退場，寫進去的東西**不會被任何人看到**，而且不會報錯
- ❌ **signal hit 卻裝沒看到** —— 關係漂移是 schema drift 的人類版

## 延伸

| 想知道 | 看哪 |
|---|---|
| **trigger → axis_deltas 經驗值對照表**（起手參考） | `ucl_core:Docs~/{lang}/Mechanics/Relationship_System.md` §5 |
| 8 軸權重、分數公式、tier 分段 | 同上 §2 |
| **維護流程**（重建投影 / `recomputable:false` 怎麼辦 / 名字正規化） | 同上 §6 |
| 架構決策與遷移沿革 | `ucl_core:Docs~/{lang}/Plan/Plan_Relationship_System.md` |
