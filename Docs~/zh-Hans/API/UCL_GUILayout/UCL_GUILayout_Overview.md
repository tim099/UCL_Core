---
title: UCL_GUILayout 整体概览
description: UCL_Core 的 IMGUI 工具集（partial class UCL_GUILayout + 一个独立 UCL_GUILayoutPainter）— 公共 API 速查、文件分工、惯用模式，以及对下游页面最有价值的三个少见 helper
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

# UCL_GUILayout 整体概览

`UCL_GUILayout` 是 UCL_Core 在 Unity IMGUI 之上叠的一层 **静态工具集**，负责所有「自动化字段编辑 / 列表编辑 / 多态字段绘制 / 下拉菜单 / 交互绘图」。

它是一个由 8 个文件组成的 `partial class`（外加一个独立的 `UCL_GUILayoutPainter`），全部位于 `namespace UCL.Core.UI` 下。下游所有 Editor 页面（`UCL_CommonEditorPage` 系列、`UCL_AgentCommandsPage`、`RCG_*EditorPage`、新的 `UCL_DocSearchPage` / `UCL_MarkdownViewerPage` 等）都靠它做字段绘制。

---

## 1. 设计分层

```
                ┌──────────────────────────────┐
                │  使用端：UCL_*EditorPage /    │
                │          RCG_*EditorPage     │
                └──────────────┬───────────────┘
                               │ 调用 static API
                               ▼
   ┌─────────────────── partial class UCL_GUILayout ─────────────────┐
   │                                                                  │
   │  UCL_GUILayout.cs              基础字段：NumField/Slider/Toggle  │
   │  UCL_GUILayout.DrawList.cs     IList 编辑（含分页 / 多态 Add）    │
   │  UCL_GUILayout.DrawDictionary  IDictionary 编辑                  │
   │  UCL_GUILayout.DrawHashSet     HashSet（反射 Add/Remove）        │
   │  UCL_GUILayoutDrawObject.cs    任意对象递归绘制（中枢）           │
   │  UCL_GUILayoutPopup.cs         下拉菜单 / 枚举 / 颜色选择器       │
   │  UCL_GUILayoutDrawableTexture  交互式画布（鼠标绘制纹理）         │
   │                                                                  │
   └──────────────────────────────┬───────────────────────────────────┘
                                  │ 依赖
                                  ▼
       UCL_GUIStyle / UCL_ObjectDictionary / UCL_LocalizeManager /
       UCL_TypeReflectCache / UCL_PolymorphicHelper / Unity IMGUI

   独立类：
     UCL_GUILayoutPainter.cs       自包含画板（封装 DrawableTexture
                                   + SelectColor + Clear）
```

`DrawObjectData` 才是真正的中枢：判别对象类型后，路由到 `DrawList` / `DrawDictionary` / `DrawHashSet` / `DrawField`，再递归回自己。

---

## 2. 文件职责速查

| 文件 | 职责 | 主要对外 API |
|---|---|---|
| `UCL_GUILayout.cs` | 基础字段、Sprite/Texture 绘制、FolderExplorer | `NumField` / `IntField` / `FloatField` / `TextField` / `TextArea` / `Toggle` / `BoolField` / `CheckBox` / `Slider` / `Vector2/3Field` / `DrawSprite` / `DrawTexture` / `LabelAutoSize` / `ButtonAutoSize` / `FolderExplorer` |
| `UCL_GUILayout.DrawList.cs` | `IList`（含 1D/2D 数组）编辑，分页 + 多态 Add | `DrawList(IList, ...)`（4 个重载） |
| `UCL_GUILayout.DrawDictionary.cs` | `IDictionary` 编辑 | `DrawDictionary(IDictionary, ...)`（3 个重载） |
| `UCL_GUILayout.DrawHashSet.cs` | `HashSet` 编辑（反射调用 Add/Remove，可吃任何 IEnumerable + Add/Remove 的类型） | `DrawHashSet(object, DrawObjectParams)` |
| `UCL_GUILayoutDrawObject.cs` | 任意对象递归绘制、字段反射、`[SerializeReference]` 多态、`[Header]`、`DrawHelpButton` | `DrawObjectData` / `DrawField` / `DrawCopyPaste` / `DrawHelpButton` / `Preview.OnGUI` |
| `UCL_GUILayoutPopup.cs` | 下拉菜单（无 / 带搜索 / 缓存版）、枚举版、颜色选择器、页码控制 | `Popup` / `PopupAuto` / `PopupSearch` / `PopupSearchCache` / `Popup<T>(enum)` / `DrawSelectPage` / `SelectColor` / `ValueDropdown` |
| `UCL_GUILayoutDrawableTexture.cs` | 鼠标绘制交互纹理 + `GL_DrawLine` 等线段绘制 | `DrawableTexture` / `GetMousePosInGrid` / `DrawPolyLine` / `GL_DrawPolyLine` / `GL_DrawLine` / `DrawLine` |
| `UCL_GUILayoutPainter.cs`（独立类） | 完整画板 UI 容器（纹理 + 颜色 + Clear） | `Init` / `SetTexture` / `Clear` / `OnTextureUpdate` / `OnGUI` |

