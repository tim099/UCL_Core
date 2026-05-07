---
title: 編集 → リコンパイル → エラー修正ループ ワークフロー
description: ステップ化された SOP — agent / ツール開発者が .cs を編集した後、Unity に強制的にリコンパイルさせ、コンパイルエラーがゼロになるまでループで修正する手順。Cmd_Recompile + UCL_CompileErrorTracker + run_cmd.py を土台にしています。
source_root: AgentCommands/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [edit recompile loop, script edit loop, compile error fix loop, agent compile loop]
tags: [workflow, agent, compile, recompile, error-fix]
---

# 🔁 編集 → リコンパイル → エラー修正ループ ワークフロー

> [!IMPORTANT]
> このワークフローは「**.cs を編集した後、本当にコンパイルに反映されたか + コンパイルエラーを踏んでいないかをどう確認するか**」という SOP を担当します。
> agent はファイルを編集し終えても Unity がリロード済みだと仮定してはいけません — Editor にフォーカスがない / Auto Refresh が OFF の状況では、
> 書いたコードが assembly に入っておらず、後続の Cmd 群はすべて旧版で動く可能性があります。

> 設計思想：**強制同期ポイント**。`Cmd_Recompile` + Python の `recompile` サブコマンドは「**ここまでの .cs 変更はすべて反映済み**」という保証境界です。

---

## 0. TL;DR — 5 分でループ全体を把握

```
[1] .cs ファイルを編集 / 生成（Edit / Write）
       ▼
[2] python run_cmd.py recompile     ← Unity のリコンパイルをトリガし、完了まで待機
       │
       ├── exit 0  → clean、後続フローへ
       └── exit 1  → コンパイルエラーあり
              ▼
[3] AgentCommands/.compile_status.json の messages を読む
       ▼
[4] エラーごとに file:line を見てソースを修正
       ▼
[5] goto [2]（最大 N ラウンド、推奨 ≤ 5；超えるなら方向違い、人間に渡す）
```

---

## 1. 前提条件（セッション開始時に一度確認）

| # | チェック項目 | 確認方法 | 通らない時の対応 |
|---|---|---|---|
| 1 | Unity Editor が起動中 | システムトレイ / ウィンドウで確認可能 | Unity を起動しプロジェクトをロード |
| 2 | Auto-Watcher が有効 | UCL_AgentCommandsPage で `Auto-Watcher ✔` を確認 | チェックボックスを ✔ に切替 |
| 3 | `run_cmd.py` が呼べる | `python <パス> --help` で usage が出る | PATH 修正 / Python インストール確認 |
| 4 | `.compile_status.json` が存在 | `AgentCommands/.compile_status.json` | Unity でコンパイルを 1 回起こす（任意のファイルを触って保存） |

> [!CAUTION]
> Auto-Watcher が Idle のままだと、すべての Cmd が pending で詰まります。有効化していないと `recompile` サブコマンドは**動きません**。

---

## 2. なぜ `recompile` を強制するか？

agent が `.cs` を書き終えても、Unity が即座にコンパイルしてくれるとは限りません：

| Unity の状態 | 振る舞い |
|---|---|
| Editor フォーカスあり + Auto Refresh ON | ファイル変更を即検知 → コンパイル（理想ケース） |
| Editor がバックグラウンド + Auto Refresh ON | フォーカスが戻った時に検知（agent からはこのタイミングが見えない） |
| Auto Refresh OFF | 自動コンパイルされない；手動で Ctrl+R が必要 |
| 直前のコンパイル失敗 | エラー状態で固まり、新 Cmd handler がロードされない |

**結論**：agent が `.cs` を編集した後は、**変更が効いていると仮定してはいけません**。`recompile` を必ず走らせて強制同期し、exit code から 0 errors を確認します。

---

## 3. コアループ（疑似コード）

```python
import subprocess, json
from pathlib import Path

RUN_CMD = "CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py"
STATUS  = Path("AgentCommands/.compile_status.json")

def recompile_and_check() -> tuple[int, list]:
    """returns (errors_count, messages); errors_count==0 → clean"""
    rc = subprocess.run(["python", RUN_CMD, "recompile"], capture_output=False)
    if rc.returncode == 0:
        return 0, []
    if rc.returncode == 1:
        st = json.loads(STATUS.read_text(encoding="utf-8-sig"))
        return st["total_errors"], [m for m in st["messages"] if m["type"] == "Error"]
    raise RuntimeError(f"infra failure: exit code {rc.returncode}")

# メインループ
MAX_ROUNDS = 5
for round_idx in range(MAX_ROUNDS):
    edit_files(...)            # agent が .cs を編集 / 生成
    err_count, errors = recompile_and_check()
    if err_count == 0:
        break
    for e in errors:
        print(f"× {e['file']}:{e['line']}  {e['message']}")
        fix_error(e)            # ソースを読む + Edit
else:
    raise RuntimeError(f"still {err_count} errors after {MAX_ROUNDS} rounds — STOP, ask human")
```

---

## 4. 詳細手順

### 4.1 .cs ファイルを編集 / 生成
- Edit / Write ツールでソースを編集
- `.meta` を**手動で作成しない**（Unity が自動生成、memory `feedback_no_direct_meta.md` 参照）
- 複数ファイルの変更は一括で済ませ、最後にまとめて recompile（1 ファイルごとに recompile しない）

### 4.2 recompile をトリガ
```bash
python "CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py" recompile
```

**Exit code 対応表**：

