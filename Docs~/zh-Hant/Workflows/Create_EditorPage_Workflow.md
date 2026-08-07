---
title: 建立新的 UCL_CommonEditorPage 子類工作流
description: 步驟化 SOP — 從零開出一頁可被 GUIPageController 推送的 Editor 頁面。涵蓋繼承關係、必/選 override、入口點掛接、**區塊折疊與排版守則（按鈕靠左、單排、關鍵操作提到折疊外）**、狀態快取分層、樣式選用、與 12 條實戰地雷。
source_root: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-08-07 (新增 L4：下拉行尾不放 FlexibleSpace；emoji 用常見款 — ⟳ Unity 畫不出來，刷新用 🔄。Tim 於閱讀心得管理頁實測)
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [Create EditorPage, UCL_CommonEditorPage workflow, 寫新 editor 頁, editor page 排版, 折疊守則]
tags: [workflow, editor, ui, imgui, layout, fold]
---

# 🛠️ 建立新的 UCL_CommonEditorPage 子類工作流

> [!IMPORTANT]
> 本工作流負責「**寫一頁繼承 `UCL_CommonEditorPage` 的 Editor 頁**」。UI 元件實作看 [UCL_GUILayout 概覽](../API/UCL_GUILayout/UCL_GUILayout_Overview.md)，樣式取用看 [UCL_GUIStyle 概覽](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md)。
>
> 設計哲學：**繼承 + override hook**。基底已處理 TopBar / Back / Close / ScrollView / HelpURL 解析；子類只填 `WindowName` 與 `ContentOnGUI()`，要客製才 override。
>
> **想新增「持久化資料類型」（不是 UI 頁）？** → [Create_UCL_Asset_Workflow](Create_UCL_Asset_Workflow.md) — UCL_Core 體系一律繼承 `UCL_Asset<T>`，禁止裸 ScriptableObject。

---

## 0. TL;DR

```
[1] 想清楚這頁職責（單一焦點，一頁一事）
       ▼
[2] 建檔 UCL_<Name>Page.cs : UCL_CommonEditorPage
       ▼
[3] override WindowName 與 ContentOnGUI()
       ▼
[4] 內容切成「區塊」→ 每塊一個 Draw<X>Panel()，加折疊（§4）
       ▼
[5] 加 [HelpURL] 指向打算寫的文件路徑（即使檔案還沒寫）
       ▼
[6] 提供 static Create()（Push 到 GUIPageController）
       ▼
[7] 在父頁 / 選單 / WelcomePage 加按鈕 → Create()
       ▼
[8] **實際開頁點過每顆按鈕**，看 error log（§9 驗收）
```

---

## 1. 繼承關係

```
UCL_GUIPage (UICore)
  └── UCL_EditorPage (EditorMenuPages)        ← TopBar / Back / Close / HelpURL 解析
        └── UCL_CommonEditorPage              ← TopBar 多顯示 ClassName + Copy
              └── UCL_<Name>Page              ← 你要寫的這頁
```

| 類別 | 職責 |
|---|---|
| `UCL_GUIPage` | `WindowName` / `IsWindow` / `OnGUI()` 最外層流程 |
| `UCL_EditorPage` | TopBar（Back / Close / Help）、ContentOnGUI ScrollView 包裝、HelpURL 反射快取、`Create<T>()` 工廠 |
| `UCL_CommonEditorPage` | TopBar 顯示「TypeName + Copy」，幫除錯與文件對齊 |

---

## 2. 必須 override 的成員

| 成員 | 型別 | 必要性 | 說明 |
|---|---|---|---|
| `WindowName` | `string` | **必填** | 視窗標題；多視窗切換時 `UCL_GUIPageController.WindowName` 吃這個 |
| `ContentOnGUI()` | `void` | **必填** | 主要內容繪製（ScrollView 由 base 包好） |

### 2.1 最小骨架

```csharp
#if UNITY_EDITOR
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_<Name>Page.md")]
    public class UCL_<Name>Page : UCL_CommonEditorPage
    {
        public override string WindowName => "UCL_<Name>";

        public static UCL_<Name>Page Create() => UCL_EditorPage.Create<UCL_<Name>Page>();

        protected override void ContentOnGUI()
        {
            // ScrollView 已被 base.OnGUI 包好，這裡專心畫
            GUILayout.Label("Hello", UCL_GUIStyle.LabelStyle);
        }
    }
}
#endif
```

