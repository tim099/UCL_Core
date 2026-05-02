# HelpURL 系统与工作流 (HelpURL System & Workflow)

## 1. 核心概念
UCL 扩展了 Unity 原生的 `HelpURLAttribute`，建立了一套支持「跨环境解析」、「多国语言支持」与「下游模块扩充」的帮助系统。

### 1.1 特殊前缀与 Prefix Resolver 机制
UCL_URL 采用 **Resolver 注册表** 架构。任何 `xxx:RelativePath` 形式（且冒号后不接 `//`）的 URL，都会去查询已注册的 Resolver：

*   **格式**：`{prefix}:Docs~/{lang}/YourDoc.md`
*   **解析逻辑 (`UCL_URL`)**：
    *   **命中 prefix**：调用该 Resolver 的 `Resolve`。Editor / Build 的差异由 **Resolver 注册端** 在 `#if UNITY_EDITOR` 中决定要传入哪一个委派，介面本身只暴露单一 `Resolve` 方法。
    *   **未命中 prefix**：保留原 URL，继续走 `{lang}` 替换与本地路径补全。

> [!NOTE]
> UCL_Core 自身的 `ucl_core:` prefix 也是透过注册机制挂上去的，没有特例。下游模块要新增自家 prefix（例如 `eov_docs:`）只要在启动时注册一次即可，**不需要修改 UCL_Core**。

### 1.2 本地化占位符：`{lang}`
*   **用途**：根据当前语系自动切换文件。
*   **计算逻辑**：系统会自动将 `{lang}` 替换为 `UCL_LocalizeService.CurLang`（例如 `en`, `zh-Hans`, `ja`）。
*   **Editor 回退机制**：若当前语系文件不存在，系统在 Editor 下会尝试寻找 `en` 版本作为回退，避免 404。
*   **归属**：`{lang}` 由 `UCL_URL` 共用层处理，**Resolver 端不必各自重复实作**。

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

## 3. 为下游模块扩充自定 Prefix

### 3.1 何时需要扩充？
当你的下游项目（例如非开源的游戏本体、但文档本身为公开的开源 repo）希望让 `[HelpURL]` 同时支持自家文档，又不能在 UCL_Core 内写死自家 URL 时。

### 3.2 注册方式：Lambda 版（推荐）
最常见的情境只需要 `Path.Combine` / 字符串拼接，使用 `UCL_UrlPrefixResolver` 即可，免实作介面：

```csharp
using UCL.Core;
using UnityEngine;

public static class EoV_DocsResolverBootstrap
{
    private const string BUILD_BASE_URL = "https://github.com/tim099/EmblemOfValorDocuments/blob/main/";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoadMethod]
#endif
    private static void Register()
    {
        UCL_URL.RegisterResolver(new UCL_UrlPrefixResolver(
            prefix: "eov_docs",
#if UNITY_EDITOR
            // [Editor] 接于本地 submodule 路径之后，便于离线阅读。
            resolver: (relativePath) => System.IO.Path.Combine(EoV_DocsPath.Root, relativePath)
#else
            // [Build] 拼接 GitHub blob 链接，玩家可直接用浏览器打开。
            resolver: (relativePath) => BUILD_BASE_URL + relativePath
#endif
        ));
    }
}
```

### 3.3 注册方式：实作介面版
若 Resolver 逻辑较复杂（需要状态、条件分支），可直接实作 `IUCL_UrlPrefixResolver`：

```csharp
public sealed class MyComplexResolver : IUCL_UrlPrefixResolver
{
    public string Prefix => "my_proj";
    public string Resolve(string relativePath)
    {
#if UNITY_EDITOR
        // [Editor] 解析为本地路径
        return /* ... */;
#else
        // [Build] 解析为云端 URL
        return /* ... */;
#endif
    }
}
```

### 3.4 使用注册后的 Prefix
与 `ucl_core:` 完全一致：

```csharp
[HelpURL("eov_docs:Docs~/{lang}/Mechanics/CombineSetting.md")]
public class CombineSettingAsset { ... }
```

> [!IMPORTANT]
> 注册时机坑：若 `UCL_URL.OpenURL` 可能在你的 Resolver 注册之前被调用，链接会解析失败。请务必同时挂 `[InitializeOnLoadMethod]`（Editor）与 `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`（Runtime），两条都要。

> [!NOTE]
> 同 prefix 后注册胜出，UCL_URL 会输出 Warning 但允许覆写；下游可借此替换 UCL 默认的云端 URL（例如指向自家 fork）。

---

## 4. 系统组件说明
*   **`UCL_URL.cs`**：URL 解析主流程，拥有 prefix → resolver 注册表，并负责 `{lang}` 替换与 en 回退。
*   **`IUCL_UrlPrefixResolver`**：Resolver 契约介面（与 `UCL_URL` 同档），只定义 `Prefix` 与单一 `Resolve` 方法；Editor / Build 差异由注册端负责切换。
*   **`UCL_UrlPrefixResolver`**：以 Lambda 委派为策略的 Resolver 轻量实作，省去下游为单一 prefix 开新类别。
*   **`UCL_GUILayoutDrawObject.DrawHelpButton`**：GUI 层级的封装，绘制 `?` 按钮并调用 `UCL_URL.OpenURL`。
*   **`UCL_EditorPage.cs`**：页面基类，自动缓存 `HelpURL` 属性并在 TopBar 绘制。
