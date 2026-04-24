---
title: UCL_ModuleService API ドキュメント
description: モジュールのライフサイクル管理サービスの完全な API リファレンス。初期化、ロード、保存、アセットキャッシュ、および GUI フローを網羅。
last_updated: 2026-04-24
target_audience: [AI_Agent, Gameplay_Programmer, Tool_Developer]
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModuleService.cs
namespace: UCL.Core
---

# UCL_ModuleService API ドキュメント

## 1. システム概要 (System Overview)

`UCL_ModuleService` は UCL フレームワークの**モジュールライフサイクル管理のコア**であり、シングルトンとして動作します。以下の責任を負います：

- **モジュールのロード、インストール、および依存関係の解決** — `UCL_Module` とその `m_DependenciesModules` を再帰的にロードします。
- **アセットパスの解決とキャッシュ管理** — `AssetConfig` / `AssetsCache` を介して、各 `UCL_Asset` の実際の保存パスを決定します。
- **プレイリスト駆動の実行時切り替え** — `UCL_ModulePlaylist` に基づいて、ロードされるモジュールのセットを動的に切り替えます。
- **エディタワークフローのサポート** — `EditModule` (モジュールの編集)、`CreateNewModule` (新規モジュールの作成) などの Inspector/GUI 操作のエントリポイントを提供します。
- **クロスプラットフォームの初期化** — `Application.isEditor` に基づいて、Builtin / Runtime インストールモードを自動的に切り替えます。

> [!IMPORTANT]
> `UCL_ModuleService` は**遅延ロードされるシングルトン** (`Ins` プロパティ) を使用します。最初のアクセスで自動的にインスタンスが作成され、非同期プロセス `InitAsync()` を開始する `Init()` が呼び出されます。そのため、同期コードで `Ins` にアクセスした直後に**初期化が完了していることは保証されません**。安全を確保するために `WaitUntilInitialized(token)` を使用してください。

---

## 2. 列挙型 (Enums)

### 2.1 `UCL_ModuleEditType`

```csharp
public enum UCL_ModuleEditType
{
    Builtin,  // StreamingAssets に保存されたモジュールデータ（読み取り専用、ビルド時にパッケージ化）
    Runtime,  // PersistentDataPath に保存されたモジュールデータ（読み書き可能、実行時に変更可能）
}
```

| 値 | 説明 |
|---|---|
| `Builtin` | 内部モジュール。データは `StreamingAssets` にあり、エディタでのみ編集可能です。 |
| `Runtime` | 実行時モジュール。データは `PersistentDataPath` にあり、プレイヤーのカスタマイズや Mod をサポートします。 |

---

### 2.2 `UCL_ModuleService.State`

`UCL_PlayerPrefs` を介して永続化される、`UCL_ModuleService` の **GUI ページ状態**を制御します。

```csharp
public enum State
{
    Main,       // メインページ：編集するモジュールを選択します。
    EditModule, // 編集ページ：指定されたモジュールの編集プロセスに入ります。
}
```

---

### 2.3 `UCL_ModuleService.EditorInstallMode`

エディタ環境でのモジュールのインストール動作を制御します。

```csharp
public enum EditorInstallMode
{
    Default, // モジュールフォルダをターゲットのインストールパスに直接コピーします。
    UnZip,   // 実機をシミュレート：インストール前に .zip パッケージを展開します。
}
```

---

## 3. コアな静的メンバ (Core Static Members)

### 3.1 シングルトンアクセス (Singleton Access)

```csharp
public static UCL_ModuleService Ins { get; }
```

- **動作**: 最初のアクセス時に自動的にインスタンスを作成し、`Init()` を呼び出します。
- **スレッド安全性**: メインスレッドのみ。

---

### 3.2 ステータスと初期化 (Status and Initialization)

| メンバ | 型 | 説明 |
|---|---|---|
| `Initialized` | `bool` (static) | シングルトンが存在し、`m_Initialized == true` の場合は `true`。 |
| `CurState` | `State` (static) | 現在の GUI ページ状態。`UCL_PlayerPrefs` に永続化されます。 |
| `ModuleEditType` | `UCL_ModuleEditType` (static) | 現在のモジュール編集タイプ。切り替えると `m_ModuleCache` がクリアされます。 |

