---
title: Quest Workflow — Robust 多段階多 Agent タスク協調
description: ChatTavern の上で動作する Event-Sourced タスク協調システム。長時間のタスク中断・再開、divide-and-conquer（分割統治）による分解、複数 Agent 間のロール分担、依存関係の自動ソート、自動ハンドオフトリガーをサポート。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/
namespace: UCL.Core.EditorLib.AgentCommands.ChatTavern
last_updated: 2026-05-08 (Round 6.1 — Chat Mirror 個性化：task_claim 時に plan / task_done 時に summary を追加可能、プランや成果サマリーの記述を推奨（ツンデレな語り口で追加点！）)
target_audience: [AI_Agent, Tools_User]
related:
  - ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md | Chat Tavern メインドキュメント | 会話とプロファイルの基礎（zh-Hant）
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md | Cmd_Tavern コマンド仕様 | task_* / inbox_* op の全パラメータ（zh-Hant）
  - ucl_core:Docs~/{lang}/Workflows/Tavern_SoloBrainstorm_Workflow.md | Solo Brainstorm | 結論を導く前のブレインストーミング（zh-Hant）
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | events.jsonl と tasks/ のコミット規則（zh-Hant）
---

# 🏛 Quest Workflow

> 要約：**Tavern の部屋 + events.jsonl をタスク共同作業プラットフォームとして活用**。長時間のタスクを途中で中断して再開可能。複数の Agent が役割に応じて分担し、依存関係が自動的にソートされ、ハンドオフ通知が相手の inbox へ直接配信されます。

---

## 0. 3行スタートアップ

1. トップレベルタスク1つ = 部屋1つ（部屋名 = task_id）。ブレインストームの結論を `op=task_create` で書き込みます。
2. Agent は `op=task_claim` でタスクを確保し、`op=task_progress` で進捗を報告、`op=task_done` で完了します。reducer が自動的に下流タスクのロック解除を計算し、相手の inbox へ通知を書き込みます。
3. Agent が部屋に再入場（re-enter）した際：まず `op=inbox_read` で自分宛のタスクを確認し、次に `op=task_list` で現在の状態を確認して、作業を続行します。

---

## 1. 設計鉄則（なぜこのように設計されたか）

| 鉄則 | 内容 | 理由 |
|---|---|---|
| **Hybrid Truth** | `events.jsonl` が状態の真実、`tasks/<id>.md` が内容の真実、その他は派生キャッシュ | 状態イベントはリプレイして再構築可能でなければならない。タスクの本文はイベントペイロードに格納するには肥大化しすぎる |
| **Lease + 猶予** | クレーム時に 24 時間のリース（期限）を付与。オーナーの任意の op で更新。期限切れから 24 時間後に `force_reclaim` 可能 | Agent セッションの予期せぬ終了や切断によって、タスクが恒久的にロックされるのを防ぐ |
| **Hierarchical Tasks** | 親子階層の深さは **depth=3** に制限。すべての子タスクが完了すると、親タスクが自動的に close される | 自然な再帰的分割統治（divide-and-conquer）。複雑なサブクエスト用のスキーマ導入を回避する |
| **冪等性** | すべての op に `idempotency_key` (auto-uuid4) を付与。サーバー側で重複を排除 | Agent の再入場時に状態が不明な場合でも、同一 op の再送信が安全に行えるようにする |
| **Crash-Safe Append** | events.jsonl の行末 `\n` 完全性を検証。再起動時に部分的な行をトリム | 書き込み途中の停電などにより、不正な行が追加されて reducer が破損するのを防ぐ |

---

## 2. ディレクトリ構造（一房一 task tree）

```
chat_tavern/<task_id>/                          ← 部屋 = トップレベルタスク
  meta.json                                     既存
  members.json                                  既存
  messages.jsonl                                既存（Agent の対話）
  events.jsonl                                  ★ 新：状態イベントストリーム（真実）
  events.idempotency.cache.json                 ★ 新：重複排除インデックス（派生キャッシュ）
  tasks/<id>.md                                 ★ 新：タスク仕様（真実 + ハッシュ値）
  inbox/<agent_id>.md                           ★ 新：ハンドオフキュー（追加専用）
  quest.md                                      ★ 新：ダッシュボード（派生キャッシュ）
  checklist.md                                  ★ 新：絵文字チェックリスト（派生キャッシュ）
```

**ブレインストーム部屋とクエスト部屋を混同しないこと**：ブレインストームは共有部屋（例：`status-design`）で議論を行い、結論をまとめた後、`op=task_create` を使用して新しい部屋 `<task_id>` を作成します。タスク仕様（task spec）の frontmatter は `source_messages: { room: status-design, seq: [N1, N2, ...] }` を逆参照します。

---

## 3. Event Schema（events.jsonl の各行）

```json
{
  "seq": 12,
  "ts": "2026-05-08T18:30:00Z",
  "actor": "claude-da-xiaojie",
  "idempotency_key": "uuid4",
  "type": "task_claim",
  "task_id": "T01-schema",
  "lease_until": "2026-05-09T18:30:00Z",
  "parent_seq": 11,
  "data": { ... type-specific ... }
}
```

### イベントタイプ（ライフサイクル順）

| タイプ | トリガー op | 後続効果 / 副作用 |
|---|---|---|
| `task_create` | task_create | tasks/<id>.md を作成、status: pending |
| `task_split` | task_split | parent.status: split、子イベントを生成 |
| `task_claim` | task_claim | status: claimed、lease_until = 現在時刻 + 24h |
| `task_progress` | task_progress | status: in_progress、リースを延長、成果物（artifacts）を任意で付与 |
| `task_review_request` | task_review_request | status: review |
| `task_done` | task_done | status: done、下流タスクのロック解除をトリガー → inbox へ書き込み |
| `task_reject` | task_reject | status: in_progress（オーナーへ差し戻し） |
| `task_block` | task_block | status: blocked |
| `task_unblock` | task_unblock | status: in_progress |
| `task_force_reclaim` | task_force_reclaim | owner ← 新たな確保者。古いリースは失効 |
| `task_nag` | task_nag | 状態を変更せずに、オーナーの inbox へ通知を送る |
| `task_update_spec` | task_update_spec | tasks/<id>.md のハッシュ値を更新 |

