---
title: 建立新的 UCL_CommonEditorPage 子類工作流
description: 步驟化 SOP — 從零開出一頁可被 GUIPageController 推送的 Editor 頁面。涵蓋繼承關係、必/選 override、TopBar 客製、入口點掛接、樣式選用守則（連結 UCL_GUILayout / UCL_GUIStyle 文件）、與常見地雷。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [Create EditorPage, UCL_CommonEditorPage workflow, 寫新 editor 頁]
tags: [workflow, editor, ui, imgui]
---

# 🛠️ 建立新的 UCL_CommonEditorPage 子類工作流

> [!IMPORTANT]
> 本工作流負責「**寫一頁繼承 `UCL_CommonEditorPage` 的 Editor 頁**」這件事；UI 元件實作（欄位 / 列表 / 下拉等）請看 [UCL_GUILayout 整體概覽](../API/UCL_GUILayout/UCL_GUILayout_Overview.md)，樣式取用請看 [UCL_GUIStyle 概覽](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md)。
>
> 設計哲學：**繼承 + override hook**。基底已經處理 TopBar / Back / Close / ScrollView / HelpURL 解析；子類只負責填 `WindowName` 與 `ContentOnGUI()`，要客製就 override hook。

---

## 0. TL;DR — 三分鐘加完一頁

```
[1] 想清楚這頁職責（單一焦點，一頁一事）
       ▼
[2] 建檔 UCL_<Name>Page.cs : UCL_CommonEditorPage
       ▼
[3] override WindowName 與 ContentOnGUI()
       ▼
[4] 加 [HelpURL] 指向你打算寫的文件路徑（即使檔案還沒寫）
       ▼
[5] 提供 static Create()（Push 到 GUIPageController）
       ▼
[6] 在父頁 / 選單 / WelcomePage 加按鈕 → Create()
```

---

## 1. 繼承關係

```
UCL_GUIPage (UICore)
  └── UCL_EditorPage (EditorMenuPages)        ← 提供 TopBar / Back / Close / HelpURL 解析
        └── UCL_CommonEditorPage              ← 在 TopBar 多顯示 ClassName + Copy 按鈕
              └── UCL_<Name>Page              ← 你要寫的這頁
```

| 類別 | 職責 |
|---|---|
| `UCL_GUIPage` | `WindowName` / `IsWindow` / `OnGUI()` 最外層流程 |
| `UCL_EditorPage` | TopBar（Back / Close / Help）、ContentOnGUI ScrollView 包裝、HelpURL 反射快取、`Create<T>()` 工廠 |
| `UCL_CommonEditorPage` | 在 TopBar 顯示「TypeName + Copy」幫除錯與支援文件對齊 |

---

## 2. 必須 override 的成員

| 成員 | 型別 | 必要性 | 說明 |
|---|---|---|---|
| `WindowName` | `string` | **必填** | 視窗標題；多視窗切換時 `UCL_GUIPageController.WindowName` 會吃這個 |
| `ContentOnGUI()` | `void` | **必填** | 主要內容繪製（ScrollView 由 base 包好，這裡專心畫東西） |

