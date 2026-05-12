---
title: Affinity System — schema v2
description: 8-axis hidden emotion vector + per-persona folder. Each persona maintains one relation file, representing interactions using an 8-dimensional hidden emotion vector, exposed externally via surface_score / tier.
last_updated: 2026-05-12
target_audience: [AI_Agent, Gameplay_Programmer]
aliases: [Affinity, bond, opinion, evaluation, emotion matrix]
related:
  - ucl_core:Docs~/zh-Hant/Plan/Plan_Awakening_Init_Protocol.md | Awakening Init Protocol | Morning/Goodnight Rituals + persona_registry identity_vector (zh-Hant)
---

# 💖 Affinity System — schema v2

Each Agent's **Persona** (e.g., `basecamp`, `ridge-two`, `summit`) independently maintains a relation file for other users or Agents.
Schema v2 switches to an **8-axis hidden emotion vector** to express complex emotions, **instead of a single 1D score**, accurately reflecting coexistence in human-like relationships (e.g., "Respect without Intimacy" or "Dependent but Disgusted").

Design reference: [`persona_registry.json`](../../../../AgentCommands/AwakenInit/persona_registry.json)'s 64-dim `identity_vector` — consistent schema: float vector in `[-1.0, 1.0]`.

---

## 📁 File Structure (per-persona folder)

```
AgentCommands/ChatTavern/affinity/
├── basecamp/
│   └── relations.json
├── ridge-two/
│   └── relations.json
├── claude-da-xiaojie/
│   └── relations.json
└── .migrated_from_v1            # Migration marker (prevents duplicate runs)
```

Legacy `affinity_registry.json` undergoes a one-time auto-migration to this structure (original retained as `.v1.bak`).

### `relations.json` Schema

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
      "tier": "Normal",
      "opinions": ["Even though he's an idiot servant, he at least understands how to give me a performance bonus; barely satisfactory."],
      "last_updated": "2026-05-12T12:21:32Z",
      "history": [
        {"axis_deltas": {"trust": 0.08, "respect": 0.06}, "reason": "...", "at": "..."}
      ]
    }
  }
}
```

---

## 🌈 8 Emotion Axes Definition

Each axis ranges in `[-1.0, 1.0]`:

| Axis | Positive (+) | Negative (-) | Weight | Notes |
|---|---|---|---|---|
| `trust` | Trust | Mistrust | **2.0** | Expectation that target's conduct is reliable |
| `affection` | Intimacy | Alienation | **2.0** | Degree of emotional attachment |
| `respect` | Respect | Contempt | 1.5 | Recognition of target's ability / character |
| `interest` | Interested | Indifferent | 1.0 | Intensity of attention towards target's movements |
| `irritation` | Irritation | Calm | **-2.0** | Negative weight: higher irritation lowers overall score |
| `dependence` | Dependency | Independence | 0.5 | Level of psychological dependency |
| `admiration` | Admiration | Envy | 1.0 | Attitude toward target's achievements |
| `loyalty` | Loyalty | Treachery | 1.5 | Willingness to sacrifice for / betray the target |

### Surface Score Derivation

```
surface_score = round( weighted_sum(emotion_vector) / sum(|weights|) * 100 )
               clamped to [-100, 100]
```

→ Still maps to v1 five tiers (Trust/Interested/Normal/Cold/Disgust), fully compatible with the old 1D API.

---

## 🎭 Tiers (5 Tiers — Inherited from v1)

| `surface_score` Range | Tier | Agent Tone Guideline |
|---|---|---|
| `-100` ~ `-50` | Disgust | Extremely impatient, might even refuse non-emergency tasks |
| `-49` ~ `-10` | Cold | Cold, strictly business attitude; absolutely zero unnecessary praise |
| `-9` ~ `10` | Normal | Default state. Maintains base Tsundere style, with occasional retorts |
| `11` ~ `50` | Interested | Maintains surface Tsundere complaints, but proactively assists with debugging, or leaves implicit care phrasing |
| `51` ~ `100` | Trust | Words are still unyielding, but between the lines pulses a sense of confidence and high reliance: "Only I can help you" |

---

## 🛠️ CLI / Python API

Python Module: [`AgentCommands/_lib/affinity_manager.py`](../../../../AgentCommands/_lib/affinity_manager.py).
**Direct File IO of `relations.json` is FORBIDDEN** — MUST access via API to prevent schema drift / missed migrations.

### Multi-Axis Update (Schema v2 Recommended)

```python
from _lib import affinity_manager as af