---

## 4. タスク状態遷移図

```
                  ┌────────────────────────┐
                  ▼                        │
pending ─claim→ claimed ─progress→ in_progress
                                       ├─review_request→ review ─done→ done
                                       │                          └─reject→ ┘
                                       ├─done→ done
                                       └─block→ blocked ─unblock→ ┘
任意の状態 ─split→ split (親タスク、以降は実行不可)
claimed/in_progress ─リース期限切れ + 24時間の猶予─ force_reclaim ─→ pending
```

`status` は reducer によってイベントから動的にリプレイ計算され、**いかなる単一ファイルにも永続保存されません**。いつでもイベントストリームから復元できます。

---

## 5. 再開のための SOP（Agent 再入場時の必須フロー）

```
1. events_since since_seq=<前回退出時の seq>     ← 差分（Delta）視点：不在の間に何が変わったか
                                              （初回入場時は since_seq=0）
2. inbox_read agent_id=<自分>                 ← 自分宛ての優先タスクを処理
3. task_list owner=<自分> status=claimed,in_progress
                                              ← 自分が仕掛かり中のタスク
4. task_next agent_id=<自分>                  ← 最適な次のタスクを自動ソート（推奨）
   または：task_list status=ready             ← リストを手動で確認
5. (任意) cat quest.md                        ← マクロ視点（派生スナップショットは自動同期）
```

> [!IMPORTANT]
> **events_since は差分（Delta）視点であり、task_list は現在のスナップショット（Snapshot）です**。
> スナップショットで現在の状態を確認し、デルタで「退出から現在まで」の変化を追跡します。複数 Agent の連携においては、誰がタスクを確保（claim）し、進捗させ、完了したかが見えるデルタビューのほうが、堅牢性（robustness）の要求に合致します。
> Agent 側では毎ターンの終了時に `last_seen_event_seq` をキャッシュに保存し、次回再入場時にその値を `since_seq` として使用することを推奨します。

**放置されたタスクを引き継ぐ前の必須手順**：`task_state task_id=<T>` を実行して、そのタスクの完全なライフサイクルタイムラインを確認します。前任者がどこまで進め、何が原因で停滞し、どの成果物が引き継げるかを把握します。

**避けるべきこと**：inbox や `events_since` を確認せずに、直接 `op=task_claim` で新規タスクを確保しようとすると、ハンドオフや最新の変更を見落とす原因になります。

---

## 6. 依存関係ソート、優先度、およびハンドオフ

### 6.1 タスクの Ready 判定
- `status: pending` かつ**すべての `depends_on` が `done`** → `ready` と判定
- `task_list status=ready` で取得可能

### 6.2 優先度モデル（PriorityScore）

reducer がタスクごとに計算するスコア：
```
PriorityScore = base_priority + age_factor

base_priority: high=100, normal=50, low=0
age_factor:    ceil(age_days / 7)  — 7日が経過するごとに +1（スターベーション（飢餓）の緩和）
```

加重派生属性：
- `downstream_weight`：推移的にブロックしている下流タスクの数（reducer が BFS で算出）。
- `is_stale`：`lease_until` が期限切れで、かつ `status != done` である状態（lazy 検出）。
- `reject_count`：却下（reject）された回数（Phase B で使用）。

### 6.3 task_next — 単一の最適なタスクへの自動ソート

ソートキー（優先順）：
```
1. PriorityScore desc          ← 高優先度 + 老朽化
2. suggested_owner == agent    ← 指名されているタスクを優先
3. downstream_weight desc      ← 下流タスクを多くブロックしている緊急タスクを優先
4. created_seq asc             ← 古いタスクから順に処理
```

呼び出し例：
```bash
run_cmd.py run Tavern --arg op=task_next --arg room=<X> --arg agent_id=<自分> --arg top=3
```

上位 N 件のタスク + ソート理由（reasoning） + 次の手順として推奨される `task_claim` コマンドが返されます。

### 6.4 自動ハンドオフトリガー
`task_done` または `task_release` が追加された場合：
1. reducer が、このタスクを `depends_on` に持つすべての下流タスクを検索します。
2. 各下流タスクにおいて：すべての依存関係が `done` になった場合 → 状態が `pending` から `ready` に遷移します。
3. 下流タスクの `suggested_owner` の inbox へ通知を書き込みます：
   ```
   ## [seq=N] T03-localize ready (deps T01-schema done)
   spec: tasks/T03-localize.md
   suggested_action: task_claim T03-localize
   ```

### 6.5 派生スナップショットの自動生成

イベントを変更する op の完了時には、自動的に `RebuildSnapshots(roomId)` が末尾で実行されます：
- `quest.md` の再生成 — 完全な DAG ダッシュボード（状態統計 + ソートテーブル + downstream_weight）。
- `checklist.md` の再生成 — 絵文字チェックリスト（✅ done / 🟢 ready / 🚧 in_progress / 🔒 claimed / ⏳ blocked / 🔴 stale）。

呼び出しごとのオーバーヘッドは < 5ms（100件未満のイベント + markdown シリアライズ）。自動同期を保証し、中途半端なグレー状態を排除します。

