# UCL_ModResourceAsset (モジュールリソースアセット)

## 1. システム概要
`UCL_ModResourceAsset` は、Ringworld プロジェクトにおいて「モジュール化された外部リソース」を扱うためのコアアセットクラスです。開発者は ID システムを通じて、Mod フォルダ内に保存された外部画像（Sprite または Texture2D）を参照でき、非同期読み込みと自動リソース解放メカニズムが提供されます。

## 2. コア機能
*   **動的パス提供**: `ModuleID` とアセット設定に基づいて、物理ファイルのパスを自動的に特定します。
*   **非同期読み込みメカニズム**: `UniTask` による地形、アイテム、またはキャラクターのテクスチャの非同期読み込みをサポートし、メインスレッドのスタッタリングを回避します。
*   **ライフサイクル管理**: `IDisposable` インターフェースを実装しており、アセットが使用されなくなったときにメモリ内のテクスチャリソースを正しく解放します。
*   **プレビュー機能**: エディタインターフェースでリアルタイムのプレビューと編集エントリを提供します。

## 3. データ構造 (UCL_ModResourcesData)
アセットのコアデータは `m_ModResourcesData` メンバに格納されており、以下を含みます。
*   `m_ModuleID`: 所属するモジュールの唯一の識別子。
*   `m_FolderPath`: モジュールリソースルートからの相対サブフォルダパス。
*   `m_FileName`: ターゲットファイル名（拡張子を含む）。

## 4. 使用例 (C#)
```csharp
// リソースエントリから Sprite を非同期で取得
public async UniTask SetupIcon(UCL_ModResourceEntry iEntry, CancellationToken iToken)
{
    Sprite icon = await iEntry.GetData().GetSpriteAsync(iToken);
    m_IconImage.sprite = icon;
}
```

## 5. 注意事項
> [!IMPORTANT]
> モジュールリソースが正しい `ModResources/` サブディレクトリに配置されていることを確認してください。そうでない場合、システムはパスに基づいてファイルを特定できません。