> [!CAUTION]
> `Create<T>()` 會把 page push 到 `UCL_GUIPageController.CurrentRenderIns`，**呼叫前必須有 controller 在跑**（通常是父頁 OnGUI 或 EditorWindow 已建好）。

### 2.2 內容切區塊 — 一律 `Draw<X>Panel()`

`ContentOnGUI()` 只當排程器，每個區塊獨立方法。好處：折疊、重排、抽取都是動一行。

```csharp
protected override void ContentOnGUI()
{
    if (!m_Loaded) LoadData();          // 首幀 lazy-load（別在 ctor 碰 IO）

    DrawStatusPanel();
    GUILayout.Space(8);
    DrawSettingsPanel();
    GUILayout.Space(8);
    DrawFilesPanel();
}
```

---

## 3. 可選 override 的 hook

| 成員 | 預設行為 | 何時 override |
|---|---|---|
| `TopBarButtons()` | ClassName + Copy 按鈕 | 加「重新整理 / 開資料夾 / 全域動作」等頂列工具 |
| `ShowCloseButton` | `true` | 不希望一鍵關掉所有頁 |
| `ShowBackButton` | `!ShowCloseButton \|\| pages.Count > 1` | 自訂導覽流程 |
| `BackButtonClicked()` | `p_Controller.Pop()` | 返回前要存檔 / 彈確認 |
| `CloseButtonClicked()` | `p_Controller.PopAll()` | 同上 |
| `Init(controller)` | base + 紀錄 `m_TypeName` | 一次性初始化、訂閱事件 |
| `SensitiveContentReason` | `null` | 頁面含 token / 密碼 → 錄影時黑屏。**只宣告不夠** — 還要在 `ContentOnGUI()` 開頭呼 `UCL_ScreenStreamGuard.GuardPage(nameof(本類), SensitiveContentReason)` 才真的擋（見 `UCL_LoginStatusPage`）|

### 3.1 TopBarButtons 範例

```csharp
protected override void TopBarButtons()
{
    base.TopBarButtons();   // 保留 ClassName + Copy
    if (GUILayout.Button("🔄 重新整理狀態", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
        RefreshAll();
}
```

**TopBar 放什麼**：影響整頁的動作（重新整理、開資料夾、跳姊妹頁）。**別放**只影響單一區塊的操作 —— 那該在該區塊的 header（§4.3）。

---

## 4. 版面與折疊守則 ⭐（2026-07-29 實戰新增）

> 這節是本文件最容易被跳過、卻最常造成「按鈕點不到」的部分。IMGUI 沒有 layout engine 幫你收拾，**版面爆掉不會編譯錯、也不會 log error，只會讓使用者操作不到**。

### 4.1 三條硬規則

| # | 規則 | 為什麼 |
|---|---|---|
| **L1** | **按鈕靠左**。`GUILayout.FlexibleSpace()` 放在**行尾**，不要放在按鈕前面把按鈕推到右邊 | Label 文字長度會隨內容 / 語系 / 縮放變動；按鈕被推到右邊時，視窗一窄就**跑出可見範圍點不到** |
| **L2** | **Label 可能很長時，按鈕放 Label 前面** | 長 Label 會把後面的按鈕整個擠出畫面。順序改成「折疊鈕 → 按鈕 → Label → FlexibleSpace」就永遠點得到 |
| **L3** | **折疊外層的控件只能占「一排」**。超過一排的東西一律進折疊區 | header 是常駐可見區，多排會讓收合失去意義（收了還是很高）|
| **L4** | **搜尋式下拉（`PopupSearchCache`）所在的行，行尾不要放 `FlexibleSpace()`** —— 讓下拉吃掉剩餘寬度；要限寬改用 `MinWidth` 包 | FlexibleSpace 會把下拉擠到最小寬度，**選項文字被截斷、點擊區縮成一小條**，不好操作（Tim 2026-08-07 實測於閱讀心得管理頁）。L1 的「FlexibleSpace 放行尾」是對**按鈕**行說的 —— 下拉行是例外，兩條不衝突 |