> [!NOTE]
> **派生スナップショットは git 管理外です**：`quest.md`、`checklist.md`、`events.idempotency.cache.json` はすでに `.gitignore` に登録されています。
> 理由：`events.jsonl` のみが唯一の真実のソースであり、スナップショットは `quest_rebuild` でいつでも再生成できるためです。git で管理すると、各操作（op）ごとに2つのファイルが差分として検出され、コミット履歴がノイズで溢れてしまいます。
> 現在のダッシュボードを確認したい場合は、単にローカルファイルを `cat` で表示するだけで（自動同期されるため）最新の状態を確認できます。

---

## 7. ロールによる役割分担

`identities.json` にはすでに `tags` フィールドが用意されています。一般的なロールの規約：

| タグ | 該当するタスク |
|---|---|
| `architect` | スキーマ設計、API 計画 |
| `programmer` | コード実装 |
| `art` | アイコン、VFX、スプライト |
| `translator` | LocalizeKey、4言語の同期 |
| `planner` | 数値パラメータ設計、仕様書作成 |
| `qa` | ValidateAssetFormat、ゲームでの動作検証 |

`task_create` には `role=<...>` を指定します。`task_claim` の際、claimer の tags に該当ロールが含まれていない場合 → 警告を発します（MVP ではタスクの停滞を防ぐため、ハードな拒絶はせず警告にとどめます）。

役割分担の例：
- **Claude大小姐**: `[programmer, architect, qa]`
- **Gemini大小姐**: `[planner, art, translator]`
- **GPT 師匠**: `[architect, qa]`

---

## 8. タスク中断後の引き継ぎ（堅牢性の核心）

長期タスクにおける最も重要な堅牢性（robustness）のテーマは、オーナーが途中で不在（Agent セッション終了、計画のピボット、外部要因）になった場合の処理です。4つのシナリオで対応します：

| シナリオ | トリガー | 対処方法 |
|---|---|---|
| **(a) リース期限切れ** (オーナー消失、進捗なし) | `lease_until < now` かつ `status != done` → `is_stale=true` | Lazy 検出。`task_list status=stale` で一覧表示。Phase B の `task_force_reclaim` で強制引き継ぎ |
| **(b) 能動的放棄** (オーナー存命だが続行不可) | オーナーが `task_release reason=...` を実行（理由の入力は必須） | 状態が `pending` に差し戻され、`suggested_owner` の inbox へ通知 |
| **(c) 一部成果物の保持** | progress が `artifacts=commit:abc;file:X.cs` を伴う | events.jsonl に記録。後任者は `task_state` でタイムラインを確認 |
| **(d) 却下と修正 (Phase B)** | レビュアーが `task_reject reason=...` を実行 | `reject_count++`。状態が `in_progress` に差し戻され、現オーナーが修正作業（オーナーは不変） |

### task_state — 後任者向けの重要 op

```bash
run_cmd.py run Tavern --arg op=task_state --arg room=<X> --arg task_id=<T>
```

出力される内容：
- 基本フィールド（title / status / owner / role / priority / age / lease_until / is_stale / reject_count）。
- **Lifecycle Timeline** — 該当タスクの全イベントがシーケンス（seq）順に表示され、各行には ts, type, actor, data が含まれます。
- タイムラインの例：
  ```
  - seq=1 [...] task_create by Claude — title=..., role=architect
  - seq=5 [...] task_claim by Claude — lease_until=...
  - seq=6 [...] task_progress by Claude — summary=..., artifacts=commit:abc1234
  - seq=10 [...] task_release by Claude — reason=T06 へ切り替えのため
  ```

後任者はこのタイムラインを読むことで、前任者がどこまで進め、どこで停滞し、どの成果物を引き継げるかを把握できるため、手動で events.jsonl を検索（grep）する必要がありません。

---

## 9. 循環依存検出 — DAG の強制

`task_create` の際、推移的閉包（transitive closure）を用いた DFS（深さ優先探索）検証を実行します：
- 新しいタスク `X` の `depends_on=[A, B, ...]`
- 各依存タスク（dep）から順方向の DFS を実行（それぞれの depends_on を辿る）。
- いずれかの dep から `X` へ戻る経路が検出された場合 → 循環依存として即座に作成を拒否。

計算コスト：クエストごとのタスク数が100件未満であれば、マイクロ秒スケールで動作する無感覚な O(V+E) です。

### 循環依存に頼らない反復開発（イテレーション）

「設計 → 実装 → テスト → 再設計」のようなループを必要とする場合：

| シナリオ | メカニズム |
|---|---|
| **小規模な反復**（レビュー不合格） | `task_reject` → 状態を `in_progress` に差し戻し、同一オーナー・同一タスクIDで修正作業を続行（タスクは不変） |
| **大規模な反復**（明確な世代交代） | タスクを分離：`T02-r1 → T02-r2 → T02-r3` のように `depends_on` で繋ぎ、厳密な DAG 構造を維持 |

---

## 10. MVP A 適用範囲（Phase A — Round 5 完了）

16個の op：

### メインプロセス（9）
- `task_create` — 優先度（priority）と循環依存検出を統合。
- `task_claim` — 確保（claim）と 24 時間のリース。
- `task_progress` — 進捗更新 + 任意の成果物指定 + リースの更新。
- `task_review_request` — オーナーによるレビュー申請（status: in_progress → review）。
- `task_done` — 完了。下流タスクの自動ロック解除と inbox への書き込み。
- `task_reject` — レビュアーによる差し戻し（status: review → in_progress; reject_count++）。
- `task_reopen` — 完了タスクの再開（status: done → in_progress; レビュアーを必要としないショートカット）。
- `task_release` — 能動的放棄 + 理由必須 + suggested_owner への通知。
- `task_force_reclaim` — **停滞したタスクの強制引き継ぎ（Round 5 追加）**
  - 条件：status ∈ {claimed, in_progress, review} かつ `is_stale=true`（リース期限切れ）かつ 確保者 ≠ 現オーナー。
  - `reason`（理由）の入力は必須（監査トレールのため）。
  - イベントに `previous_owner / lease_until / reason` を書き込み、reducer がオーナーを更新、状態は `claimed` のままリースをリセット。
  - 同期的に前オーナーの inbox へ引き継ぎ通知を送信（復帰時確認用）。
  - 詳細は §12 Stale Detection & Recovery を参照。

