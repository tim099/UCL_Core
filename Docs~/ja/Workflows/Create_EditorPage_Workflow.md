---
title: 新しい UCL_CommonEditorPage サブクラスを作成するワークフロー
description: ステップ化された SOP — GUIPageController で push できる Editor ページをゼロから 1 枚生み出す手順。継承関係、必須／任意 override、TopBar カスタマイズ、エントリポイント接続、スタイル選定の指針（UCL_GUILayout / UCL_GUIStyle ドキュメントへリンク）、よくある落とし穴を網羅。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [Create EditorPage, UCL_CommonEditorPage workflow, 寫新 editor 頁]
tags: [workflow, editor, ui, imgui]
---

# 🛠️ 新しい UCL_CommonEditorPage サブクラスを作成するワークフロー

> [!IMPORTANT]
> このワークフローは「**`UCL_CommonEditorPage` を継承する Editor ページを 1 枚書く**」ことのみを扱います。UI コンポーネント（フィールド / リスト / ドロップダウンなど）の実装は [UCL_GUILayout 全体概要](../API/UCL_GUILayout/UCL_GUILayout_Overview.md) を、スタイル取得は [UCL_GUIStyle 概要](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md) を参照してください。
>
> 設計思想：**継承 + override hook**。基底クラスが TopBar / Back / Close / ScrollView / HelpURL 解析をすべて処理済み。サブクラスは `WindowName` と `ContentOnGUI()` を埋めるだけで済み、カスタマイズしたいときだけ hook を override します。

---

## 0. TL;DR — 3 分で 1 ページ追加

```
[1] このページの責務をはっきりさせる（単一フォーカス、1 ページ 1 用途）
       ▼
[2] UCL_<Name>Page.cs : UCL_CommonEditorPage を作成
       ▼
[3] WindowName と ContentOnGUI() を override
       ▼
[4] [HelpURL] にこれから書く予定のドキュメントパスを指定（ファイルが未作成でも可）
       ▼
[5] static Create() を提供（GUIPageController に Push）
       ▼
[6] 親ページ / メニュー / WelcomePage にボタンを追加 → Create()
```

---

## 1. 継承関係

```
UCL_GUIPage (UICore)
  └── UCL_EditorPage (EditorMenuPages)        ← TopBar / Back / Close / HelpURL 解析を提供
        └── UCL_CommonEditorPage              ← TopBar に ClassName + Copy ボタンを追加表示
              └── UCL_<Name>Page              ← 書きたいページ
```

| クラス | 責務 |
|---|---|
| `UCL_GUIPage` | `WindowName` / `IsWindow` / `OnGUI()` の最外層フロー |
| `UCL_EditorPage` | TopBar（Back / Close / Help）、ContentOnGUI ScrollView ラップ、HelpURL リフレクションキャッシュ、`Create<T>()` ファクトリ |
| `UCL_CommonEditorPage` | TopBar に「TypeName + Copy」を表示してデバッグとサポートドキュメントの照合を助ける |

---

## 2. 必ず override するメンバー

| メンバー | 型 | 必要性 | 説明 |
|---|---|---|---|
| `WindowName` | `string` | **必須** | ウィンドウタイトル；複数ウィンドウ切替時 `UCL_GUIPageController.WindowName` がこれを読む |
| `ContentOnGUI()` | `void` | **必須** | 主要コンテンツの描画（ScrollView は base がラップ済み、ここでは描画に専念） |

### 2.1 最小スケルトン

```csharp
#if UNITY_EDITOR
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_<Name>Page.md")]
    public class UCL_<Name>Page : UCL_CommonEditorPage
    {
        public override string WindowName => "UCL_<Name>";

        public static UCL_<Name>Page Create()
        {
            return UCL_EditorPage.Create<UCL_<Name>Page>();
        }

        protected override void ContentOnGUI()
        {
            // ここから描画開始；ScrollView は base.OnGUI でラップ済み
            GUILayout.Label("Hello", UCL_GUIStyle.LabelStyle);
        }
    }
}
#endif
```

