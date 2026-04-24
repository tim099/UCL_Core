# UCL_Module & UCL_ModuleEntry API

## 1. UCL_Module
モジュール固有のロジックとデータの主要なコンテナです。単一のモジュールのロード、保存、およびインストールを処理します。

### コアプロパティ (Core Properties)
- `ID`: モジュールのユニークな識別子。
- `ModuleEditType`: 現在の編集モード（`Builtin` または `Runtime`）。
- `ModuleEntry`: このモジュールインスタンスのパス固有の操作を提供します。
- `m_Config`: `Version` (バージョン)、`Title` (タイトル)、`Description` (説明)、および `DependenciesModules` (依存モジュール) などのメタデータを格納します。

### 主要メソッド (Key Methods)
- `Load(string id, UCL_ModuleEditType type)`: 指定されたソースからモジュール設定をロードします。
- `Save()`: 現在のモジュール設定を `Config.json` に永続化します。
- `CheckAndInstall()`: 現在のバージョンを組み込みバージョンと比較し、更新が必要な場合は `Install()` をトリガーします。
- `Install()`: 組み込みソースから実行時の永続ストレージにモジュール内容をコピーまたは展開します。
- `ExportModule(bool exportConfig)`: 配布用にモジュールを Zip 圧縮します。
- `GetAssetMeta(string typeName)`: 特定のアセットタイプのグループ化およびソート用のメタデータを取得します。

---

## 2. UCL_ModuleEntry
ID によってモジュールを参照するために使用される軽量なシリアライズ可能なクラスです。Inspector のポップアップや依存関係リストで頻繁に使用されます。

### コアプロパティ (Core Properties)
- `ID`: 参照されるモジュールの ID。
- `Module`: `UCL_ModuleService` を介して完全な `UCL_Module` インスタンスを遅延ロードして返します。

### 静的ヘルパー (Static Helpers)
- `CoreModuleID`: システムの「コア」 (Core) モジュールの定数。
- `CoreModule`: コアモジュール用に事前に設定された `UCL_ModuleEntry` を返します。

---

## 3. 関連する列挙型 (Related Enums)

### UCL_AssetType
アセットの異なるルート場所を定義します：
- `StreamingAssets`: アプリの読み取り専用フォルダ内のアセット。
- `PersistentDatas`: ユーザーが書き込み可能なデータパス内のアセット。
- `BuiltinModules`: 組み込みモジュールのソースファイルのルート。
- `SteamMods`: Steam ワークショップコンテンツのパス。

### ELoadingState
非同期ロードの進行状況を追跡します：
- `None`, `Loading`, `Complete`, `Disposed`。