---

## 3. 公共 API 速查（按用途分组）

### 3.1 基础字段（`UCL_GUILayout.cs`）

| API | 用途 |
|---|---|
| `NumField<T>(label, value, minWidth)` | 泛型数字字段，支持 int/float/double，键盘过滤非数字 |
| `IntField(label, value, ...)` / `FloatField(label, value, minWidth)` | 类型专用版本 |
| `IntFieldAuto(value, dic, ...)` ⭐ | 整数字段，**外部值改变时自动清缓存**（避免显示过期值） |
| `TextField(label, value, ...)` / `TextArea(label, value)` | 单行 / 多行文本 |
| `Toggle(value, size)` | 显示 `▼` / `►`（折叠图标语意） |
| `Toggle(dic, key, ...)` | 同上但用 `UCL_ObjectDictionary` 持有状态 |
| `BoolField(value, size)` / `BoolField(dic, key, size, default)` | 显示 `✔` / 空 |
| `CheckBox(value, size)` | 标准 checkbox（无 label） |
| `CheckBox(value, label, size, labelSize)` | checkbox + 右侧 label，盒与字均吃 DPI `Scale`（替代原生 `GUILayout.Toggle` 在 hi-DPI 下小到看不清的问题） |
| `Slider(label, value, min, max, dic)` | 滑条 + 数字输入 + 同步 |
| `Vector2Field` / `Vector3Field` / `VectorField` | 向量分量编辑（含 IntVec 变体） |
| `DrawSprite(sprite, ...)` / `DrawTexture(tex, ...)` / `GraphicsDrawTexture(...)` | 绘图（普通版 / Graphics.DrawTexture 支持自定义 Material） |
| `LabelAutoSize(name, fontSize, color)` / `ButtonAutoSize(name, fontSize, ...)` | 自适应宽度 |
| ~~`Label(name, Color color)`~~ | **已废弃** — 改用 `GUILayout.Label(text, UCL_GUIStyle.GetLabelStyle(color))` |
| `FolderExplorer(dic, path, ...)` | 路径导航 + 文件筛选 UI |

### 3.2 集合编辑（`DrawList` / `DrawDictionary` / `DrawHashSet`）

| API | 用途 |
|---|---|
| `DrawList(IList, dic, name, alwaysShowDetail)` | 列表编辑：折叠头 + 自动分页（10 / 页） + Copy/Paste + 多态 Add（若 element type 实现 `UCLI_TypeListable`） |
| `DrawList(IList, DrawObjectParams)` | 参数化版（传递 fieldNameFunc / overrideDrawElement） |
| `DrawDictionary(IDictionary, dataDic, name, alwaysShowDetail, fieldNameFunc)` | 字典编辑，键与值各自递归绘制 |
| `DrawHashSet(object, DrawObjectParams)` | 反射版集合编辑（不限 HashSet，凡有 Add/Remove method 即可） |

> **共通行为**：分页限制 = `MaxItemsPerPage = 10`；移动／删除模式可选；标题列含 Copy/Paste；rank 1/2 数组皆支持。

### 3.3 对象递归绘制（`UCL_GUILayoutDrawObject.cs`）