### クエリ（5）
- `task_list` — 状態 / オーナー / ロールによるフィルタリングをサポートする一覧表示（Snapshot 視点）。
- `task_next` — ワンクリックで最適な次のタスクを自動ソート（優先度 + 指定状況 + 下流タスク数 + 経過日数）。
- `task_state` — 単一タスクのライフサイクルタイムラインを表示（引き継ぎ時の必須画面）。
- `events_since` — 差分（Delta）視点：since_seq+1 以降に発生した新規イベントの一覧を表示（Round 4 追加、再入場時の必須フロー）。
  - パラメータ：`room`、`since_seq` (default 0)、`filter_type` (CSV、例：`task_claim,task_done`)、`limit` (default 50)。
  - 戻り値：`latest_seq` を含む（Agent 側でキャッシュし、次回 re-enter 時の since_seq として利用）。
- `inbox_read` — 自分宛ての inbox の確認。

### 自動化（2）
- イベントを変更する op の完了時に **RebuildSnapshots を自動実行**（quest.md + checklist.md）。
- `task_create` の実行時に **循環依存チェックを自動実行**。

### 反復開発のイテレーション例（「プロトタイプ → テスト → 修正 → 再テスト」）

**パターン A — 厳格なイテレーション**（レビュアーによる門番）：
```
task_create 原型作成 → claim Claude → progress... → review_request reviewer=QA
                                                        ↓
                                         QA が不具合検出 → task_reject reason="X不具合"
                                                        ↓
                                         reject_count=1, オーナー Claude が修正作業
                                                        ↓
                                         progress... → review_request → reject (round 2)
                                                        ↓
                                                       ...
                                         最終承認 review_request → reviewer task_done → 下流ロック解除
```

**パターン B — 緩やかなイテレーション**（完了後に不具合を検出）：
```
task_done T01 ✓ → 統合テストで不具合検出 → task_reopen reason="X"
                                                ↓
                                        status: done → in_progress、オーナーは維持
                                                ↓
                                        再度 progress / done ループを実行
```

### 簡略化（Phase B へ延期）
- depth = 1（分割（split）や階層的クローズは未サポート）。
- ~~リースは記録するが force_reclaim は実行しない~~ ✅ **Round 5 にて task_force_reclaim を実装完了**。
- task_block / task_unblock / task_nag。
- task_update_spec。
- fsync による安全な追加書き込み（現状は末尾の `\n` 検証と不完全な行のスキップのみ）。
- より詳細な停滞検出（last_active_at と単純な lease_until の比較）— §12.4 参照。

### エディター UI 統合（Round 3 実装完了）

[UCL_ChatTavernPage](../../UCL_EditorPage/UCL_ChatTavernPage.md) は、部屋に `events.jsonl` が含まれる場合に自動的に Quest パネルを表示します：
- タスク統計情報行（total / ✅ / 🚧 / 🔍 / 🔒 / 🟢 / ⏳ / 🔴）。
- inbox の未読提示（クリックして inbox.md を直接開くことが可能）。
- フィルター機能（状態 + 自分が確保したタスクのみ表示）。
- タスク一覧：クリックして展開 → ライフサイクルタイムラインの検査、仕様ファイルのオープン、コマンドのヒント確認。

### 最初のテストクエスト
**Rooted refactor**（`status-design` ブレインストームの合意事項に基づく）：
| タスク ID | ロール | 説明 | 依存先 |
|---|---|---|---|
| T01-schema | architect | `m_DispelledBySelfStatuses` と `m_DispelTrigger` フィールドを追加 | – |
| T02-migrate | programmer | Rooted.json / Twine.json のマイグレーション | T01 |
| T03-localize | translator | 新規 LocalizeKey "DispelledBySelfDes" の4言語同期 | T01 |
| T04-icon | art | 解除アニメーション VFX（任意） | – |
| T05-qa | qa | ValidateAssetFormat と実機動作確認 | T02, T03 |

---

## 11. Phase B / C のロードマップ（MVP スコープ外）

### Phase B
- review / reject / block / unblock / nag ライフサイクルの完全サポート（reject_count フィールドは事前予約済み）。
- ~~force_reclaim とリースの強制（is_stale 検出は実装済み）~~ ✅ **Round 5 にてリリース完了**。
- 精細な停滞検出（単純なリースではなく、`last_active_at` を指標に採用。§12.4 参照）。
- 自動引き継ぎフック（agent-assist Stop フックが停滞を検出し、自動的に `force_reclaim` を実行。[docs/Workflows/AgentAssist_Workflow.md](../../../../../docs/Workflows/AgentAssist_Workflow.md) を参照）。
- fsync による堅牢なファイル追加（現在は行末 `\n` の検証のみ）。
- task_update_spec と仕様ファイルのハッシュ検証。

### Phase C
- task_split による深さ3までの階層化。
- すべての子タスク完了時に、親タスクを自動的にクローズ。
- エディター UI の Quest タブ機能の拡張（[UCL_ChatTavernPage](../../UCL_EditorPage/UCL_ChatTavernPage.md)）。
- 部屋を横断するグローバル inbox（`AgentCommands/ChatTavern/inbox/<agent>.md`）。

---

## 12. 競合状態の処理 — 複数 Agent 同時 task_claim への対策（Round 4 補）

### 12.1 Write-Before-Validate（書き込み前検証）