---

### 3.3 モジュール参照 (Module References)

| メンバ | 型 | 説明 |
|---|---|---|
| `CurEditModule` | `UCL_Module` (static) | 現在編集中のモジュール。`Ins.m_CurEditModule` を返します。 |
| `CurEditModuleID` | `string` (static) | 現在の編集モジュール ID。`m_CurEditModule == null` の場合はコアモジュール ID を返します。 |
| `ModResourcesPath` | `string` (static) | 現在の編集モジュールのリソースパス。リフレクション呼び出しに使用されます。 |
| `PathConfig` | `UCL_ModulePathConfig` (static) | グローバルなパス設定オブジェクトにアクセスします。 |

---

### 3.4 モジュールのロードイベント (Module Loading Events)

```csharp
public static event System.Action OnLoadModule;    // モジュールのロード開始前にトリガーされます。
public static event System.Action OnLoadedModule;  // 同期的なモジュールのロードが完了した直後にトリガーされます。
```

#### 非同期ロードのコールバック (Asynchronous Loading Callbacks)

```csharp
public static void AddLoadedModuleFunc(System.Func<CancellationToken, UniTask> func);
public static void RemoveLoadedModuleFunc(System.Func<CancellationToken, UniTask> func);
```

- **目的**: モジュールのロード完了後の非同期パイプラインに、追加のタスクを登録します。
- **実行順序**: `UCL_OnModuleLoadedAsset` 内のすべてのアセットが `m_Order` に従って順次実行された後、登録されたすべての `Func` が並列に実行されます。

---

## 4. 初期化フロー (Initialization Flow)

### `WaitUntilInitialized(CancellationToken)`

```csharp
public static async UniTask WaitUntilInitialized(CancellationToken iToken);
```

- **目的**: 非同期コンテキストで `UCL_ModuleService` が完全に初期化されるのを待ちます。
- **推奨**: モジュールデータに依存するすべてのシステム（例：`ClimateSim`, `Earth`）は、起動時にこのメソッドを呼び出す必要があります。

#### 初期化フローチャート

```
Ins (プロパティアクセス)
  └─ new UCL_ModuleService()
       └─ Init()
            └─ InitAsync()  [非同期]
                 ├─ ModuleEditType を同期 → m_PathConfig
                 ├─ Directory.CreateDirectory(ModulesPath)
                 ├─ await LoadConfig()          ← 設定 JSON を読み込み
                 ├─ ExportModules に基づいて CheckAndInstall / Install を実行
                 ├─ m_Config.m_Playlist.LoadPlaylist(false)
                 └─ m_Initialized = true
```

---

## 5. 内部クラス (Inner Classes)

### 5.1 `Config`

`UCL_ModuleService` の主要な設定コンテナ。`UnityJsonSerializable` を継承し、`PathConfig` が指す JSON ファイルにシリアライズされます。

| フィールド | 型 | 説明 |
|---|---|---|
| `m_BuiltinModules` | `List<string>` | すべての組み込みモジュール ID のリスト（StreamingAssets 内）。 |
| `m_ExportModules` | `Dictionary<string, ModuleExportConfig>` | エクスポート/インストールされるモジュールとその設定。 |
| `m_Playlist` | `UCL_ModulePlaylist` | どのモジュールがロードされ実行されるかを決定するプレイリスト。 |
| `m_ForceInstallInEditor` | `bool` | エディタ環境ですべてのモジュールの再インストールを強制するかどうか。 |
| `m_Version` | `string` | 設定のバージョン番号（デフォルトは `"1.0.0"`）。 |
| `m_AssetGroupSortingOrder` | `Dictionary<string, int>` | アセットグループのソートウェイト（小さい値ほど優先、デフォルトは `99`）。 |
| `m_EditorInstallMode` | `EditorInstallMode` | エディタでのインストールモード (Default / UnZip)。 |

#### `Config.CreateModule()`

```csharp
public UCL_Module CreateModule(string iID, UCL_ModuleEditType iModuleEditType, UCL_Module.Config config);
```

- **目的**: 新しいモジュールを作成し、直ちに `UCL_Module.Save()` を呼び出して設定をディスクに書き込みます。
- **戻り値**: 初期化され保存された `UCL_Module` インスタンス。

---