| API | 用途 |
|---|---|
| `DrawObjectData(obj, dic, displayName, alwaysShowDetail, fieldNameFunc, fieldType, exSetting)` | **中枢**：自动判别 `EObjectType`（String / Bool / Enum / Number / IList / IDictionary / Color / Vector / Component / Struct…）并路由 |
| `DrawObjectData(target, DrawObjectParams)` | 参数化版 |
| `DrawField(obj, dic, displayName, ...)` | 反射展开所有字段（递归调用自己） |
| `DrawCopyPaste(ref obj, dic, fieldType)` | Copy/Paste 按钮组（JSON 序列化），返回 `true` 表示粘贴成功 |
| `DrawHelpButton(url)` | 对应 `[HelpURLAttribute]` 的「?」按钮，点下打开 URL（已被 `UCL_EditorPage.TopBar` 使用） |
| `Preview.OnGUI(name, target, dic, space)` | 只读预览（递归显示但不可编辑） |

支持的属性绘制扩展：`[Header]`（自动本地化）、`[SerializeReference]` 多态、`IShowInCondition`（条件显示）、`IStrList`（字符串下拉）、`IValueDropdown`、`ITexture2D`、`UCL_FolderExplorerAttribute`、`UCL_IntSliderAttribute`、`UCL_SliderAttribute`。

### 3.4 下拉菜单与分页（`UCL_GUILayoutPopup.cs`）

| API | 用途 |
|---|---|
| `Popup(selectedIndex, options, dic, key, ...)` | 基础下拉（开／关状态存在 `dic[key]`） |
| `Popup(selectedIndex, options, ref bool opened, ...)` | 手动 ref bool 版本 |
| `PopupAuto(selectedIndex, options, dic, key, searchThreshold, ...)` ⭐ | item 数 ≥ `searchThreshold` 时自动加搜索栏；常用简化入口 |
| `PopupSearch(selectedIndex, options, dic, key, ...)` | 一律带搜索栏（Regex，命中字红标） |
| `PopupSearchCache(index, displayOptions, dic, key, ...)` ⭐ | 额外缓存 Regex 与筛选结果，**100+ 项目反复操作的性能版** |
| `Popup<T>(enumValue, dic, getNameFunc, ...)` | 枚举专用，内置 `UCL_LocalizeLib.GetEnumLocalize(...)` 本地化 |
| `PopupAuto<T>(enumValue, dic, [key], searchThreshold, ...)` | 枚举 + 自动搜索触发 |
| `DrawSelectPage(dic, itemsCount, maxItemsPerPage)` | 翻页栏（`|<` `<` `>` `>|` + 直接输入页码），返回 `(pageIndex, startIndex)` |
| `SelectColor(initialColor)` | 颜色选择器（预设色板 + RGBA 滑条） |
| `ValueDropdown(selectedIndex, options, dic, key, ...)` | 类似 PopupSearch + 额外缓存选项哈希 |

### 3.5 交互绘图

| API | 用途 |
|---|---|
| `DrawableTexture(texture2D, dic, w, h, drawColor)` | 鼠标绘制纹理（自动处理跨 Drag 边界、补插值） |
| `GetMousePosInGrid(rect, w, h)` | 取得鼠标在网格内格子坐标（超界返回 `Vector2Int.left`） |
| `DrawPolyLine` / `GL_DrawPolyLine` / `GL_DrawLine` / `DrawLine` | 折线 / 旋转线段 |
| `UCL_GUILayoutPainter.OnGUI()` | 完整画板（画布 + 颜色选择 + Clear） |

---

## 4. 跨文件共通模式

