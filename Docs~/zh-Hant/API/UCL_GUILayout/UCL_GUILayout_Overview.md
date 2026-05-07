---
title: UCL_GUILayout 整體概覽
description: UCL_Core 的 IMGUI 工具集（partial class UCL_GUILayout + 一個獨立 UCL_GUILayoutPainter）— 公共 API 速查、檔案分工、慣用模式、以及對下游頁面最有價值的三個少見 helper
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

# UCL_GUILayout 整體概覽

`UCL_GUILayout` 是 UCL_Core 在 Unity IMGUI 之上疊的一層 **靜態工具集**，負責所有「自動化欄位編輯 / 列表編輯 / 多型欄位繪製 / 下拉選單 / 互動繪圖」。

它是一個由 8 個檔案組成的 `partial class`（外加一個獨立的 `UCL_GUILayoutPainter`），全部在 `namespace UCL.Core.UI` 下。下游所有 Editor 頁面（`UCL_CommonEditorPage` 系列、`UCL_AgentCommandsPage`、`RCG_*EditorPage`、新的 `UCL_DocSearchPage` / `UCL_MarkdownViewerPage` 等）都靠它做欄位繪製。

---

## 1. 設計分層

```
                ┌──────────────────────────────┐
                │  使用端：UCL_*EditorPage /    │
                │          RCG_*EditorPage     │
                └──────────────┬───────────────┘
                               │ 呼叫 static API
                               ▼
   ┌─────────────────── partial class UCL_GUILayout ─────────────────┐
   │                                                                  │
   │  UCL_GUILayout.cs              基礎欄位：NumField/Slider/Toggle  │
   │  UCL_GUILayout.DrawList.cs     IList 編輯（含分頁 / 多型 Add）    │
   │  UCL_GUILayout.DrawDictionary  IDictionary 編輯                  │
   │  UCL_GUILayout.DrawHashSet     HashSet（反射 Add/Remove）        │
   │  UCL_GUILayoutDrawObject.cs    任意物件遞迴繪製（中樞）           │
   │  UCL_GUILayoutPopup.cs         下拉選單 / 列舉 / 顏色選擇器       │
   │  UCL_GUILayoutDrawableTexture  互動式畫布（滑鼠繪製紋理）         │
   │                                                                  │
   └──────────────────────────────┬───────────────────────────────────┘
                                  │ 依賴
                                  ▼
       UCL_GUIStyle / UCL_ObjectDictionary / UCL_LocalizeManager /
       UCL_TypeReflectCache / UCL_PolymorphicHelper / Unity IMGUI

   獨立類別：
     UCL_GUILayoutPainter.cs       自包含畫板（封裝 DrawableTexture
                                   + SelectColor + Clear）
```

`DrawObjectData` 是真正的中樞：判別物件型別後，路由到 `DrawList` / `DrawDictionary` / `DrawHashSet` / `DrawField`，再遞迴回自己。

---

## 2. 檔案職責速查

| 檔案 | 職責 | 主要對外 API |
|---|---|---|
| `UCL_GUILayout.cs` | 基礎欄位、Sprite/Texture 繪製、FolderExplorer | `NumField` / `IntField` / `FloatField` / `TextField` / `TextArea` / `Toggle` / `BoolField` / `CheckBox` / `Slider` / `Vector2/3Field` / `DrawSprite` / `DrawTexture` / `LabelAutoSize` / `ButtonAutoSize` / `Label(name, Color)` / `FolderExplorer` |
| `UCL_GUILayout.DrawList.cs` | `IList`（含 1D/2D 陣列）編輯，分頁 + 多型 Add | `DrawList(IList, ...)`（4 個多載） |
| `UCL_GUILayout.DrawDictionary.cs` | `IDictionary` 編輯 | `DrawDictionary(IDictionary, ...)`（3 個多載） |
| `UCL_GUILayout.DrawHashSet.cs` | `HashSet` 編輯（反射呼叫 Add/Remove，可吃任何 IEnumerable + Add/Remove 的型別） | `DrawHashSet(object, DrawObjectParams)` |
| `UCL_GUILayoutDrawObject.cs` | 任意物件遞迴繪製、欄位反射、`[SerializeReference]` 多型、`[Header]`、`DrawHelpButton` | `DrawObjectData` / `DrawField` / `DrawCopyPaste` / `DrawHelpButton` / `Preview.OnGUI` |
| `UCL_GUILayoutPopup.cs` | 下拉選單（無/有搜尋/快取版）、列舉版、顏色選擇器、頁碼控制 | `Popup` / `PopupAuto` / `PopupSearch` / `PopupSearchCache` / `Popup<T>(enum)` / `DrawSelectPage` / `SelectColor` / `ValueDropdown` |
| `UCL_GUILayoutDrawableTexture.cs` | 滑鼠繪製互動紋理 + `GL_DrawLine` 等線段繪製 | `DrawableTexture` / `GetMousePosInGrid` / `DrawPolyLine` / `GL_DrawPolyLine` / `GL_DrawLine` / `DrawLine` |
| `UCL_GUILayoutPainter.cs`（獨立類） | 完整畫板 UI 容器（紋理 + 顏色 + Clear） | `Init` / `SetTexture` / `Clear` / `OnTextureUpdate` / `OnGUI` |