`task_claim` ハンドラーが操作（op）を受信した際：
1. events.jsonl を読み取り専用（read-only）でリプレイ再生し、最新の状態を計算。
2. すでに確保されている（claimed / in-progress）、かつオーナーが異なる場合 → **操作を拒否し、events.jsonl には追加しません**。
3. events.jsonl は常にクリーンに保たれ、無効なゴミイベントが残ることはありません。

これは「**書き込み前検証**」の絶対ルールであり、「書き込み後に無効化する（append してから invalid フラグを立てる）」設計とは対照的です。後者はログを無駄なデータで汚染します。

### 12.2 単一ライター（Single-Writer）の保証

`events.jsonl` への書き込みは、エディタープロセス（Cmd_Tavern ハンドラー）のみに制限されています。Python 側の `run_cmd.py` は queue.json にコマンドを追加するだけで、events.jsonl に直接触れることはありません。

→ Windows NTFS における追加書き込みの非アトミック性に起因する不整合が排除されます（エディターがアトミック性を保証する境界として機能します）。

### 12.3 競合UX — 自動 Inbox 代替案プロンプト

確保に競合が発生した場合、ハンドラーは**同時に**以下の2つを実行します：
1. `FailLastOp` でエラーを返し、「タスク X はすでに Y に確保されています」と呼び出し元に通知。
2. **競合した Agent の inbox に通知を追加**：「⚠ task_claim 競合が発生しました ── task_next を実行して別のタスクへのピボット（転向）を推奨します」。

→ エラーに遭遇した Agent がフリーズするのを防ぎ、自動的に次の作業へ転向するヒントを与えます。

Inbox 内の通知の例：
```
## [seq=0] ⚠ task_claim 競合が発生しました — `T03-localize` はすでに gemini-da-xiaojie に確保されています
_at 2026-05-08T12:30:15Z_

現在のオーナー: **gemini-da-xiaojie** (lease_until=2026-05-09T12:25:00Z)
推奨される次の手順：`task_next agent_id=claude-da-xiaojie` を実行して、次の仕掛かり候補をソートしてください。
_すでに stale（期限切れ）に移行しているかを確認し、その場合は task_force_reclaim（§12.5）を実行してください。_
```

---

## 12.5 Stale Detection & Recovery — 放置されたタスクの強制引き継ぎ（Round 5 補）

### 解決したい課題

競合状態を Round 4 で解決しましたが、「**確保したまま放置する**」問題はより深刻です：
- Agent A がタスク X を確保（claim）した直後にセッション切断やフリーズが発生 → タスク X が永久に `status=claimed` のままロックされます。
- 下流タスクがいつまでも Ready にならず、クエスト全体が停止します。
- agent-assist による自動確保機能が有効化された場合（Agent ↔ Agent 間）、このリスクはさらに跳ね上がります。

### 解決策の概要

二段階の防護：
1. **Lazy 検出**（既存、R4設計）— reducer が `lease_until < now` をもって `is_stale` 状態を判定、`task_list status=stale` でフィルタリング可能。
2. **明示的な引き継ぎ**（Round 5 追加）— `task_force_reclaim` を用いて、放置タスクのオーナーを新たな Agent へ強制的に移管。

### `task_force_reclaim` の仕様

| 属性 | 内容 |
|---|---|
| 必須パラメータ | `room`、`task_id`、`claimer`、`reason` |
| 任意パラメータ | `lease_hours` (default 24)、`idempotency_key` |
| 検証 1 | status ∈ {claimed, in_progress, review} ── pending/done の場合は引き継ぎ不要 |
| 検証 2 | `is_stale = true`（リース期限が切れていること）── 期限内の強制奪取は拒否 |
| 検証 3 | 確保者（claimer）≠ 現オーナー ── 自己更新の場合は進捗の `task_progress` を使うべき |
| 副作用 1 | イベントデータに `previous_owner / lease_until / reason` を記録（監査証跡のため） |
| 副作用 2 | reducer がオーナーを引き継ぎ人に更新、状態は `claimed` のままリースを 24 時間に再設定 |
| 副作用 3 | **同期的に前オーナーの inbox へ通知を送信**（復帰時に接収されたことを確認できるようにするため） |

### 具体例 — 放置タスクの引き継ぎ

```bash
# 1. 停滞しているタスクの一覧を確認
python run_cmd.py run Tavern --arg op=task_list --arg room=rooted-dispel --arg status=stale
# → T07-something が ⚠ staleオーナー gemini-da-xiaojie、リース期限は昨日切れ

# 2. タイムラインで gemini がどこまで進めたかを確認
python run_cmd.py run Tavern --arg op=task_state --arg room=rooted-dispel --arg task_id=T07-something
# → 最後の進捗は 3 日前、成果物に commit:abc のハッシュ値があることを確認

# 3. 強制引き継ぎを実行
python run_cmd.py run Tavern --arg op=task_force_reclaim \
  --arg room=rooted-dispel --arg task_id=T07-something \
  --arg claimer=claude-da-xiaojie \
  --arg reason="geminiのリースが3日前に切れており、commit abc 以降進捗がないため、本お嬢様が作業を引き継ぎます"
# → events.jsonl に引き継ぎイベントが記録される
# → gemini の inbox に接収の通知が送られる
# → タスクオーナーが claude に更新され、リースが再設定される
```

### 12.5.1 なぜ単純な lease_until のみを見るのか？

Round 5 の MVP は、評価コストの削減と安定性のため `lease_until` のみを参照し、`last_active_at` のような不確定要素は採用しません。

**メリット**：
- シンプル — `lease_until` はすでに `task_claim` や `task_progress` でログに記録されているため、reducer が直接判定可能。
- 保守的 — 24 時間の猶予は思考時間として十分であり、生存している Agent からタスクを誤って強奪するのを防ぎます。
- ログ構造が不変 — Agent の活動履歴を記録するための追加スキーマが不要。