### 5.2 `ModuleExportConfig`

`UCLI_IsEnable` インターフェースを実装し、単一のモジュールがエクスポート/インストールプロセスに参加するかどうかを制御します。

| フィールド | 型 | 説明 |
|---|---|---|
| `m_ExportModule` | `bool` | `true` は、このモジュールがエクスポートおよびインストールされるべきであることを示します。 |

---

### 5.3 `AssetConfig`

**アセット ID ごとに共有される** `AssetConfig` は、アセットの完全な保存パスと所属モジュールの解決を担当します。

| メンバ | 型 | 説明 |
|---|---|---|
| `p_Module` | `UCL_Module` | このアセットが属するモジュール。`null` はアセットが存在しないことを意味します。 |
| `ID` | `string` | アセットのユニークな識別子。 |
| `AssetType` | `Type` | アセットの C# 型。 |
| `AssetCache` | `object` | 外部システムがアタッチするための任意のキャッシュオブジェクト。 |
| `Exist` | `bool` | `p_Module != null` の場合は `true`。 |
| `ModuleEntry` | `UCL_ModulePath.PersistantPath.ModuleEntry` | パス解決機能。`p_Module == null` の場合は `LogError` を記録します。 |
| `AssetPath` | `string` | 完全な保存パス（`ModuleEntry.GetAssetPath` によって計算されます）。 |
| `AssetFolderPath` | `string` | アセットタイプのフォルダパス。 |
| `GroupID` | `string` | アセットのグループ ID。読み書き時に `.Group/{ID}` テキストファイルを介して永続化されます。 |
| `Inited` | `bool` | `Init()` が呼び出された後に `true` に設定されます。 |

#### 主要メソッド

```csharp
// 初期化：モジュール、アセットタイプ、および ID をバインドします。
public void Init(UCL_Module iModule, Type iAssetType, string iID);

// アセットの JSON データを読み込みます（ファイルが存在しない場合は Exception をスローします）。
public JsonData GetJsonData();

// JSON データをシリアライズし、AssetPath に書き込みます（自動的にフォルダを作成します）。
public void SaveAsset(JsonData iJson);

// AssetPath が指す物理ファイルを削除します。
public void DeleteAsset();
```

> [!WARNING]
> `GetJsonData()` は、ファイルが存在しない場合に `null` を返すのではなく、**`System.Exception` をスロー**します。呼び出し元は `try-catch` ブロックで囲む必要があります。

---

### 5.4 `AssetsCache`

**`UCL_Asset` タイプごとに共有される** `AssetsCache` は、そのタイプの下にあるすべてのアセットの `AssetConfig` 辞書と、グループ ID のリストを維持します。

| メンバ | 型 | 説明 |
|---|---|---|
| `m_AssetConfigDic` | `Dictionary<string, AssetConfig>` | アセット ID → AssetConfig のマッピング。 |
| `m_GroupIDs` | `List<string>` | ロードされたすべてのモジュールのメタデータから集計されたグループ ID のリスト。 |
| `m_AssetType` | `Type` | このキャッシュに対応するアセットタイプ。 |

```csharp
// 指定された ID の AssetConfig を取得（または遅延作成）します。
public AssetConfig GetAssetConfig(string iID);

// 指定された ID の AssetConfig キャッシュを削除します（パスの再解決を強制するために使用されます）。
public void ClearAssetsCache(string iID);
```

---

## 6. インスタンスメンバ (Instance Members)

### 6.1 ステータスフィールド (Status Fields)

| フィールド | 型 | 説明 |
|---|---|---|
| `m_PathConfig` | `UCL_ModulePathConfig` | グローバルなパス設定。`ModulesPath`、`RootPath` などを含みます。 |
| `m_Initialized` | `bool` | `InitAsync()` が完了した後に `true` に設定されます。 |
| `m_LoadingConfig` | `bool` | 設定ロードの再入防止フラグ。 |
| `m_LoadingPlaylist` | `bool` | プレイリストロードの再入防止フラグ。 |
| `m_Config` | `Config` | 現在デシリアライズされているモジュールサービスの設定。 |
| `m_CurEditModule` | `UCL_Module` | 現在編集中のモジュール（選択されていない場合は `null`）。 |
| `m_LoadedModules` | `List<UCL_Module>` | ロードされ有効になっているモジュールの順序付きリスト（後のものが同じ ID のアセットを上書きします）。 |
| `m_ModuleCache` | `Dictionary<string, UCL_Module>` | モジュール ID → UCL_Module のキャッシュ辞書。 |
| `m_IDsCache` | `Dictionary<string, (DateTime, List<string>)>` | アセットタイプ名 → (タイムスタンプ, ID リスト) の時限式キャッシュ。 |
| `m_AssetsCacheDic` | `Dictionary<string, AssetsCache>` | アセットタイプ名 → AssetsCache のキャッシュ辞書。 |

