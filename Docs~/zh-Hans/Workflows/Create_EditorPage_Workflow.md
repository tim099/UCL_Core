---
title: 创建新的 UCL_CommonEditorPage 子类工作流程
description: 步骤化 SOP — 从零开出一页可被 GUIPageController 推送的 Editor 页面。涵盖继承关系、必/选 override、TopBar 定制、入口点挂接、样式选用守则（链接 UCL_GUILayout / UCL_GUIStyle 文档）、与常见地雷。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [Create EditorPage, UCL_CommonEditorPage workflow, 寫新 editor 頁]
tags: [workflow, editor, ui, imgui]
---

# 🛠️ 创建新的 UCL_CommonEditorPage 子类工作流程

> [!IMPORTANT]
> 本工作流程只负责「**写一页继承 `UCL_CommonEditorPage` 的 Editor 页**」这件事；UI 组件实现（字段 / 列表 / 下拉等）请看 [UCL_GUILayout 整体概览](../API/UCL_GUILayout/UCL_GUILayout_Overview.md)，样式取用请看 [UCL_GUIStyle 概览](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md)。
>
> 设计哲学：**继承 + override hook**。基底已经处理 TopBar / Back / Close / ScrollView / HelpURL 解析；子类只负责填 `WindowName` 与 `ContentOnGUI()`，要定制就 override hook。

---

## 0. TL;DR — 三分钟加完一页

```
[1] 想清楚这页职责（单一焦点，一页一事）
       ▼
[2] 建档 UCL_<Name>Page.cs : UCL_CommonEditorPage
       ▼
[3] override WindowName 与 ContentOnGUI()
       ▼
[4] 加 [HelpURL] 指向你打算写的文档路径（即使文件还没写）
       ▼
[5] 提供 static Create()（Push 到 GUIPageController）
       ▼
[6] 在父页 / 菜单 / WelcomePage 加按钮 → Create()
```

---

## 1. 继承关系

```
UCL_GUIPage (UICore)
  └── UCL_EditorPage (EditorMenuPages)        ← 提供 TopBar / Back / Close / HelpURL 解析
        └── UCL_CommonEditorPage              ← 在 TopBar 多显示 ClassName + Copy 按钮
              └── UCL_<Name>Page              ← 你要写的这页
```

| 类别 | 职责 |
|---|---|
| `UCL_GUIPage` | `WindowName` / `IsWindow` / `OnGUI()` 最外层流程 |
| `UCL_EditorPage` | TopBar（Back / Close / Help）、ContentOnGUI ScrollView 包装、HelpURL 反射缓存、`Create<T>()` 工厂 |
| `UCL_CommonEditorPage` | 在 TopBar 显示「TypeName + Copy」帮助调试与支持文档对齐 |

---

## 2. 必须 override 的成员

