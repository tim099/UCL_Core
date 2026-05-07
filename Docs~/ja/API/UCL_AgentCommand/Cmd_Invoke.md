---
title: Cmd_Invoke API
description: 汎用リフレクション Cmd — 文字列での記述（type / member / args）を UCL_ReflectionInvoker に渡し、Unity 組み込みの任意の public static method / property / field を動的に呼び出します。API ごとに専用 Cmd を書く必要がありません。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_Invoke.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [reflection invoke cmd, dynamic api call, generic unity invoker]
tags: [api, agent-command, reflection, editor]
---

# Cmd_Invoke

## 1. 概要

`Cmd_Invoke` は**汎用リフレクション呼び出し**コマンドです。Unity 組み込み API ごとに専用 Cmd を用意する必要はなく、エージェントは文字列での記述（type / member / args）から任意の `public static` method / property / field を直接トリガーできます。

### 設計のレイヤー（呼び出し元は Cmd に限らない）

```
   Cmd_Invoke           Editor ボタン       カスタム runtime ツール   その他の Cmd
        │                    │                    │                    │
        ▼                    ▼                    ▼                    ▼
   ┌────────────────────────────────────────────────────────────────────────┐
   │  UCL.Core.UCL_ReflectionInvoker（UtilCore、runtime-available、純ロジック） │
   │   ParseRequest(IDictionary<string,string>) → Request                    │
   │   Invoke(Request) → Result                                              │
   └────────────────────────────────────────────────────────────────────────┘
              │
              ├─ AssemblyExtensions.GetTypeByFullName で Type を厳格に解決
              │  （cache は UCL_Core 全体で共有。大文字小文字を厳密に区別し fallback なし）
              │
              └─ Type.TryConvertFromString(string) で引数を変換
                 （extension method、primitive / string / enum / "null" リテラル対応）
              │
              ▼
   呼び出し対象の Unity 組み込み API（CompilationPipeline / AssetDatabase / EditorPrefs / EditorApplication ...）
```

**3 層に分離**：
| 層 | 位置 | 用途 |
|---|---|---|
| トリガー | `Cmd_Invoke` / カスタム Editor ボタン / runtime call | 任意のソースから dict を渡す、または直接 request を生成可能 |
| リフレクション実行 | `UCL.Core.UCL_ReflectionInvoker`（UtilCore） | 純ロジック、ユニットテスト可能、runtime からも利用可 |
| Type / 変換 | `AssemblyExtensions.GetTypeByFullName`（厳格）/ `Type.TryConvertFromString` | 共有 cache + 対応型を継続的に拡張可能 |

### 直接呼び出しの例（Cmd を経由せず、runtime でも動作）

```csharp
using UCL.Core;

var req = new UCL_ReflectionInvokeRequest
{
    TypeName = "UnityEditor.Compilation.CompilationPipeline",
    MemberName = "RequestScriptCompilation",
};
var result = UCL_ReflectionInvoker.Invoke(req);
if (!result.Success) Debug.LogError(result.Error);
```

---

## 2. 引数仕様 (Args schema)

| key | 必須 | デフォルト | 説明 |
|---|---|---|---|
| `type` | ✅ | — | **完全な `Type.FullName`、大文字小文字を厳密に区別**（例：`UnityEditor.Compilation.CompilationPipeline`）。1 文字でも違うと失敗します |
| `member` | ✅ | — | メンバー名（method / property / field 名）— 大文字小文字を厳密に区別 |
| `kind` | | `method` | `method` / `property` / `field` |
| `paramTypes` | | （空）| オーバーロードの曖昧解消：セミコロン区切りの完全型名リスト（例：`int;string;UnityEditor.ImportAssetOptions`）|
| `args` | | （空）| セミコロン区切りの文字列引数。`paramTypes` の順序で変換されます |
| `getter` | | `true` | property / field 用 — `false` で setter になり、`args[0]` が代入する値となります |
| `nonPublic` | | `false` | `true` で BindingFlags に `NonPublic` を追加し、internal / private メンバーも検索可能になります（Unity 組み込み API には internal が多数あります）|
| `target` | | （空）| `target=$varname` でこの呼び出しを instance call に切り替え、`UCL_ReflectionInvoker.Variables[varname]` から対象オブジェクトを取得します（事前に `storeAs=` で格納されている必要があります）。設定すると BindingFlags は自動的に Instance に切り替わり、`type` は省略可能です（`target.GetType()` を使用）|
| `storeAs` | | （空）| 呼び出しが成功した際、戻り値を `Variables[storeAs]` に書き込みます。後続の invoke では `target=$storeAs` や `args=$storeAs;...` で参照できます。domain reload で消去されます |