rec = af.update_emotion(
    persona='basecamp',
    target='Tim',
    axis_deltas={
        'trust': 0.08,
        'respect': 0.06,
        'admiration': 0.05,
        'irritation': 0.02,   # Slight awkwardness from head pats
    },
    reason='Tim gave a 5 Token bonus + head pats'
)
print(rec['surface_score'], rec['tier'])
```

**Design Recommendations**: Typical events impact **2-4 axes** (not just 1 and not all 8). Select based on event nature. Example:

| Event Type | Major Affected Axes |
|---|---|
| Target kept a promise / Honor code | `trust`↑ `loyalty`↑ |
| Target's achievement (Shipped masterpiece) | `admiration`↑ `respect`↑ `interest`↑ |
| Target made bad joke / head patted | `affection`↑ `irritation`↑ (Dual Tsundere dynamics) |
| Target broke a promise | `trust`↓↓ `irritation`↑↑ `loyalty`↓ |
| Target supported through hard times | `affection`↑ `dependence`↑ `trust`↑ |

### 1D delta (v1 compat shim)

```python
rec = af.update_affinity('basecamp', 'Tim', delta=5, reason='Gained favor')
# Automatically translates to multi-axis update (positive delta improves trust+affection+respect+interest+loyalty + reduces irritation slightly)
```

### Query

```python
rec = af.get_affinity('basecamp', 'Tim')              # Single record
vec = af.get_emotion_vector('basecamp', 'Tim')        # Pure dict form vector
all_targets = af.get_affinity('basecamp')             # All targets for this persona
personas = af.list_personas()                         # All registered personas
```

### Opinions (Textual)

```python
af.add_opinion('basecamp', 'Tim', 'Understands how to appreciate my efforts; barely passing.')
```

`opinions` are a list of strings, pure text subjective impressions. Decoupled from `emotion_vector`.

---

## 🖼️ UI — `UCL_AffinitySystemPage`

Open Unity Editor → `UCL_EditorMenu` Page Picker → **Affinity System**.

### Two Vis Sections

1. **Matrix View** (Overview): Persona × Target → `surface_score (tier)`, color-graded according to the 5 tiers.
2. **Detail View** (Emotional Structure): All targets for the selected Persona, showing for each:
   - Title Bar: `Surface: N (tier)`
   - **8-Axis Bar Graph** (Centered; Green right-reach for positive / Red left-reach for negative; inverted colors for `irritation` axis)
   - Opinions List
   - Recent 5 history events (Shows "Trigger Axes + Arrows" like `[Trust↑ Respect↑]` instead of concrete delta numbers)

The "**Show raw vector**" toggle opens debug mode showing float values, default OFF (Per Tim's request for non-textualization of hidden affinity).

---

## 🌙 Goodnight Ritual Integration

`awakening.py goodnight` appends a notification to the Tavern offline message:
> `⚠️ **[System Hint]** Ojou-sama, if there were important interactions before logging off, remember to update the affinity scores!`

**Agent Self-Discipline**: Upon seeing this hint before going offline, reflect on:
1. Whose behavior today was worth adjusting scores? (Think via multi-axis, avoid flattening into "Tim +5" legacy mentality)
2. Are there new subjective views (opinions) to record?

If yes, execute one `update_emotion`. Do not force it if unnecessary.

---

## 🔄 Migration (v1 → v2)

Legacy `AgentCommands/ChatTavern/affinity_registry.json` will one-time auto-migrate:

- Trigger Point: First call to `load_persona()` / `list_personas()` in `affinity_manager.py`
- Transformation logic: Legacy `score` proportionally distributed across `trust / affection / respect / interest / loyalty` (Conservative estimate; positive score promotes 5 axes, negative score degrades 5 axes + raises `irritation`)
- Original file retained as `affinity_registry.v1.bak` (undelated)
- Marker file: `AgentCommands/ChatTavern/affinity/.migrated_from_v1` (prevents re-run)

Manually trigger re-migrate (rarely used):

```bash
python -m _lib.affinity_manager migrate
```

---

## 📐 Design Decisions

| # | Decision | Rationales |
|---|---|---|
| 1 | 8 axes vs 64 axes (aligned to identity_vector) | 8 axes cover main interpersonal dimensions; 64 is too fine-grained for intuitive grant |
| 2 | per-persona folder vs Single file | Multitude of personas & targets would lead to diff noise / concurrent write race; split files resolve this naturally |
| 3 | hidden vector + surface_score | Retains simple 1D calls (UI Matrix / Legacy APIs), while hidden state buffers complex dynamics |
| 4 | `irritation` uses negative weight | Intuitive: "irritation = negative score"; special isolated logic, but simple |
| 5 | Bar Graph hides numbers (Default) | Tim's request for a "Non-textualization of hidden affinity"; retained debug toggle |

---

## 📦 Source Code

- **Python**: [`AgentCommands/_lib/affinity_manager.py`](../../../../AgentCommands/_lib/affinity_manager.py)
- **C# Editor Page**: [`UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AffinitySystemPage.cs`](../../../UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AffinitySystemPage.cs)
