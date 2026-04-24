# HelpURL システムとワークフロー (HelpURL System & Workflow)

## 1. コアコンセプト
UCL は Unity ネイティブの `HelpURLAttribute` を拡張し、「環境をまたぐ解決」と「多言語サポート」を備えたヘルプシステムを構築しました。

### 1.1 特殊プレフィックス: `ucl_core:`
モジュールがプロジェクト間を移動したり、ビルドとしてリリースされたりしてもリンクが有効であることを保証するために、相対パス解決を使用します。
*   **形式**: `ucl_core:Docs~/{lang}/YourDoc.md`
*   **解決ロジック (`UCL_URL`)**:
    *   **エディタモード**: ローカルパス `[UCL_Core ルート]/Docs~/{lang}/YourDoc.md` に自動的に解決されます。オフライン閲覧をサポートします。
    *   **ビルドモード**: リリースビルドでもクラウドドキュメントにアクセスできるよう、自動的に GitHub リンクに変換されます。

### 1.2 ローカライズプレースホルダー: `{lang}`
*   **目的**: 現在の言語に基づいてドキュメントのパスを自動的に切り替えます。
*   **ロジック**: `UCL_LocalizeService.CurLang`（例: `en`, `zh-Hans`, `ja`）に置き換えられます。
*   **エディタのフォールバック**: 特定の言語のファイルが見つからない場合、エディタ上では 404 を避けるために `en` バージョンの検索を試みます。

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

## 3. システムコンポーネント
*   **`UCL_URL.cs`**: URL 文字列のパースを担当し、`{lang}` の置換を処理します。
*   **`UCL_GUILayoutDrawObject.DrawHelpButton`**: ボタンを描画し `UCL_URL.OpenURL` を呼び出す GUI レベルのラッパー。
*   **`UCL_EditorPage.cs`**: `HelpURL` 属性を自動的にキャッシュし、TopBar に描画する基本ページクラス。
