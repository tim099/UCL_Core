# HelpURL システムとワークフロー (HelpURL System & Workflow)

## 1. コアコンセプト
UCL は Unity ネイティブの `HelpURLAttribute` を拡張し、「環境をまたぐ解決」、「多言語サポート」、そして「下流モジュールの拡張性」を備えたヘルプシステムを構築しました。

### 1.1 特殊プレフィックスと Prefix Resolver メカニズム
`UCL_URL` は **Resolver 登録表** アーキテクチャを採用しています。`xxx:RelativePath` 形式（コロンの後に `//` が続かないもの）の URL は、登録済み Resolver にディスパッチされます：

*   **形式**: `{prefix}:Docs~/{lang}/YourDoc.md`
*   **解決ロジック (`UCL_URL`)**:
    *   **prefix マッチ**: 該当 Resolver の `Resolve` を呼び出します。Editor / Build の差異は **登録側** が `#if UNITY_EDITOR` で切り替える責務であり、インターフェース自体は単一の `Resolve` メソッドのみを公開します。
    *   **未マッチ prefix**: 元の URL を保持し、`{lang}` 置換とローカルパス補完の処理に進みます。

> [!NOTE]
> UCL_Core 自身の `ucl_core:` prefix も同じ登録メカニズムで掛けられており、特例はありません。下流モジュールが独自 prefix（例: `eov_docs:`）を追加する場合、起動時に一度登録すれば良く、**UCL_Core の修正は不要** です。

### 1.2 ローカライズプレースホルダー: `{lang}`
*   **目的**: 現在の言語に基づいてドキュメントのパスを自動的に切り替えます。
*   **ロジック**: `UCL_LocalizeService.CurLang`（例: `en`, `zh-Hans`, `ja`）に置き換えられます。
*   **エディタのフォールバック**: 特定の言語のファイルが見つからない場合、エディタ上では 404 を避けるために `en` バージョンの検索を試みます。
*   **責務**: `{lang}` 置換は `UCL_URL` の共通層で処理されます。**Resolver 側で個別に実装する必要はありません。**

### 1.3 隠しフォルダ: `Docs~`
*   **物理的意義**: Unity は `~` で終わるフォルダを無視します。ドキュメントを `Docs~` に保存することで、`.meta` ファイルを生成したり Project ウィンドウを散らかしたりすることなく、モジュールディレクトリ内に保持できます。

---

## 2. ワークフロー

### ステップ A: ドキュメントの作成
1.  `Assets/UCL/UCL_Core/Docs~/{lang}/` 内に Markdown ファイルを作成します。
    - 例: `Docs~/ja/MyFeature.md`
2.  技術的な説明やガイドを記述します。

> [!IMPORTANT]
> ドキュメントが特定の Class に関するものである場合、ファイル名は Class 名と**一致させなければなりません**（例：`class UCL_ModuleServiceEditPage` の場合は `UCL_ModuleServiceEditPage.md`）。

### ステップ B: 属性の付与 (HelpURL)
#### ケース 1: アセットまたはデータクラスの場合
クラス宣言の上に `[HelpURL]` を追加します。必ず `{lang}` を使用してください。
```csharp
[HelpURL("ucl_core:Docs~/{lang}/API/MyFeatureAsset.md")]
public class MyFeatureAsset : UCL_ModResourceAsset { ... }
```

#### ケース 2: エディタページ (`UCL_EditorPage`) の場合
同様に `[HelpURL]` を追加します。
```csharp
[HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/MyFeatureEditPage.md")]
public class MyFeatureEditPage : UCL_EditorPage { ... }
```

---

## 3. 下流モジュールでカスタム Prefix を拡張する

### 3.1 拡張が必要な場面
下流プロジェクト（例：非公開のゲーム本体だが、ドキュメント自体は公開リポジトリで管理）で `[HelpURL]` を自前のドキュメントへ向けつつ、**UCL_Core 内に特定プロジェクト依存の URL を埋め込みたくない** 場合。

### 3.2 登録方法: Lambda 形式（推奨）
ほとんどの場合、`Path.Combine` / 文字列連結で十分です。インターフェースを実装せずに `UCL_UrlPrefixResolver` を使えます：

```csharp
using UCL.Core;
using UnityEngine;

public static class EoV_DocsResolverBootstrap
{
    private const string BUILD_BASE_URL = "https://github.com/tim099/EmblemOfValorDocuments/blob/main/";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoadMethod]
#endif
    private static void Register()
    {
        UCL_URL.RegisterResolver(new UCL_UrlPrefixResolver(
            prefix: "eov_docs",
#if UNITY_EDITOR
            // [Editor] ローカル submodule パスに連結し、オフライン閲覧を可能にする。
            resolver: (relativePath) => System.IO.Path.Combine(EoV_DocsPath.Root, relativePath)
#else
            // [Build] GitHub blob URL に連結し、プレイヤーがブラウザで開けるようにする。
            resolver: (relativePath) => BUILD_BASE_URL + relativePath
#endif
        ));
    }
}
```

### 3.3 登録方法: インターフェース形式
状態や条件分岐を持つ複雑な Resolver なら、`IUCL_UrlPrefixResolver` を直接実装します：

```csharp
public sealed class MyComplexResolver : IUCL_UrlPrefixResolver
{
    public string Prefix => "my_proj";
    public string Resolve(string relativePath)
    {
#if UNITY_EDITOR
        // [Editor] ローカルパスを返す
        return /* ... */;
#else
        // [Build] クラウド URL を返す
        return /* ... */;
#endif
    }
}
```

### 3.4 登録した Prefix の使用
`ucl_core:` と完全に同じです：

```csharp
[HelpURL("eov_docs:Docs~/{lang}/Mechanics/CombineSetting.md")]
public class CombineSettingAsset { ... }
```

> [!IMPORTANT]
> **登録タイミングの落とし穴**: `UCL_URL.OpenURL` が Resolver 登録より前に呼ばれる可能性がある場合、リンク解決は静かに失敗します。必ず `[InitializeOnLoadMethod]`（Editor）と `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`（Runtime）の **両方** を付けてください。

> [!NOTE]
> 同じ prefix では後勝ち。`UCL_URL` は上書き時に Warning を出しますが許容するため、下流側で UCL のデフォルトクラウド URL（例: 自身の fork に変更）を差し替えられます。

---

## 4. システムコンポーネント
*   **`UCL_URL.cs`**: URL 解決のメインフロー。prefix → resolver の登録表を保持し、`{lang}` 置換と `en` フォールバックも担当。
*   **`IUCL_UrlPrefixResolver`**: Resolver の契約インターフェース（`UCL_URL` と同ファイル）。`Prefix` と単一の `Resolve` のみを定義し、Editor / Build の切替は登録側の責務とする。
*   **`UCL_UrlPrefixResolver`**: Lambda デリゲートで戦略を渡せる軽量 Resolver 実装。下流が prefix ごとにクラスを作る手間を省きます。
*   **`UCL_GUILayoutDrawObject.DrawHelpButton`**: `?` ボタンを描画し `UCL_URL.OpenURL` を呼び出す GUI レベルのラッパー。
*   **`UCL_EditorPage.cs`**: `HelpURL` 属性を自動的にキャッシュし、TopBar に描画する基本ページクラス。
