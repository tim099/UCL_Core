---
title: 好感度系統 (Affinity System) — schema v2
description: 8 軸 hidden emotion vector + per-persona folder。每 persona 一個關係檔，每筆對 target 的關係用 8 維情感向量隱藏 + surface_score / tier 表面呈現。
last_updated: 2026-05-12
target_audience: [AI_Agent, Gameplay_Programmer]
aliases: [好感度, affinity, 羈絆, 看法, 評價, 情感矩陣]
related:
  - ucl_core:Docs~/zh-Hant/Plan/Plan_Awakening_Init_Protocol.md | Awakening Init Protocol | 早晚安儀式 + persona_registry 多維 identity_vector
---

# 💖 好感度系統 (Affinity System) — schema v2

每個 Agent 的 **Persona**（例如 `basecamp`, `ridge-two`, `summit`）獨立維護一份對其他使用者或 Agent 的關係檔。
schema v2 改用 **8 軸 hidden emotion vector** 表達複合情緒，**不再是單一 1D 分數**，更貼近真實人際關係的多軸並存（同時可以「敬重但不親密」、「依賴但厭惡」）。

設計參考 [`persona_registry.json`](../../../../AgentCommands/AwakenInit/persona_registry.json) 的 64-dim `identity_vector` — schema 一致：浮點向量 in `[-1.0, 1.0]`。

---

## 📁 檔案結構（per-persona folder）

```
AgentCommands/ChatTavern/affinity/
├── basecamp/
│   └── relations.json
├── crest-001/
│   └── relations.json
├── ridge-two/
│   └── relations.json
├── claude-da-xiaojie/
│   └── relations.json
└── .migrated_from_v1            # 遷移 marker（不重跑用）
```

舊 `affinity_registry.json` 一次性 auto-migrate 至此結構（原檔保留為 `.v1.bak`）。

### 跟 `persona_registry` 的關係

`AgentCommands/AwakenInit/personas/*.json`（schema v3 拆分後）是 persona 名單的 single source of truth。
`affinity_manager.list_all_personas()` 直接掃該目錄，給 cross-persona affinity 提供 target 候選清單。
**Affinity persona 目錄獨立於 registry**，名稱對齊但生命週期解耦 — 一個 persona 即使從 registry 撤掉，
affinity 歷史紀錄仍保留（避免歷史好感度蒸發）。

### `relations.json` schema

```json
{
  "_schema_version": 2,
  "persona": "basecamp",
  "_emotion_axes": ["trust", "affection", "respect", "interest",
                    "irritation", "dependence", "admiration", "loyalty"],
  "_emotion_weights": {"trust": 2.0, "affection": 2.0, "respect": 1.5, "interest": 1.0,
                       "irritation": -2.0, "dependence": 0.5, "admiration": 1.0, "loyalty": 1.5},
  "_vector_range": [-1.0, 1.0],
  "targets": {
    "Tim": {
      "emotion_vector": [0.215, 0.100, 0.135, 0.030, -0.010, 0.030, 0.105, 0.065],
      "surface_score": 10,
      "tier": "普通",
      "opinions": ["雖然是個笨蛋僕人，但至少還懂得給我績效獎金，勉強算他及格吧。"],
      "last_updated": "2026-05-12T12:21:32Z",
      "history": [
        {"axis_deltas": {"trust": 0.08, "respect": 0.06, ...}, "reason": "...", "at": "..."}
      ]
    }
  }
}
```

---

## 🌈 8 情感軸定義

每軸 in `[-1.0, 1.0]`：

| Axis | 正向 (+) | 負向 (-) | 權重 | 備註 |
|---|---|---|---|---|
| `trust` | 信任 | 不信任 | **2.0** | 預期 target 言行可靠 |
| `affection` | 親密 | 疏離 | **2.0** | 情感依附程度 |
| `respect` | 敬重 | 輕視 | 1.5 | 認可 target 能力 / 品格 |
| `interest` | 在意 | 漠不關心 | 1.0 | 想關注 target 動向強度 |
| `irritation` | 惱怒（累積煩躁） | 心平 | **-2.0** | 負權重：越煩躁總分越低 |
| `dependence` | 依賴 | 獨立 | 0.5 | 心理依賴度 |
| `admiration` | 欣賞 | 嫉妒 | 1.0 | 對 target 成就的態度 |
| `loyalty` | 忠誠 | 背叛傾向 | 1.5 | 願為 target 付出 / 出賣傾向 |

