---
title: UCL_AgentCommandsPage
description: AgentCommands/queue.json に永続化された agent コマンドをキュー追加・閲覧・実行するためのエディタページ。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AgentCommandsPage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-08-21
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# UCL_AgentCommandsPage

## 1. 概要

`UCL_AgentCommandsPage` は **Agent Commands** システムのエディタ UI です — AI agent が実行したいエディタアクションを JSON ファイルに書き込み、ユーザー（あるいは agent が間接的に）Unity Editor 内のボタンで実行する、軽量なパイプラインです。

ページの位置：

- **コード**：`Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AgentCommandsPage.cs`
- **UCL Core メニュー**：`Tools/UCL/Agent Commands/...`（[`UCL_AgentCommandRunner`](#5-関連クラス) が提供）
- **プロジェクト入口（RCG / Emblem of Valor）**：`EditorMenu → Agent Commands` ボタン

これは 4 つの協調する型を組み合わせた、薄い IMGUI シェルです：

| 型 | 役割 |
|---|---|
| `UCL_AgentCommand` | キューされた 1 件のコマンドのデータモデル（Id / Type / Mode / Args / 実行結果） |
| `UCL_AgentCommandQueue` | `<repoRoot>/AgentCommands/queue.json` の読み書き |
| `UCL_AgentCommandHandlerBase` | すべての handler の抽象基底 — リフレクションで自動発見 |
| `UCL_AgentCommandRegistry` | 発見された handler を `CommandType`（大文字小文字を区別しない）でインデックス化 |
| `UCL_AgentCommandRunner` | 非同期 runner — ディスパッチ前に `UCL_ModuleService.WaitUntilInitialized` を await |

## 2. ページレイアウト

```
┌─ TopBar ────────────────────────────────────────────────────────────┐
│ [Back] [Close] | UCL_AgentCommandsPage [Copy] [Refresh] [Run] [...] │
├─ Queue パス / 統計 ─────────────────────────────────────────────────┤
│ Queue: <repo>/AgentCommands/queue.json                              │
│ Total: 3 | Pending: 1 | Done: 1 | Repeatable: 1                     │
├─ Commands（queue.json の内容） ────────────────────────────────────┤
│ ● [Pending] ExportEquipmentNotes (OneShot)            [Remove]      │
│ ● [Done]    Ping                  (Repeatable)        [Remove]      │
├─ Available Commands（Registry から自動列挙） ──────────────────────┤
│ ExportEquipmentNotes  [説明を見る] [+ OneShot] [+ Repeatable]      │
│   全 Equipment の Note / Description を Markdown として書き出す     │
│   ▶ Args Schema                                                     │
│ Ping  [説明を見る] [+ OneShot] [+ Repeatable]                      │
│   Sanity check — args["msg"] を Console に出力                      │
│   ▶ Args Schema                                                     │
├─ Add Command（手動 fallback） ─────────────────────────────────────┤
│ Type: [登録済み型の grid]                                          │
│ Schema: msg=任意の文字列（任意、デフォルト "pong"）                 │
│ Mode: ( ) OneShot  ( ) Repeatable                                   │
│ Description: [...]   Args: [k=v;k=v]                                │
│ [Add 'Ping' (OneShot)]                                              │
└─────────────────────────────────────────────────────────────────────┘
```

## 3. トップバーの操作

| ボタン | 動作 |
|---|---|
| `Refresh` | `queue.json` をディスクから再読み込みしてメモリキャッシュへ |
| `Run Pending Commands` | `UCL_AgentCommandRunner.Menu_RunPending()` を呼ぶ（async）；約 1.5 秒後に自動 refresh |
| `Open Folder` | `AgentCommands/` フォルダを直接開く |

## 3b. セクションの折りたたみ（Tim 2026-08-21）

主要セクション（キュー状況 / コマンド追加 / 失敗記録 / テンプレート / 履歴 / ヒント）はそれぞれ
折りたためます。折りたたみボタンは一律 `UCL_GUILayout.Toggle`（▼/►）で、状態はページ instance の
`m_FoldDic` に保持されます。

| セクション | 既定 |
|---|---|
| 📋 キュー状況（queue パス / Watcher 状態バー / コマンド一覧） | 展開 |
| ➕ コマンド追加（ピッカー + フォーム） | 展開 |
| ❌ 失敗記録 | **失敗があれば展開**、無ければ折りたたみ |
| テンプレート / 履歴 / 💡 ヒント | 折りたたみ |

> [!NOTE]
> **キューセレクタは意図的に折りたためません** —— 他の全セクションが「どのキューの話か」を決めるため、
> 隠すと下の読み値すべてが主語を失います。
> 折りたたみヘッダは**閉じていても要約を表示**します（キュー統計・失敗件数・テンプレ/履歴件数）。
> 要約まで隠すと「ここに用があるか」を知るために開かねばならず、折りたたみの意味がなくなります。

## 3c. ❌ 失敗記録パネル（再実行可、Tim 2026-08-21）

2026-08-07 以降、失敗した OneShot は**即時デキュー**されます（キューを詰まらせず副作用も再現しないため）。
つまりキュー一覧からは見えません。本パネルは `<DataRoot>/_cmd_failed/<cmdId>.json` を一覧します ——
特定種類に限らず**すべての**失敗 Cmd です。

| ファイル | 内容 | 保持期間 |
|---|---|---|
| `_cmd_results/<id>.json` | 機械可読の verdict（**Args なし**） | 3 日後に自動削除 |
| `_cmd_errors/<id>.md` | 人間向けの完全な stack + Args | 永久 |
| `_cmd_failed/<id>.json` ★ | **構造化・再実行可**：Type / Mode / Args / error / queueId / 再実行履歴 | 再実行または手動削除まで |

★ `UCL_AgentCommandFailedStore` が Runner の失敗分岐で書き込みます。三つ目が必要な理由：前二つでは
再実行できません —— 一方は Args を持たず、他方は**人間向けのビュー**（人間向けビューに parser を書くのは
第二の真実源を作ることで、書式が変われば無言で壊れます）。

| ボタン | 動作 |
|---|---|
| `再実行` | 記録の `QueueId`（**元のキュー**）へ新しい cmd（新 id）を追加して即実行。元の記録は残り `RetryCount` が増加 |
| `フォームへ反映` | Type / Mode / Args をコマンド追加フォームへ戻す —— 引数ミス系は**直してから実行**するのでこちら |
| `削除` / `記録を全削除` | 記録のみ削除、キューには触れません（全削除は確認ダイアログあり） |

> [!CAUTION]
> **再実行はコマンドをもう一度走らせ、副作用も再現します** —— 酒場アナウンスの再投稿（同一 SHA の二重支給）、
> 送金の重複。したがって人が押すボタンのみで、**自動リトライはありません**：`ensure_idle` タイムアウトは
> 「未送出」（再試行は安全）ですが、**送出後**の失敗は既に効いている可能性があり、両者は画面上で同じ形です。

> [!IMPORTANT]
> **対象キューが実行中の場合、再実行はブロックされます。** Runner はキューをメモリ上のリストとして読み、
> 終了時にリスト全体を書き戻すため、その間の load→add→save は上書きされます（lost update）。
> 🩸 初回検証での実測：同じキューで実行中の Cmd から再実行を呼ぶと、記録は「再実行済み」となり
> 新 cmd id もログに出たのに、queue.json は空で verdict もエラーレポートも存在しませんでした ——
> **再実行がエラーメッセージ無しで消滅**。
> 現在の挙動：まず `IsRunningForAgent` を確認し、書き込み後に**新 id が入ったか読み戻して検証**、
> 検証できなければ再実行済みとしません。

> [!NOTE]
> 本 store は 2026-08-21 追加のため、**それ以前の失敗には構造化記録が無く再実行できません**。
> その件数はヘッダに別途表示されます（`_cmd_errors/` から算出）。「再実行不可」を「失敗なし」として
> 描かないためです。

## 3d. コマンド選択時に例示引数を自動入力（Tim 2026-08-21）

コマンド追加のドロップダウンでコマンドを切り替えると、Args 欄に該当 handler の `ExampleArgs` が
**自動入力**されます（宣言が無ければクリア）。コマンドが変わった時点で欄に残る args は別コマンドのもので、
残す方が消すより危険です（有効な一組に見えるため）。「例を入力」ボタンは戻したい時のために残しています。

> [!NOTE]
> テンプレート / 履歴 / 失敗記録から「フォームへ反映」した内容は例示値で**上書きされません** ——
> それらの経路は自動入力の検知基準を同期します。

## 4. 新しいコマンド型を追加するには

コマンドシステムは **convention-over-configuration** で動きます — `UCL_AgentCommandHandlerBase` を継承するクラスを書くだけです。次の domain reload で registry がリフレクションにより自動発見します。

```csharp
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands;
using UnityEngine;

namespace YourGame.AgentCommands
{
    public class Cmd_HelloWorld : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "HelloWorld";
        public override string ShortDescription => "Console に挨拶を出力します。";
        public override string ArgsSchema => "name=挨拶したい相手";
        public override string HelpURL => "ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_AgentCommandsPage.md";

        public override async UniTask ExecuteAsync(
            Dictionary<string, string> args, CancellationToken token)
        {
            string name = args != null && args.TryGetValue("name", out var n) ? n : "world";
            Debug.Log($"[AgentCmd] Hello, {name}!");
            await UniTask.CompletedTask;
        }
    }
}
#endif
```

Unity が再コンパイルすると、`HelloWorld` が **Available Commands** リストに自動的に表示されます — `ShortDescription`、折りたためる `ArgsSchema`、`HelpURL` が設定されていれば `[説明を見る]` ボタンも一緒に。

> [!IMPORTANT]
> `CommandType` の照合は**大文字小文字を区別しません**が、AppDomain 全体で**一意**でなければなりません。重複した type は error を log し、後から登録された方が優先されます。

## 5. 関連クラス

| クラス | ファイル | 備考 |
|---|---|---|
| `UCL_AgentCommand` | `EditorCore/UCL_AgentCommands/UCL_AgentCommand.cs` | データモデル + `UCL_AgentCommandMode` enum（OneShot / Repeatable） |
| `UCL_AgentCommandQueue` | `EditorCore/UCL_AgentCommands/UCL_AgentCommandQueue.cs` | 手書き JSON I/O（Unity `JsonUtility` は `Dictionary` 非対応） |
| `UCL_AgentCommandHandlerBase` | `EditorCore/UCL_AgentCommands/UCL_AgentCommandHandlerBase.cs` | `CommandType` + `ExecuteAsync` だけ override すれば OK |
| `UCL_AgentCommandRegistry` | `EditorCore/UCL_AgentCommands/UCL_AgentCommandRegistry.cs` | static ctor で `typeof(UCL_AgentCommandHandlerBase).GetAllSubclass()` をスキャン |
| `UCL_AgentCommandRunner` | `EditorCore/UCL_AgentCommands/UCL_AgentCommandRunner.cs` | ディスパッチ前に `await UCL_ModuleService.WaitUntilInitialized(token)` |

## 6. queue.json スキーマ

```json
{
  "Commands": [
    {
      "Id": "20260504-120000-helloworld",
      "Type": "HelloWorld",
      "Mode": "OneShot",
      "RunCount": 0,
      "Args": { "name": "Tim" },
      "CreatedAt": "2026-05-04T12:00:00.0000000Z",
      "LastRunAt": null,
      "LastRunResult": null,
      "LastRunError": null,
      "Description": "（任意）agent が残した人間可読のメモ"
    }
  ]
}
```

| フィールド | 意味 |
|---|---|
| `Id` | 一意の識別子；慣例は `yyyyMMdd-HHmmss-<typelower>` |
| `Type` | 登録済み handler の `CommandType` と一致する必要あり（大文字小文字区別なし） |
| `Mode` | `"OneShot"`（成功後はキューから直接削除）または `"Repeatable"`（毎回再実行） |
| `RunCount` | 成功実行回数。runner が加算する。OneShot は RunCount が 1 に増える前にキューから削除されるため、このフィールドは主に Repeatable に対して意味を持つ。 |
| `Args` | 自由形式の `string→string` map、`ExecuteAsync` に渡る |
| `LastRun*` | runner が書き込み；`Result` は `"Success"` / `"Failed"` |

## 7. 初期化契約

Runner は handler をディスパッチする前に `UCL_ModuleService.WaitUntilInitialized(token)` を呼びます。この API は遅延初期化の**起動**（`UCL_ModuleService.Ins` へのアクセスにより）と完了の**待機**を兼ねます — そのため handler はモジュールシステムが ready で `UCL_Asset.Util.GetData()` が non-null を返すと安全に仮定できます。

> [!NOTE]
> handler がプロジェクト固有の prewarm（例：`RCG_IconSprite.InitSpriteAsset`）を必要とする場合、その handler 自身が `await` してください。フレームワーク runner はプロジェクト層への逆依存を持ちません。

## 8. 関連ドキュメント

- [`UCL_CommonEditorPage`](./UCL_CommonEditorPage.md) — 直接の親クラス
- [`UCL_ModuleService_API`](../UCL_ModuleService/UCL_ModuleService_API.md) — `WaitUntilInitialized` の説明
- `Workflows/HelpURL_Workflow.md`（本 repo） — `ucl_core:` / `eov_docs:` URL スキーム

## 9. 落とし穴

> [!CAUTION]
> 旧 RCG 版のように **`Register(...)` 呼び出しを書かないでください**。新 registry は純粋にリフレクション駆動です — `UCL_AgentCommandRegistry` には `Register` メソッドそのものが存在しません。

> [!IMPORTANT]
> Editor 専用。システム全体が `#if UNITY_EDITOR` でラップされており、runtime コードパスから参照してはいけません。

## ★ NEW: Lock-file Watcher (auto-trigger)

Since 2026-05-05, `UCL_AgentCommandWatcher` (`[InitializeOnLoad]`) polls `<repoRoot>/AgentCommands/pending.trigger` once per second; when present, it does an atomic `File.Move` to `pending.trigger.running` and invokes the Runner. The page now shows a Watcher status row (Auto-Watcher toggle / Idle/Pending/Running indicator / Last trigger time / Simulate Trigger button).

The "Export Cmd Catalog" stand-alone button is removed — add an `ExportCommandCatalog` cmd via the Add Command form instead (same code path, same output).

Python wrapper: `<UCL_CORE>/Tools~/AgentCommands/run_cmd.py` (writes the trigger; `ensure_idle()` blocks if a previous batch hasn't finished).

For the full design (state machine, ensure_idle, failure modes), see the project workflow doc: `docs/Workflows/AgentCommands_Workflow.md` §8a.0.
