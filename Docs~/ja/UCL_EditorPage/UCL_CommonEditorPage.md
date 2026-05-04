---
title: UCL_CommonEditorPage
description: UCL エディタページの標準基底クラス。TypeName ラベルと Copy ボタンを提供。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_CommonEditorPage.cs
namespace: UCL.Core.EditorLib.Page
---

# UCL_CommonEditorPage

## 1. 概要

`UCL_CommonEditorPage` は UCL フレームワーク内のすべてのカスタムエディタページの**標準基底クラス**です。[`UCL_EditorPage`](./UCL_EditorPage.md) を薄く拡張したもので、汎用的に有用な 2 つの機能を最初から提供します：

1. **TypeName ラベル** — ページのクラス名を自動的にトップバーに表示
2. **Copy ボタン** — ワンクリックでクラス名をシステムクリップボードにコピー（開発中のサブクラスページ間移動に便利）

非自明なエディタページを構築する際は、**`UCL_EditorPage` を直接継承するのではなく `UCL_CommonEditorPage` を継承してください** — これによりトップバーレイアウトの慣例を無料で得られ、ページが UCL エディタエコシステム（[`UCL_ModuleEditPage`](./UCL_ModuleEditPage.md)、[`UCL_ModuleServiceEditPage`](./UCL_ModuleServiceEditPage.md)、[`UCL_ModulePlayListPage`](./UCL_ModulePlayListPage.md)、[`UCL_SelectAssetPage`](./UCL_SelectAssetPage.md) ⋯）と視覚的に統一されます。

## 2. 提供されるもの

### 2.1 クラス定義（抜粋）

```csharp
public class UCL_CommonEditorPage : UCL_EditorPage
{
    protected string m_TypeName;

    public override void Init(UCL_GUIPageController iGUIPageController)
    {
        base.Init(iGUIPageController);
        m_TypeName = this.GetType().Name;
    }

    protected override void TopBarButtons()
    {
        base.TopBarButtons();
        GUILayout.Label(m_TypeName, UCL_GUIStyle.LabelStyle);
        if (GUILayout.Button(UCL_LocalizeManager.Get("Copy"),
                             UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
        {
            GUIUtility.systemCopyBuffer = m_TypeName;
        }
    }
}
```

### 2.2 デフォルトトップバー

```
┌──────────────────────────────────────────────────────┐
│ [Back] [Close] │ <あなたのページクラス名> │ [Copy] │ ...│
└──────────────────────────────────────────────────────┘
   ↑ UCL_EditorPage より      ↑ UCL_CommonEditorPage より
```

サブクラスは `TopBarButtons()` をオーバーライドし、**最初に `base.TopBarButtons()` を呼び出して**右側を拡張します。

## 3. 使用すべき場面

| シナリオ | 推奨基底クラス |
|---|---|
| トップバー操作不要のシンプルなページ | `UCL_EditorPage` |
| **大半のカスタムエディタページ** | **`UCL_CommonEditorPage`** ⭐ |
| 単一 Module インスタンスを編集するページ | `UCL_ModuleEditPage`（既にサブクラス）|
| リストから資産を選ぶページ | `UCL_SelectAssetPage`（既にサブクラス）|

判断基準：TypeName ラベルを抑制する強い理由がない限り、**デフォルトで `UCL_CommonEditorPage` を選ぶ**。

## 4. 拡張方法 — 標準パターン

### 4.1 最小サブクラス

```csharp
namespace YourGame.Page
{
    public class YourEditorPage : UCL_CommonEditorPage
    {
        public override string WindowName => "あなたのページタイトル";

        public static YourEditorPage Create()
        {
            // ★ 静的ファクトリを使用。手動で new + Push しない。
            return UCL_EditorPage.Create<YourEditorPage>();
        }

        protected override void ContentOnGUI()
        {
            // あなたの IMGUI
            GUILayout.Label("Hello", UCL_GUIStyle.LabelStyle);
        }
    }
}
```

### 4.2 トップバーボタンの追加

`TypeName + Copy` ブロックが最左に保たれるよう、**必ず `base.TopBarButtons()` を最初に呼び出す**：