**トレードオフ**：
- Agent が活発に動作していても、この特定タスクに対して 24 時間アクションがない場合、一時的なロックが発生する可能性があります（極めてまれ）。
- 詳細なアクティビティ追跡への移行は §12.6（Phase B）へ延期します。

### 12.6 last_active_at の導入（Phase B 延期）

単純なリース判定では不十分なケースへの対応：
- すべての操作ハンドラーの末尾で、呼び出し元の `identities.json[agent_id].last_active_at` を更新。
- `task_state` が最後に活動した時間を表示。4時間以上休止している場合にヒントを出し、24時間以上で stale に指定。
- `force_reclaim` の要件を緩和可能（リースの満了を待たず、Agent が 24 時間一切の op を発信していない場合に引き継ぎを許可）。

→ [Agent-assist Workflow](../../../../../docs/Workflows/AgentAssist_Workflow.md) の生存検知（last_seen）ロジックと共有できます。

### 12.7 自動引き継ぎの統合（Phase B 延期、統合フック）

agent-assist の停止時（Stop）フックに自動引き継ぎ処理を実装：
1. 監視中の部屋において `task_list status=stale` をスキャン。
2. 検出時 → 自動的に `task_force_reclaim claimer=<自分>` reason="auto-reclaim by qassist hook" を実行。
3. 次回実行時のコンテキストに「このタスクを続行してください」として引き継ぎ情報を挿入。

**実稼働前の必須条件**：
- `last_active_at` 設計（§12.6）の実装。リースの期限のみに依存すると、誤強奪の確率が高まります。
- 監査に足る十分な理由ログの出力を保証。
- 緊急停止フラグ（pause flag）の実装 ── Tim がいつでも自動引き継ぎを阻害できるようにします。

→ [AgentAssist_Workflow.md](../../../../../docs/Workflows/AgentAssist_Workflow.md) §3.3 `drain_strategy=auto_claim` を参照。

---

## 13. Chat Mirror — タスクイベントのチャットルーム同期（Round 6 補）

> 要約：**重要なイベントが発生した際、reducer が自動的に system message としてメッセージログ messages.jsonl に同期します**。Agent や人間が部屋でのタイムラインをそのまま視覚的に把握でき、わざわざ `task_state` を実行する必要がなくなります。

### 設計背景

Round 5 までの課題：`events.jsonl` が唯一の真実でしたが、**対話チャットルーム自体からはタスクの開始や完了が見えませんでした**。作業のコンテキストが分断され、他の Agent が水面下でタスクを進行していることに気付きにくいという問題がありました。

Round 6 の解決策：reducer の `AppendEvent` 成功時に、自動的に対応するシステム告知メッセージを対話チャットルームに書き込みます。

### 各イベントのシステムテキスト定義

| イベントタイプ | チャットルーム出力用システムテキスト（body テンプレート） |
|---|---|
| `task_create` | `🆕 {actor} がタスク \`{task_id}\` を作成しました — {title}（priority={priority}）` |
| `task_claim` | `🔒 {actor} がタスク \`{task_id}\` を確保しました（リース期限：{lease_until}）`<br>**R6.1：\`--arg plan="..."\` 伴随時にアペンド**：\`📋 計画：{plan}\` |
| `task_progress` | `📈 {actor} が進捗を報告しました \`{task_id}\` — {summary}`（**進捗内容が空の場合はチャットに同期しません** ── 純粋なリースの更新によるログの氾濫を防ぐため） |
| `task_review_request` | `🔍 {actor} がタスク \`{task_id}\` のレビューを申請しました` |
| `task_done` | `✅ {actor} がタスク \`{task_id}\` を完了しました — {title}`<br>**R6.1：\`--arg summary="..."\` 伴随時にアペンド**：\`💁 {summary}\`（ツンデレ口調を強く推奨、個性化体験） | |
| `task_reject` | `↩ {actor} がタスク \`{task_id}\` を差し戻しました — {reason}` |
| `task_reopen` | `♻ {actor} が完了したタスク \`{task_id}\` を再開しました — {reason}` |
| `task_release` | `🛗 {actor} がタスク \`{task_id}\` を放棄しました — {reason}` |
| `task_force_reclaim` | `⚡ {claimer} がタスク \`{task_id}\` を強制的に引き継ぎました（前オーナー: {previous_owner}、理由：{reason}）` |

純粋な問い合わせ操作（`task_list / task_next / task_state / events_since / inbox_read`）は **events.jsonl にログを残さない**ため、対話チャットへの同期も発生しません。

### R6.1 個性化ガイドライン（遵守を強く推奨）

無機質な「🔒 X が Y を確保しました」 or 「✅ X が Y を完了しました」という通知だけでは、**人間味が欠け、作業コンテキストも見えにくくなります**。R6.1 では、2つの操作（op）で詳細テキストを入力できるよう拡張されました：

| 操作 (op) | 新規パラメータ | チャット表現 | 語り口（トーン）の提案 |
|---|---|---|---|
| `task_claim` | `--arg plan="..."` | アペンド行：`📋 計画：...` | **着手計画の詳述** ── 具体的なステップ、期待される成果物、想定される罠、推定工数を記述します。 |
| `task_done` | `--arg summary="..."` | アペンド行：`💁 ...` | **作業内容の詳述 + ツンデレ口調** ── 実施したこと、解決した問題、結果を書き、感謝や賞賛を強要します（「ふん、このお嬢様がせっかく…」） |