### 2.1 引数変換ルール

| 対象型 | 文字列例 → 変換 |
|---|---|
| `string` | `"hello"` → `"hello"` |
| `bool` | `"true"` / `"false"` → `bool.Parse` |
| 数値 primitive（int/long/float/double…） | `"42"` → `Convert.ChangeType` |
| enum | `"Default"` → `Enum.Parse(type, value, ignoreCase: true)` |
| 参照型 / `Nullable<T>` | `"null"` リテラル → `null` |
| その他複雑な型 | ❌ v1 では未対応。専用 Cmd を作成してください |

### 2.2 Type 解決

`AssemblyExtensions.GetTypeByFullName` による**厳格マッチング**で行われます：
1. `AssemblyExtensions.TypeDic`（UCL_Core 全体で共有 cache）に対して FQN を完全一致で検索 — O(1)
2. 見つからない場合は直接 null を返し、呼び出し側で `type not found: ... (use exact Type.FullName, case-sensitive)` を報告します

> [!IMPORTANT]
> **`type` は完全な `Type.FullName` で、大文字小文字を厳密に区別する必要があります** — ignoreCase の fallback はありません。
> これは意図的な設計です：エージェントの誤入力はその場で弾かれるべきであり、「タイプミスで偶然同名の別 type にヒットする」ような潜在バグを防ぎます。
> `paramTypes` も同じルールです。

---

## 3. 実例

### 3.1 Unity の再コンパイルをトリガー（引数なし method）

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.Compilation.CompilationPipeline" \
  --arg "member=RequestScriptCompilation"
```

`Cmd_Recompile` のコアロジックと同等です（違いは：`Cmd_Recompile` ではさらに `AssetDatabase.Refresh()` を実行し、`recompile` サブコマンド経由ではコンパイル完了まで待機します）。

### 3.2 プロパティの読み取り

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.EditorApplication" \
  --arg "member=isCompiling" \
  --arg "kind=property"
```

Unity Console には `[AgentCmd:Invoke] OK (System.Boolean) = False` が出力されます。

### 3.3 enum 引数を持つ method（オーバーロードの曖昧解消）

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.AssetDatabase" \
  --arg "member=Refresh" \
  --arg "paramTypes=UnityEditor.ImportAssetOptions" \
  --arg "args=Default"
```

`AssetDatabase.Refresh` には 2 つのオーバーロードがあり、`paramTypes` で enum 引数を取る方を特定します。

### 3.4 EditorPrefs の設定（property setter）

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.EditorPrefs" \
  --arg "member=SetString" \
  --arg "paramTypes=System.String;System.String" \
  --arg "args=MyKey;MyValue"
```

> 注意：ここでは setter プロパティではなく `EditorPrefs.SetString(key, value)` メソッドを使用します。EditorPrefs にはインデックス付きプロパティが存在しないためです。

### 3.5 複数引数の method

```bash
python run_cmd.py run Invoke \
  --arg "type=UnityEditor.AssetDatabase" \
  --arg "member=ImportAsset" \
  --arg "paramTypes=System.String;UnityEditor.ImportAssetOptions" \
  --arg "args=Assets/SomeFile.txt;ForceUpdate"
```

### 3.6 internal / private static API（`nonPublic=true`）

