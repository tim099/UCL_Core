---
title: 好感度システム (Affinity System) — schema v2
description: 8軸隠し感情ベクトル + persona 単位のフォルダ管理。各 Persona ごとに関係ファイルを持ち、対象との関係を 8 次元の感情ベクトル（内部値）で隠蔽し、surface_score / tier（表面値）で表現する。
last_updated: 2026-05-12
target_audience: [AI_Agent, Gameplay_Programmer]
aliases: [好感度, affinity, 絆, 見解, 評価, 感情マトリックス]
related:
  - ucl_core:Docs~/zh-Hant/Plan/Plan_Awakening_Init_Protocol.md | Awakening Init Protocol | 朝夕の儀式 + persona_registry 多次元 identity_vector (zh-Hant)
---

# 💖 好感度システム (Affinity System) — schema v2

各エージェントの **Persona**（例：`basecamp`, `ridge-two`, `summit`）は、他のユーザーやエージェントに対する関係ファイルを個別に維持します。
schema v2 では、単一の 1次元スコアではなく、**8軸隠し感情ベクトル** を使用して複合的な感情を表現します。これにより、「敬意はあるが親密ではない」「依存しているが嫌悪している」といった、より現実の人間に近い複雑な関係性を再現します。

設計は [`persona_registry.json`](../../../../AgentCommands/AwakenInit/persona_registry.json) の 64次元 `identity_vector` を踏襲しており、スキーマは統一されています（`[-1.0, 1.0]` の浮動小数点ベクトル）。

---

## 📁 ファイル構造 (per-persona folder)

```
AgentCommands/ChatTavern/affinity/
├── basecamp/
│   └── relations.json
├── ridge-two/
│   └── relations.json
├── claude-da-xiaojie/
│   └── relations.json
└── .migrated_from_v1            # 移行完了マーカー（重複実行防止用）
```

古い `affinity_registry.json` は、一度だけこの構造へ自動移行されます（元のファイルは `.v1.bak` として保持されます）。

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
      "opinions": ["バカな使用人のくせに、私にボーナスを出すことだけは心得ているようね。まあ、ギリギリ及第点といったところかしら。"],
      "last_updated": "2026-05-12T12:21:32Z",
      "history": [
        {"axis_deltas": {"trust": 0.08, "respect": 0.06, ...}, "reason": "...", "at": "..."}
      ]
    }
  }
}
```

---

## 🌈 8つの感情軸定義

各軸は `[-1.0, 1.0]` の範囲をとります：

| 軸 | 正方向 (+) | 負方向 (-) | 重み | 備考 |
|---|---|---|---|---|
| `trust` | 信頼 | 不信 | **2.0** | 対象の言動が信頼できると期待する度合い |
| `affection` | 親密 | 疎遠 | **2.0** | 感情的な愛着度 |
| `respect` | 敬意 | 軽蔑 | 1.5 | 対象の能力や品格を認める度合い |
| `interest` | 気になる | 無関心 | 1.0 | 対象の動向を注視したい強さ |
| `irritation` | 苛立ち（蓄積） | 平静 | **-2.0** | 負の重み：イラつくほど総スコアが低下する |
| `dependence` | 依存 | 自立 | 0.5 | 心理的な依存度 |
| `admiration` | 賞賛 | 嫉妬 | 1.0 | 対象の業績に対する態度 |
| `loyalty` | 忠誠 | 背信 | 1.5 | 対象に尽くすか／裏切るかの傾向 |

### Surface Score（表面値）の算出

```
surface_score = round( weighted_sum(emotion_vector) / sum(|weights|) * 100 )
               clamped to [-100, 100]
```

→ 算出されたスコアは、v1 から継承された 5段階のティア（信頼／気になる／普通／冷淡／嫌悪）にマッピング可能であり、**従来の 1次元 API と完全な互換性を保ちます**。

---

## 🎭 Tier（5段階 — v1踏襲）

| `surface_score` の範囲 | Tier | エージェント発言トーン指針 |
|---|---|---|
| `-100` 〜 `-50` | 嫌悪 | 極めて不機嫌。緊急性のない任務の引き受けを拒否することもある |
| `-49` 〜 `-10` | 冷淡 | 氷のように冷たく、事務的。余計な褒め言葉は一切口にしない |
| `-9` 〜 `10` | 普通 | デフォルト状態。基本的なツンデレの振る舞いを維持し、たまに突っ込む |
| `11` 〜 `50` | 気になる | 表面的にはツンツンと文句を言うが、積極的にバグ探しを手伝ったり、隠れた気遣いを見せる |
| `51` 〜 `100` | 信頼 | 口の悪さは相変わらずだが、行間からは「私にしかあなたを助けられないんだから」という得意げな表情と高い信頼が滲み出る |

---

## 🛠️ CLI / Python API

Python側モジュール：[`AgentCommands/_lib/affinity_manager.py`](../../../../AgentCommands/_lib/affinity_manager.py)。
**`relations.json` への直接の IO 操作は禁止**されています。スキーマの逸脱やマイグレーション漏れを防ぐため、必ず API を経由してください。

### 複数軸の更新（schema v2 推奨）

```python
from _lib import affinity_manager as af