---

## 3. 公共 API 速查（按用途分組）

### 3.1 基礎欄位（`UCL_GUILayout.cs`）

| API | 用途 |
|---|---|
| `NumField<T>(label, value, minWidth)` | 泛型數字欄位，支援 int/float/double，鍵盤過濾非數字 |
| `IntField(label, value, ...)` / `FloatField(label, value, minWidth)` | 型別專用版本 |
| `IntFieldAuto(value, dic, ...)` ⭐ | 整數欄位，**外部值改變時自動清快取**（避免顯示過期值） |
| `TextField(label, value, ...)` / `TextArea(label, value)` | 單行 / 多行文字 |
| `Toggle(value, size)` | 顯示 `▼` / `►`（折疊圖示語意） |
| `Toggle(dic, key, ...)` | 同上但用 `UCL_ObjectDictionary` 持有狀態 |
| `BoolField(value, size)` / `BoolField(dic, key, size, default)` | 顯示 `✔` / 空 |
| `CheckBox(value, size)` | 標準 checkbox（無 label） |
| `CheckBox(value, label, size, labelSize)` | checkbox + 右側 label，盒與字皆吃 DPI `Scale`（取代原生 `GUILayout.Toggle` 在 hi-DPI 下小到看不清的問題） |
| `Slider(label, value, min, max, dic)` | 滑條 + 數字輸入 + 同步 |
| `Vector2Field` / `Vector3Field` / `VectorField` | 向量分量編輯（含 IntVec 變體） |
| `DrawSprite(sprite, ...)` / `DrawTexture(tex, ...)` / `GraphicsDrawTexture(...)` | 繪圖（普通版 / Graphics.DrawTexture 支援自訂 Material） |
| `LabelAutoSize(name, fontSize, color)` / `ButtonAutoSize(name, fontSize, ...)` | 自適應寬度 |
| `Label(name, Color color)` | 一行帶色 Label（不必走 rich text） |
| `FolderExplorer(dic, path, ...)` | 路徑導航 + 檔案篩選 UI |

### 3.2 集合編輯（`DrawList` / `DrawDictionary` / `DrawHashSet`）

| API | 用途 |
|---|---|
| `DrawList(IList, dic, name, alwaysShowDetail)` | 列表編輯：折疊頭 + 自動分頁（10 / 頁） + Copy/Paste + 多型 Add（若 element type 實作 `UCLI_TypeListable`） |
| `DrawList(IList, DrawObjectParams)` | 參數化版（傳遞 fieldNameFunc / overrideDrawElement） |
| `DrawDictionary(IDictionary, dataDic, name, alwaysShowDetail, fieldNameFunc)` | 字典編輯，鍵與值各自遞迴繪製 |
| `DrawHashSet(object, DrawObjectParams)` | 反射版集合編輯（不限 HashSet，凡有 Add/Remove method 即可） |

> **共通行為**：分頁限制 = `MaxItemsPerPage = 10`；移動／刪除模式可選；標題列含 Copy/Paste；rank 1/2 陣列皆支援。

### 3.3 物件遞迴繪製（`UCL_GUILayoutDrawObject.cs`）

