# UCL_CSVAsset (CSV データアセット)

## 1. システム概要
`UCL_CSVAsset` は、モジュール化された CSV ファイルを処理するための専用アセットクラスです。`UCL_Asset` 体系を継承し、`UCL_ModResourcesData` を利用して Mod フォルダ内の物理ファイルを特定します。また、`UCL.Core.CsvLib` と統合されており、構造化されたテーブルデータへのアクセスを提供します。

## 2. コア機能
*   **モジュール化ファイルの読み込み**: 特定の Mod の `ModResources` ディレクトリから `.csv` ファイルを読み込むことをサポートします。
*   **リアルタイムパース**: `GetCSVData()` メソッドを提供し、生の CSV テキストを、行 (Row) や列 (Column) の操作インターフェースを備えた `CSVData` オブジェクトに変換します。
*   **非同期サポート**: `GetCSVTextAsync` を内蔵しており、バックグラウンドスレッドで大容量のテキスト読み込みを実行し、UI のフリーズを回避します。
*   **内容サマリープレビュー**: Unity Inspector や UCL エディタページにおいて、CSV ファイルの最初の 5 行を自動的に表示し、データ構造を素早く確認できます。

## 3. 使用方法
### C# スクリプトでの参照
```csharp
[SerializeField] private UCL_CSVEntry m_ConfigTable;

public void LoadConfig()
{
    CSVData data = m_ConfigTable.GetCSVData();
    if (data != null)
    {
        // 最初の行、2番目の列のデータを取得
        string val = data.GetData(0, 1);
        Debug.Log($"Config Value: {val}");
    }
}
```

## 4. データ構造
このアセットは内部で `UCL_ModResourcesData` をラップしています。
*   `m_ModuleID`: ファイルが属する Mod の識別子。
*   `m_FolderPath`: モジュールリソースルートからの相対パス。
*   `m_FileName`: CSV ファイル名（`.csv` 拡張子を含む）。

## 5. 注意事項
> [!TIP]
> 全プラットフォームで文字が正しく解析されるよう、ファイルには `UTF-8` エンコーディングを使用することをお勧めします。