### 4.2 標準 header 寫法（照抄這個）

```csharp
void DrawTavernAdminSection()
{
    using (new GUILayout.VerticalScope("box"))
    {
        bool aShow;
        // header 一排：折疊鈕 → 標題 → 關鍵操作按鈕 → FlexibleSpace 收尾
        using (new GUILayout.HorizontalScope())
        {
            aShow = UCL_GUILayout.Toggle(m_FoldDic, "TavernAdminFold", 21, iDefaultValue: true);
            GUILayout.Label("<b>🍺 酒館後台</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
            if (GUILayout.Button("開啟酒館後台管理頁", UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
            {
                UCL_ChatTavernAdminPage.Create();
            }
            GUILayout.FlexibleSpace();      // ← 一律最後
        }
        if (!aShow) return;                 // ← 收合就結束，內容不繪製

        // 折疊內：說明文字、低頻設定、明細列表
        GUILayout.Label("Discord mirror 同步進度、persona 頭像 override、底層 config / state 檔案管理。",
            UCL_GUIStyle.LabelStyle);
    }
}
```

要點：
- **每個 Label / Button 都帶 `GUILayout.ExpandWidth(false)`** — 否則它會搶走剩餘寬度，把後面的控件推出去
- `if (!aShow) return;` 直接返回，別用 `if (aShow) { ... }` 包一大塊（少一層縮排、diff 更乾淨）
- 折疊鈕的 size 用 `21`（全專案一致的 ▼/► 尺寸）
- **按鈕 / 標題的 emoji 用「常見 emoji」，別用特殊符號字元** —— Unity IMGUI 內建字型
  畫不出 `⟳`（U+27F3）這類箭頭符號，會顯示成空白或豆腐，而且**不報錯**；
  刷新一律用 `🔄`（Tim 2026-08-07 實測）。不確定能不能顯示 → 抄既有頁面用過的
  （🔄 📂 📖 🍺 ✕ ⚠ 都驗過），別自己從字元表挑。

下拉選單行的寫法（L4 —— 與按鈕行相反，行尾**沒有** FlexibleSpace）：

```csharp
using (new GUILayout.HorizontalScope())
{
    GUILayout.Label("媒材", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
    m_KindSel = UCL_GUILayout.PopupSearchCache(m_KindSel, kinds, m_PickerDic, "KindPicker");
    // 行尾不放 FlexibleSpace —— 下拉要吃掉剩餘寬度才好點好讀。
    // 真要限制下拉寬度：用 MinWidth 包住，不是用 FlexibleSpace 擠。
    // GUILayout.BeginHorizontal(GUILayout.MinWidth(UCL_GUIStyle.GetScaledSize(150))); ... EndHorizontal();
}
```

### 4.3 什麼該留在折疊外層？

| 該留外層 | 該進折疊 |
|---|---|
| 開關 toggle（系統啟用 / daemon 開關）| 說明文字、註解、提示 |
| 狀態燈（● 運行中 / ○ 已停止）| 明細列表（per-room 進度、URL 列表）|
| 「開啟 XXX 管理頁」跳轉鈕 | 低頻設定（路徑、進階參數）|
| 重啟 / 立即觸發 等高頻動作 | 一次性設定完就不動的欄位 |

判準：**「收合狀態下我還會想按它嗎？」** 會 → 留外層；不會 → 進折疊。

實例：`UCL_ControlPanelPage` 把 Discord outbound/inbound 兩顆開關抽成 `DrawDiscordSyncToggles()` 畫在折疊**外**，因為那是全頁最高頻操作；說明文字則收進折疊。

### 4.4 折疊預設值怎麼選

```csharp
UCL_GUILayout.Toggle(m_FoldDic, "<Key>Fold", 21, iDefaultValue: <預設>)
```

- **展開**：使用者開頁第一眼要看的（同步狀態、當前健康度、最常操作的區塊）
- **收合**：偶爾才動的（頭像 override、底層檔案路徑、資料路徑設定）

一頁最多 1~2 塊預設展開；其餘收合 —— 否則等於沒做折疊。

---

## 5. 狀態快取分層 ⭐（2026-07-29 血證）

