---
title: UCL_GUILayout 全体概要
description: UCL_Core の IMGUI ツールセット（partial class UCL_GUILayout + 独立した UCL_GUILayoutPainter）— 公開 API クイックリファレンス、ファイル分担、慣用パターン、下流ページにとって最も価値のある 3 つの目立たない helper
source_files: |
  Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayout.cs
  Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayout.DrawList.cs
  Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayout.DrawDictionary.cs
  Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayout.DrawHashSet.cs
  Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayoutDrawObject.cs
  Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayoutPopup.cs
  Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayoutDrawableTexture.cs
  Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayoutPainter.cs
namespace: UCL.Core.UI
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [UCL_GUILayout, GUILayout 工具集, IMGUI helpers, DrawObject, Popup, DrawableTexture]
tags: [api, ui, imgui, editor]
---

# UCL_GUILayout 全体概要

`UCL_GUILayout` は UCL_Core が Unity IMGUI の上に重ねた **静的ツールセット**で、「自動フィールド編集 / リスト編集 / ポリモーフィックフィールド描画 / ドロップダウン / インタラクティブ描画」をすべて担います。

8 ファイルで構成される `partial class`（さらに独立した `UCL_GUILayoutPainter` を含む）で、すべて `namespace UCL.Core.UI` に属します。下流のすべての Editor ページ（`UCL_CommonEditorPage` ファミリー、`UCL_AgentCommandsPage`、`RCG_*EditorPage`、新しい `UCL_DocSearchPage` / `UCL_MarkdownViewerPage` など）は、フィールド描画にこれを使用します。

---

## 1. 設計レイヤー

```
                ┌──────────────────────────────┐
                │  使用側：UCL_*EditorPage /    │
                │          RCG_*EditorPage     │
                └──────────────┬───────────────┘
                               │ static API を呼び出す
                               ▼
   ┌─────────────────── partial class UCL_GUILayout ─────────────────┐
   │                                                                  │
   │  UCL_GUILayout.cs              基本フィールド：NumField/Slider/Toggle │
   │  UCL_GUILayout.DrawList.cs     IList 編集（ページ + ポリモーフィック Add）│
   │  UCL_GUILayout.DrawDictionary  IDictionary 編集                  │
   │  UCL_GUILayout.DrawHashSet     HashSet（リフレクション Add/Remove）│
   │  UCL_GUILayoutDrawObject.cs    任意オブジェクト再帰描画（中枢）   │
   │  UCL_GUILayoutPopup.cs         ドロップダウン / 列挙 / カラーピッカー │
   │  UCL_GUILayoutDrawableTexture  インタラクティブキャンバス（マウス描画）│
   │                                                                  │
   └──────────────────────────────┬───────────────────────────────────┘
                                  │ 依存
                                  ▼
       UCL_GUIStyle / UCL_ObjectDictionary / UCL_LocalizeManager /
       UCL_TypeReflectCache / UCL_PolymorphicHelper / Unity IMGUI

   独立クラス：
     UCL_GUILayoutPainter.cs       自己完結ペインター（DrawableTexture
                                   + SelectColor + Clear をラップ）
```

`DrawObjectData` こそ真の中枢です：オブジェクトの型を判別後、`DrawList` / `DrawDictionary` / `DrawHashSet` / `DrawField` にルーティングし、再び自身に再帰します。

---

## 2. ファイル責務クイックリファレンス

| ファイル | 責務 | 主な公開 API |
|---|---|---|
| `UCL_GUILayout.cs` | 基本フィールド、Sprite/Texture 描画、FolderExplorer | `NumField` / `IntField` / `FloatField` / `TextField` / `TextArea` / `Toggle` / `BoolField` / `CheckBox` / `Slider` / `Vector2/3Field` / `DrawSprite` / `DrawTexture` / `LabelAutoSize` / `ButtonAutoSize` / `FolderExplorer` |
| `UCL_GUILayout.DrawList.cs` | `IList`（1D/2D 配列含む）編集、ページング + ポリモーフィック Add | `DrawList(IList, ...)`（4 つのオーバーロード） |
| `UCL_GUILayout.DrawDictionary.cs` | `IDictionary` 編集 | `DrawDictionary(IDictionary, ...)`（3 つのオーバーロード） |
| `UCL_GUILayout.DrawHashSet.cs` | `HashSet` 編集（リフレクションで Add/Remove を呼ぶ。IEnumerable + Add/Remove を持つ任意の型に対応） | `DrawHashSet(object, DrawObjectParams)` |
| `UCL_GUILayoutDrawObject.cs` | 任意オブジェクトの再帰描画、フィールドリフレクション、`[SerializeReference]` ポリモーフィズム、`[Header]`、`DrawHelpButton` | `DrawObjectData` / `DrawField` / `DrawCopyPaste` / `DrawHelpButton` / `Preview.OnGUI` |
| `UCL_GUILayoutPopup.cs` | ドロップダウン（プレーン / 検索付 / キャッシュ版）、列挙版、カラーピッカー、ページ送り | `Popup` / `PopupAuto` / `PopupSearch` / `PopupSearchCache` / `Popup<T>(enum)` / `DrawSelectPage` / `SelectColor` / `ValueDropdown` |
| `UCL_GUILayoutDrawableTexture.cs` | マウス描画インタラクティブテクスチャ + `GL_DrawLine` などの線分描画 | `DrawableTexture` / `GetMousePosInGrid` / `DrawPolyLine` / `GL_DrawPolyLine` / `GL_DrawLine` / `DrawLine` |
| `UCL_GUILayoutPainter.cs`（独立クラス） | 完全なペインター UI コンテナ（テクスチャ + 色 + Clear） | `Init` / `SetTexture` / `Clear` / `OnTextureUpdate` / `OnGUI` |