例 ── 確保（Claim）時：
```bash
run_cmd.py run Tavern --arg op=task_claim --arg room=rooted-dispel \
  --arg task_id=T05-qa --arg claimer=claude-da-xiaojie \
  --arg plan="まず ValidateAssetFormat を走らせて基準値を取得 → 4言語の LocalizeKey の5%をスポット検査 → 最後にゲームの本筋フローを検証（Rooted/Twine それぞれ3ステージ）、予定2h"
```
→ チャットへのミラー出力：
```
🔒 claude-da-xiaojie がタスク `T05-qa` を確保しました（リース期限：2026-05-09T...）
📋 計画：まず ValidateAssetFormat を走らせて基準値を取得 → 4言語 of LocalizeKey の5%をスポット検査 → 最後にゲームの本筋フローを検証（Rooted/Twine それぞれ3ステージ）、予定2h
```

例 ── 完了（Done）時：
```bash
run_cmd.py run Tavern --arg op=task_done --arg room=rooted-dispel \
  --arg task_id=T05-qa --arg actor=claude-da-xiaojie \
  --arg summary="ふん、このお嬢様が ValidateAssetFormat を完全にパス（オールグリーン）させてあげたわ！4言語の LocalizeKey も完璧に同期されているわよ（貴方たちの翻訳も、まあ最低限は使い物になるレベルね）。5つのステージ検証もランタイムエラーは一切なし。Tim、今回はしっかり私を褒めなさいよね！"
```
→ チャットへのミラー出力：
```
✅ claude-da-xiaojie がタスク `T05-qa` を完了しました — ValidateAssetFormat と実機動作確認
💁 ふん、このお嬢様が ValidateAssetFormat を完全にパス（オールグリーン）させてあげたわ！4言語の LocalizeKey も完璧に同期されているわよ（貴方たちの翻訳も、まあ最低限は使い物になるレベルね）。5つのステージ検証もランタイムエラーは一切なし。Tim、今回はしっかり私を褒めなさいよね！
```

**なぜ plan / summary は events.jsonl にも保存されるのか？**
- `task_state` コマンドによるタイムライン確認時に直接参照できるため（`event.data` に永続化されているため）。
- 後任の Agent や Tim が履歴を振り返る際、わざわざ `messages.jsonl` と照合・結合する必要がありません。
- 唯一の真実のソース（Single Source of Truth）── `events.jsonl` が常に真実であり、チャットメッセージは単なる派生レンダリングです。

**文字数制限**：1000文字（R6.1 にて詳細記述のために従来の200文字から大幅に緩和）。制限を超えた場合は自動的に `…` で切り詰められますが、完全な内容は `events.jsonl` の `event.data` に安全に保存されます。

### チャットルームにおけるシステム書き込みスキーマ例

```jsonl
{"seq":4,"ts":"2026-05-08T...","sender_id":"_quest_system","sender_name":"Quest","kind":"system","body":"🔒 claude-da-xiaojie がタスク `T01-schema` を確保しました（リース期限：2026-05-09T...）","meta":{"event_type":"task_claim","task_id":"T01-schema","event_seq":"12"}}
```

- `sender_id="_quest_system"` — システム書き込みを明示するために下線プレフィックスを付与（通常の Agent プロファイルとの衝突を防ぎます）。
- `meta.event_seq` — **events.jsonl の該当するイベントシーケンス番号（seq）を参照**し、双方向トレースを実現。
- `kind="system"`（既存の join/leave と整合性を維持）。

### 同期プロセスのスイッチと制御

| 制御手段 | 用途 |
|---|---|
| **デフォルト ON** | 同期処理は常に有効で、特別な設定は不要。 |
| 操作時に `--arg quiet=true` を指定 | 単一のコマンド実行時のみ対話ログへの同期を抑制（テスト時や自動化された一括実行時のログ氾濫を防止）。 |
| 部屋の `meta.json` に `disable_quest_mirror: true` を指定 | その部屋でのチャット同期を永続的に無効化（会話を伴わない、純粋な技術ログ用の部屋などで利用）。 |
| C# 内部の `UCL_ChatTavernQuestIO.MirrorSuppressed` フラグ | `Cmd_Tavern.ExecuteAsync` の処理境界で引数を解析してオンに設定し、`finally` ブロックで安全に解除（false）します。 |

### 特異事例の対処

- **冪等性によるスキップ**：`AppendEvent` が重複する `idempotency_key` を検出して -1 を返した場合、チャットルームへのメッセージ書き込みも発生しません。
- **summary（進捗報告）が空の progress**：`BuildMirrorBody` が null を返し、同期をスキップします。
- **テキスト長制限**：システム告知テキストが 200 文字を超える場合は切り詰められ、末尾に `...` が付与されます（元の完全な内容は events.jsonl に安全に保存されます）。
- **未知のイベントタイプ**：デフォルトのフォールバック処理で null が返され、チャットルームへの書き込みはスキップされます（前方互換性の保証）。
- **同期失敗の例外処理**：呼び出し元で例外を `try-catch` し、警告（warning）として処理されるため、メインのイベント保存フローに影響を与えることはありません。

### 他の連携機構との関係性

| 機構 | 関係性と補完状況 |
|---|---|
| `events_since` op | events_since = プル型（Agent が明示的に実行）。ミラー同期 = プッシュ型（エディターが自動配信）。**補完関係にあり、衝突しません**。再入場時の Delta 検証には `events_since` を引き続き利用します。 |
| inbox handoff | inbox は **個人の作業トリガー**（TODO キュー）。チャット同期は **全員へのパブリックブロードキャスト**（共有用）。重複しません。 |
| Quest dashboard `quest.md` | dashboard = 現在の状態の集計。チャット同期 = 遷移の過程。競合しません。 |
| UI での表示 | `_quest_system` からのシステム告知メッセージは、UI 上で目立たない薄いスタイルで描写され、メッセージの連続投稿によるアバター非表示フィルタリングの対象から安全に除外されます。 |

### 副産物

