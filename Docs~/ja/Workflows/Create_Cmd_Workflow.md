---
title: 新しい Agent Command Handler を作成するワークフロー
description: queue.json から呼び出せる `Cmd_<Name>.cs` をゼロから書くための SOP。命名規則、ファイル配置、メタデータ、ExecuteAsync の書き方、Editor 内検証、よくある落とし穴を網羅。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# 🛠️ 新しい Agent Command Handler を作成するワークフロー

> [!IMPORTANT]
> このワークフローは「**`Cmd_<Name>.cs` サブクラスを 1 本書く**」ことだけを扱います。システム全体の動作（queue / trigger / watcher / runner）は [UCL_AgentCommand_Architecture](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md) を参照。
>
> 設計思想：**convention-over-configuration**。`UCL_AgentCommandHandlerBase` を継承して正しい場所に置けば、次の domain reload で `UCL_AgentCommandRegistry` がリフレクションで自動発見します。**Registry には触らないでください。**

---

## 0. TL;DR — 5 分で Cmd を追加

```
[1] CommandType を決める（PascalCase、AppDomain 内ユニーク）
       ▼
[2] 配置場所を選ぶ（§2 デシジョンツリー）— UCL_Core 内 vs 下流モジュール
       ▼
[3] Cmd_<Name>.cs を作成：UCL_AgentCommandHandlerBase を継承
       ▼
[4] 4 つのメタデータをオーバーライド：CommandType / ShortDescription / ArgsSchema / HelpURL
       ▼
[5] ExecuteAsync(args, token) を実装
       ▼
[6] Unity domain reload を待つ → UCL_AgentCommandsPage を開く
       → Available Commands に新しい指令が表示される → Add + Run Pending → Console を確認
```

---

## 1. 事前判断

| # | 質問 | 影響 |
|---|------|------|
| 1 | **CommandType の命名** | PascalCase、動詞始まり、AppDomain 内ユニーク；衝突は LogError + 後勝ち |
| 2 | **OneShot か Repeatable か** | agent が queue 書き込み時に決める；handler はハードコードしない |
| 3 | **どの引数か** | `Dictionary<string,string>` のみ；複雑なオブジェクトは JSON 文字列に詰める |
| 4 | **所属モジュール** | UCL_Core 内 vs 下流モジュール（§2 参照）|

### 1.1 命名規則

| 対象 | 例 | ルール |
|---|---|---|
| C# クラス | `Cmd_DebugLog` | `Cmd_` 接頭辞 + PascalCase |
| ファイル名 | `Cmd_DebugLog.cs` | クラス名と完全一致 |
| `CommandType` 値 | `"DebugLog"` | `Cmd_` 接頭辞なし；queue.json では大小区別なし |
| Namespace（UCL_Core 内）| `UCL.Core.EditorLib.AgentCommands` | フレームワーク層の汎用指令 |
| Namespace（下流）| `<YourModule>.AgentCommands` | 例：`RCG.AgentCommands` |

> [!CAUTION]
> namespace の途中に `Editor` を入れない（例：`MyMod.Editor.AgentCommands`）。`UnityEditor.Editor` と衝突して CS0118 を引き起こします。

---

## 2. ファイル配置のデシジョンツリー

```
この Cmd は…
├── 汎用ツール（ファイル I/O、UCL_Asset 操作、カタログ出力 — 下流型に依存しない）
│       → Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Cmd_<Name>.cs
│         namespace UCL.Core.EditorLib.AgentCommands
│
└── 下流モジュール固有（RCG_*Data 呼び出し / プロジェクトの EditorPage / 特定アセット改変）
        → そのモジュールの AgentCommands フォルダ
          namespace <YourModule>.AgentCommands
```

### 2.1 なぜ分けるのか

- **UCL_Core は submodule** で、UCL を使う任意の Unity プロジェクトで再利用できる必要があります。下流型を参照するとポータビリティが壊れます。
- **下流モジュール**は自分の namespace を自由に `using` でき、プロジェクト固有の Editor API を呼べます。
- 判断基準：**この Cmd は下流モジュールが無い純 UCL プロジェクトで意味がある？** → Yes なら UCL_Core；No なら下流。

> [!TIP]
> 迷ったら下流モジュールに先に書く。後で汎用化が分かったら UCL_Core に昇格（移動 + namespace 変更）。**逆方向の降格** はより手間がかかります。

---

## 3. 標準テンプレート（コピーして改変）

```csharp
// Handler: <CommandType> — <この Cmd が何をするかの一行説明>
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// ブロック責務：<この Cmd の責務、いつ起動されるか、想定ユーザ>
    /// 物理的意味：<変更されるアセット / 書き込まれるファイル / ゲーム状態への影響>
    /// 数値影響：<数値変更が無い場合は「なし」；ある場合は範囲を明記>
    /// </summary>
    public class Cmd_Example : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Example";
        public override string ShortDescription => "サンプル Cmd — args[\"msg\"] を Console に表示";
        public override string ArgsSchema =>
            "msg=表示する文字列（任意、デフォルト \"hello\"）";
        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md";

        public override async UniTask ExecuteAsync(
            Dictionary<string, string> args, CancellationToken token)
        {
            string msg = args != null && args.TryGetValue("msg", out var m) ? m : "hello";
            Debug.Log($"[AgentCmd] {CommandType} → {msg}");
            await UniTask.CompletedTask;
        }
    }
}
#endif
```