---

## 3. 公開 API クイックリファレンス（用途別）

### 3.1 基本フィールド（`UCL_GUILayout.cs`）

| API | 用途 |
|---|---|
| `NumField<T>(label, value, minWidth)` | ジェネリック数値フィールド。int/float/double 対応、非数値キーをフィルタ |
| `IntField(label, value, ...)` / `FloatField(label, value, minWidth)` | 型専用版 |
| `IntFieldAuto(value, dic, ...)` ⭐ | 整数フィールド。**外部値が変わると自動でキャッシュをクリア**（古い値の表示を回避） |
| `TextField(label, value, ...)` / `TextArea(label, value)` | 単行 / 複数行テキスト |
| `Toggle(value, size)` | `▼` / `►` を表示（折りたたみアイコンのセマンティクス） |
| `Toggle(dic, key, ...)` | 上に同じ。状態を `UCL_ObjectDictionary` に保持 |
| `BoolField(value, size)` / `BoolField(dic, key, size, default)` | `✔` / 空 を表示 |
| `CheckBox(value, size)` | 標準チェックボックス（label なし） |
| `CheckBox(value, label, size, labelSize)` | チェックボックス + 右側 label。ボックスと文字の両方が DPI `Scale` に追従（hi-DPI で小さすぎて読めないネイティブ `GUILayout.Toggle` の代替） |
| `Slider(label, value, min, max, dic)` | スライダー + 数値入力 + 同期 |
| `Vector2Field` / `Vector3Field` / `VectorField` | ベクトル成分編集（IntVec バリアント含む） |
| `DrawSprite(sprite, ...)` / `DrawTexture(tex, ...)` / `GraphicsDrawTexture(...)` | 描画（通常版 / Graphics.DrawTexture でカスタム Material 対応） |
| `LabelAutoSize(name, fontSize, color)` / `ButtonAutoSize(name, fontSize, ...)` | 幅自動調整 |
| ~~`Label(name, Color color)`~~ | **非推奨** — 代わりに `GUILayout.Label(text, UCL_GUIStyle.GetLabelStyle(color))` を使用 |
| `FolderExplorer(dic, path, ...)` | パスナビゲーション + ファイルフィルタ UI |

### 3.2 コレクション編集（`DrawList` / `DrawDictionary` / `DrawHashSet`）

| API | 用途 |
|---|---|
| `DrawList(IList, dic, name, alwaysShowDetail)` | リスト編集：折りたたみヘッダ + 自動ページング（10 件 / ページ）+ Copy/Paste + ポリモーフィック Add（element type が `UCLI_TypeListable` を実装している場合） |
| `DrawList(IList, DrawObjectParams)` | パラメータ化版（fieldNameFunc / overrideDrawElement を渡せる） |
| `DrawDictionary(IDictionary, dataDic, name, alwaysShowDetail, fieldNameFunc)` | 辞書編集。キーと値はそれぞれ再帰描画 |
| `DrawHashSet(object, DrawObjectParams)` | リフレクション版コレクション編集（HashSet に限らず、Add/Remove メソッドがあれば可） |

> **共通動作**：ページング上限 = `MaxItemsPerPage = 10`；移動／削除モードは選択可能；タイトル行に Copy/Paste；rank 1/2 配列にも対応。

### 3.3 オブジェクト再帰描画（`UCL_GUILayoutDrawObject.cs`）