| exit | 意味 | アクション |
|---|---|---|
| 0 | コンパイル完了、0 errors | 継続 |
| 1 | コンパイル完了、エラーあり | §4.3 でエラー修正へ |
| 2 | Cmd_Recompile が Unity に拾われていない（queue が消化されない） | §1 の前提条件を確認 — Watcher / Editor 状態 |
| 3 | `.compile_status.json` の解析失敗 | ファイル破損 / エンコーディング問題 |
| 4 | mtime が進んでいない（コンパイルが走っていない） | UCL_CompileErrorTracker のイベント未フック？ [CompileError_Diagnose_Workflow](CompileError_Diagnose_Workflow.md) を参照 |

### 4.3 エラーメッセージを読む
- stdout に先頭 5 件が出力される
- 全件は `AgentCommands/.compile_status.json` の `messages` 配列。フィールドは：
  - `type`: `"Error"` / `"Warning"`
  - `file`: 相対パス（Unity project root 起点）
  - `line`: 行番号
  - `column`: 列番号
  - `message`: エラー文字列（CS 番号付き、例：`CS0103: ...`）

### 4.4 エラーを修正
各エラーについて：
1. **ソースを開く**：Read ツールで `file:line` 周辺（前後 ±10 行）を確認
2. **原因判定**：よくある CS エラーと照合（[CompileError_Diagnose_Workflow §よくあるエラー](CompileError_Diagnose_Workflow.md)）
3. **ソース修正**：Edit で最小範囲の修正
4. **連鎖破壊を防ぐ**：`RCG_X` を変更する場合は他に参照がないか先に Grep

### 4.5 §4.2 に戻る
recompile を再実行し、該当エラーが消えていること（および新エラーを生んでいないこと）を確認。

### 4.6 終了条件
- ✅ exit 0 → 後続ワークフロー（ExportNotes / テスト / commit など）へ
- ❌ 5 ラウンド連続でエラーが残る → **停止**。エラー一覧と試した修正内容を人間に渡す。盲目的に続けると傷口が広がるだけ。

---

## 5. 故障モード対応表

| 症状 | 想定原因 | 切り分け / 解決策 |
|---|---|---|
| `recompile` が exit 2、queue が Recompile cmd で詰まる | Auto-Watcher が無効 | UCL_AgentCommandsPage を開き `✔ Auto-Watcher` を確認 |
| `recompile` が exit 4 | UCL_CompileErrorTracker が status を書いていない | `Tracker just loaded, no compile event captured yet` プレースホルダを確認；任意のファイルを触ってコンパイルを 1 回起こす |
| 同一エラーを直しても消えない | Unity が対象ファイルを再コンパイルしていない | ファイルが本当に保存されているか確認；再度 `recompile` |
| ファイル A を編集したのにファイル B でエラー | namespace / asmdef の隔離；CS0246 の using 不足 | [CompileError_Diagnose_Workflow §asmdef](CompileError_Diagnose_Workflow.md) を参照 |
| 新エラーを次々誘発する | ソース変更で contract を壊している | 元バージョンに戻して再計画；ここで停止して人間に渡す判断もアリ |
| `recompile` は走ったが内容が反映されない | 編集対象が `_Editor` サブモジュール / `Editor/` 配下 | 対応する asmdef が dirty か + script type が Editor-only か確認 |

---

## 6. 他ワークフローとの関係

```
   Create_EditorPage_Workflow          新ページの作成
   Create_Cmd_Workflow                 新 Cmd の作成
              │
              ▼ .cs 編集後
   ┌────────────────────────────────────────────┐
   │  Edit_Recompile_Loop_Workflow（このファイル）│  ← 強制同期 + エラー修正
   └────────────────────────────────────────────┘
              │
              ▼ コンパイル 0 errors 後
   後続：Cmd_ExportNotes 実行 / 自動テスト / commit

   コンパイルエラー解析の詳細：CompileError_Diagnose_Workflow を参照
```

---

## 7. 使用例

### 例 A：agent が新 Cmd を追加した後の検証
```bash
# 1. Edit / Write で Cmd_Foo.cs を作成
# 2. recompile をトリガ
python "CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py" recompile
# → exit 0 を期待；exit 1 なら compile_status.json を読んで修正後に再実行

# 3. 新 Cmd が登録されていることを確認
python "CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py" catalog | grep "Foo"

# 4. 新 Cmd を実行
python "CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py" run Foo --arg x=1
```

### 例 B：agent が EditorPage をリファクタした後の検証
```bash
# 1. RCG_StoryDataEditorPage.cs を編集
# 2. recompile
python "CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py" recompile
# 3. ExportNotes で出力が揃っているか検証
python "CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py" run ExportNotes --arg targets=story
# 4. ファイルを目視 / git diff で確認
```

---

## 8. 検収チェックリスト

agent のセルフチェック（毎ラウンド終了時に 1 回）：

- [ ] 直近の `recompile` が exit 0
- [ ] `.compile_status.json` の `total_errors == 0`
- [ ] 編集対象 .cs に `__DELETE_ME__` / `_Deprecated` などの一時マーカーが残っていない
- [ ] `.meta` を手動作成していない
- [ ] ループ脱出時のラウンド数 ≤ 5（超過は詰まりサイン、続けてはいけない）

---

## 9. 関連ドキュメント

- [Create_Cmd_Workflow](Create_Cmd_Workflow.md) — 新 `Cmd_<Name>.cs` の作成
- [Create_EditorPage_Workflow](Create_EditorPage_Workflow.md) — 新 `UCL_*Page` の作成
- [CompileError_Diagnose_Workflow](CompileError_Diagnose_Workflow.md) — コンパイルエラーの詳細切り分け（asmdef / CS0246 ほか）
- [HelpURL_Workflow](HelpURL_Workflow.md) — `[HelpURL]` プレフィックス解析
- `run_cmd.py` — Python CLI ラッパー（`recompile` / `run` / `submit` / `wait` / `catalog`）
- `Cmd_Recompile` — Editor 側でリコンパイルを発火する Agent Command