### 3.1 必須フィールド

| フィールド | 型 | 必須 | 説明 |
|---|---|---|---|
| `CommandType` | `string` | ✅ | queue.json `"Type"` の比較基準；PascalCase |
| `ShortDescription` | `string` | ⚠強く推奨 | UI ドロップダウン + カタログの一行サマリ |
| `ArgsSchema` | `string` | ⚠強く推奨 | `key=説明` 形式；引数なしは `"(no args)"` |
| `HelpURL` | `string` | 任意 | `ucl_core:` または下流モジュール登録の prefix |
| `ExecuteAsync` | `UniTask` | ✅ | 実装本体；await が無い場合は末尾に `await UniTask.CompletedTask;` |

---

## 4. ExecuteAsync 実装の指針

### 4.1 Runner が事前にしてくれていること

`UCL_AgentCommandRunner` は handler を呼ぶ前に：

1. ✅ `await UCL_ModuleService.WaitUntilInitialized(token)` 完了
2. ✅ Unity main thread；`AssetDatabase` / `EditorUtility` 安全
3. ✅ `UCL_Asset<T>.Util.GetData(...)` 呼び出し可能

> [!IMPORTANT]
> handler 内で **再度 `WaitUntilInitialized` を await しない**。モジュール固有の予熱が必要な場合のみ、handler 内でローカルに `await` してください。

### 4.2 引数パース

| ケース | 書き方 |
|---|---|
| 必須 string | `args.TryGetValue("k", out var v)` → 無ければ `throw new ArgumentException(...)` |
| 任意 + デフォルト | `args.TryGetValue("k", out var v) ? v : "default"` |
| bool | `args.TryGetValue("k", out var v) && bool.TryParse(v, out var b) && b` |
| int | `args.TryGetValue("k", out var v) && int.TryParse(v, out var n)` |
| 複雑オブジェクト | JSON を value に詰めて `JsonUtility.FromJson<T>(args["k"])` |

### 4.3 エラーハンドリング

- **不正引数** → `throw new ArgumentException($"[{CommandType}] ...")`
- **アセット不在** → `throw new InvalidOperationException(...)`
- **回復不能** → 同上、**catch で握りつぶさない**
- **キャンセル** → ループ先頭で `token.ThrowIfCancellationRequested()`

### 4.4 出力ファイルのパス規則

> [!IMPORTANT]
> - `queue.json` → **git ルート** の `AgentCommands/queue.json`
> - Cmd の出力 → **Unity プロジェクトルート** の `AgentCommands/<output>.md`（`Assets/` 内ではない！）

```csharp
string outputPath = args.TryGetValue("outputPath", out var p)
    ? p : "AgentCommands/default_report.md";
string fullPath = System.IO.Path.Combine(
    UnityEngine.Application.dataPath, "..", outputPath);
System.IO.File.WriteAllText(fullPath, content);
```

---

## 5. 検証 SOP

### 5.1 Editor 内検証（必須）

1. 保存して Unity の再コンパイルを待つ — Console に赤エラーなし
2. `Tools/UCL/Agent Commands/Open Page` から [`UCL_AgentCommandsPage`](../UCL_EditorPage/UCL_AgentCommandsPage.md) を開く
3. Available Commands に `<CommandType> — <ShortDescription>` が表示されること
4. Args Schema を展開して内容確認
5. 「Help を見る」ボタンで `HelpURL` の文書に飛ぶこと
6. テスト用に Add → OneShot → Args 記入
7. ▶ Run Pending Commands → 期待通りの `[AgentCmd]` ログ
8. OneShot 成功 → queue から消える；失敗 → queue に残り `LastRunError` 表示

### 5.2 Python ラッパー

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run <CommandType> \
    --arg key=value --timeout 60
```

---

## 6. よくある落とし穴

| # | 落とし穴 | 症状 | 対処 |
|---|---|---|---|
| 1 | **`#if UNITY_EDITOR` 忘れ** | Build 時に基底クラス未解決 | ファイル全体を `#if UNITY_EDITOR ... #endif` で囲む |
| 2 | **CommandType 衝突** | Console: `duplicate CommandType` | リネーム（大小区別なし）|
| 3 | **namespace 中段に `Editor`** | CS0118 | `Editor` を中段に含めない namespace に変更 |
| 4 | **`ExecuteAsync` に await が無い** | warning CS1998 | 末尾に `await UniTask.CompletedTask;` |
| 5 | **catch で例外を握りつぶす** | runner が成功と誤認 | `throw` で投げる |
| 6 | **コンパイル前に Run** | 古い handler が走る | コンパイル完了を待つ |
| 7 | **出力先が `Assets/...`** | Asset Database に取り込まれる | `<UnityProjectRoot>/AgentCommands/` に出力 |
| 8 | **UCL_Core 側 Cmd が下流型を参照** | Submodule のポータビリティ破壊 | 汎用化 or 下流モジュールに戻す |