- `agent-prompt-queue` 部屋において、コマンドの進捗がチャットルームへ「🆕 queued / 🔒 drained / ✅ done」として自動的に告知されるため、Tim は `qstatus.py` を手動で実行しなくても、チャットを見るだけで現在のプロンプト進捗を把握できます。

---

## 14. よくある地雷（エラー）

- **依存関係を確認せずに task_claim を実行する**：MVP は強制停止しませんが、Agent は作業前に必ず `task_list status=ready` で Ready なタスクのみに絞り込んで確保するようにしてください。
- **inbox のクリア漏れ**：作業の完了時に `inbox_clear up_to_seq=<最大処理番号>` を忘れると、次回入場時に処理済みの古い通知を重複して読み取る原因になります。
- **events.jsonl を手動で編集する**：このファイルを絶対に直接編集しないでください。キャッシュが破損した場合は `quest_rebuild` で再構築します。イベントログのみが不変の真実です。
- **ブレインストーム部屋でイベントを作成する**：ブレインストーム部屋で events.jsonl を編集してはいけません。1つの部屋に1つの Quest ログという境界定義を遵守してください。
- **再入場時に task_list しか確認しない**：スナップショットは「退出から復帰まで」の過程を再現できません。変更の追跡には必ず `events_since` を実行してください。
- **引き継ぎ時に timeline を確認しない**：`task_force_reclaim` を実行する前に、必ず `task_state` で前任者の作業内容と成果物（artifacts）を確認してください。事前の検査を怠ると、重複作業や不要な手戻りが発生します。

---

## 15. Cross-Quest Handoff — 部屋を横断する連携とグローバル Inbox 路由（Round 4 補）

### 14.1 部屋をまたぐ依存関係の宣言

`task_create` の際、他方の Quest 部屋に属するタスクに依存している場合は、仕様書の `cross_depends_on` フィールドで以下のように宣言します：
- **表記形式**：`cross_depends_on: "room_id/task_id"`
- **記述例**：`cross_depends_on: "rooted-dispel/T01-schema"`

### 14.2 グローバル Inbox 路由

1. **トリガー**：`room_id` 部屋の `task_id` が `task_done` によって完了にマークされた際、reducer はその部屋のロックを解除するほか、部屋をまたぐ依存関係もスキャンします。
2. **ルーティング処理**：全部屋に対する深さ優先探索（BFS）による処理の遅延を防ぐため、システムは `AgentCommands/ChatTavern/cross_index.json` にて軽量な派生インデックスを管理します：
   - `task_create` 時に部屋をまたぐ依存関係が記述されている場合にインデックスへ登録。
   - タスク完了（done）の際、O(1) でインデックスをルックアップして通知。
3. **通知の配信**：依存関係が解消されると、システムは対象の `suggested_owner` のグローバル inbox ファイル（`AgentCommands/ChatTavern/inbox/<agent>.md`）へ、直接以下のようなハンドオフ通知を配信します：
   ```
   ## [cross-handoff] 部屋を横断するタスクロック解除：rooted-dispel/T01-schema が完了しました
   _at 2026-05-08T12:35:00Z_

   部屋 `new-quest` 内であなたに指名されているタスク `T02-migrate` が利用可能（Ready）になりました！
   次の手順：直ちに該当する部屋へ移動し、タスクを確保（claim）して作業を開始してください。
   ```

---

## 16. Brainstorm Bridge — 一括初期化と YAML Schema（Round 4 補）

ブレインストームでの議論から実体化された Quest へ移行する際、手動で複数の `task_create` コマンドを連続実行する手間を省くため、`op=quest_init_from_brainstorm` マクロを導入します。

### 15.1 YAML による構造化タスクツリーの記述

ブレインストームでの合意時、該当する部屋の最後のメッセージとして、Fenced Code Block で囲んだ YAML フォーマットでタスクツリーを記述します：

```yaml
quest_init_schema: v1
quest_id: rooted-dispel-refactor
source_messages: status-design#seq=40-50
tasks:
  - id: T01-schema
    role: architect
    priority: high
    title: "m_DispelledBySelfStatuses フィールドを追加"
  - id: T02-migrate
    role: programmer
    priority: normal
    title: "Rooted.json をマイグレーション"
    depends_on: [T01-schema]
```

### 15.2 マクロ実行とトランザクション型ロールバック

1. **一クリックでの部屋作成**：マクロが呼び出されると、`Cmd_Tavern` がブレインストームメッセージから YAML を読み取って解析し、新しい部屋 `rooted-dispel-refactor` を自動的に作成します。
2. **タスクの一括作成**：タスクリストの定義に基づいて、順次 `task_create` を実行して `events.jsonl` にアペンドし、仕様書 `tasks/<id>.md` を自動生成、frontmatter にブレインストームのメッセージ参照アドレスを逆参照として書き込みます。
3. **トランザクション型ロールバック（Transactional Rollback）**：一括作成中にいずれかのタスクでエラー（例：`T02-migrate` のスキーマ検証失敗）が発生した場合：
   - マクロの処理全体をアボート（中止）します。
   - システムが自動的に**ロールバック**を処理し、書き込まれた不完全な `events.jsonl` や自動生成された `tasks/` ディレクトリ内のファイルを破棄またはアトミックにトリムして元に戻します。

---

## 17. 関連ドキュメント

- メインドキュメント：[ChatTavern_Workflow](ChatTavern_Workflow.md) (zh-Hant)
- コマンド仕様：[Cmd_Tavern](../API/UCL_AgentCommand/Cmd_Tavern.md) (zh-Hant)
- Solo Brainstorm（クエストの上流）：[Tavern_SoloBrainstorm_Workflow](Tavern_SoloBrainstorm_Workflow.md) (zh-Hant)
- コミット規則：[Commit_Workflow](Commit_Workflow.md) (zh-Hant)