| 模式 | 说明 |
|---|---|
| **状态管理** | 全部走 `UCL_ObjectDictionary`（键值字典），避免每帧重新初始化（如缓存 Regex / TypeList / 折叠状态） |
| **三段式重载** | 同名 API 通常有：(1) **无状态**（传值）、(2) **有状态**（`dic + key`）、(3) **参数化**（`DrawObjectParams`） — 从轻到重按需求挑 |
| **统一样式** | 一律走 `UCL_GUIStyle`，自动 DPI 缩放（`GetScaledSize()`）；**禁止**把 `UCL_GUIStyle.LabelStyle` 传给 Toggle/Button/TextField 第三参（会破坏交互视觉，见 `UCL_GUIStyle.LabelStyle` XML 注释） |
| **多态自动检测** | `[SerializeReference]` 字段由 `UCL_PolymorphicHelper.GetConcreteSubtypes()` 列举子类型，UI 用下拉菜单切换实例 — 写 data class 套上 `UCLI_TypeListable` + `[SerializeReference]` 即免手写 UI |
| **反射缓存** | `TypeFieldInfoCache` 共用 `UCL_TypeReflectCache`，一次解析全页面共享；ctor **不可** 触碰 service（见 `Polymorphism_In_UCL.md`） |
| **分页限制** | 大集合自动切 `MaxItemsPerPage = 10`；搜索下拉切 20；wrap-around 翻页 |
| **Copy/Paste 内置** | List / Dict / HashSet 标题列已内置；任意对象可调用 `DrawCopyPaste(ref obj, ...)` |
| **返回值语意** | struct 按值返回；class 修改后仍返回原引用；`IList` / `IDictionary` 直接 in-place 修改不返回 |

---

## 5. 三个值得记住的少见 helper

下游页面通常只会用到 `DrawObjectData` / `DrawList` / 基本字段，下面三个是**真正会省事**但容易被忽略的：

### 5.1 `IntFieldAuto(value, dic, ...)`
**何时用**：字段显示的值来自外部数据（可能被别处改写），需要追踪旧值并在差异发生时自动清掉内部编辑缓存。
```csharp
int count = UCL_GUILayout.IntFieldAuto(list.Count, m_DataDic);
// 若外部 list.Count 被改写，下一轮 OnGUI 时编辑缓存自动清除
```
**对比**：`IntField` 不会侦测外部值变动，编辑中时可能盖掉新数据。

### 5.2 `PopupSearchCache(index, options, dic, key, ...)`
**何时用**：选项数 100+、且用户会反复过滤。普通 `PopupSearch` 每帧重编译 Regex 与重做 LINQ Where；`Cache` 版会把 Regex、命中索引集合都缓存在 `dic` 内，只在 query 变动时重算。
```csharp
int sel = UCL_GUILayout.PopupSearchCache(curIdx, allCardIds, m_DataDic, "CardPicker");
```
**估算**：~500 项目时 GUI 响应差距明显。

### 5.3 `DrawCopyPaste(ref obj, dic, fieldType)`
**何时用**：复杂 nested struct 想跨字段／跨页面复制粘贴，又不想手写 JSON serialize。
```csharp
object o = config;
if (UCL_GUILayout.DrawCopyPaste(ref o, m_DataDic, typeof(GameConfig)))
{
    config = (GameConfig)o; // 粘贴成功，o 已替换
}
```
**机制**：底层走 `UCL.Core.CopyPaste` + JSON；类型不符会被挡下。

---

## 6. 与其他文档的关联

- **多态字段机制**：见 [Architecture/Polymorphism_In_UCL.md](../../Architecture/Polymorphism_In_UCL.md)（解释 `[SerializeReference]` × `UCLI_TypeListable` × `UCL_PolymorphicHelper` × `UCL_TypeReflectCache` 在 GUI 与序列化的角色）
- **HelpURL 机制**：见 [Workflows/HelpURL_Workflow.md](../../Workflows/HelpURL_Workflow.md)（`DrawHelpButton` 解析的 `ucl_core:` / `eov_docs:` prefix）
- **样式中央**：见 `UCL_GUIStyle.cs` XML 注释（特别是 `LabelStyle` 的禁止 Toggle/Button 警示）

---

## 7. 何时**不要**用 UCL_GUILayout

- 纯文本 Label / Button / 简单 Layout — 直接用 Unity `GUILayout`，不必绕 UCL（除非要色彩 / 自动字号）。
- Runtime UGUI / UI Toolkit — 本工具集是 IMGUI（编辑器期间 + 部分 runtime debug overlay），不对应 UGUI。
- 高频每帧重绘百万字段 — IMGUI 本身吃不消，这层更不该硬扛；该换 UI Toolkit + VisualElement。