| API | 用途 |
|---|---|
| `DrawObjectData(obj, dic, displayName, alwaysShowDetail, fieldNameFunc, fieldType, exSetting)` | **中樞**：自動判別 `EObjectType`（String / Bool / Enum / Number / IList / IDictionary / Color / Vector / Component / Struct…）並路由 |
| `DrawObjectData(target, DrawObjectParams)` | 參數化版 |
| `DrawField(obj, dic, displayName, ...)` | 反射展開所有欄位（遞迴呼叫自己） |
| `DrawCopyPaste(ref obj, dic, fieldType)` | Copy/Paste 按鈕組（JSON 序列化），回傳 `true` 表示貼上成功 |
| `DrawHelpButton(url)` | 對應 `[HelpURLAttribute]` 的「?」按鈕，點下開啟 URL（已被 `UCL_EditorPage.TopBar` 使用） |
| `Preview.OnGUI(name, target, dic, space)` | 唯讀預覽（遞迴顯示但不可編輯） |

支援的屬性繪製擴展：`[Header]`（自動在地化）、`[SerializeReference]` 多型、`IShowInCondition`（條件顯示）、`IStrList`（字串下拉）、`IValueDropdown`、`ITexture2D`、`UCL_FolderExplorerAttribute`、`UCL_IntSliderAttribute`、`UCL_SliderAttribute`。

### 3.4 下拉選單與分頁（`UCL_GUILayoutPopup.cs`）

| API | 用途 |
|---|---|
| `Popup(selectedIndex, options, dic, key, ...)` | 基礎下拉（開／關狀態存在 `dic[key]`） |
| `Popup(selectedIndex, options, ref bool opened, ...)` | 手動 ref bool 版本 |
| `PopupAuto(selectedIndex, options, dic, key, searchThreshold, ...)` ⭐ | item 數 ≥ `searchThreshold` 時自動加搜尋欄；常用簡化入口 |
| `PopupSearch(selectedIndex, options, dic, key, ...)` | 一律帶搜尋欄（Regex，命中字紅標） |
| `PopupSearchCache(index, displayOptions, dic, key, ...)` ⭐ | 額外快取 Regex 與篩選結果，**100+ 項目反覆操作的性能版** |
| `Popup<T>(enumValue, dic, getNameFunc, ...)` | 列舉專用，內建 `UCL_LocalizeLib.GetEnumLocalize(...)` 在地化 |
| `PopupAuto<T>(enumValue, dic, [key], searchThreshold, ...)` | 列舉 + 自動搜尋觸發 |
| `DrawSelectPage(dic, itemsCount, maxItemsPerPage)` | 翻頁列（`|<` `<` `>` `>|` + 直接輸入頁碼），回傳 `(pageIndex, startIndex)` |
| `SelectColor(initialColor)` | 顏色選擇器（預設色板 + RGBA 滑條） |
| `ValueDropdown(selectedIndex, options, dic, key, ...)` | 類似 PopupSearch + 額外快取選項雜湊 |

### 3.5 互動繪圖

| API | 用途 |
|---|---|
| `DrawableTexture(texture2D, dic, w, h, drawColor)` | 滑鼠繪製紋理（自動處理跨 Drag 邊界、補插值） |
| `GetMousePosInGrid(rect, w, h)` | 取得滑鼠在網格內格子座標（超界回 `Vector2Int.left`） |
| `DrawPolyLine` / `GL_DrawPolyLine` / `GL_DrawLine` / `DrawLine` | 折線 / 旋轉線段 |
| `UCL_GUILayoutPainter.OnGUI()` | 完整畫板（畫布 + 顏色選擇 + Clear） |

---

## 4. 跨檔共通模式