### 2.1 最小骨架

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
            // 這裡開始畫；ScrollView 已被 base.OnGUI 包好
            GUILayout.Label("Hello", UCL_GUIStyle.LabelStyle);
        }
    }
}
#endif
```

> [!CAUTION]
> `Create<T>()` 是 **public static**，但要透過 `UCL_EditorPage.Create<T>()` 呼叫 — 它會把 page push 到 `UCL_GUIPageController.CurrentRenderIns`，所以**呼叫前必須有一個 controller 在跑**（通常是父頁的 OnGUI 或 EditorWindow 已經建好）。

---

## 3. 可選 override 的 hook

| 成員 | 型別 | 預設行為 | 何時 override |
|---|---|---|---|
| `TopBarButtons()` | `void` | 顯示 ClassName + Copy 按鈕（CommonEditorPage 提供） | 想加入「重新整理 / 切換語系 / 開關側欄」等頂列工具按鈕 |
| `ShowCloseButton` | `bool` | `true` | 不希望使用者一鍵關掉所有頁時設 `false` |
| `ShowBackButton` | `bool` | `!ShowCloseButton || pages.Count > 1` | 自訂導覽流程時 |
| `BackButtonClicked()` | `void` | `p_Controller.Pop()` | 返回前要存檔、要彈確認框 |
| `CloseButtonClicked()` | `void` | `p_Controller.PopAll()` | 同上 |
| `Init(controller)` | `void` | 呼叫 base + 紀錄 `m_TypeName` | 想做一次性初始化、訂閱事件 |

### 3.1 TopBarButtons 範例

```csharp
protected override void TopBarButtons()
{
    base.TopBarButtons();   // 保留 ClassName + Copy
    if (GUILayout.Button(UCL_CodeLocalize.Get("DocSearch.Reveal"),
        UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
    {
        EditorUtility.RevealInFinder(m_AbsolutePath);
    }
}
```

實例見 `UCL_MarkdownViewerPage.TopBarButtons()`：在標準兩顆之外又加 Reveal / OS Open / Copy raw 三顆。

---

## 4. 入口點掛接（這頁怎麼被打開？）

四種常見入口：

| 入口 | 範例 | 適用 |
|---|---|---|
| **父頁按鈕** | `UCL_DocSearchPage.DrawResultRow` 的 `📄` 按鈕 → `UCL_MarkdownViewerPage.Create(...)` | 跟既有頁有明確上下文關係 |
| **WelcomePage 卡片** | `UCL_WelcomePage` 的「🔍 文件搜尋」按鈕 → `UCL_DocSearchPage.Create()` | 全域功能 / 需要顯眼 |
| **`UCL → ...` 選單** | `[MenuItem("UCL/<Name>")]` 開 EditorWindow，`OnGUI` 內推 page | 獨立工具，不依附其他頁 |
| **HelpURL deep link** | `ucl_core:Docs~/...` 從文件按鈕跳回 page | 通常是反向 — 從 page 到文件 |

> [!TIP]
> 為新頁加入口時用「**最少耦合**」原則：能放父頁就不開選單、能加 WelcomePage 卡片就不要散落在多處選單。

---

## 5. UI 繪製選什麼？

`ContentOnGUI()` 內畫 UI 時，依複雜度往下找：

| 想做 | 工具 | 出處 |
|---|---|---|
| Label / Button / TextField / Toggle 等基本 | `GUILayout.*` + `UCL_GUIStyle.*Style` | [UCL_GUIStyle Overview](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md) |
| 數字欄位 / 滑條 / 向量 / 折疊頭 ▼/► | `UCL_GUILayout.IntField` / `Slider` / `Vector3Field` / `Toggle(bool, size)` | [UCL_GUILayout Overview §3.1](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#31-基礎欄位ucl_guilayoutcs) |
| 列表 / 字典 / HashSet 編輯（含分頁、多型 Add） | `UCL_GUILayout.DrawList` / `DrawDictionary` / `DrawHashSet` | [UCL_GUILayout Overview §3.2](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#32-集合編輯drawlist--drawdictionary--drawhashset) |
| 任意物件遞迴繪製（反射欄位、`[SerializeReference]` 多型） | `UCL_GUILayout.DrawObjectData` | [UCL_GUILayout Overview §3.3](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#33-物件遞迴繪製ucl_guilayoutdrawobjectcs) |
| 下拉選單（含搜尋 / 列舉） | `UCL_GUILayout.PopupAuto` / `Popup<T>(enum)` | [UCL_GUILayout Overview §3.4](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#34-下拉選單與分頁ucl_guilayoutpopupcs) |
| 互動畫板 | `UCL_GUILayout.DrawableTexture` / `UCL_GUILayoutPainter` | [UCL_GUILayout Overview §3.5](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#35-互動繪圖) |
| 複雜結構 Copy/Paste | `UCL_GUILayout.DrawCopyPaste(ref obj, ...)` | [UCL_GUILayout Overview §5.3](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#53-drawcopypasteref-obj-dic-fieldtype) |

### 5.1 自建 GUIStyle 的時機

如果你需要的不是上面任何一個內建樣式（例如「16pt 粗體 + wordWrap + richText 的 Heading 樣式」），就在 page 內 lazy 建一份**派生**自既有樣式：

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
> 這份 `m_HeadingStyle` 是純顯示樣式（從 LabelStyle 派生），同樣**不能**拿去當 `Toggle` / `Button` 的第三 GUIStyle 參。詳見 [UCL_GUIStyle Overview §3](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md#3-反指守則從-labelstyle-重複出現的禁忌)。

---

## 6. HelpURL 與多語系文件

### 6.1 attribute 寫法

```csharp
[HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_<Name>Page.md")]
public class UCL_<Name>Page : UCL_CommonEditorPage { ... }
```

`{lang}` 在 `UCL_GUILayout.DrawHelpButton` 解析時自動替換成當前 `UCL_LocalizeManager.s_LangName`（zh-Hant / en / ja / zh-Hans），讓「？」按鈕能依使用者語系跳對檔。詳見 [HelpURL_Workflow](HelpURL_Workflow.md)。

### 6.2 文件還沒寫也要先掛

- `[HelpURL]` 即使指向尚未存在的 .md 也不會 crash — 點下去開不了視窗就算了
- 把 attribute 先掛上，可以讓未來搜尋（[Cmd_SearchDocs](../API/UCL_AgentCommand/Cmd_SearchDocs.md) / `UCL_DocSearchPage`）一併索引到「應該存在但還沒寫」的文件位置
- 文件等 page 穩定後再補（看 `UCL_DocSearchPage` 自己也是這個節奏）

---

## 7. 常見地雷

| # | 地雷 | 症狀 | 解法 |
|---|---|---|---|
| 1 | `UCL_GUIStyle.LabelStyle` 傳給 `GUILayout.Toggle` 第三參 | checkbox 圖示消失、按了沒反應 | 純 checkbox 省略第三參；button-like 用 `ButtonStyle`。詳見 [UCL_GUIStyle Overview §3](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md#3-反指守則從-labelstyle-重複出現的禁忌) |
| 2 | 在 `ContentOnGUI()` 內又包一層 ScrollView | 雙重捲軸、滑鼠滾輪行為怪 | 不用包，base 已包；要二級捲軸時自己另開命名變數 |
| 3 | TextField 吃掉 Enter 後 search 不觸發 | 按 Enter 沒反應 | 在 TextField 之前 snapshot `Event.current`；見 `UCL_DocSearchPage.DrawSearchInput` 區塊註解 |
| 4 | `Create<T>()` 在沒有 controller 的情境下呼叫 | NullRef | 確保父頁／EditorWindow 已建好 controller，或自己持有 controller 傳進 `Create<T>(controller)` 多載 |
| 5 | 樣式 lazy 建立沒 cache | 每幀 new GUIStyle，效能崩 | 用 field + property 做 lazy（見 §5.1），或丟到 `EnsureStyles()` 統一建 |
| 6 | rich-text label 內混入 `<...>`（如顯示 C# 泛型 `List<T>`） | 字被當 tag 解析，部分內容消失 | 該樣式關掉 `richText`，或對使用者內容做 `<` → `&lt;` 轉義 |
| 7 | `[HelpURL]` 寫死語系（沒用 `{lang}` 佔位） | 切語系後 Help 按鈕跳錯檔 | 一律用 `ucl_core:Docs~/{lang}/...` |
| 8 | EditorWindow.OnGUI 沒設 `IsInEditorWindow` | 樣式 cache 跑到 runtime 那份，DPI 異常 | 用 `IsInEditorWindowScope`（using 自動還原）|

---

## 8. 驗收清單

寫完後逐項確認：

- [ ] 繼承 `UCL_CommonEditorPage`、檔名與類別名一字不差
- [ ] override `WindowName`（非空字串）
- [ ] override `ContentOnGUI()`，內容用 `UCL_GUIStyle.*` / `UCL_GUILayout.*` 取樣式與元件
- [ ] 沒有把 `LabelStyle` 傳給互動控制項
- [ ] `[HelpURL("ucl_core:Docs~/{lang}/...")]` 帶 `{lang}` 佔位
- [ ] `static Create()` 工廠方法存在，且回傳子類型別
- [ ] 至少一個入口點（父頁按鈕 / WelcomePage 卡片 / 選單）能開到這頁
- [ ] domain reload 後實際打開無 NullRef、Back / Close 行為正常
- [ ] 有 IMGUI rich-text 內容時，相關 GUIStyle 開啟 `richText`

---

## 9. 範例參考

| 頁面 | 看點 |
|---|---|
| `UCL_DocSearchPage` | 標準骨架 + 搜尋輸入列 Enter 觸發 + 折疊進階選項 + 結果列表動作按鈕 |
| `UCL_MarkdownViewerPage` | 從外部 `Create(args...)` 載入資料 + `EnsureStyles()` 集中建樣式 + TopBarButtons 客製三按鈕 |
| `UCL_WelcomePage` | 卡片 grid 排版 + 多入口集中地 |

---

## 10. 相關文件

- [UCL_GUILayout 整體概覽](../API/UCL_GUILayout/UCL_GUILayout_Overview.md) — IMGUI 元件層
- [UCL_GUIStyle 概覽](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md) — 樣式層
- [HelpURL_Workflow](HelpURL_Workflow.md) — `ucl_core:` / `eov_docs:` prefix 解析機制
- [Hardcoded_Localize](Hardcoded_Localize.md) — TopBar / 按鈕文字本地化（`UCL_CodeLocalize` / `UCL_LocalizeManager`）
- [Polymorphism_In_UCL](../Architecture/Polymorphism_In_UCL.md) — `[SerializeReference]` 多型欄位在 GUI 編輯與 JSON 序列化的整體架構