### 5.1 折疊狀態**必須**與 PopupSearchCache 分開存

```csharp
// ✅ 正確：兩個獨立 dictionary
readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();      // PopupSearchCache（LoadData 會 Clear）
readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();  // 折疊狀態（永不隨資料重載清）
```

> [!CAUTION]
> **血證（UCL_ChatTavernAdminPage, Tim QA 2026-07-29）**：折疊狀態原本跟 PopupSearchCache 共用同一個
> `UCL_ObjectDictionary`。`LoadData()` 內為了刷新下拉選項而呼 `m_Dic.Clear()`，**把折疊值一併清掉** →
> 下一幀 `Toggle(..., iDefaultValue: true)` 讀不到既存值 → 退回預設 = **強制展開**。
>
> 症狀是「按某個開關就自動展開、而且收不起來」，看起來像 key 撞名，實際是**共用快取的失效政策不同**：
> - PopupSearchCache = **衍生資料**（選項變了就該失效 → 該被 Clear）
> - 折疊狀態 = **使用者 UI 偏好**（不該因資料重載被重置）
>
> 而且觸發面比表象廣：任何呼 `LoadData()` 的路徑（寫檔、Refresh、套用設定、增刪項目）都會重置折疊。

**通則**：同一個 cache 容器只放**同一種失效政策**的資料。混放遲早被彼此的清空策略連坐。

### 5.2 LoadData / 首幀 lazy-load

```csharp
bool m_Loaded = false;

protected override void ContentOnGUI()
{
    if (!m_Loaded) LoadData();   // 首幀才讀檔；base 沒有 OnEnter hook
    ...
}

void LoadData()
{
    m_Loaded = true;
    // 一次讀齊 config / state / 檔案清單進快取；draft 欄位同步重置
    // ⚠ 別在這裡 Clear 折疊字典（見 §5.1）
}
```

- **不要每幀讀檔**（`File.ReadAllText` 在 OnGUI 內 = 每幀數十次 IO）
- 需要週期刷新的（daemon 狀態、log tail）用 `EditorApplication.timeSinceStartup` 節流，2 秒一次夠用
- 外部工具會併發改同一份檔時，考慮 **mtime 感知 reload + 欄位級 3-way merge**（見 `UCL_ScreenStreamPage.MergeField`）—— 否則會拿舊快取蓋掉外部剛寫的值

### 5.3 draft 欄位與「套用」

編輯中的值放 draft 欄位，按「套用」才寫檔。理由：每次 keystroke 都寫檔會撕裂外部讀者，也讓 undo 不可能。

---

## 6. 入口點掛接（這頁怎麼被打開？）

| 入口 | 適用 |
|---|---|
| **父頁按鈕** | 跟既有頁有明確上下文關係 |
| **WelcomePage 卡片** | 全域功能 / 需要顯眼 |
| **`UCL → ...` 選單**（`[MenuItem]`）| 獨立工具，不依附其他頁 |
| **HelpURL deep link** | 通常是反向（page → 文件）|

### 6.1 強制規則：非衍生頁必須出現在 UCL_EditorMenuPage（二擇一）

> [!IMPORTANT]
> **非衍生頁**（standalone、不依賴特定父頁上下文）**必須**從 [`UCL_EditorMenuPage`](../../UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_EditorMenuPage.cs) 找得到入口，否則使用者沒途徑再次打開它。
>
> 1. **外部按鈕**（高頻 / 旗艦）— 在 `UCL_EditorMenuPage.ContentOnGUI()` 寫一顆 Button → `Create()`
> 2. **Page Picker 下拉**（低頻 / 次要）— override `public override bool ShowInPageMenu => true;`，反射自動收錄
>
> 判斷：每天會點 / 第一次進專案要看到 → 外部按鈕；偶爾用 / onboarding 後鮮少點 → 下拉。
> **衍生頁**（需參數才有意義）→ 兩邊都不掛。不確定就先放下拉（Picker 永遠有空間）。

> [!CAUTION]
> **外部按鈕只給 UCL_Core 內部的頁**。`UCL_EditorMenuPage` 屬 UCL_Core，作為跨專案 submodule **不能反向 reference 下游專案型別**（`using EOV.Foo` 直接編不過）。下游專案的非衍生頁 → **唯一合法掛法是 `ShowInPageMenu => true`**（Page Picker 走反射跨 assembly 掃子類）。