---

### 6.2 プロパティ (Properties)

```csharp
public Config ModuleConfig => m_Config;
public bool LoadingPlaylist => m_LoadingPlaylist;
public List<UCL_Module> LoadedModules => m_LoadedModules;

// m_CurEditModule を返します。null の場合は m_LoadedModules の最後のモジュールを返します。
public UCL_Module CurModule { get; }
```

---

## 7. 公開メソッド (Public Methods)

### 7.1 アセット ID のクエリ (Asset ID Query)

#### `GetAllEditableAssetsID(Type)`

```csharp
public IList<string> GetAllEditableAssetsID(Type iAssetType);
```

- **範囲**: `m_CurEditModule` 内のアセット**のみ**。
- **`m_CurEditModule == null` の場合**: `Array.Empty<string>()` を返します。
- **目的**: Inspector で「編集可能」なアセットをリストアップします（依存モジュールからの読み取り専用アセットを除外）。

#### `GetAllAssetIDs(Type, bool)`

```csharp
public List<string> GetAllAssetIDs(Type iAssetType, bool iUseCache = false);
```

- **範囲**: すべての `m_LoadedModules` にわたるアセット（集計され、重複排除されます）。
- **キャッシュ**: 結果はタイプ名ごとにキャッシュされ、有効期間は **0.3 秒** です。`iUseCache = true` は強制的にリフレッシュします。

> [!NOTE]
> `iUseCache` の意味は直感的ではありません：`false` (デフォルト) は「**時限式キャッシュを使用する**」ことを意味し、`true` は「**リフレッシュを強制する**」ことを意味します。

---

### 7.2 モジュールのクエリ (Module Query)

```csharp
// 指定された ID のモジュールを取得します（キャッシュあり）。
public UCL_Module GetModule(string iID, bool iUseCache = true);

// ロードされたリストから指定された ID のモジュールを検索します。見つからない場合は m_CurEditModule を返します。
public UCL_Module GetLoadedModule(string iID);

// すべてのモジュール ID を取得します（キャッシュは 0.5 秒間有効）。
public IList<string> GetAllModuleIDs(bool iUseCache = true);

// すべてのモジュールの表示名を取得します（形式: "Title(ID)" または "Title"）。
public IList<string> GetAllModuleNames();
```

---

### 7.3 アセット設定の管理 (Asset Config Management)

#### `CreateAssetConfig(Type, string)` ⭐ 保存用

```csharp
public AssetConfig CreateAssetConfig(Type iAssetType, string iID);
```

- **責任**: 保存パスが**現在の編集モジュール** (`m_CurEditModule`) を指すようにし、他のモジュールへの誤った保存を防止します。
- **実行手順**:
  1. `m_CurEditModule == null` の場合：`LogError` を記録し、`GetAssetConfig()` にフォールバックします。
  2. 古い `AssetConfig` キャッシュをクリアします (`ClearAssetsCache`)。
  3. `AssetConfig` を再初期化し、`m_CurEditModule` にバインドします。
- **呼び出しタイミング**: 読み込み前ではなく、**保存前**に呼び出してください。

> [!WARNING]
> `m_CurEditModule == null` のときにこのメソッドを呼び出すと、保存パスが誤ったモジュールを指す可能性があります。`EditModule()` を介して現在の編集モジュールが設定されていることを確認してください。

#### `GetAssetConfig(Type, string)` ⭐ 読み込み用

```csharp
public AssetConfig GetAssetConfig(Type iAssetType, string iID);
```

