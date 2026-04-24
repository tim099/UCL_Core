# 硬编码多国语言流程 (UCL_CodeLocalize)

## 1. 概觀
`UCL_CodeLocalize` 是一个高效能的硬编码多国语言工具，旨在将核心 UI 字符串直接存储在 C# 代码中。它作为外部 JSON/CSV 本地化文件的可靠后备方案（Fallback）以及高速替代方案。

### 为什么使用硬编码本地化？
*   **安全性**：关键 UI 字符串（如“存档”、“取消”、“错误”）永远可用，即使外部资产文件丢失也不会显示原始 ID。
*   **效能**：使用 C# `switch` 表达式实现 O(1) 或接近 O(1) 的查询速度，且在运行时零内存分配。
*   **维护性**：利用 `partial class` 将各语言的翻译拆分到独立文件中。

## 2. 架构
系统由一个核心逻辑文件与多个语系专用的 partial 文件组成：
*   `UCL_CodeLocalize.cs`：核心调度逻辑，基于 `UCL_LocalizeManager.s_LangName`。
*   `UCL_CodeLocalize.en.cs`：英文翻译（最终后备）。
*   `UCL_CodeLocalize.zh-Hant.cs`：繁体中文翻译。
*   ...（其他语言）

## 3. 如何使用

### 3.1 获取翻译字符串
只需在代码中调用 `UCL_CodeLocalize.Get(key)`：
```csharp
string windowTitle = UCL_CodeLocalize.Get("UCL_ModuleServiceEditPage");
```

### 3.2 后备逻辑 (Fallback Logic)
1.  系统通过 `UCL_LocalizeManager.s_LangName` 识别当前语系。
2.  尝试在对应的语言文件中寻找 Key。
3.  若找不到（返回 `null`），则后退至 **英文 (en)** 版本。
4.  若英文版也找不到，则返回 **Key** 本身。

## 4. 如何新增词条

### 步骤 1：在语系文件中新增内容
开启对应的语言文件（例如 `UCL_CodeLocalize.zh-Hans.cs`），并将键值对新增至 `switch` 表达式中：

```csharp
static public string Get_zhHans(string iKey)
{
    return iKey switch
    {
        "My_New_Key" => "我的新词条",
        // ... 现有词条
        _ => null
    };
}
```

### 步骤 2：确保英文后备
务必在 `UCL_CodeLocalize.en.cs` 中也新增该词条，以确保其他语系的使用者在缺失翻译时至少能看到英文说明。

```csharp
static public string Get_en(string iKey)
{
    return iKey switch
    {
        "My_New_Key" => "My New Key",
        _ => iKey // 英文分支应始终以 iKey 作为默认返回
    };
}
```

## 5. 最佳实践
> [!IMPORTANT]
> 请将 `UCL_CodeLocalize` 用于 **核心 UI** 与 **框架字符串**。对于需要非程序人员频繁更新的游戏内容（如道具名称、剧情对白），请继续使用 `UCL_LocalizeAsset`（外部 CSV/Text 文件）。
