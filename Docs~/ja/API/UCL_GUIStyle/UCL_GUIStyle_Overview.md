---
title: UCL_GUIStyle 概要
description: UCL_Core の IMGUI スタイル中央集約 — BoxStyle / ButtonStyle / LabelStyle / TextField/Area / Slider などの共通スタイルを提供。DPI グローバルスケーリングと EditorWindow / Runtime のデュアルキャッシュ機構付き。重要なアンチパターン（LabelStyle をインタラクティブコントロールに渡してはいけない）を含む。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUIStyle.cs
namespace: UCL.Core.UI
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [UCL_GUIStyle, GUIStyle 中央, IMGUI styles]
tags: [api, ui, imgui, editor, style]
---

# UCL_GUIStyle 概要

`UCL_GUIStyle` は UCL_Core の IMGUI スタイル集約地です。すべてのページ（`UCL_CommonEditorPage` ファミリー、`UCL_DocSearchPage`、`RCG_*EditorPage` など）はここから共通 GUIStyle を取得すべきで、自前で `new GUIStyle(GUI.skin.xxx)` を**しないでください**。そうしないと DPI スケーリングと EditorWindow / Runtime のデュアルキャッシュが効きません。

---

## 1. レイヤー

```
   呼び出し側：UCL_GUILayout / *EditorPage
              │
              ▼  静的エントリ（IsInEditorWindow で自動分岐）
   UCL_GUIStyle
     ├── BoxStyle / ButtonStyle / LabelStyle / TextFieldStyle / TextAreaStyle
     ├── GetButtonStyle(Color, fontSize)   ← Tuple キーで内部キャッシュ
     ├── GetLabelStyle(Color, fontSize)    ← Tuple キーで内部キャッシュ
     ├── ButtonTextRed / Yellow / Green    ← プリセット色ショートカット
     ├── PushGUIColor / PopGUIColor / UCL_GUIColorScope
     ├── SetSizeOnGUI()                    ← Small / Medium / Big / XL の Scale 切替
     └── CurStyleData → StyleData          ← 実際のスタイル保持者
                              │
                              ├── Scale（PlayerPrefs で永続化されるグローバルスケール）
                              ├── ApplyScale()  / SetScale(value)
                              └── m_*StyleDic（(Color, fontSize) でキャッシュ）
```

デュアルキャッシュ：
- `IsInEditorWindow == false`（Runtime / 一般 GUI）→ `s_Data`
- `IsInEditorWindow == true`（エディタ OnGUI）→ `s_EditorWindowData`

EditorWindow の OnGUI 冒頭で `IsInEditorWindow = true` を設定し、終端で復元してください。`IsInEditorWindowScope`（using-disposable）の利用を推奨します。

---

## 2. API クイックリファレンス

### 2.1 スタイルエントリ（GUIStyle 直取得）

| API | 用途 |
|---|---|
| `BoxStyle` | `GUILayout.Box` — 白文字、richText、wordWrap |
| `ButtonStyle` | 標準 `GUILayout.Button` 白文字。button-like Toggle もこれを使用 |
| `TextFieldStyle` | 単行 `GUILayout.TextField` |
| `TextAreaStyle` | 複数行 `GUILayout.TextArea` |
| `LabelStyle` ⚠ | `GUILayout.Label` プレーンテキスト。**Toggle / Button / TextField の GUIStyle 引数に渡すな** |
| `ButtonTextRed/Yellow/Green` | 赤 / 黄 / 緑 文字色の Button（危険 / 注意 / 確認） |

### 2.2 カスタマイズ（色 / フォントサイズを取る）

| API | 用途 |
|---|---|
| `GetButtonStyle(Color, fontSize)` | 内部で `(Color, int)` をキーにキャッシュ |
| `GetLabelStyle(Color, fontSize)` | 同上。⚠ 同じくインタラクティブコントロールには**渡さない** |
| `GetScaledSize(float)` | 任意のサイズに現在の `Scale` を乗算（カスタム幅／高さ／fontSize 用） |