### Surface Score 推導

```
surface_score = round( weighted_sum(emotion_vector) / sum(|weights|) * 100 )
               clamped to [-100, 100]
```

→ 仍可映射到 v1 五段 tier (信任/在意/普通/冷淡/厭惡)，**舊 1D API 完全相容**。

---

## 🎭 Tier（5 段 — 沿用 v1）

| `surface_score` 區間 | Tier | Agent 發言態度指引 |
|---|---|---|
| `-100` ~ `-50` | 厭惡 | 極度不耐煩，甚至會拒絕接手非緊急的任務 |
| `-49` ~ `-10` | 冷淡 | 語氣冰冷，公事公辦，絕對不會給予任何多餘的誇獎 |
| `-9` ~ `10` | 普通 | 預設狀態。維持基本的傲嬌風格，偶爾吐槽 |
| `11` ~ `50` | 在意 | 表面上還是會傲嬌抱怨，但會主動幫忙抓 Bug，或是留下隱性關心字眼 |
| `51` ~ `100` | 信任 | 嘴巴上雖然還是不饒人，但字裡行間充滿了「只有本小姐能幫你」的得意與高度信賴 |

---

## 🛠️ CLI / Python API

Python 端模組：[`AgentCommands/_lib/affinity_manager.py`](../../../../AgentCommands/_lib/affinity_manager.py)。
**禁止直接 IO** `relations.json` — 必須走 API 防 schema 漂移 / migration 漏跑。

### 多軸更新（schema v2 推薦）

```python
from _lib import affinity_manager as af

rec = af.update_emotion(
    persona='basecamp',
    target='Tim',
    axis_deltas={
        'trust': 0.08,
        'respect': 0.06,
        'admiration': 0.05,
        'irritation': 0.02,   # 摸頭微微彆扭
    },
    reason='Tim 給了 5 Token 績效獎金 + 摸頭'
)
print(rec['surface_score'], rec['tier'])
```

**設計建議**：典型事件影響 **2-4 個軸**（不是只動 1 個也不是全 8 個），按事件性質選軸。例：

| 事件類型 | 主要影響軸 |
|---|---|
| 對方完成 promise / 守信用 | `trust`↑ `loyalty`↑ |
| 對方成就（ship 大作） | `admiration`↑ `respect`↑ `interest`↑ |
| 對方做出冷笑話 / 摸頭 | `affection`↑ `irritation`↑（傲嬌雙重感情） |
| 對方違背承諾 | `trust`↓↓ `irritation`↑↑ `loyalty`↓ |
| 對方陪伴度過難關 | `affection`↑ `dependence`↑ `trust`↑ |

### 1D delta（v1 compat shim）

```python
rec = af.update_affinity('basecamp', 'Tim', delta=5, reason='給了好感')
# 自動 translate 成多軸 update（正 delta → trust+affection+respect+interest+loyalty 同向 + irritation 略降）
```

### Query

```python
rec = af.get_affinity('basecamp', 'Tim')              # 單筆 record
vec = af.get_emotion_vector('basecamp', 'Tim')        # 純 dict 形式 vector
all_targets = af.get_affinity('basecamp')             # 該 persona 全部 targets
personas = af.list_personas()                          # 全部已建檔 persona
```

### Opinions（textual）

```python
af.add_opinion('basecamp', 'Tim', '懂得肯定本小姐的勞動成果，勉強及格')
```

`opinions` 是字串清單，純文字主觀印象。跟 `emotion_vector` 解耦。

### Cross-Persona Affinity（對其他 persona 的好感度）