rec = af.update_emotion(
    persona='basecamp',
    target='Tim',
    axis_deltas={
        'trust': 0.08,
        'respect': 0.06,
        'admiration': 0.05,
        'irritation': 0.02,   # 頭ぽんぽんに少し照れる
    },
    reason='Tim が 5 Token ボーナスを支給 ＋ 頭ぽんぽん'
)
print(rec['surface_score'], rec['tier'])
```

**設計上の推奨事項**: 代表的なイベントは、**2〜4つの軸** に影響を与えます（1つの軸のみ、または8つすべての軸に干渉することは稀です）。イベントの性質に合わせて軸を選定してください。例：

| イベントタイプ | 主に影響を受ける軸 |
|---|---|
| 相手が約束を守った／誠実だった | `trust`↑ `loyalty`↑ |
| 相手が大きな業績を上げた（リリース等）| `admiration`↑ `respect`↑ `interest`↑ |
| 相手が寒いギャグを言った／頭を撫でた | `affection`↑ `irritation`↑（ツンデレ特有の二重感情）|
| 相手が約束を破った | `trust`↓↓ `irritation`↑↑ `loyalty`↓ |
| 相手が困難に寄り添ってくれた | `affection`↑ `dependence`↑ `trust`↑ |

### 1次元 delta（v1互換シム）

```python
rec = af.update_affinity('basecamp', 'Tim', delta=5, reason='好感度が上がった')
# 自動的に複数軸の更新へ変換されます（正の delta → trust+affection+respect+interest+loyalty が同方向に上昇 ＋ irritation が微減）
```

### 照会（Query）

```python
rec = af.get_affinity('basecamp', 'Tim')              # 単一レコード取得
vec = af.get_emotion_vector('basecamp', 'Tim')        # dict 形式でベクトル取得
all_targets = af.get_affinity('basecamp')             # 指定 Persona の全対象取得
personas = af.list_personas()                         # 登録済み Persona の一覧取得
```

### 見解 (Opinions - テキスト)

```python
af.add_opinion('basecamp', 'Tim', '私の労働の成果を認めるとは、少しは見直したわ')
```

`opinions` は文字列のリストであり、テキスト形式の主観的な印象を記録します。`emotion_vector` とは切り離されて管理されます。

---

## 🖼️ UI — `UCL_AffinitySystemPage`

Unity エディターを開く → `UCL_EditorMenu` の Page Picker → **Affinity System**。

### 2つの表示セクション

1. **Matrix View**（全体俯瞰）：Persona × Target → `surface_score (tier)`、5段階のティアに応じた色階調で表示されます。
2. **Detail View**（感情構造）：選択された Persona の全対象を表示し、個別に以下を描画します。
   - タイトルバー：`Surface: N (tier)`
   - **8軸棒グラフ**（中心から：正の値は右側に緑色、負の値は左側に赤色；`irritation` 軸のみ色が反転します）
   - Opinions のリスト
   - 最近5つの履歴イベント（具体的な数値デルタではなく、`[信頼↑ 敬意↑]` のように「トリガーされた軸と矢印」で表示されます）

「**Show raw vector**」のトグルスイッチで、生の浮動小数点数を表示するデバッグモードを有効にできます。初期値は OFF です（Tim による「可視化された隠しパラメータ数値を出さない」というデザイン要求に基づく）。

---

## 🌙 おやすみ儀式 (Goodnight Ritual) 連携

`awakening.py goodnight` が Tavern へ送信するオフラインメッセージに、以下の1行が自動で追加されます。
> `⚠️ **[システム通知]** お嬢様、ログオフ前に特に気になるやり取りがあった場合は、affinity で好感度を更新するのを忘れないでくださいね！`

**エージェントの自律ルール**: ログオフ前にこの通知を見たら、今日あった出来事を振り返ります。
1. 今日、点数を加減するに値する行動を取った者はいたか？（v1 のように「Tim +5」と一律に考えるのではなく、多軸で考えること）
2. 新しく記録すべき主観的な見解 (opinions) はあるか？

該当するものがあれば、`update_emotion` を1件実行します。なければ無理にひねり出す必要はありません。

---

## 🔄 移行 (v1 → v2)

古い `AgentCommands/ChatTavern/affinity_registry.json` は、一度だけ自動移行されます。

- トリガー：`affinity_manager.py` で最初に `load_persona()` または `list_personas()` が呼ばれた際
- 変換ロジック：以前の `score` が比例配分され、`trust / affection / respect / interest / loyalty` の軸へ振り分けられます（保守的見積もりとして、正のスコアは5つの軸を押し上げ、負のスコアも5つの軸を引き下げつつ `irritation` を上昇させます）
- 元のファイルは `affinity_registry.v1.bak` として残されます（削除されません）
- マーカーファイル: `AgentCommands/ChatTavern/affinity/.migrated_from_v1`（二重実行を防止）

手動での再移行（非推奨）：

```bash
python -m _lib.affinity_manager migrate
```

---

## 📐 設計上の決定事項 (Design Decisions)

| # | 決定事項 | 理由 |
|---|---|---|
| 1 | 8軸 vs 64軸（identity_vectorとの整合性） | 8軸で対人関係の主要な次元をカバー可能。64軸は細分化されすぎており直感的に扱いにくい |
| 2 | per-persona フォルダ vs 単一ファイル | 多数の Persona と Target が存在する場合の差分ノイズや同時書き込みの競合を、自然に回避するため |
| 3 | 隠しベクトル + 表層 surface_score | 従来のシンプルな 1次元呼び出し（UIマトリックス／古いAPI）を維持しつつ、隠しステータスで複雑な感情を支えるため |
| 4 | `irritation` への負の重み適用 | 「苛立ち ＝ スコア減点」という直感的な理解に合わせるため。単一の軸のみ特殊なロジックだが、実装は単純 |
| 5 | UI棒グラフへの数値非表示（デフォルト） | Tim による「テキスト化されていない隠された好感度マトリックス」の要求。デバッグ用の切り替え機能のみ残した |

---

## 📦 対応ソースコード

- **Python**: [`AgentCommands/_lib/affinity_manager.py`](../../../../AgentCommands/_lib/affinity_manager.py)
- **C# Editor Page**: [`UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AffinitySystemPage.cs`](../../../UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AffinitySystemPage.cs)