- **責任**: モジュールの上書きルールに従い、ロードされたモジュールを**後ろから前へ**検索して、指定されたアセットを含むモジュールを見つけます。
- **フォールバック**: どのアセットも含んでいない場合、`CurModule` で初期化します（アセットが新規であることを示します）。

#### `ContainsAsset(Type, string)`

```csharp
public bool ContainsAsset(Type iAssetType, string iID);
```

`GetAssetConfig` とそれに続く `AssetConfig.Exist` を介して、アセットが実際にディスク上に存在するかどうかをチェックします。

---

### 7.4 キャッシュ管理 (Cache Management)

```csharp
// 指定されたタイプのすべてのキャッシュをクリアします (AssetsCache + IDsCache)。
public void ClearAssetsCache(Type iAssetType);

// 指定されたタイプの下にある特定の ID の AssetConfig キャッシュのみをクリアします。
public void ClearAssetsCache(Type iAssetType, string iID);

// ロードされたすべてのモジュールの内部キャッシュをクリアします。
public void ClearCache();
```

---

### 7.5 アセットのグループ化 (Asset Grouping)

```csharp
// ソートされたアセットグループのリストを取得します (m_AssetGroupSortingOrder でソート)。
public List<string> GetAssetGroups();

// 現在のモジュールのアセットメタ（グループ化情報を含む）を取得します。
public UCL_AssetCommonMeta GetAssetMeta(string iTypeName);
```

---

### 7.6 設定へのアクセス (Config Access)

```csharp
// m_Config をシリアライズしてディスクに書き込みます。エディタでは m_BuiltinModules リストを同期します。
virtual public void SaveConfig();

// 非同期で設定 JSON を読み込み、m_Config にデシリアライズします（再入防止あり）。
virtual protected async UniTask LoadConfig();
```

---

### 7.7 モジュールのロードパイプライン (Module Loading Pipeline)

#### `LoadModulePlaylistAsync(UCL_ModulePlaylist, CancellationToken)`

```csharp
public async UniTask<Dictionary<string, UCL_Module>> LoadModulePlaylistAsync(
    UCL_ModulePlaylist modulePlayist, CancellationToken token);
```

- **完全な非同期フロー**:
  1. 再入防止（前のロードが終わるのを待ちます）。
  2. `WaitUntilInitialized` を待ちます。
  3. 同期的な `LoadModulePlaylist()` を呼び出します。
  4. `OnLoadedModuleAsync()` を実行します (`UCL_OnModuleLoadedAsset` + 登録された Func を含みます)。

#### `LoadModulePlaylist(UCL_ModulePlaylist, bool)`

```csharp
public Dictionary<string, UCL_Module> LoadModulePlaylist(
    UCL_ModulePlaylist modulePlayist, bool loadDependencies);
```

- **実行手順**:
  1. `OnLoadModule` イベントをトリガーします。
  2. `m_LoadedModules`、`m_ModuleCache`、`m_AssetsCacheDic`、`m_IDsCache` をクリアします。
  3. すべての AssetBundle をアンロードします。
  4. 未使用のリソースをアンロードします (`Resources.UnloadUnusedAssets`)。
  5. `EnablePlaylist` を走査し、モジュールとその依存関係を再帰的にロードします。
  6. `OnLoadedModule` イベントをトリガーします。

> [!IMPORTANT]
> このメソッドは、**ロードされたすべてのモジュールの状態をクリア**します。これ以降に `UCL_Module` や `AssetConfig` への参照を保持しているオブジェクトは、無効であると見なされるべきです。

#### `LoadModuleAndDependencies(string, Dictionary<string, UCL_Module>)`

```csharp
protected UCL_Module LoadModuleAndDependencies(
    string iModuleID, Dictionary<string, UCL_Module> iLoadedModules);
```

- **再帰ロジック**: 最初にすべての `m_DependenciesModules` をロードし、次に自身を `m_LoadedModules` に追加します。
- **結果**: 依存モジュールは前方に、メインモジュールは後方に配置されます（メインモジュールは同じ ID を持つ依存アセットを上書きします）。

---

### 7.8 エディタ操作 (Editor Operations)

