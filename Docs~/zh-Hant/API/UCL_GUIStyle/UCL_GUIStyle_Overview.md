---
title: UCL_GUIStyle 概覽
description: UCL_Core 的 IMGUI 樣式中央 — 提供 BoxStyle / ButtonStyle / LabelStyle / TextField/Area / Slider 等共用樣式，附 DPI 全域縮放與 EditorWindow / Runtime 雙 cache 機制；包含一個關鍵反指守則（LabelStyle 不可給互動控制項）。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUIStyle.cs
namespace: UCL.Core.UI
last_updated: 2026-05-08 (補 §2.5 GUILayout 尺寸縮放守則 — Width/Height 一律包 GetScaledSize)
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [UCL_GUIStyle, GUIStyle 中央, IMGUI styles]
tags: [api, ui, imgui, editor, style]
---

# UCL_GUIStyle 概覽

`UCL_GUIStyle` 是 UCL_Core 的 IMGUI 樣式集中地。所有頁面（`UCL_CommonEditorPage` 系列、`UCL_DocSearchPage`、`RCG_*EditorPage`…）都該從這裡取共用 GUIStyle，**不要**自己 `new GUIStyle(GUI.skin.xxx)`，否則沒辦法吃 DPI 縮放與 EditorWindow / Runtime 雙 cache。

---

## 1. 分層

```
   呼叫端：UCL_GUILayout / *EditorPage
              │
              ▼  靜態入口（IsInEditorWindow 自動分流）
   UCL_GUIStyle
     ├── BoxStyle / ButtonStyle / LabelStyle / TextFieldStyle / TextAreaStyle
     ├── GetButtonStyle(Color, fontSize)   ← Tuple key 內部 cache
     ├── GetLabelStyle(Color, fontSize)    ← Tuple key 內部 cache
     ├── ButtonTextRed / Yellow / Green    ← 預設色捷徑
     ├── PushGUIColor / PopGUIColor / UCL_GUIColorScope
     ├── SetSizeOnGUI()                    ← Small / Medium / Big / XL 切換 Scale
     └── CurStyleData → StyleData          ← 實際樣式持有者
                              │
                              ├── Scale（PlayerPrefs 持久化的全域縮放）
                              ├── ApplyScale()  / SetScale(value)
                              └── m_*StyleDic（依 (Color, fontSize) cache）
```

雙 cache：
- `IsInEditorWindow == false`（Runtime / 一般 GUI）→ `s_Data`
- `IsInEditorWindow == true`（編輯器 OnGUI）→ `s_EditorWindowData`

EditorWindow 的 OnGUI 開頭請設 `IsInEditorWindow = true`，結尾還原；推薦用 `IsInEditorWindowScope`（using-disposable）。

---

## 2. API 速查

### 2.1 樣式入口（直接拿 GUIStyle）

| API | 用途 |
|---|---|
| `BoxStyle` | `GUILayout.Box` — 白字、richText、wordWrap |
| `ButtonStyle` | `GUILayout.Button` 標準白字；button-like Toggle 也吃這個 |
| `TextFieldStyle` | 單行 `GUILayout.TextField` |
| `TextAreaStyle` | 多行 `GUILayout.TextArea` |
| `LabelStyle` ⚠ | `GUILayout.Label` 純文字；**禁止**當 Toggle / Button / TextField 的 GUIStyle 參 |
| `ButtonTextRed/Yellow/Green` | 紅 / 黃 / 綠字色 Button（危險 / 提醒 / 確認） |

### 2.2 客製（吃顏色 / 字級）

| API | 用途 |
|---|---|
| `GetButtonStyle(Color, fontSize)` | 內部依 `(Color, int)` cache |
| `GetLabelStyle(Color, fontSize)` | 同上；⚠ 一樣**不要**給互動控制項 |
| `GetScaledSize(float)` | 把任意尺寸乘上當前 `Scale`（自訂寬高 / fontSize 用） |

### 2.3 GUI.color stack

| API | 用途 |
|---|---|
| `PushGUIColor(Color)` / `PopGUIColor()` | 配對使用 |
| `UCL_GUIColorScope`（IDisposable）| `using (new UCL_GUIColorScope(Color.red)) {...}` 區段內覆寫，離開自動還原 |

### 2.4 縮放控制

| API | 用途 |
|---|---|
| `StyleData.Scale`（讀） / `StyleData.SetScale(value)`（寫） | 全域 GUI 縮放（PlayerPrefs 持久化） |
| `StyleData.ApplyScale()` | 手動觸發 cache 樣式重算字級（`SetScale` 內部會呼叫） |
| `SetSizeOnGUI()` | 繪製 Small / Medium / Big / XL 四顆按鈕，給使用者自選 Scale |