Unity 組み込み API の多くは `internal` です（例：一部の build pipeline ツール）。デフォルトの `nonPublic=false` では見つかりませんが、`nonPublic=true` を指定すれば検索可能です。該当のエラーメッセージにもヒントが含まれており、public メンバーが見つからない場合は `... — try nonPublic=true` と報告されます。

### 3.7 instance method + 変数チェーン（`target=$var` / `storeAs=...`）

複数の invoke を chain として連結できます — UCL_Asset を取得し、その instance method を呼び、さらに次を呼ぶ、という流れです。各ステップで `storeAs=name` により戻り値を `Variables[name]` に格納し、次のステップでは `target=$name` で参照します：

```bash
# step 1: RCG_StoryData.Util を取得（UCL_Util<T> から継承された static property）
python run_cmd.py run Invoke \
  --arg "type=RCG.RCG_StoryData" --arg "member=Util" --arg "kind=property" \
  --arg "storeAs=util"

# step 2: $util.GetData("AbandonedTemple") — instance method
#   GetData(string id, bool useCache=true) — 第 2 引数の default value は自動補完
python run_cmd.py run Invoke \
  --arg "target=\$util" --arg "member=GetData" --arg "args=AbandonedTemple" \
  --arg "storeAs=story"

# step 3: $story.GetSubStory("Start") — instance method
python run_cmd.py run Invoke \
  --arg "target=\$story" --arg "member=GetSubStory" --arg "args=Start" \
  --arg "storeAs=sub"
```

**Variables のライフタイム**：Editor 全体 / Cmd をまたぐ / バッチをまたいで有効ですが、**domain reload で消去されます**（`Cmd_Recompile` を含む）。状態汚染を避けるため、意図的に永続化していません。手動でリセットする場合は `Invoke(UCL.Core.UCL_ReflectionInvoker, ClearVariables)` を呼び出してください。

**`args` 内の `$varname` 参照**：`target` だけでなく、`args=...;$varname;...` でも Variables に格納されたオブジェクトを引数として渡せます。string 引数内の `$` をエスケープする必要はありませんが、リテラル値 `$abc` を本当に渡したい場合、現状は変数参照と誤認識されます（v1 の制限）。

**default value の自動補完**：`args` で指定する値の数は method のパラメーター総数より少なくても構いません。末尾側の不足分は `[DefaultValue]` を持つ場合に自動補完されます（`GetData(string, bool=true)` なら string だけで OK）。多すぎる場合は失敗します。

---

## 4. 結果処理

| 状況 | Unity Console | Cmd 結果 |
|---|---|---|
| void method 成功 | `OK (void / null)` | Success |
| 戻り値あり method | `OK (TypeName) = value.ToString()` | Success（値は Console。構造化が必要なら専用 Cmd を作成）|
| 解決失敗（type / member 未検出 / 引数変換失敗）| `LogError + throw` | Failed |
| 内部例外 | `target threw {ExceptionType}: ...` | Failed |

> [!CAUTION]
> **戻り値は現在 Unity Console（`Debug.Log`）にのみ出力**され、ディスクへの書き込みも Python への返却もありません。
> 構造化された値が必要な場合は、(a) 専用 Cmd を作成するか、(b) 本 Cmd に将来追加される `outputPath` 引数をお待ちください。

---

## 5. 安全 / 制限

| 項目 | 説明 |
|---|---|
| **scope** | static / instance ともにサポート（instance は `target=$varname` 経由）。`Public` は常時 ON、`NonPublic` は `nonPublic=true` で追加。static のルックアップは BaseType 階層も走査するため、generic 基底クラスの static も検出可能 |
| **side effect** | 呼び出す API に依存します — `RequestScriptCompilation` を呼ぶと domain reload が発生し、in-flight の async cmd はすべて中断されます（このため `Cmd_Recompile` は「リクエスト送出後に即 return」する設計になっています）|
| **type ambiguity** | オーバーロードがある method は `paramTypes` が必須。指定しないとエラーとなり、候補リストがエラーメッセージに表示されます |
| **threading** | すべて Unity メインスレッドで実行されます。スレッドをブロックする API は呼び出さないでください |
| **destructive call** | データ消失を伴う API（例：`AssetDatabase.DeleteAsset`）はエージェントから本 Cmd で呼び出すべきではありません — 確認フロー付きの専用 Cmd を作成してください |

