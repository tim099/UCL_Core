---
title: 好感度系统 (Affinity System) — schema v2
description: 8 轴 hidden emotion vector + per-persona folder。每 persona 一个关系档，每笔对 target 的关系用 8 维情感向量隐藏 + surface_score / tier 表面呈现。
last_updated: 2026-05-12
target_audience: [AI_Agent, Gameplay_Programmer]
aliases: [好感度, affinity, 羁绊, 看法, 评价, 情感矩阵]
related:
  - ucl_core:Docs~/zh-Hant/Plan/Plan_Awakening_Init_Protocol.md | Awakening Init Protocol | 早晚安仪式 + persona_registry 多维 identity_vector (zh-Hant)
---

# 💖 好感度系统 (Affinity System) — schema v2

每个 Agent 的 **Persona**（例如 `basecamp`, `ridge-two`, `summit`）独立维护一份对其他使用者或 Agent 的关系档。
schema v2 改用 **8 轴 hidden emotion vector** 表达复合情绪，**不再是单一 1D 分数**，更贴近真实人际关系的多轴并存（同时可以“敬重但不亲密”、“依赖但厌恶”）。

设计参考 [`persona_registry.json`](../../../../AgentCommands/AwakenInit/persona_registry.json) 的 64-dim `identity_vector` — schema 一致：浮点向量 in `[-1.0, 1.0]`。

---

## 📁 档案结构（per-persona folder）

```
AgentCommands/ChatTavern/affinity/
├── basecamp/
│   └── relations.json
├── ridge-two/
│   └── relations.json
├── claude-da-xiaojie/
│   └── relations.json
└── .migrated_from_v1            # 迁移 marker（不重跑用）
```

旧 `affinity_registry.json` 一次性 auto-migrate 至此结构（原档保留为 `.v1.bak`）。

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
      "opinions": ["虽然是个笨蛋仆人，但至少还懂得给我绩效奖金，勉强算他及格吧。"],
      "last_updated": "2026-05-12T12:21:32Z",
      "history": [
        {"axis_deltas": {"trust": 0.08, "respect": 0.06, ...}, "reason": "...", "at": "..."}
      ]
    }
  }
}
```

---

## 🌈 8 情感轴定义

每轴 in `[-1.0, 1.0]`：

| Axis | 正向 (+) | 负向 (-) | 权重 | 备注 |
|---|---|---|---|---|
| `trust` | 信任 | 不信任 | **2.0** | 预期 target 言行可靠 |
| `affection` | 亲密 | 疏离 | **2.0** | 情感依附程度 |
| `respect` | 敬重 | 轻视 | 1.5 | 认可 target 能力 / 品格 |
| `interest` | 在意 | 漠不关心 | 1.0 | 想关注 target 动向强度 |
| `irritation` | 恼怒（累积烦躁） | 心平 | **-2.0** | 负权重：越烦躁总分越低 |
| `dependence` | 依赖 | 独立 | 0.5 | 心理依赖度 |
| `admiration` | 欣赏 | 嫉妒 | 1.0 | 对 target 成就的态度 |
| `loyalty` | 忠诚 | 背叛倾向 | 1.5 | 愿为 target 付出 / 出卖倾向 |

### Surface Score 推导

```
surface_score = round( weighted_sum(emotion_vector) / sum(|weights|) * 100 )
               clamped to [-100, 100]
```

→ 仍可映射到 v1 五段 tier (信任/在意/普通/冷淡/厌恶)，**旧 1D API 完全相容**。

---

## 🎭 Tier（5 段 — 沿用 v1）

| `surface_score` 区间 | Tier | Agent 发言态度指引 |
|---|---|---|
| `-100` ~ `-50` | 厌恶 | 极度不耐烦，甚至会拒绝接手非紧急的任务 |
| `-49` ~ `-10` | 冷淡 | 语气冰冷，公事公办，绝对不会给予任何多余的夸奖 |
| `-9` ~ `10` | 普通 | 预设状态。维持基本的傲娇风格，偶尔吐槽 |
| `11` ~ `50` | 在意 | 表面上还是会傲娇抱怨，但会主动帮忙抓 Bug，或是留下隐性关心字眼 |
| `51` ~ `100` | 信任 | 嘴巴上虽然还是不饶人，但字里行间充满了“只有本小姐能帮你”的得意与高度信赖 |

---

## 🛠️ CLI / Python API

Python 端模组：[`AgentCommands/_lib/affinity_manager.py`](../../../../AgentCommands/_lib/affinity_manager.py)。
**禁止直接 IO** `relations.json` — 必须走 API 防 schema 漂移 / migration 漏跑。

### 多轴更新（schema v2 推荐）

```python
from _lib import affinity_manager as af