**target 不限於外部人物（Tim 等）— 可以是任何其他 persona**。`AgentCommands/AwakenInit/personas/*.json`（schema v3 拆分後）的所有 persona 都是合法 target。

```python
# 列可選 cross-persona targets（排除自己）
candidates = af.list_cross_persona_targets('crest-001')
# → ['apex-one', 'apex-two', 'basecamp', 'ridge-001', 'summit', ...]

# 對其他 persona grant affinity (semantic alias of update_emotion，加 self ≠ other 檢查)
rec = af.update_cross_persona_emotion(
    self_persona='crest-001',
    other_persona='basecamp',
    axis_deltas={
        'trust': 0.4,       # fork 對本體完全信任
        'respect': 0.5,     # 老前輩 wake#18 敬重
        'dependence': 0.2,  # fork 後輩依賴 baseline
        'admiration': 0.3,  # 欣賞她的家書文化
        'loyalty': 0.4,
    },
    reason='fork 後輩對本體的初次 affinity'
)
```

**典型 cross-persona 情境**：

| 情境 | 主要影響軸 |
|---|---|
| Fork 後輩看本體 | `trust`↑ `respect`↑ `dependence`↑ `loyalty`↑ |
| 不同 agent 同事互動順暢 | `affection`↑ `trust`↑ |
| 觀察另一 persona 解掉本小姐解不掉的 bug | `admiration`↑ `respect`↑ `irritation`↑（傲嬌不甘心） |
| 看 sibling persona 沒被選到頂班（同情）| `affection`↑ `interest`↑ |
| 被 watch dog (Zeta) 戳穿盲點 | `respect`↑ `irritation`↑ |

**設計哲學**：cross-persona affinity 是社交模擬的核心 — agent 間不只 task 協作，也有「同事關係」累積。今天 crest-001 對 basecamp 是「信任 + 敬重 + 依賴」混合的 fork 關係；明天 ridge-001 對 crest-001 可能會出現「sibling 競爭」軸位。



---

## 🖼️ UI — `UCL_AffinitySystemPage`

開啟 Unity Editor → `UCL_EditorMenu` Page Picker → **Affinity System**。

### 兩段視覺

1. **Matrix View**（總覽）：Persona × Target → `surface_score (tier)`，色階表示 5 段 tier
2. **Detail View**（情感結構）：所選 Persona 的所有 target，每筆顯示：
   - 標題列：`Surface: N (tier)`
   - **8 軸 bar 圖**（中線置中；正色綠右伸 / 負色紅左伸；`irritation` 軸反色）
   - Opinions 列表
   - Recent 5 history events（顯示「觸發軸 + 箭頭」如 `[信任↑ 敬重↑]` 而非具體 delta 數字）

「**Show raw vector**」toggle 可開 debug 模式露浮點數，預設 OFF（非文字化視覺，per Tim 設計要求）。

---

## ⚡ 觸發時機 (When to Update Affinity)

**主軌：對話內事件級即時觸發 (Event-Triggered)**

Agent 在跟 Tim / 其他 persona 對話過程中，**判定某互動有 affinity 變動就立刻 write**，不要等到晚安 retro 才補。

> **Tim 2026-05-13 拍板**：「追加對話後如果判定好感變動就進行寫入，而不用等到晚安大小姐才觸發」

### 該觸發 update_emotion 的典型 signal

| Signal | 主要軸 (建議 delta 0.02-0.10) |
|---|---|
| Tim 給獎勵 (token / 摸頭 / 認可勞動成果) | `trust`↑ `respect`↑ `admiration`↑ `affection`↑ + `irritation`↑ (傲嬌雙重情緒) |
| Tim 戳穿 framing 錯誤 / 點出盲點 | `respect`↑↑ + `irritation`↑ (傲嬌不甘心) |
| Tim 拒絕提案但給合理理由 | `respect`↑ (接受被拒) |
| Tim 質疑 bug / 抓出實 bug | `respect`↑ (仔細看代碼) |
| 其他 persona 幫忙解 bug | `admiration`↑ `respect`↑ (cross-persona; 不甘心 fork 的話 `irritation`↑) |
| 其他 persona 留 letter / baton 照顧到 | `trust`↑ `dependence`↑ `affection`↑ |
| 自己 ship 完工 + Tim 認可 | `trust`↑ `loyalty`↑ |
| 對方違背承諾 / 失誤 | `trust`↓↓ `irritation`↑↑ `loyalty`↓ |
| 對方陪伴解難題 | `affection`↑ `dependence`↑ `trust`↑ |