```csharp
protected override void TopBarButtons()
{
    base.TopBarButtons();   // ★ UCL_CommonEditorPage の TypeName + Copy

    if (GUILayout.Button("Refresh",
            UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
    {
        Reload();
    }
    if (GUILayout.Button("Run",
            UCL_GUIStyle.GetButtonStyle(Color.green), GUILayout.ExpandWidth(false)))
    {
        Execute();
    }
}
```

> [!IMPORTANT]
> トップバーの全ボタンに `GUILayout.ExpandWidth(false)` を付けてください。これがないとボタンが横方向を貪欲に占有し、ウィンドウ幅が広いとレイアウトが崩れます。

### 4.3 Init オーバーライド

ページがコンストラクタ的な初期化を要する場合、`Init()` をオーバーライドし、`m_TypeName` が設定されるよう先に `base.Init()` を呼びます：

```csharp
public override void Init(UCL_GUIPageController iGUIPageController)
{
    base.Init(iGUIPageController);   // ★ 必ず先に呼ぶ
    LoadInitialData();
}
```

### 4.4 OnClose オーバーライド

ユーザーがページを離れる際に状態をクリーンアップする必要がある場合：

```csharp
public override void OnClose()
{
    SaveDirtyChanges();
    base.OnClose();   // ★ base は最後に呼ぶ
}
```

## 5. 参考サブクラス

以下のクラスは `UCL_CommonEditorPage` を継承し、慣例的な拡張パターンを示しています：

| サブクラス | 用途 |
|---|---|
| [`UCL_ModuleEditPage`](./UCL_ModuleEditPage.md) | 単一 Module の設定とコンテンツを編集 |
| [`UCL_ModuleServiceEditPage`](./UCL_ModuleServiceEditPage.md) | インストール済み Module 全管理 |
| [`UCL_ModulePlayListPage`](./UCL_ModulePlayListPage.md) | Module ロード順 playlist 管理 |
| [`UCL_SelectAssetPage`](./UCL_SelectAssetPage.md) | 型付きリストから資産を 1 つ選ぶ |

新しいエディタページを設計する際は、**まず `UCL_ModuleEditPage` を読んでください** — オーバーライドパターン（Init / TopBarButtons / ContentOnGUI / OnClose 全部）の標準例です。

## 6. 共通パターン

### 6.1 `UCL_ObjectDictionary` でサブ状態をキャッシュ

多くのページで foldout / toggle ごとの UI 状態をフレーム間で保持する必要があります。慣例：

```csharp
private UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();

protected override void ContentOnGUI()
{
    UCL_GUILayout.DrawObjectData(myObject, m_DataDic.GetSubDic("MyObject"), "MyObject");
}
```

### 6.2 ボタンから非同期処理を発火

`UniTask` と `.Forget()` を使い IMGUI スレッドをブロックしないようにします：

```csharp
if (GUILayout.Button("Run Async", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
{
    DoWorkAsync().Forget();
}

private async UniTask DoWorkAsync()
{
    await SomeService.InitAsync(default);
    // …
}
```

### 6.3 常時再描画（外部状態が変動する場合）

ページが GUI 入力なしで変化する外部状態を反映する場合（タイマー、進捗など）：

```csharp
[UCL.Core.ATTR.RequiresConstantRepaint]
public class YourEditorPage : UCL_CommonEditorPage { … }
```

## 7. 注意点

> [!CAUTION]
> **`base.TopBarButtons()` を省略しないでください**。省略すると TypeName ラベルと Copy ボタンが消え、見た目で UCL エディタファミリーから外れた印象になり、デバッグ時にクラス名をコピーする利便も失われます。

> [!CAUTION]
> **標準のトップバーを使いたい場合、`UCL_EditorPage` を直接継承しないでください**。TypeName + Copy ブロックを手動で再実装するとコードが重複し、フレームワーク更新時にずれるリスクがあります。

> [!IMPORTANT]
> 外部からページインスタンスを生成する際は、**必ず静的ファクトリ `UCL_EditorPage.Create<T>()`** を使ってください。`new T(); UCL_GUIPageController.CurrentRenderIns.Push(p);` ではなく。ファクトリは重複ページ検出と適切な初期化を処理します。

## 8. 関連

- [`UCL_EditorPage`](./UCL_EditorPage.md) — 直接の基底クラス
- [`UCL_ModuleEditPage`](./UCL_ModuleEditPage.md) — 標準オーバーライド例
- [`UCL_GUIPage`](./UCL_GUIPage.md) — ルートページ抽象