### 2.3 GUI.color スタック

| API | 用途 |
|---|---|
| `PushGUIColor(Color)` / `PopGUIColor()` | ペアで使用 |
| `UCL_GUIColorScope`（IDisposable） | `using (new UCL_GUIColorScope(Color.red)) {...}` ブロック内で上書き、退出時に自動復元 |

### 2.4 スケール制御

| API | 用途 |
|---|---|
| `StyleData.Scale`（読み）/ `StyleData.SetScale(value)`（書き） | グローバル GUI スケール（PlayerPrefs で永続化） |
| `StyleData.ApplyScale()` | キャッシュスタイルのフォントサイズ再計算を手動トリガー（`SetScale` 内部で呼ばれる） |
| `SetSizeOnGUI()` | Small / Medium / Big / XL の 4 ボタンを描画し、ユーザに Scale を選ばせる |

### 2.5 EditorWindow / Runtime の切替

| API | 用途 |
|---|---|
| `IsInEditorWindow`（bool field） | OnGUI 中は true、終了時に false |
| `IsInEditorWindowScope` | using で包んだ安全版（復元忘れを防ぐ） |
| `CurStyleData` | 現在の `IsInEditorWindow` に応じた `StyleData` インスタンスを自動返却 |

---

## 3. アンチパターン（`LabelStyle` で繰り返し起きる落とし穴）

`LabelStyle` / `GetLabelStyle(...)` が返すスタイルには、**toggle / button / textfield に必要な二状態 background sprite と padding 設定がありません**。インタラクティブコントロールの第三 GUIStyle 引数に渡すと問題が起きます：

| コントロール | 症状 |
|---|---|
| `Toggle` | チェックボックスアイコンが消え、押しても反応しない |
| `Button` | 押下時のフィードバック表示なし |
| `TextField` | 枠線 / padding が異常 |

**正解**：

| やりたいこと | 使うもの |
|---|---|
| 純粋なチェックボックス Toggle | `GUILayout.Toggle(value, label)`（第三引数を省略し、デフォルトの `GUI.skin.toggle` を使う） |
| Button-like 二状態（AND/OR、Tab） | 第三引数に `UCL_GUIStyle.ButtonStyle` を渡す |
| カラー / 大文字 label が欲しい | `GUILayout.Label(text, UCL_GUIStyle.GetLabelStyle(Color, size))` |

---

## 4. 新しい GUIStyle を書くタイミング

**通常は不要**。まずこのデシジョンツリーを試してください：

```
何を表示する？
├── プレーンテキスト → LabelStyle / Label(name, Color) / LabelAutoSize
├── ボタン           → ButtonStyle / GetButtonStyle / ButtonTextRed/Yellow/Green / ButtonAutoSize
├── 入力欄           → TextFieldStyle / TextAreaStyle
├── コンテナ枠       → BoxStyle
└── その他特殊スタイル（折りたたみヘッダ、code ブロックなど）
        → 自分の page 内で lazy に new GUIStyle(UCL_GUIStyle.LabelStyle) {...} を作成
          wordWrap / richText / fontSize などを上書き
```

ページ内で自作スタイルを作る例は `UCL_MarkdownViewerPage.cs` の `m_HeadingStyles[]` / `m_CodeBlockStyle` を参照（`LabelStyle` から派生し、fontSize と richText を調整）。

---

## 5. 関連ドキュメント

- [UCL_GUILayout 全体概要](../UCL_GUILayout/UCL_GUILayout_Overview.md) — 実際に UI を描画するツールセット（このレイヤーが提供する GUIStyle を消費）
- [Create_EditorPage_Workflow](../../Workflows/Create_EditorPage_Workflow.md) — 新しい Editor ページの起こし方、いつどのスタイルを使うか