| API | 用途 |
|---|---|
| `DrawObjectData(obj, dic, displayName, alwaysShowDetail, fieldNameFunc, fieldType, exSetting)` | **中枢**：`EObjectType`（String / Bool / Enum / Number / IList / IDictionary / Color / Vector / Component / Struct…）を自動判別してルーティング |
| `DrawObjectData(target, DrawObjectParams)` | パラメータ化版 |
| `DrawField(obj, dic, displayName, ...)` | リフレクションで全フィールドを展開（自身を再帰呼び出し） |
| `DrawCopyPaste(ref obj, dic, fieldType)` | Copy/Paste ボタン群（JSON シリアライズ）。貼り付け成功時 `true` を返す |
| `DrawHelpButton(url)` | `[HelpURLAttribute]` に対応する「?」ボタン。クリックで URL を開く（`UCL_EditorPage.TopBar` で既に使用） |
| `Preview.OnGUI(name, target, dic, space)` | 読み取り専用プレビュー（再帰表示するが編集不可） |

サポートされる属性描画拡張：`[Header]`（自動ローカライズ）、`[SerializeReference]` ポリモーフィズム、`IShowInCondition`（条件表示）、`IStrList`（文字列ドロップダウン）、`IValueDropdown`、`ITexture2D`、`UCL_FolderExplorerAttribute`、`UCL_IntSliderAttribute`、`UCL_SliderAttribute`。

### 3.4 ドロップダウンとページング（`UCL_GUILayoutPopup.cs`）

| API | 用途 |
|---|---|
| `Popup(selectedIndex, options, dic, key, ...)` | 基本ドロップダウン（開閉状態は `dic[key]` に保持） |
| `Popup(selectedIndex, options, ref bool opened, ...)` | 手動 ref bool 版 |
| `PopupAuto(selectedIndex, options, dic, key, searchThreshold, ...)` ⭐ | 件数 ≥ `searchThreshold` で自動的に検索欄を追加；最も使われる簡易エントリ |
| `PopupSearch(selectedIndex, options, dic, key, ...)` | 常に検索欄付き（Regex、ヒット文字を赤で強調） |
| `PopupSearchCache(index, displayOptions, dic, key, ...)` ⭐ | Regex とフィルタ結果も追加でキャッシュ。**100 件以上で繰り返し操作する場面のパフォーマンス版** |
| `Popup<T>(enumValue, dic, getNameFunc, ...)` | 列挙専用。`UCL_LocalizeLib.GetEnumLocalize(...)` で内蔵ローカライズ |
| `PopupAuto<T>(enumValue, dic, [key], searchThreshold, ...)` | 列挙 + 自動検索トリガー |
| `DrawSelectPage(dic, itemsCount, maxItemsPerPage)` | ページ送り行（`|<` `<` `>` `>|` + ページ番号直接入力）。`(pageIndex, startIndex)` を返す |
| `SelectColor(initialColor)` | カラーピッカー（プリセットパレット + RGBA スライダー） |
| `ValueDropdown(selectedIndex, options, dic, key, ...)` | PopupSearch 類似 + オプションのハッシュも追加キャッシュ |

### 3.5 インタラクティブ描画

| API | 用途 |
|---|---|
| `DrawableTexture(texture2D, dic, w, h, drawColor)` | マウス描画テクスチャ（Drag 境界跨ぎ・補間ギャップを自動処理） |
| `GetMousePosInGrid(rect, w, h)` | グリッド内のマウスのセル座標を取得（範囲外は `Vector2Int.left`） |
| `DrawPolyLine` / `GL_DrawPolyLine` / `GL_DrawLine` / `DrawLine` | 折れ線 / 回転線分 |
| `UCL_GUILayoutPainter.OnGUI()` | 完全なペインター（キャンバス + カラー選択 + Clear） |

---

## 4. ファイル横断の共通パターン