> [!CAUTION]
> `Create<T>()` は **public static** ですが、`UCL_EditorPage.Create<T>()` 経由で呼び出してください — この呼び出しはページを `UCL_GUIPageController.CurrentRenderIns` に push するため、**呼び出し前に controller が走っている必要があります**（通常は親ページの OnGUI または EditorWindow が既に作成済みの状態）。

---

## 3. 任意で override できる hook

| メンバー | 型 | 既定動作 | override タイミング |
|---|---|---|---|
| `TopBarButtons()` | `void` | ClassName + Copy ボタンを表示（CommonEditorPage が提供） | 「再読み込み / 言語切替 / サイドバー開閉」などの上部ツールボタンを追加したいとき |
| `ShowCloseButton` | `bool` | `true` | ユーザがワンクリックで全ページを閉じられないようにしたいとき `false` |
| `ShowBackButton` | `bool` | `!ShowCloseButton || pages.Count > 1` | カスタムナビゲーションフロー時 |
| `BackButtonClicked()` | `void` | `p_Controller.Pop()` | 戻る前に保存・確認ダイアログを出したいとき |
| `CloseButtonClicked()` | `void` | `p_Controller.PopAll()` | 上に同じ |
| `Init(controller)` | `void` | base 呼び出し + `m_TypeName` 記録 | 一回限りの初期化、イベント購読をしたいとき |

### 3.1 TopBarButtons の例

```csharp
protected override void TopBarButtons()
{
    base.TopBarButtons();   // ClassName + Copy を残す
    if (GUILayout.Button(UCL_CodeLocalize.Get("DocSearch.Reveal"),
        UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
    {
        EditorUtility.RevealInFinder(m_AbsolutePath);
    }
}
```

実例は `UCL_MarkdownViewerPage.TopBarButtons()` を参照：標準 2 つに加え Reveal / OS Open / Copy raw の 3 つを追加。

---

## 4. エントリポイント接続（このページはどう開くのか？）

よくあるエントリポイントは 4 種：

| エントリ | 例 | 適用ケース |
|---|---|---|
| **親ページのボタン** | `UCL_DocSearchPage.DrawResultRow` の `📄` ボタン → `UCL_MarkdownViewerPage.Create(...)` | 既存ページと明確な文脈関係がある |
| **WelcomePage カード** | `UCL_WelcomePage` の「🔍 ドキュメント検索」ボタン → `UCL_DocSearchPage.Create()` | グローバル機能 / 目立つ場所が必要 |
| **`UCL → ...` メニュー** | `[MenuItem("UCL/<Name>")]` で EditorWindow を開き、`OnGUI` 内でページを push | 他ページに依存しない独立ツール |
| **HelpURL ディープリンク** | `ucl_core:Docs~/...` でドキュメントボタンからページへ戻る | 通常は逆方向（ページ → ドキュメント） |

> [!TIP]
> 新ページのエントリ追加は「**最小カップリング**」を原則に：親ページに置けるならメニューを開かない、WelcomePage カードに集約できるなら複数メニューに散らさない。

---

## 5. UI 描画は何を選ぶか？

`ContentOnGUI()` 内で UI を描画するときは、複雑度が低い順に下から探す：