---

## 7. 既存 EditorPage ロジックのラップ（推奨パターン）

> [!TIP]
> ほとんどの場合、Cmd は車輪の再発明をしない — EditorPage のボタンが既に存在するなら、その背後の static メソッドを直接呼ぶ。

```csharp
public override async UniTask ExecuteAsync(
    Dictionary<string, string> args, CancellationToken token)
{
    SomeEditorPage.DoExport();
    await UniTask.CompletedTask;
}
```

---

## 8. 最小スケルトン

```csharp
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands  // 下流モジュールの場合は変更
{
    /// <summary>
    /// <この Cmd の一行責務>
    /// </summary>
    public class Cmd_<Name> : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "<Name>";
        public override string ShortDescription => "<UI 用の一行サマリ>";
        public override string ArgsSchema => "(no args)";
        public override string HelpURL => "ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md";

        public override async UniTask ExecuteAsync(
            Dictionary<string, string> args, CancellationToken token)
        {
            Debug.Log($"[AgentCmd] {CommandType} executed.");
            await UniTask.CompletedTask;
        }
    }
}
#endif
```

---

## 9. 文書配置の自動判定スキーム（将来）

> このワークフローがプロジェクト層 `docs/Workflows/` ではなく `Assets/UCL/UCL_Core/Docs~/` 配下にある理由は、**UCL_Core フレームワーク自体の拡張方法のみを記述しており**、下流型に依存しないためです。

### 9.1 判定原則：「記述対象のソース位置」で決定

| 文書が記述する対象が… | 文書の配置先 |
|---|---|
| 完全に `Assets/UCL/UCL_Core/` 内 | `Assets/UCL/UCL_Core/Docs~/{lang}/...`（多言語）|
| 完全に下流モジュール内 | `docs/...`（プロジェクト層）|
| 両者にまたがる — 「下流 X が UCL_Core の Y をどう使うか」 | `docs/Workflows/`（呼び出し側視点）|

### 9.2 必須 frontmatter フィールド

```yaml
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/
namespace: UCL.Core.EditorLib.AgentCommands
```

自動分類器は：

1. `source_root` を読む
2. `startsWith("Assets/UCL/UCL_Core/")` → `Assets/UCL/UCL_Core/Docs~/{lang}/` 配下にあるべき
3. `startsWith("Assets/Scripts/")` → `docs/` 配下にあるべき
4. 不一致 → 警告 + 移動提案

### 9.3 推奨実装：`Cmd_ValidateDocPlacement`

| ステップ | 動作 |
|---|---|
| 1 | `Assets/UCL/UCL_Core/Docs~/**/*.md` + `docs/**/*.md` をスキャンし frontmatter を読む |
| 2 | `source_root` から「あるべき配置先」を計算（§9.1）|
| 3 | 実パスと比較；不一致を violations に記録 |
| 4 | 追加チェック：UCL_Core 配下文書が下流 namespace（例：`RCG_`）を参照していないか |
| 5 | `AgentCommands/doc_placement_report.md` を出力 |

### 9.4 新規作成時のチェックリスト

- [ ] 記述対象の .cs ファイルはどこ？（→ `source_root`）
- [ ] その .cs は下流型を参照する？（→ 配置に影響）
- [ ] frontmatter は完備？（`source_root` / `namespace` / `last_updated` / `target_audience`）
- [ ] 多言語：4 部（UCL_Core）か 1 部（プロジェクト層）か？

---

## 10. 関連文書

- 🤖 [UCL_AgentCommand_Architecture](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)
- 📖 [UCL_AgentCommand](../API/UCL_AgentCommand/UCL_AgentCommand.md)
- 🪟 [UCL_AgentCommandsPage](../UCL_EditorPage/UCL_AgentCommandsPage.md)
- 🔗 [HelpURL_Workflow](HelpURL_Workflow.md)
- 📁 UCL_Core handler フォルダ：`Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/`
- 📖 プロジェクト層補足：[`docs/Workflows/AgentCommands_Workflow.md`](../../../../../../docs/Workflows/AgentCommands_Workflow.md)

---

## 他の言語

- 🇬🇧 [English](../../en/Workflows/Create_Cmd_Workflow.md)
- 🇯🇵 日本語（このファイル）
- 🇨🇳 [简体中文](../../zh-Hans/Workflows/Create_Cmd_Workflow.md)
- 🇹🇼 [繁體中文](../../zh-Hant/Workflows/Create_Cmd_Workflow.md)