| 成员 | 类型 | 必要性 | 说明 |
|---|---|---|---|
| `WindowName` | `string` | **必填** | 视窗标题；多视窗切换时 `UCL_GUIPageController.WindowName` 会吃这个 |
| `ContentOnGUI()` | `void` | **必填** | 主要内容绘制（ScrollView 由 base 包好，这里专心画东西） |

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
            // 这里开始画；ScrollView 已被 base.OnGUI 包好
            GUILayout.Label("Hello", UCL_GUIStyle.LabelStyle);
        }
    }
}
#endif
```

> [!CAUTION]
> `Create<T>()` 是 **public static**，但要透过 `UCL_EditorPage.Create<T>()` 调用 — 它会把 page push 到 `UCL_GUIPageController.CurrentRenderIns`，所以**调用前必须有一个 controller 在跑**（通常是父页的 OnGUI 或 EditorWindow 已经建好）。

---

## 3. 可选 override 的 hook

| 成员 | 类型 | 默认行为 | 何时 override |
|---|---|---|---|
| `TopBarButtons()` | `void` | 显示 ClassName + Copy 按钮（CommonEditorPage 提供） | 想加入「重新整理 / 切换语系 / 开关侧栏」等顶列工具按钮 |
| `ShowCloseButton` | `bool` | `true` | 不希望用户一键关掉所有页时设 `false` |
| `ShowBackButton` | `bool` | `!ShowCloseButton || pages.Count > 1` | 自定义导览流程时 |
| `BackButtonClicked()` | `void` | `p_Controller.Pop()` | 返回前要存档、要弹确认框 |
| `CloseButtonClicked()` | `void` | `p_Controller.PopAll()` | 同上 |
| `Init(controller)` | `void` | 调用 base + 记录 `m_TypeName` | 想做一次性初始化、订阅事件 |

### 3.1 TopBarButtons 范例

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

实例见 `UCL_MarkdownViewerPage.TopBarButtons()`：在标准两颗之外又加 Reveal / OS Open / Copy raw 三颗。

---

## 4. 入口点挂接（这页怎么被打开？）

四种常见入口：

| 入口 | 范例 | 适用 |
|---|---|---|
| **父页按钮** | `UCL_DocSearchPage.DrawResultRow` 的 `📄` 按钮 → `UCL_MarkdownViewerPage.Create(...)` | 跟既有页有明确上下文关系 |
| **WelcomePage 卡片** | `UCL_WelcomePage` 的「🔍 文档搜索」按钮 → `UCL_DocSearchPage.Create()` | 全局功能 / 需要显眼 |
| **`UCL → ...` 菜单** | `[MenuItem("UCL/<Name>")]` 开 EditorWindow，`OnGUI` 内推 page | 独立工具，不依附其他页 |
| **HelpURL deep link** | `ucl_core:Docs~/...` 从文档按钮跳回 page | 通常是反向 — 从 page 到文档 |

> [!TIP]
> 为新页加入口时用「**最少耦合**」原则：能放父页就不开菜单、能加 WelcomePage 卡片就不要散落在多处菜单。

---

## 5. UI 绘制选什么？

`ContentOnGUI()` 内画 UI 时，按复杂度往下找：

| 想做 | 工具 | 出处 |
|---|---|---|
| Label / Button / TextField / Toggle 等基本 | `GUILayout.*` + `UCL_GUIStyle.*Style` | [UCL_GUIStyle Overview](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md) |
| 数字字段 / 滑条 / 向量 / 折叠头 ▼/► | `UCL_GUILayout.IntField` / `Slider` / `Vector3Field` / `Toggle(bool, size)` | [UCL_GUILayout Overview §3.1](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#31-基礎欄位ucl_guilayoutcs) |
| 列表 / 字典 / HashSet 编辑（含分页、多态 Add） | `UCL_GUILayout.DrawList` / `DrawDictionary` / `DrawHashSet` | [UCL_GUILayout Overview §3.2](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#32-集合編輯drawlist--drawdictionary--drawhashset) |
| 任意对象递归绘制（反射字段、`[SerializeReference]` 多态） | `UCL_GUILayout.DrawObjectData` | [UCL_GUILayout Overview §3.3](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#33-物件遞迴繪製ucl_guilayoutdrawobjectcs) |
| 下拉菜单（含搜索 / 枚举） | `UCL_GUILayout.PopupAuto` / `Popup<T>(enum)` | [UCL_GUILayout Overview §3.4](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#34-下拉選單與分頁ucl_guilayoutpopupcs) |
| 交互画板 | `UCL_GUILayout.DrawableTexture` / `UCL_GUILayoutPainter` | [UCL_GUILayout Overview §3.5](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#35-互動繪圖) |
| 复杂结构 Copy/Paste | `UCL_GUILayout.DrawCopyPaste(ref obj, ...)` | [UCL_GUILayout Overview §5.3](../API/UCL_GUILayout/UCL_GUILayout_Overview.md#53-drawcopypasteref-obj-dic-fieldtype) |

### 5.1 自建 GUIStyle 的时机

如果你需要的不是上面任何一个内置样式（例如「16pt 粗体 + wordWrap + richText 的 Heading 样式」），就在 page 内 lazy 建一份**派生**自既有样式：

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
> 这份 `m_HeadingStyle` 是纯显示样式（从 LabelStyle 派生），同样**不能**拿去当 `Toggle` / `Button` 的第三 GUIStyle 参。详见 [UCL_GUIStyle Overview §3](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md#3-反指守則從-labelstyle-重複出現的禁忌)。

---

## 6. HelpURL 与多语系文档

### 6.1 attribute 写法

```csharp
[HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_<Name>Page.md")]
public class UCL_<Name>Page : UCL_CommonEditorPage { ... }
```

`{lang}` 在 `UCL_GUILayout.DrawHelpButton` 解析时自动替换成当前 `UCL_LocalizeManager.s_LangName`（zh-Hant / en / ja / zh-Hans），让「？」按钮能依用户语系跳对档。详见 [HelpURL_Workflow](HelpURL_Workflow.md)。

### 6.2 文档还没写也要先挂

- `[HelpURL]` 即使指向尚未存在的 .md 也不会 crash — 点下去开不了视窗就算了
- 把 attribute 先挂上，可以让未来搜索（[Cmd_SearchDocs](../API/UCL_AgentCommand/Cmd_SearchDocs.md) / `UCL_DocSearchPage`）一并索引到「应该存在但还没写」的文档位置
- 文档等 page 稳定后再补（看 `UCL_DocSearchPage` 自己也是这个节奏）

---

## 7. 常见地雷

| # | 地雷 | 症状 | 解法 |
|---|---|---|---|
| 1 | `UCL_GUIStyle.LabelStyle` 传给 `GUILayout.Toggle` 第三参 | checkbox 图标消失、按了没反应 | 纯 checkbox 省略第三参；button-like 用 `ButtonStyle`。详见 [UCL_GUIStyle Overview §3](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md#3-反指守則從-labelstyle-重複出現的禁忌) |
| 2 | 在 `ContentOnGUI()` 内又包一层 ScrollView | 双重滚动条、鼠标滚轮行为怪 | 不用包，base 已包；要二级滚动时自己另开命名变量 |
| 3 | TextField 吃掉 Enter 后 search 不触发 | 按 Enter 没反应 | 在 TextField 之前 snapshot `Event.current`；见 `UCL_DocSearchPage.DrawSearchInput` 区块注释 |
| 4 | `Create<T>()` 在没有 controller 的情境下调用 | NullRef | 确保父页／EditorWindow 已建好 controller，或自己持有 controller 传进 `Create<T>(controller)` 重载 |
| 5 | 样式 lazy 建立没 cache | 每帧 new GUIStyle，性能崩 | 用 field + property 做 lazy（见 §5.1），或丢到 `EnsureStyles()` 统一建 |
| 6 | rich-text label 内混入 `<...>`（如显示 C# 泛型 `List<T>`） | 字被当 tag 解析，部分内容消失 | 该样式关掉 `richText`，或对用户内容做 `<` → `&lt;` 转义 |
| 7 | `[HelpURL]` 写死语系（没用 `{lang}` 占位） | 切语系后 Help 按钮跳错档 | 一律用 `ucl_core:Docs~/{lang}/...` |
| 8 | EditorWindow.OnGUI 没设 `IsInEditorWindow` | 样式 cache 跑到 runtime 那份，DPI 异常 | 用 `IsInEditorWindowScope`（using 自动还原） |

---

## 8. 验收清单

写完后逐项确认：

- [ ] 继承 `UCL_CommonEditorPage`、文件名与类别名一字不差
- [ ] override `WindowName`（非空字符串）
- [ ] override `ContentOnGUI()`，内容用 `UCL_GUIStyle.*` / `UCL_GUILayout.*` 取样式与组件
- [ ] 没有把 `LabelStyle` 传给交互控件
- [ ] `[HelpURL("ucl_core:Docs~/{lang}/...")]` 带 `{lang}` 占位
- [ ] `static Create()` 工厂方法存在，且返回子类型别
- [ ] 至少一个入口点（父页按钮 / WelcomePage 卡片 / 菜单）能开到这页
- [ ] domain reload 后实际打开无 NullRef、Back / Close 行为正常
- [ ] 有 IMGUI rich-text 内容时，相关 GUIStyle 开启 `richText`

---

## 9. 范例参考

| 页面 | 看点 |
|---|---|
| `UCL_DocSearchPage` | 标准骨架 + 搜索输入栏 Enter 触发 + 折叠进阶选项 + 结果列表动作按钮 |
| `UCL_MarkdownViewerPage` | 从外部 `Create(args...)` 加载数据 + `EnsureStyles()` 集中建样式 + TopBarButtons 定制三按钮 |
| `UCL_WelcomePage` | 卡片 grid 排版 + 多入口集中地 |

---

## 10. 相关文档

- [UCL_GUILayout 整体概览](../API/UCL_GUILayout/UCL_GUILayout_Overview.md) — IMGUI 组件层
- [UCL_GUIStyle 概览](../API/UCL_GUIStyle/UCL_GUIStyle_Overview.md) — 样式层
- [HelpURL_Workflow](HelpURL_Workflow.md) — `ucl_core:` / `eov_docs:` prefix 解析机制
- [Hardcoded_Localize](Hardcoded_Localize.md) — TopBar / 按钮文字本地化（`UCL_CodeLocalize` / `UCL_LocalizeManager`）
- [Polymorphism_In_UCL](../Architecture/Polymorphism_In_UCL.md) — `[SerializeReference]` 多态字段在 GUI 编辑与 JSON 序列化的整体架构