| やりたいこと | ツール | 出典 |
|---|---|---|
| Label / Button / TextField / Toggle などの基本 | `GUILayout.*` + `UCL_GUIStyle.*Style` | [UCL_GUIStyle 概要](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md) |
| 数値フィールド / スライダー / ベクトル / 折りたたみ ▼/► | `UCL_GUILayout.IntField` / `Slider` / `Vector3Field` / `Toggle(bool, size)` | [UCL_GUILayout 概要 §3.1](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#31-基礎欄位ucl_guilayoutcs) |
| リスト / 辞書 / HashSet 編集（ページング、ポリモーフィック Add 含む） | `UCL_GUILayout.DrawList` / `DrawDictionary` / `DrawHashSet` | [UCL_GUILayout 概要 §3.2](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#32-集合編輯drawlist--drawdictionary--drawhashset) |
| 任意オブジェクトの再帰描画（リフレクションフィールド、`[SerializeReference]` ポリモーフィズム） | `UCL_GUILayout.DrawObjectData` | [UCL_GUILayout 概要 §3.3](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#33-物件遞迴繪製ucl_guilayoutdrawobjectcs) |
| ドロップダウン（検索 / 列挙含む） | `UCL_GUILayout.PopupAuto` / `Popup<T>(enum)` | [UCL_GUILayout 概要 §3.4](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#34-下拉選單與分頁ucl_guilayoutpopupcs) |
| インタラクティブペインター | `UCL_GUILayout.DrawableTexture` / `UCL_GUILayoutPainter` | [UCL_GUILayout 概要 §3.5](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#35-互動繪圖) |
| 複雑構造の Copy/Paste | `UCL_GUILayout.DrawCopyPaste(ref obj, ...)` | [UCL_GUILayout 概要 §5.3](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#53-drawcopypasteref-obj-dic-fieldtype) |

### 5.1 自前 GUIStyle を作るタイミング

組み込みスタイルでは届かないもの（例：「16pt 太字 + wordWrap + richText の Heading スタイル」）が必要なら、page 内で既存スタイルから**派生**して lazy に作成：

```csharp
GUIStyle m_HeadingStyle;
GUIStyle HeadingStyle => m_HeadingStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
{
    fontSize = 18,
    fontStyle = FontStyle.Bold,
    richText = true,
    wordWrap = true,
};
```

> [!CAUTION]
> この `m_HeadingStyle` は表示専用スタイル（LabelStyle 派生）で、同じく `Toggle` / `Button` の第三 GUIStyle 引数として**使えません**。詳細は [UCL_GUIStyle 概要 §3](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md#3-反指守則從-labelstyle-重複出現的禁忌)。

---

## 6. HelpURL と多言語ドキュメント

### 6.1 attribute の書き方

```csharp
[HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_<Name>Page.md")]
public class UCL_<Name>Page : UCL_CommonEditorPage { ... }
```

`{lang}` は `UCL_GUILayout.DrawHelpButton` の解析時に自動で現在の `UCL_LocalizeManager.s_LangName`（zh-Hant / en / ja / zh-Hans）に置換され、「？」ボタンがユーザの言語に応じて正しいファイルへジャンプします。詳細は [HelpURL_Workflow](HelpURL_Workflow.md)。

### 6.2 ドキュメントが未作成でも先に attribute を付ける

- `[HelpURL]` がまだ存在しない .md を指していても crash しません — クリックして開けないだけです
- attribute を先に付けておけば、将来の検索（[Cmd_SearchDocs](../API/UCL_AgentCommand/Cmd_SearchDocs.md) / `UCL_DocSearchPage`）が「あるべきだがまだ書かれていない」ドキュメント位置をインデックスできます
- ドキュメントは page が安定してから補えばよい（`UCL_DocSearchPage` 自体もこのリズム）

---

## 7. よくある落とし穴

| # | 落とし穴 | 症状 | 解決策 |
|---|---|---|---|
| 1 | `UCL_GUIStyle.LabelStyle` を `GUILayout.Toggle` の第三引数に渡す | チェックボックスアイコンが消え、押しても反応しない | 純粋なチェックボックスは第三引数を省略；button-like は `ButtonStyle` を使う。詳細は [UCL_GUIStyle 概要 §3](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md#3-反指守則從-labelstyle-重複出現的禁忌) |
| 2 | `ContentOnGUI()` 内でさらに ScrollView をラップ | スクロールバーが二重、マウスホイール挙動が変 | ラップ不要。base が既にラップ済み。二次スクロールが必要な場合は別名の変数で別途用意 |
| 3 | TextField が Enter を呑み込み search が発火しない | Enter を押しても無反応 | TextField の前に `Event.current` をスナップショット；`UCL_DocSearchPage.DrawSearchInput` のブロックコメントを参照 |
| 4 | controller がない状況で `Create<T>()` を呼ぶ | NullRef | 親ページ／EditorWindow が controller を構築済みであることを確認するか、自前で controller を保持して `Create<T>(controller)` オーバーロードに渡す |
| 5 | スタイルの lazy 構築でキャッシュしない | 毎フレーム new GUIStyle、性能が崩壊 | field + property で lazy 化（§5.1 参照）、または `EnsureStyles()` で一括構築 |
| 6 | rich-text label に `<...>` が混入（C# ジェネリック `List<T>` の表示など） | テキストが tag として解析され一部が消える | そのスタイルで `richText` を切る、またはユーザコンテンツの `<` を `&lt;` にエスケープ |
| 7 | `[HelpURL]` に言語をハードコード（`{lang}` プレースホルダなし） | 言語切替後 Help ボタンが間違ったファイルへ | 必ず `ucl_core:Docs~/{lang}/...` を使う |
| 8 | EditorWindow.OnGUI で `IsInEditorWindow` を設定し忘れ | スタイルキャッシュが runtime 側を見て DPI が異常 | `IsInEditorWindowScope`（using で自動復元）を使う |

---

## 8. 検収チェックリスト

書き終わったら 1 項目ずつ確認：

- [ ] `UCL_CommonEditorPage` を継承、ファイル名とクラス名が完全一致
- [ ] `WindowName` を override（空文字列でない）
- [ ] `ContentOnGUI()` を override し、内容で `UCL_GUIStyle.*` / `UCL_GUILayout.*` からスタイルとコンポーネントを取得
- [ ] `LabelStyle` をインタラクティブコントロールに渡していない
- [ ] `[HelpURL("ucl_core:Docs~/{lang}/...")]` に `{lang}` プレースホルダを含む
- [ ] `static Create()` ファクトリメソッドが存在し、サブクラス型を返す
- [ ] 少なくとも 1 つのエントリポイント（親ページボタン / WelcomePage カード / メニュー）からこのページに到達できる
- [ ] domain reload 後に実際開いて NullRef なし、Back / Close 動作が正常
- [ ] IMGUI rich-text コンテンツがある場合、関連 GUIStyle で `richText` が有効

---

## 9. 参考実装例

| ページ | 注目点 |
|---|---|
| `UCL_DocSearchPage` | 標準スケルトン + 検索入力欄 Enter 発火 + 折りたたみ詳細オプション + 結果行アクションボタン |
| `UCL_MarkdownViewerPage` | 外部 `Create(args...)` でデータ読込 + `EnsureStyles()` でスタイル構築を一元化 + TopBarButtons に 3 ボタンをカスタム |
| `UCL_WelcomePage` | カードグリッドレイアウト + 多エントリ集約 |

---

## 10. 関連ドキュメント

- [UCL_GUILayout 全体概要](../API/UCL_GUILayout/UCL_GUILayout_Overview.md) — IMGUI コンポーネント層
- [UCL_GUIStyle 概要](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md) — スタイル層
- [HelpURL_Workflow](HelpURL_Workflow.md) — `ucl_core:` / `eov_docs:` プレフィックス解析機構
- [Hardcoded_Localize](Hardcoded_Localize.md) — TopBar / ボタン文字列のローカライズ（`UCL_CodeLocalize` / `UCL_LocalizeManager`）
- [Polymorphism_In_UCL](../Architecture/Polymorphism_In_UCL.md) — `[SerializeReference]` ポリモーフィックフィールドが GUI 編集と JSON シリアライズで占める全体アーキテクチャ
