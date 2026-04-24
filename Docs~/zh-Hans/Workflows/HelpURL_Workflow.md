# HelpURL 系统与工作流 (HelpURL System & Workflow)

## 1. 核心概念
UCL 扩展了 Unity 原生的 `HelpURLAttribute`，建立了一套支持“跨环境解析”与“多国语言支持”的帮助系统。

### 1.1 特殊前缀：`ucl_core:`
为了确保模块在不同项目中移动、或是发布成 Build 版本后链接依然有效，我们引入了相对路径解析：
*   **格式**：`ucl_core:Docs~/{lang}/YourDoc.md`
*   **解析逻辑 (`UCL_URL`)**：
    *   **Editor 模式**：自动解析为本地路径 `[UCL_Core根目录]/Docs~/{lang}/YourDoc.md`。支持离线阅读。
    *   **Build 模式**：自动转换为 GitHub 上的对应链接，确保玩家也能访问云端文件。

### 1.2 本地化占位符：`{lang}`
*   **用途**：根据当前语系自动切换文件。
*   **计算逻辑**：系统会自动将 `{lang}` 替换为 `UCL_LocalizeService.CurLang`（例如 `en`, `zh-Hans`, `ja`）。
*   **Editor 回退机制**：若当前语系文件不存在，系统在 Editor 下会尝试寻找 `en` 版本作为回退，避免 404。

### 1.3 隐藏文件夹：`Docs~`
*   **物理意义**：Unity 会自动忽略以 `~` 结尾的文件夹。因此我们将文件放在 `Docs~` 下，这样既能保存在模块目录内，又不会产生 `.meta` 文件。

---

## 2. 工作流 (Workflow)

### 步骤 A：编写说明文件
1.  在 `Assets/UCL/UCL_Core/Docs~/{lang}/` 目录下建立 Markdown 文件。
    - 范例：`Docs~/zh-Hans/MyFeature.md`
2.  编写相关功能的技术说明或操作指南。

> [!IMPORTANT]
> 若文件是针对特定的 Class，文件命名**必须**与 Class 名称一致（例如 `UCL_ModuleServiceEditPage.md` 对应 `class UCL_ModuleServiceEditPage`）。

### 步骤 B：挂载属性 (HelpURL)
#### 情况 1：对于一般的资产或数据类别
直接在类别宣告上方加上 `[HelpURL]`，务必使用 `{lang}`：
```csharp
[HelpURL("ucl_core:Docs~/{lang}/API/MyFeatureAsset.md")]
public class MyFeatureAsset : UCL_ModResourceAsset { ... }
```

#### 情况 2：对于编辑器页面 (`UCL_EditorPage`)
同样加上 `[HelpURL]`：
```csharp
[HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/MyFeatureEditPage.md")]
public class MyFeatureEditPage : UCL_EditorPage { ... }
```

---

## 3. 系统组件说明
*   **`UCL_URL.cs`**：负责解析 URL 字符串，处理 `{lang}` 替换。
*   **`UCL_GUILayoutDrawObject.DrawHelpButton`**：GUI 层级的封装，绘制 `?` 按钮并调用 `UCL_URL.OpenURL`。
*   **`UCL_EditorPage.cs`**：页面基类，自动缓存 `HelpURL` 属性并在 TopBar 绘制。