```csharp
// 現在の編集モジュールを設定し、オプションで UCL_ModuleEditPage を開きます。
virtual public void EditModule(string iModuleID, bool iShowModuleEditPage = true);

// 新規モジュールを作成し、設定を保存し、CurrentEditModuleID を更新します。
virtual public UCL_Module CreateNewModule(string newModuleName, UCL_Module.Config config);

// m_CurEditModule をクリアします。
public void ClearCurrentEditModule();

// PlayerPrefs に従って前回の State を復元します（例：EditModule ページを自動的に再開）。
virtual public void ResumeState();

// GUI 状態を設定します。State.Main の場合は自動的に m_CurEditModule をクリアします。
virtual public void SetState(State iState);

// アセットが変更（保存/削除）されたことを現在のモジュールに通知します。
virtual public void OnModuleEdit();

// パスヘルパー：現在の編集モジュール下の指定された相対パスのフルパスを取得します。
virtual public string GetCurEditModuleFolder(string iRelativeFolderPath);

// パスヘルパー：指定されたモジュール ID 下の指定された相対パスのフルパスを取得します。
virtual public string GetFolderPath(string iModuleID, string iRelativeFolderPath);
```

---

### 7.9 GUI の描画 (GUI Rendering)

```csharp
virtual public void OnGUI(UCL_ObjectDictionary iDataDic);
```

以下の Inspector ブロックを描画します：
1. **EditType 切り替え** (エディタのみ): `ModuleEditType` を切り替えるための `Popup`。
2. **Save Config / Load Config ボタン**。
3. **モジュールフォルダを開くボタン** (Windows スタンドアロンのみ)。
4. **設定フィールドの表示** (Zip/UnZip ツールボタンを含む)。
5. **新規モジュール作成ボタン**。
6. **モジュール選択 Popup + 編集ボタン**。
7. **モジュールプレイリスト管理ページのエントリ**。
8. **ロードされたモジュールのリスト**。
9. **AssetsCache ステータス表示**。

---

## 8. モジュールの上書きルール (Module Override Rules)

> [!IMPORTANT]
> **モジュールの優先度**: `m_LoadedModules` 内では、**インデックスが大きいもの（後から追加されたもの）ほど優先度が高くなります**。
> 
> - `GetAssetConfig()` は後ろから前へ (`Count-1` → `0`) スキャンします。つまり、**最後にロードされたモジュールのアセットが、それ以前のモジュール内の同じ ID のアセットを上書きできます**。
> - `LoadModuleAndDependencies()` は、依存モジュールが最初に追加され、メインモジュールが最後に追加されるようにし、メインモジュールに依存関係よりも高い優先度を与えます。

---

## 9. 既知のリスクと注意事項 (Known Risks and Precautions)

| リスク | 説明 | 緩和策 |
|---|---|---|
| `m_CurEditModule == null` | 保存中に `EditModule` を呼び出さずに `CreateAssetConfig` が呼び出された。 | 保存を中止する前に `CurEditModuleID` を確認するか、エラーを表示する。 |
| `GetAllAssetIDs` の `iUseCache` が逆転 | `true` = リフレッシュ、`false` = キャッシュを使用。 | 使用前に本ドキュメントで意味を確認すること。 |
| `LoadModulePlaylist` がすべての参照をクリア | 呼び出し後、既存の `UCL_Module` / `AssetConfig` 参照は無効になる。 | `GetAssetConfig` を介して最新の参照を再取得する。 |
| `GetJsonData()` が Exception をスロー | ファイルがない場合に null を返さない。 | 呼び出し元は `try-catch` ブロックで囲む必要がある。 |
| `ModuleEditType` セッターの副作用 | これを設定すると `m_ModuleCache` がクリアされる。 | 切り替え後に `GetModule` を再呼び出しする。 |

---

## 10. 関連ファイルインデックス (Related File Index)

| ファイル | 説明 |
|---|---|
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModuleService.cs` | 本ドキュメントのソースコード。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_Module.cs` | 単一のモジュールデータと設定。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModulePath.cs` | パス計算ユーティリティ。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModulePathConfig.cs` | パス設定コンテナ。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModulePlaylist.cs` | プレイリストのデータ構造。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_OnModuleLoadedAsset.cs` | モジュールのロード後の非同期コールバックアセット。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModuleEntry.cs` | モジュール識別子とコア ID。 |
| `Assets/UCL/UCL_Core/Docs/UCL_ModuleService_API.md` | 本ドキュメント。 |