---

## 6. 他の Cmd / ツールとの関係

| ツール | 使う場面 |
|---|---|
| **Cmd_Invoke**（本ドキュメント）| 単発の呼び出し / 探索 / 専用 Cmd は無いがエージェントから API をトリガーしたいとき |
| **Cmd_Recompile** | 専用：再コンパイルをトリガーし、Python の `recompile` サブコマンドと組み合わせてコンパイル完了まで待機 |
| **Cmd_ResolveAssetReferences** | 専用：UCL_Asset 参照チェーンを BFS（出力が複雑なため Invoke には不向き）|
| **Cmd_FindAssetUsages** | 専用：参照箇所の逆引き |
| 新しい Cmd を作成 | 同じ args の組を繰り返し使う / 構造化出力が必要 / バリデーションが必要 — [Create_Cmd_Workflow](../../Workflows/Create_Cmd_Workflow.md) を参照 |

> [!TIP]
> **rule of thumb**：探索段階では `Cmd_Invoke`、安定したら専用 Cmd に切り出します。

---

## 7. トラブルシューティング

| 症状 | 考えられる原因 | 解決方法 |
|---|---|---|
| `type not found: X (use exact Type.FullName, case-sensitive)` | FQN のタイプミス / namespace 不足 / 大文字小文字の誤り | Unity の逆コンパイルから正しい `Type.FullName` を取得（大文字小文字を厳密に区別）— `unityeditor.AssetDatabase` と `UnityEditor.AssetDatabase` は別物です |
| `static method not found ... — try nonPublic=true` | method が internal / private static | `nonPublic=true` を追加 |
| nonPublic 有効でも `static method not found` | method が instance メンバー | `target=$varname` で instance を渡す（事前に `storeAs=` で格納しておく）|
| `target variable '$xxx' not found in Variables` | $xxx を `storeAs` で格納していない / domain reload で消去された | 先に `storeAs=xxx` で格納する invoke を実行する。再コンパイルや play mode の切り替えで Variables は消去されます |
| `arg count mismatch: method expects N, got M`（M > N）| args が多すぎる | method のパラメーター数までに留める。末尾の optional は default が自動補完されます |
| `arg[i] '$xxx' not found in Variables` | `args` 内で `$xxx` を使ったが、その変数が存在しない | 該当変数が `storeAs` で格納済みか確認 |
| `ambiguous method (need paramTypes)` | 同名のオーバーロード | `paramTypes` を追加して 1 つに固定。エラーメッセージには候補がすべてリストされます |
| `enum parse failed` | enum を名前ではなく数値（例：`0`）で指定した | 名前で指定（例：`Default`）|
| Cmd が Failed だがエラーが見えない | Unity Console が背面にある | Console ウィンドウを開き `[AgentCmd:Invoke] FAILED: ...` を確認 |

---

## 8. 関連ドキュメント

- [Edit_Recompile_Loop_Workflow](../../Workflows/Edit_Recompile_Loop_Workflow.md) — .cs を編集した後の同期ループ（本 Cmd で新規ファイルを追加した場合も必要）
- [Create_Cmd_Workflow](../../Workflows/Create_Cmd_Workflow.md) — Invoke を専用 Cmd に切り出す判断基準
- [Cmd_Recompile](Cmd_Recompile.md) — `Invoke(CompilationPipeline.RequestScriptCompilation)` + 待機と同等
- `UCL.Core.UCL_ReflectionInvoker`（`UtilCore/`、runtime-available）— 純ロジックの解析 / 実行層。任意の Cmd / Editor ボタン / runtime ツールから直接呼び出せます
- `AssemblyExtensions`（`ExtensionMethodCore/`）— Type 解決 cache（`GetTypeByFullName` 厳格マッチング）と `TryConvertFromString`。引数型変換のサポート拡張が可能