| パターン | 説明 |
|---|---|
| **状態管理** | すべて `UCL_ObjectDictionary`（キー値マップ）経由。毎フレームの再初期化を回避（例：Regex / TypeList / 折りたたみ状態のキャッシュ） |
| **三段オーバーロード** | 同名 API は通常 (1) **状態なし**（値を渡す）、(2) **状態あり**（`dic + key`）、(3) **パラメータ化**（`DrawObjectParams`）— 軽い→重いの順に必要に応じて選ぶ |
| **統一スタイル** | 必ず `UCL_GUIStyle` を経由。DPI スケーリング自動対応（`GetScaledSize()`）。**`UCL_GUIStyle.LabelStyle` を Toggle/Button/TextField の第三引数に渡してはいけない**（インタラクティブ表示が壊れる。`UCL_GUIStyle.LabelStyle` の XML コメント参照） |
| **ポリモーフィズム自動検出** | `[SerializeReference]` フィールドは `UCL_PolymorphicHelper.GetConcreteSubtypes()` で具象サブタイプを列挙し、ドロップダウンでインスタンスを切り替え。data class に `UCLI_TypeListable` + `[SerializeReference]` を付ければ手書き UI 不要 |
| **リフレクションキャッシュ** | `TypeFieldInfoCache` は `UCL_TypeReflectCache` で共有。一度の解析でページ全体に再利用。コンストラクタで service に触れて**はいけない**（`Polymorphism_In_UCL.md` 参照） |
| **ページング上限** | 大規模コレクションは自動的に `MaxItemsPerPage = 10` で分割。検索ドロップダウンは 20；ラップアラウンド送り |
| **Copy/Paste 内蔵** | List / Dict / HashSet のタイトル行に内蔵済み。任意オブジェクトは `DrawCopyPaste(ref obj, ...)` を呼べる |
| **戻り値セマンティクス** | struct は値で返す。class は変更後も同じ参照を返す。`IList` / `IDictionary` は in-place 変更で戻り値なし |

---

## 5. 覚えておく価値のある 3 つの目立たない helper

下流ページが普段使うのは `DrawObjectData` / `DrawList` / 基本フィールドだけですが、以下の 3 つは**本当に手間を省ける**のに見落とされがちです：

### 5.1 `IntFieldAuto(value, dic, ...)`
**使いどころ**：表示する値が外部データ由来（他所から書き換えられる可能性あり）で、旧値を追跡し差分発生時に内部編集キャッシュを自動クリアしたい場合。
```csharp
int count = UCL_GUILayout.IntFieldAuto(list.Count, m_DataDic);
// 外部の list.Count が書き換わると、次の OnGUI で編集キャッシュが自動クリアされる
```
**比較**：`IntField` は外部値の変動を検出しないため、編集中に新しいデータを上書きしてしまうことがあります。

### 5.2 `PopupSearchCache(index, options, dic, key, ...)`
**使いどころ**：選択肢が 100 件以上、かつユーザーが繰り返しフィルタする場面。通常の `PopupSearch` は毎フレーム Regex を再コンパイルし LINQ Where を再実行しますが、`Cache` 版は Regex とヒットインデックス集合を `dic` 内にキャッシュし、query が変わったときだけ再計算します。
```csharp
int sel = UCL_GUILayout.PopupSearchCache(curIdx, allCardIds, m_DataDic, "CardPicker");
```
**目安**：~500 件で GUI レスポンスの差は明確に体感できます。

### 5.3 `DrawCopyPaste(ref obj, dic, fieldType)`
**使いどころ**：複雑な nested struct をフィールド／ページをまたいでコピー＆ペーストしたいが、JSON serialize を手書きしたくない場合。
```csharp
object o = config;
if (UCL_GUILayout.DrawCopyPaste(ref o, m_DataDic, typeof(GameConfig)))
{
    config = (GameConfig)o; // 貼り付け成功。o は置き換え済み
}
```
**仕組み**：内部で `UCL.Core.CopyPaste` + JSON を使用。型不一致はブロックされます。

---

## 6. 関連ドキュメント

- **ポリモーフィックフィールドの仕組み**：[Architecture/Polymorphism_In_UCL.md](../../Architecture/Polymorphism_In_UCL.md)（GUI とシリアライズにおける `[SerializeReference]` × `UCLI_TypeListable` × `UCL_PolymorphicHelper` × `UCL_TypeReflectCache` の役割を解説）
- **HelpURL の仕組み**：[Workflows/HelpURL_Workflow.md](../../Workflows/HelpURL_Workflow.md)（`DrawHelpButton` が解析する `ucl_core:` / `eov_docs:` プレフィックス）
- **スタイル中央**：`UCL_GUIStyle.cs` の XML コメント（特に `LabelStyle` の Toggle/Button への警告）

---

## 7. UCL_GUILayout を**使わない**ほうがよい場面

- 純粋なテキスト Label / Button / 単純な Layout — Unity `GUILayout` を直接使えば十分（色付け / 自動フォントサイズが不要なら UCL を経由する必要なし）。
- Runtime UGUI / UI Toolkit — このツールセットは IMGUI（エディタ時 + 一部 runtime debug overlay）専用で、UGUI には対応しません。
- 毎フレーム数百万フィールドを再描画するような高頻度ケース — IMGUI 自体が耐えきれず、このレイヤーで無理をすべきではありません。UI Toolkit + VisualElement に切り替えてください。
