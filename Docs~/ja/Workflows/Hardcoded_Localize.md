# ハードコードされた多言語ワークフロー (UCL_CodeLocalize)

## 1. 概要
`UCL_CodeLocalize` は、コア UI 文字列を C# コードに直接保存するために設計された、高性能なハードコードされた多言語ユーティリティです。外部の JSON/CSV ローカライズファイルが欠落している場合の信頼できるフォールバック（Fallback）として、また高速な代替手段として機能します。

### なぜハードコードされたローカライズを使用するのか？
*   **安全性**：重要な UI 文字列（「保存」、「キャンセル」、「エラー」など）は、外部アセットファイルが失われた場合でも常に利用可能です。
*   **パフォーマンス**：C# の `switch` 式を使用して O(1) またはそれに近いクエリ速度を実現し、実行時のメモリ割り当てをゼロにします。
*   **メンテナンス性**：`partial class` を利用して、各言語の翻訳を独立したファイルに分割します。

## 2. アーキテクチャ
システムは、1 つのコアロジックファイルと、複数の言語固有の partial ファイルで構成されています。
*   `UCL_CodeLocalize.cs`：`UCL_LocalizeManager.s_LangName` に基づくコアディスパッチロジック。
*   `UCL_CodeLocalize.en.cs`：英語翻訳（最終フォールバック）。
*   `UCL_CodeLocalize.zh-Hant.cs`：繁体字中国語翻訳。
*   ...（その他の言語）

## 3. 使用方法

### 3.1 翻訳文字列の取得
コード内で `UCL_CodeLocalize.Get(key)` を呼び出すだけです。
```csharp
string windowTitle = UCL_CodeLocalize.Get("UCL_ModuleServiceEditPage");
```

### 3.2 フォールバックロジック (Fallback Logic)
1.  システムは `UCL_LocalizeManager.s_LangName` を介して現在の言語を識別します。
2.  対応する言語ファイル内でキーを検索しようとします。
3.  見つからない場合（`null` を返す）、**英語 (en)** バージョンにフォールバックします。
4.  英語でも見つからない場合は、**キー（Key）** 自体を返します。

## 4. 新しい翻訳の追加方法

### ステップ 1：言語ファイルにキーを追加する
関連する言語ファイル（例：`UCL_CodeLocalize.ja.cs`）を開き、`switch` 式にキーと値のペアを追加します。

```csharp
static public string Get_ja(string iKey)
{
    return iKey switch
    {
        "My_New_Key" => "私の新しいキー",
        // ... 既存の項目
        _ => null
    };
}
```

### ステップ 2：英語のフォールバックを確実にする
他の言語のユーザーが翻訳を欠いている場合でも、少なくとも英語の説明が表示されるように、必ず `UCL_CodeLocalize.en.cs` にエントリを追加してください。

```csharp
static public string Get_en(string iKey)
{
    return iKey switch
    {
        "My_New_Key" => "My New Key",
        _ => iKey // 英語ブランチは常にデフォルトとして iKey を返す必要があります
    };
}
```

## 5. ベストプラクティス
> [!IMPORTANT]
> `UCL_CodeLocalize` は **コア UI** および **フレームワーク文字列** に使用してください。非プログラマーによる頻繁な更新が必要なゲームコンテンツ（アイテム名、ダイアログなど）については、引き続き `UCL_LocalizeAsset`（外部 CSV/テキストファイル）を使用してください。
