# UCL_ModulePath & パス管理 API

## 1. UCL_ModulePath (静的クラス)
フォルダ構造の定義とモジュールの配布管理（Zip 圧縮/インストール）のためのコアユーティリティです。

### 主要機能
- **Zip 圧縮 (Zipping)**: `ZipAllModules()` は、エクスポート対象としてマークされたすべてのモジュールを `StreamingAssets` に圧縮します。
- **ビルド前処理 (Pre-build Processing)**: `OnPreprocessBuild()` は、Unity ビルドがトリガーされる前にモジュールを自動的にパッケージ化します。

---

## 2. UCL_ModulePath.PersistantPath.ModulesEntry
特定の `UCL_ModuleEditType` の下にあるモジュールのセットを管理します。

### コアプロパティ (Core Properties)
- `RootFolder`: ベースパス（例：`persistentDataPath/.Modules`）。
- `ModulesPath`: 個々のモジュールディレクトリを含むサブフォルダ。
- `ConfigPath`: このルートのグローバルな `Config.json` へのパス。

### メソッド (Methods)
- `LoadConfig()`: グローバルなモジュール設定をロードします。
- `GetModulePath(string id)`: 特定のモジュールフォルダへの絶対パスを返します。
- `GetModuleEntry(string id)`: 特定のモジュールに対するパス操作用の `ModuleEntry` オブジェクトを返します。
- `ZipAllModules()`: 個々のモジュールを `StreamingAssets` 内の `.zip` ファイルにパッケージ化します。

---

## 3. UCL_ModulePath.PersistantPath.ModuleEntry
**単一のモジュール**にスコープされたパス操作。

### 主要メソッド (Key Methods)
- `Install()`: モジュールを Builtin から Runtime に同期します。フォルダコピーと `.zip` 展開の両方をサポートします。
- `UnInstall()`: Runtime パスからモジュールを削除します。
- `GetAssetPath(Type type, string id)`: 特定のアセットの `.json` ファイルへのパスを返します。
- `GetAssetFolderPath(Type type)`: モジュール内の特定のアセットタイプのディレクトリを返します。
- `ZipModule(string targetFolder)`: モジュールを `.zip` ファイルに圧縮します。

---

## 4. パス設定ロジック (Path Configuration Logic)
システムは相対パスを定義するために `UCL_ModulePathConfig` に依存しています。

### 標準的な構造：
- **Root**: `ModulesRoot`
    - **Config**: `Config.json`
    - **Modules**: `Modules/`
        - `{ModuleID}/`
            - `Config.json`
            - `Resources/`
                - `{AssetType}/`
                    - `{AssetID}.json`
                    - `.CommonDataMeta` (アセットメタデータ)
