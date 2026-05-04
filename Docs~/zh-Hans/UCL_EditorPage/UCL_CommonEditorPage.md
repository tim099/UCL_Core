---
title: UCL_CommonEditorPage
description: UCL 编辑器页面的标准基类，提供 TypeName 标签与 Copy 按钮。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_CommonEditorPage.cs
namespace: UCL.Core.EditorLib.Page
---

# UCL_CommonEditorPage

## 1. 概述

`UCL_CommonEditorPage` 是 UCL 框架中所有自定义编辑器页面的**标准基类**。它是对 [`UCL_EditorPage`](./UCL_EditorPage.md) 的薄薄一层扩展，提供两项通用功能：

1. **TypeName 标签** — 自动将页面的类名显示在顶部栏
2. **Copy 按钮** — 一键将类名复制到系统剪贴板（开发时跨 page 子类跳转很方便）

当你创建任何非 trivial 的编辑器页面时，**继承 `UCL_CommonEditorPage` 而非直接继承 `UCL_EditorPage`** —— 你免费获得约定俗成的顶部栏布局，并且页面会与 UCL 编辑器生态（[`UCL_ModuleEditPage`](./UCL_ModuleEditPage.md)、[`UCL_ModuleServiceEditPage`](./UCL_ModuleServiceEditPage.md)、[`UCL_ModulePlayListPage`](./UCL_ModulePlayListPage.md)、[`UCL_SelectAssetPage`](./UCL_SelectAssetPage.md) ⋯）视觉一致。

## 2. 提供什么

### 2.1 类定义（节选）

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

### 2.2 默认顶部栏布局

```
┌──────────────────────────────────────────────────────┐
│ [Back] [Close] │ <你的页面类名> │ [Copy] │ ...        │
└──────────────────────────────────────────────────────┘
   ↑ 来自 UCL_EditorPage    ↑ 来自 UCL_CommonEditorPage
```

子类在右侧扩展时，覆写 `TopBarButtons()` 并**先调用 `base.TopBarButtons()`**。

## 3. 何时使用

| 情境 | 建议基类 |
|---|---|
| 简单页面、不需要任何顶部栏控制 | `UCL_EditorPage` |
| **大多数自定义编辑器页面** | **`UCL_CommonEditorPage`** ⭐ |
| 编辑单一 Module instance 的页面 | `UCL_ModuleEditPage`（已是子类） |
| 从列表中挑选资产的页面 | `UCL_SelectAssetPage`（已是子类） |

判断原则：除非有强烈理由要去掉 TypeName 标签，否则**默认选 `UCL_CommonEditorPage`**。

## 4. 如何扩展 — 标准模式

### 4.1 最小子类

```csharp
namespace YourGame.Page
{
    public class YourEditorPage : UCL_CommonEditorPage
    {
        public override string WindowName => "你的页面标题";

        public static YourEditorPage Create()
        {
            // ★ 用静态工厂；不要手动 new + Push。
            return UCL_EditorPage.Create<YourEditorPage>();
        }

        protected override void ContentOnGUI()
        {
            // 你的 IMGUI 内容
            GUILayout.Label("Hello", UCL_GUIStyle.LabelStyle);
        }
    }
}
```

### 4.2 加入顶部栏按钮

永远**先调用 `base.TopBarButtons()`**，让 `TypeName + Copy` 区块保持在最左：

```csharp
protected override void TopBarButtons()
{
    base.TopBarButtons();   // ★ 来自 UCL_CommonEditorPage 的 TypeName + Copy

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
> 顶部栏每个按钮都要加 `GUILayout.ExpandWidth(false)`。否则按钮会贪婪地填满水平空间，窗口变宽时整行布局就坏掉。

### 4.3 Init 覆写

若页面需要类似构造函数的初始化逻辑，覆写 `Init()` 并先调用 `base.Init()` 确保 `m_TypeName` 被填入：

```csharp
public override void Init(UCL_GUIPageController iGUIPageController)
{
    base.Init(iGUIPageController);   // ★ 必须先调用
    LoadInitialData();
}
```

### 4.4 OnClose 覆写

若用户离开页面时需要清理状态：

```csharp
public override void OnClose()
{
    SaveDirtyChanges();
    base.OnClose();   // ★ 最后调用 base
}
```

## 5. 参考子类

以下类继承自 `UCL_CommonEditorPage`，展示了标准的扩展模式：

| 子类 | 用途 |
|---|---|
| [`UCL_ModuleEditPage`](./UCL_ModuleEditPage.md) | 编辑单一 Module 的设置与内容 |
| [`UCL_ModuleServiceEditPage`](./UCL_ModuleServiceEditPage.md) | 管理所有已安装的 Module |
| [`UCL_ModulePlayListPage`](./UCL_ModulePlayListPage.md) | 管理 Module 加载顺序 playlist |
| [`UCL_SelectAssetPage`](./UCL_SelectAssetPage.md) | 从类型列表中挑一个资产 |

设计新编辑器页面时，**先读 `UCL_ModuleEditPage`** —— 那是覆写模式（Init / TopBarButtons / ContentOnGUI / OnClose 全示范）的标准范例。

## 6. 常见模式

### 6.1 用 `UCL_ObjectDictionary` 缓存子状态

许多页面需要记住 per-foldout / per-toggle 的 UI 状态跨帧保留。惯例：

```csharp
private UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();

protected override void ContentOnGUI()
{
    UCL_GUILayout.DrawObjectData(myObject, m_DataDic.GetSubDic("MyObject"), "MyObject");
}
```

### 6.2 从按钮触发异步任务

使用 `UniTask` 与 `.Forget()` 避免阻塞 IMGUI thread：

```csharp
if (GUILayout.Button("执行异步", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
{
    DoWorkAsync().Forget();
}

private async UniTask DoWorkAsync()
{
    await SomeService.InitAsync(default);
    // …
}
```

### 6.3 持续重绘（数据随外部状态变动时）

若页面反映的外部状态会在没有 GUI 输入时变化（计时器、进度等）：

```csharp
[UCL.Core.ATTR.RequiresConstantRepaint]
public class YourEditorPage : UCL_CommonEditorPage { … }
```

## 7. 注意事项

> [!CAUTION]
> **不要省略 `base.TopBarButtons()`**。省略后 TypeName 标签与 Copy 按钮会消失 —— 视觉上页面便不再属于 UCL 编辑器家族，并且失去开发时复制类名的便利。

> [!CAUTION]
> **想要标准顶部栏时，不要直接继承 `UCL_EditorPage`**。手动重新实现 TypeName + Copy 区块会重复代码，且未来框架更新时容易漂移。

> [!IMPORTANT]
> 从外部创建页面实例时，**永远使用静态工厂 `UCL_EditorPage.Create<T>()`**，不要 `new T(); UCL_GUIPageController.CurrentRenderIns.Push(p);`。工厂会处理重复页面检测与正确初始化。

## 8. 相关

- [`UCL_EditorPage`](./UCL_EditorPage.md) — 直接基类
- [`UCL_ModuleEditPage`](./UCL_ModuleEditPage.md) — 标准覆写范例
- [`UCL_GUIPage`](./UCL_GUIPage.md) — 根页面抽象
