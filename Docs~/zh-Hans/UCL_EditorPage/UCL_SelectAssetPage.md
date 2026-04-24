# UCL_SelectAssetPage (资产选择页面)

## 1. 系统概觀
`UCL_SelectAssetPage` 是 UCL 框架中用于管理特定类型资产（Asset）的核心导航页面。它提供了一个结构化的列表，让开发者可以快速搜索、预览、编辑或删除模块中的各种数据资产。

## 2. 界面功能详解

### 2.1 顶部功能列 (Top Bar)
*   **建立 [资产名称]**：点击后进入 `UCL_CreateAssetPage` 以建立该类型的新资产。
*   **RefreshData**：重新扫描磁盘文件，确保列表中的数据与实体文件同步。
*   **OpenFolder**：直接开启该类型资产在文件管理器中的存储目录。
*   **帮助按钮 (?)**：若资产类别定义了 `[HelpURL]`，会在此显示链接按钮。
*   **Copy 类别名称**：一键复制当前资产类型的完整 C# 类别名称。

### 2.2 资产列表与搜索 (Asset List & Search)
*   **搜索栏 (Search)**：支持正则表达式 (Regex) 进行模糊搜索。符合条件的文字会以红色标记。
*   **分页控制 (Pagination)**：
    *   每页默认显示 10 个资产。
    *   提供“上一页/下一页”与直接输入页码跳转的功能。

### 2.3 列表项操作 (List Item Actions)
*   **资产 ID/名称**：显示资产的唯一识别码或本地化名称。
*   **Edit (编辑)**：开启该资产的专属编辑页面 (`UCL_CommonEditPage`)。
*   **Preview (预览)**：在页面右侧显示该资产的内容快照，无需进入编辑页面。
*   **Delete (删除)**：弹出确认窗口后删除资产文件。
*   **分组编辑 (Group Edit)**：若启用了 Meta 管理，可在列表中直接修改资产的分组信息。

### 2.4 右侧预览区 (Preview Area)
当点击列表中的“Preview”按钮时，右侧会根据资产实现的 `Preview` 逻辑显示其详细内容（如 CSV 表格内容、Sprite 图像等）。

## 3. 开发者设定

### 3.1 关联说明文件 (HelpURL)
开发者可以通过为资产类别添加 `[HelpURL]` 属性，让该页面的帮助按钮指向特定文档：

```csharp
[HelpURL("ucl_core:Docs~/{lang}/API/UCL_Asset/MyCustomAsset.md")]
public class MyCustomAsset : UCL_CSVAsset { ... }
```

## 4. 注意事项
> [!TIP]
> 当资产数量庞大时，善用 **Search** 与 **Pagination** 可以大幅提升管理效率。若发现列表未显示新建立的文件，请点击 **RefreshData**。