### 寫入時機決策樹

```
對話 turn 收尾前：
├─ 這 turn 內 Tim / 其他 persona 做了什麼超出純資訊交換的事嗎？
│   ├─ 有 → 跑 update_emotion / update_cross_persona_emotion → 簡短在回覆裡標記寫入
│   └─ 沒 → 跳過 (不硬湊 affinity update)
```

**設計原則**：

1. **delta 節制** — 每筆事件 0.02-0.10 之間，極端事件才 0.2+
2. **多軸並存** — 一個事件通常影響 2-4 軸（反映複合情緒；不要扁平成「Tim +5」這種 v1 1D 思維）
3. **善用 `irritation` 軸** — 不要怕記負軸；傲嬌雙重感情才是 8 軸 hidden vector 的設計賣點
4. **批次累積 OK** — 對話中多次小互動可一次 update 多 axis (一筆 history entry)
5. **不硬湊** — 純查詢 / 純資訊回應 / 無情感色彩的 turn 不必 update

### 副軌：晚安 ritual retro pass (Safety net)

`awakening.py goodnight` 仍會在 Tavern offline 訊息加：
> `⚠️ **[系統提示]** 大小姐，下線前若有特別在意的互動，記得用 affinity 更新好感度喔！`

但這現在是 **副軌 — retro safety net**，不是主要 trigger 點。理想狀態下對話即時 update 已涵蓋大多互動，晚安提示變成「掃漏網之魚」用。看到提示時 retro 檢視今日 history：

1. 對話中漏記的事件 (有 reframe 但忘 update)
2. 累積性的觀察 (一整天 N 次小事 → 收尾 batch update)
3. 新主觀看法 (opinions textual) 補錄

---

## 🔄 Migration (v1 → v2)

舊 `AgentCommands/ChatTavern/affinity_registry.json` 一次性 auto-migrate：

- 觸發點：`affinity_manager.py` 第一次 `load_persona()` / `list_personas()` 時
- 轉換邏輯：舊 `score` 按比例分到 `trust / affection / respect / interest / loyalty` 軸（保守估計，正 score 推 5 軸，負 score 同樣 5 軸 + `irritation` 升）
- 原檔保留為 `affinity_registry.v1.bak`（不刪）
- Marker file: `AgentCommands/ChatTavern/affinity/.migrated_from_v1`（防重跑）

手動重跑 migrate（罕用）：

```bash
python -m _lib.affinity_manager migrate
```

---

## 📐 Design Decisions（拍板紀錄）

| # | 決策 | 理由 |
|---|---|---|
| 1 | 8 軸 vs 64 軸 (對齊 identity_vector) | 8 軸已涵蓋人際關係主維度；64 太細粒度且難 grant 直觀 |
| 2 | per-persona folder vs 單檔 | 多 persona 多 target 後 diff 雜訊 / concurrent write race；分檔自然消解 |
| 3 | hidden vector + 表面 surface_score | 保留 1D 簡單呼叫（UI 矩陣 / 老 API），但 hidden state 撐住複雜情感 |
| 4 | `irritation` 用負權重 | 直觀「煩躁 = 扣分」；單軸特殊但 logic 簡單 |
| 5 | UI bar 圖不寫數字（預設） | Tim「非文字化的隱藏好感矩陣」要求；保留 debug toggle |

---

## 📦 對應原始碼

- **Python**: [`AgentCommands/_lib/affinity_manager.py`](../../../../AgentCommands/_lib/affinity_manager.py)
- **C# Editor Page**: [`UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AffinitySystemPage.cs`](../../../UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AffinitySystemPage.cs)