**衍生 vs 非衍生**：
- ❌ 衍生：`UCL_MarkdownViewerPage`（要 `Create(filePath)`）、列表項詳情頁、編輯特定 asset 的頁
- ✅ 非衍生：設定總覽、Skill 管理、文件搜尋、酒館、Welcome — 開了就有用

### 6.2 ⚠ assembly 邊界：跨組件開頁要走反射

> [!CAUTION]
> **血證（2026-07-28）**：`UCL_ChatTavernAdminPage`（在 `UCL_Core` 組件）想加一顆鈕開 `UCL_SecretManagerPage`
> （在 `UCL_CoreEditor` 組件）。但 `UCL_CoreEditor.asmdef` **references `UCL_Core`** → 反向直接引用 = **循環依賴**，編不過。
>
> 解法：以型別全名反射開頁，找不到就 log warning + 按鈕 no-op（下游專案沒裝該模組也不會編不過）：
>
> ```csharp
> static void OpenPageByReflection(string typeName)   // "UCL.Core.EditorLib.SecretManager.UCL_SecretManagerPage"
> {
>     foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
>     {
>         var t = asm.GetType(typeName);
>         if (t == null) continue;
>         t.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
>         return;
>     }
>     Debug.LogWarning($"[Page] 找不到 {typeName} — 本專案未含該模組？");
> }
> ```
>
> 加鈕前先確認兩邊 asmdef 的引用方向（`UCL_Core_Scripts/UCL_Core.asmdef` vs `Editor/UCL_CoreEditor.asmdef`）。

---

## 7. UI 繪製選什麼？

| 想做 | 工具 |
|---|---|
| Label / Button / TextField / Toggle | `GUILayout.*` + `UCL_GUIStyle.*Style` |
| 數字欄位 / 滑條 / 向量 / **折疊頭 ▼►** | `UCL_GUILayout.IntField` / `Slider` / `Vector3Field` / **`Toggle(dic, key, size, iDefaultValue)`** |
| 列表 / 字典 / HashSet 編輯 | `UCL_GUILayout.DrawList` / `DrawDictionary` / `DrawHashSet` |
| 任意物件遞迴繪製 | `UCL_GUILayout.DrawObjectData` |
| 下拉（無搜尋）| `UCL_GUILayout.PopupAuto` |
| **下拉（含搜尋 + 快取）** ⭐ | `UCL_GUILayout.PopupSearchCache(idx, options, dic, key)` |
| **下拉（分組）** | `UCL_GUILayout.PopupGrouped`（前綴自動摺疊）|
| 列舉下拉 | `UCL_GUILayout.Popup<T>(enum, dic)` |
| 互動畫板 | `UCL_GUILayout.DrawableTexture` / `UCL_GUILayoutPainter` |
| 複雜結構 Copy/Paste | `UCL_GUILayout.DrawCopyPaste(ref obj, ...)` |

詳見 [UCL_GUILayout 概覽](../API/UCL_GUILayout/UCL_GUILayout_Overview.md)。

### 7.1 自建 GUIStyle 的時機

需要的不是內建樣式（例如「18pt 粗體 + wordWrap + richText 的 Heading」）→ page 內 lazy 建一份**派生**樣式：

```csharp
GUIStyle m_HeadingStyle;
GUIStyle HeadingStyle => m_HeadingStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
{
    fontSize = 18, fontStyle = FontStyle.Bold, richText = true, wordWrap = true,
};
```