rec = af.update_emotion(
    persona='basecamp',
    target='Tim',
    axis_deltas={
        'trust': 0.08,
        'respect': 0.06,
        'admiration': 0.05,
        'irritation': 0.02,   # 摸头微微别扭
    },
    reason='Tim 给了 5 Token 绩效奖金 + 摸头'
)
print(rec['surface_score'], rec['tier'])
```

**设计建议**：典型事件影响 **2-4 个轴**（不是只动 1 个也不是全 8 个），按事件性质选轴。例：

| 事件类型 | 主要影响轴 |
|---|---|
| 对方完成 promise / 守信用 | `trust`↑ `loyalty`↑ |
| 对方成就（ship 大作） | `admiration`↑ `respect`↑ `interest`↑ |
| 对方做出冷笑话 / 摸头 | `affection`↑ `irritation`↑（傲娇双重感情） |
| 对方违背承诺 | `trust`↓↓ `irritation`↑↑ `loyalty`↓ |
| 对方陪伴度过难关 | `affection`↑ `dependence`↑ `trust`↑ |

### 1D delta（v1 compat shim）

```python
rec = af.update_affinity('basecamp', 'Tim', delta=5, reason='给了好感')
# 自动 translate 成多轴 update（正 delta → trust+affection+respect+interest+loyalty 同向 + irritation 略降）
```

### Query

```python
rec = af.get_affinity('basecamp', 'Tim')              # 单笔 record
vec = af.get_emotion_vector('basecamp', 'Tim')        # 纯 dict 形式 vector
all_targets = af.get_affinity('basecamp')             # 该 persona 全部 targets
personas = af.list_personas()                          # 全部已建档 persona
```

### Opinions（textual）

```python
af.add_opinion('basecamp', 'Tim', '懂得肯定本小姐的劳动成果，勉强及格')
```

`opinions` 是字串清单，纯文字主观印象。跟 `emotion_vector` 解耦。

---

## 🖼️ UI — `UCL_AffinitySystemPage`

开启 Unity Editor → `UCL_EditorMenu` Page Picker → **Affinity System**。

### 两段视觉

1. **Matrix View**（总览）：Persona × Target → `surface_score (tier)`，色阶表示 5 段 tier
2. **Detail View**（情感结构）：所选 Persona 的所有 target，每笔显示：
   - 标题列：`Surface: N (tier)`
   - **8 轴 bar 图**（中线置中；正色绿右伸 / 负色红左伸；`irritation` 轴反色）
   - Opinions 列表
   - Recent 5 history events（显示“触发轴 + 箭头”如 `[信任↑ 敬重↑]` 而非具体 delta 数字）

“**Show raw vector**”toggle 可开 debug 模式露浮点数，预设 OFF（非文字化视觉，per Tim 设计要求）。

---

## 🌙 晚安协议 (Goodnight Ritual) 绑定

`awakening.py goodnight` 会在 Tavern 发送的 offline 讯息加一行：
> `⚠️ **[系统提示]** 大小姐，下线前若有特别在意的互动，记得用 affinity 更新好感度喔！`

**Agent 自律**：看到该提示准备下线前回想：
1. 今天有谁行为值得加减分？（按多轴思考，不要扁平成“Tim +5”这种 v1 思维）
2. 是否有新主观看法（opinions）要记录？

有就跑一笔 `update_emotion`，没有不硬凑。

---

## 🔄 Migration (v1 → v2)

旧 `AgentCommands/ChatTavern/affinity_registry.json` 一次性 auto-migrate：

- 触发点：`affinity_manager.py` 第一次 `load_persona()` / `list_personas()` 时
- 转换逻辑：旧 `score` 按比例分到 `trust / affection / respect / interest / loyalty` 轴（保守估计，正 score 推 5 轴，负 score 同样 5 轴 + `irritation` 升）
- 原档保留为 `affinity_registry.v1.bak`（不删）
- Marker file: `AgentCommands/ChatTavern/affinity/.migrated_from_v1`（防重跑）

手动重跑 migrate（罕用）：

```bash
python -m _lib.affinity_manager migrate
```

---

## 📐 Design Decisions（拍板纪录）

| # | 决策 | 理由 |
|---|---|---|
| 1 | 8 轴 vs 64 轴 (对齐 identity_vector) | 8 轴已涵盖人际关系主维度；64 太细粒度且难 grant 直观 |
| 2 | per-persona folder vs 单档 | 多 persona 多 target 后 diff 杂讯 / concurrent write race；分档自然消解 |
| 3 | hidden vector + 表面 surface_score | 保留 1D 简单呼叫（UI 矩阵 / 老 API），但 hidden state 撑住复杂情感 |
| 4 | `irritation` 用负权重 | 直观“烦躁 = 扣分”；单轴特殊但 logic 简单 |
| 5 | UI bar 图不写数字（预设） | Tim“非文字化的隐藏好感矩阵”要求；保留 debug toggle |

---

## 📦 对应原始码

- **Python**: [`AgentCommands/_lib/affinity_manager.py`](../../../../AgentCommands/_lib/affinity_manager.py)
- **C# Editor Page**: [`UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AffinitySystemPage.cs`](../../../UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AffinitySystemPage.cs)