### 2.5 GUILayout 尺寸縮放守則（重要）

**寫死的 `GUILayout.Width(N)` / `GUILayout.Height(N)` 數字一律包 `UCL_GUIStyle.GetScaledSize(N)`**，否則使用者按 `SetSizeOnGUI()` 切到 Big / XL 時，文字字級放大但容器尺寸沒跟著放，會被擠出/截斷。

```csharp
// ❌ 寫死 — 不會跟著 Scale 變
m_Scroll = GUILayout.BeginScrollView(m_Scroll, GUILayout.Height(360));
if (GUILayout.Button("Save", GUILayout.Width(80))) { ... }

// ✅ 走 GetScaledSize — Small/Medium/Big/XL 切換時會等比放大
m_Scroll = GUILayout.BeginScrollView(m_Scroll, GUILayout.Height(UCL_GUIStyle.GetScaledSize(360)));
if (GUILayout.Button("Save", GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)))) { ... }
```

適用對象：
- `GUILayout.Width(...)` / `GUILayout.Height(...)`
- `GUILayout.MinWidth(...)` / `GUILayout.MaxHeight(...)` 等同類
- `GUILayoutUtility.GetRect(width, height, ...)` 內的固定數字
- 自定 GUIStyle 的 `fontSize` 也同理

**例外**（不必包）：
- `GUILayout.ExpandWidth(true/false)` / `GUILayout.ExpandHeight(...)` — 純 bool 沒尺寸
- 跟其他 layout 計算出來的相對值（已經是縮放後的）
- 真正想要「無論 Scale 都這麼大」的 fixed asset 邊框

UCL_GUILayout 內部既有 helpers（`NumField` / `Label` 等）已自帶 GetScaledSize — 透過 wrapper 用就免操心，**只有寫 raw `GUILayout.*` 時要自律**。

### 2.6 EditorWindow / Runtime 切換

| API | 用途 |
|---|---|
| `IsInEditorWindow`（bool field） | OnGUI 期間設 true / 結束設 false |
| `IsInEditorWindowScope` | using 包起來的安全版本（避免忘了還原） |
| `CurStyleData` | 依當前 `IsInEditorWindow` 自動回傳對應的 `StyleData` 實例 |

---

## 3. 反指守則（從 `LabelStyle` 重複出現的禁忌）

`LabelStyle` / `GetLabelStyle(...)` 回傳的樣式 **沒有 toggle / button / textfield 的兩態 background sprite 與 padding 設定**。傳給互動控制項當第三 GUIStyle 參會出問題：

| 控制項 | 症狀 |
|---|---|
| `Toggle` | 核取方塊圖示消失、按了沒反應 |
| `Button` | 按下沒有反饋態 |
| `TextField` | 邊框 / padding 失常 |

**正解**：

| 想做的事 | 用什麼 |
|---|---|
| 純 checkbox toggle | `GUILayout.Toggle(value, label)`（省略第三參數，吃 `GUI.skin.toggle` 預設） |
| Button-like 兩態（AND/OR、Tab）| 第三參傳 `UCL_GUIStyle.ButtonStyle` |
| 想要彩色 / 大字 label | `GUILayout.Label(text, UCL_GUIStyle.GetLabelStyle(Color, size))` |

---

## 4. 寫新 GUIStyle 的時機

**通常不需要**。先試這條決策樹：

```
要顯示什麼？
├── 純文字 → LabelStyle / Label(name, Color) / LabelAutoSize
├── 按鈕   → ButtonStyle / GetButtonStyle / ButtonTextRed/Yellow/Green / ButtonAutoSize
├── 輸入框 → TextFieldStyle / TextAreaStyle
├── 容器框 → BoxStyle
└── 其他特殊樣式（折疊頭、code 區塊等）
        → 在自己的 page 內 lazy 建一個 new GUIStyle(UCL_GUIStyle.LabelStyle) {...}
          並覆寫 wordWrap / richText / fontSize 等
```

頁面內自建樣式的範例見 `UCL_MarkdownViewerPage.cs` 的 `m_HeadingStyles[]` / `m_CodeBlockStyle`（依 `LabelStyle` 派生、調整 fontSize 與 richText）。

---

## 5. 相關文件

- [UCL_GUILayout 整體概覽](../UCL_GUILayout/UCL_GUILayout_Overview.md) — 真正畫 UI 的工具集（吃這層提供的 GUIStyle）
- [Create_EditorPage_Workflow](../../Workflows/Create_EditorPage_Workflow.md) — 怎麼開新 Editor 頁、什麼時候用什麼 style