> [!CAUTION]
> 派生自 `LabelStyle` 的樣式**不能**拿去當 `Toggle` / `Button` 的 GUIStyle 參（會失去互動外觀）。見 [UCL_GUIStyle Overview §3](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md#3-反指守則從-labelstyle-重複出現的禁忌)。

### 7.2 狀態顯示要「誠實」

管理頁的核心價值是**讓不可見的狀態可見**。三條慣例：

- **意圖 vs 實效要分開顯示**。config 說 `enabled=true` 不代表真的在跑 → 分別顯示「設定開關」與「實際存活」，並在兩者不一致時標明原因（`⚠ config X=false → 仍不會運作`）
- **不確定就說不確定**。偵測不到別寫「未運行」，寫「未偵測到」+ 說明偵測範圍
- **錯誤要有 UI 出口**。daemon 的 `LastError` 一定要有地方顯示，否則就是靜默失敗

---

## 8. HelpURL 與多語系文件

```csharp
[HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_<Name>Page.md")]
```

`{lang}` 由 `UCL_GUILayout.DrawHelpButton` 解析時替換成當前語系（zh-Hant / en / ja / zh-Hans）。詳見 [HelpURL_Workflow](HelpURL_Workflow.md)。

**文件還沒寫也要先掛** — 指向不存在的 .md 不會 crash，且能讓文件搜尋索引到「應該存在但還沒寫」的位置。

> [!TIP]
> 若該頁只維護單一語系（canonical），`{lang}` 仍照掛，但別在別語系目錄放半成品翻譯 —— stale 翻譯比沒有翻譯更誤導（2026-07-28 實錄：兩份 workflow 的 en/ja/zh-Hans 版長期落後，最後整批刪除只留 zh-Hant）。

---

## 9. 常見地雷

| # | 地雷 | 症狀 | 解法 |
|---|---|---|---|
| 1 | `LabelStyle` 傳給 `GUILayout.Toggle` 第三參 | checkbox 消失、按了沒反應 | 純 checkbox 省略第三參；button-like 用 `ButtonStyle` |
| 2 | `ContentOnGUI()` 內又包 ScrollView | 雙重捲軸 | base 已包；要二級捲軸才自己開 |
| 3 | TextField 吃掉 Enter | 按 Enter 沒反應 | TextField 前 snapshot `Event.current`（見 `UCL_DocSearchPage.DrawSearchInput`）|
| 4 | 無 controller 時呼 `Create<T>()` | NullRef | 確保父頁 / EditorWindow 已建 controller |
| 5 | 樣式 lazy 建立沒 cache | 每幀 new GUIStyle，效能崩 | field + property lazy（§7.1）|
| 6 | rich-text label 混入 `<...>`（如 `List<T>`）| 字被當 tag 吃掉 | 關 `richText` 或轉義 `<` → `&lt;` |
| 7 | `[HelpURL]` 寫死語系 | 切語系跳錯檔 | 一律 `{lang}` 佔位 |
| 8 | EditorWindow.OnGUI 沒設 `IsInEditorWindow` | 樣式 cache 錯、DPI 異常 | 用 `IsInEditorWindowScope`（using 自動還原）|
| 9 | 直接用 `EditorGUILayout.Popup` | 沒搜尋、選項多找不到 | 改 `UCL_GUILayout.PopupSearchCache` |
| 10 | `GUILayout.Width(80)` 寫死數字 | 切 Big/XL Scale 被擠出 / 截斷 | **一律包 `UCL_GUIStyle.GetScaledSize(N)`**（含 MinWidth / MaxHeight / GetRect / fontSize）|
| **11** | **`FlexibleSpace()` 放在按鈕前面** | Label 一長、視窗一窄 → **按鈕跑出畫面點不到**（不會編譯錯、不會 log） | FlexibleSpace 一律**行尾**；長 Label 場景把按鈕放 Label **前面**（§4.1 L1/L2）|
| **12** | **折疊狀態與 PopupSearchCache 共用同一個 `UCL_ObjectDictionary`** | 按某個寫檔按鈕後折疊被強制展開、收不起來 | 拆成 `m_Dic`（popup）與 `m_FoldDic`（折疊），見 §5.1 血證 |
| 13 | C# 9 插值字串的洞裡換行（三元運算子排版）| `error CS8967: Newlines inside a non-verbatim interpolated string` | 先把三元算進區域變數，再插值 |

---

## 10. 驗收清單

- [ ] 繼承 `UCL_CommonEditorPage`、檔名與類別名一字不差
- [ ] override `WindowName`（非空）、`ContentOnGUI()`
- [ ] 沒把 `LabelStyle` 傳給互動控制項
- [ ] `[HelpURL("ucl_core:Docs~/{lang}/...")]` 帶 `{lang}`
- [ ] `static Create()` 存在且回傳子類型別
- [ ] 至少一個入口點能開到這頁；非衍生頁已掛 EditorMenu 外部按鈕 **或** `ShowInPageMenu => true`（§6.1）
- [ ] 內容切成 `Draw<X>Panel()`，每塊有折疊；**每頁最多 1~2 塊預設展開**（§4.4）
- [ ] **折疊外層控件只占一排**，且**按鈕靠左、`FlexibleSpace()` 在行尾**（§4.1）
- [ ] 折疊狀態存**專用** dictionary，不與 PopupSearchCache 共用（§5.1）
- [ ] 所有 `Width/Height` 包 `GetScaledSize`；**把視窗拉窄到一半，確認每顆按鈕仍點得到**
- [ ] 不在 OnGUI 內每幀讀檔（首幀 lazy-load + 節流刷新，§5.2）
- [ ] daemon / 外部狀態有 UI 出口顯示 `LastError`（§7.2）
- [ ] domain reload 後打開無 NullRef、Back / Close 正常
- [ ] **實際開頁 + 點過每一顆按鈕（含折疊收合兩種狀態）後 error log 沒新增 Error/Exception**
  - IMGUI 跑在 Editor 期，NullRef / 除零不會被編譯抓到 —— 編譯 0 error ≠ 頁面能用
  - 查 log：本專案走 `AgentCommands/Tools/debuglog_query.py errors`（或各專案自家 logger 慣例的 `Errors_latest.log`）
  - ⚠ 註：舊版本文件曾連結 `RuntimeError_Diagnose_Workflow.md`，該檔在本 repo 不存在（下游專案私有文件）→ 已改為描述做法而非死連結

---

## 11. 範例參考

| 頁面 | 看點 |
|---|---|
| `UCL_ControlPanelPage` | **本文 §4 折疊 + 關鍵操作提到外層的標準示範**；section 化、按鈕靠左單排 |
| `UCL_ChatTavernAdminPage` | 五區塊折疊 + `m_FoldDic`/`m_Dic` 分層（§5.1 血證來源）+ 動態發現清單（stream 增刪）+ core 項目擋刪 + 二段確認 |
| `UCL_ScreenStreamPage` | mtime 感知 reload + 欄位級 3-way merge（防外部併發改寫被蓋）+ 底圖疊比例框的視覺化預覽 |
| `UCL_MediaAdminPage` | 薄 UI + 重活全委派外部 CLI（async spawn 不凍結 Editor）+ 「套用後回讀驗證」 |
| `UCL_DocSearchPage` | 搜尋輸入列 Enter 觸發 + 折疊進階選項 + 結果列動作按鈕 |
| `UCL_MarkdownViewerPage` | 外部 `Create(args)` 載入 + `EnsureStyles()` 集中建樣式 + TopBarButtons 客製 |
| `UCL_WelcomePage` | 卡片 grid 排版 + 多入口集中地 |

---

## 12. 相關文件

- [UCL_GUILayout 概覽](../API/UCL_GUILayout/UCL_GUILayout_Overview.md) — IMGUI 元件層
- [UCL_GUIStyle 概覽](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md) — 樣式層（含尺寸縮放守則）
- [HelpURL_Workflow](HelpURL_Workflow.md) — `ucl_core:` prefix 解析
- [Hardcoded_Localize](Hardcoded_Localize.md) — 按鈕文字本地化（內部管理頁可硬編 zh-Hant，見該文件邊界）
- [Create_UCL_Asset_Workflow](Create_UCL_Asset_Workflow.md) — 持久化資料類型
- [Polymorphism_In_UCL](../Architecture/Polymorphism_In_UCL.md) — `[SerializeReference]` 多型
- [Edit_Recompile_Loop_Workflow](Edit_Recompile_Loop_Workflow.md) — 改 .cs 後的編譯期驗證
- [CompileError_Diagnose_Workflow](CompileError_Diagnose_Workflow.md) — 編譯期錯誤排查（含「intermediate 快照假綠」陷阱）
- 執行期錯誤：走 `debuglog_query.py errors` / 各專案自家 logger（UCL_Core 內無對應 workflow 文件）