| 模式 | 說明 |
|---|---|
| **狀態管理** | 全部走 `UCL_ObjectDictionary`（鍵值字典），避免每幀重新初始化（如快取 Regex / TypeList / 折疊狀態） |
| **三段式多載** | 同名 API 通常有：(1) **無狀態**（傳值）、(2) **有狀態**（`dic + key`）、(3) **參數化**（`DrawObjectParams`） — 從輕到重依需求挑 |
| **統一樣式** | 一律走 `UCL_GUIStyle`，自動 DPI 縮放（`GetScaledSize()`）；**禁止**把 `UCL_GUIStyle.LabelStyle` 傳給 Toggle/Button/TextField 第三參（會破壞互動視覺，見 `UCL_GUIStyle.LabelStyle` XML 註解） |
| **多型自動偵測** | `[SerializeReference]` 欄位由 `UCL_PolymorphicHelper.GetConcreteSubtypes()` 列舉子型別，UI 用下拉選單切換實例 — 寫 data class 套上 `UCLI_TypeListable` + `[SerializeReference]` 即免手寫 UI |
| **反射快取** | `TypeFieldInfoCache` 共用 `UCL_TypeReflectCache`，一次解析全頁面共享；ctor **不可** 觸碰 service（見 `Polymorphism_In_UCL.md`） |
| **分頁限制** | 大集合自動切 `MaxItemsPerPage = 10`；搜尋下拉切 20；wrap-around 翻頁 |
| **Copy/Paste 內建** | List / Dict / HashSet 標題列已內建；任意物件可呼叫 `DrawCopyPaste(ref obj, ...)` |
| **回傳語意** | struct 按值回傳；class 修改後仍回原參考；`IList` / `IDictionary` 直接 in-place 修改不回傳 |

---

## 5. 三個值得記住的少見 helper

下游頁面通常只會用到 `DrawObjectData` / `DrawList` / 基本欄位，下面三個是**真正會省事**但容易被忽略的：

### 5.1 `IntFieldAuto(value, dic, ...)`
**何時用**：欄位顯示的值來自外部資料（可能被別處改寫），需要追蹤舊值並在差異發生時自動清掉內部編輯快取。
```csharp
int count = UCL_GUILayout.IntFieldAuto(list.Count, m_DataDic);
// 若外部 list.Count 被改寫，下一輪 OnGUI 時編輯快取自動清除
```
**對比**：`IntField` 不會偵測外部值變動，編輯中時可能蓋掉新資料。

### 5.2 `PopupSearchCache(index, options, dic, key, ...)`
**何時用**：選項數 100+、且使用者會反覆過濾。普通 `PopupSearch` 每幀重編譯 Regex 與重做 LINQ Where；`Cache` 版會把 Regex、命中索引集合都快取在 `dic` 內，只在 query 變動時重算。
```csharp
int sel = UCL_GUILayout.PopupSearchCache(curIdx, allCardIds, m_DataDic, "CardPicker");
```
**估算**：~500 項目時 GUI 響應差距明顯。

### 5.3 `DrawCopyPaste(ref obj, dic, fieldType)`
**何時用**：複雜 nested struct 想跨欄位/跨頁面複製貼上，又不想手寫 JSON serialize。
```csharp
object o = config;
if (UCL_GUILayout.DrawCopyPaste(ref o, m_DataDic, typeof(GameConfig)))
{
    config = (GameConfig)o; // 貼上成功，o 已替換
}
```
**機制**：底層走 `UCL.Core.CopyPaste` + JSON；型別不符會被擋下。

---

## 6. 與其他文件的關聯

- **多型欄位機制**：見 [Architecture/Polymorphism_In_UCL.md](../../Architecture/Polymorphism_In_UCL.md)（解釋 `[SerializeReference]` × `UCLI_TypeListable` × `UCL_PolymorphicHelper` × `UCL_TypeReflectCache` 在 GUI 與序列化的角色）
- **HelpURL 機制**：見 [Workflows/HelpURL_Workflow.md](../../Workflows/HelpURL_Workflow.md)（`DrawHelpButton` 解析的 `ucl_core:` / `eov_docs:` prefix）
- **樣式中央**：見 `UCL_GUIStyle.cs` XML 註解（特別是 `LabelStyle` 的禁止 Toggle/Button 警示）

---

## 7. 何時**不要**用 UCL_GUILayout

- 純文字 Label / Button / 簡單 Layout — 直接用 Unity `GUILayout`，不必繞 UCL（除非要色彩 / 自動字級）。
- Runtime UGUI / UI Toolkit — 本工具集是 IMGUI（編輯器期間 + 部分 runtime debug overlay），不對應 UGUI。
- 高頻每幀重繪百萬欄位 — IMGUI 本身吃不消，這層更不該硬扛；該換 UI Toolkit + VisualElement。
