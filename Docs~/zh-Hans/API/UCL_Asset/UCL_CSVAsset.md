# UCL_CSVAsset (CSV 数据资产)

## 1. 系统概览
`UCL_CSVAsset` 是一个专门用于处理模块化 CSV 文件的资产类别。它继承自 `UCL_Asset` 体系，利用 `UCL_ModResourcesData` 定位 Mod 文件夹中的实体文件，并整合了 `UCL.Core.CsvLib` 来提供结构化的表格数据访问。

## 2. 核心功能
*   **模块化文件读取**：支持从特定 Mod 的 `ModResources` 目录下读取 `.csv` 文件。
*   **实时解析**：提供 `GetCSVData()` 方法，将原始 CSV 文本转换为具备行（Row）与列（Column）操作界面的 `CSVData` 对象。
*   **异步支持**：内置 `GetCSVTextAsync`，支持在后台线程进行大体量文本读取，避免 UI 冻结。
*   **内容摘要预览**：在 Unity Inspector 或 UCL 编辑器分页中，自动显示 CSV 文件的前 5 行内容，方便快速检视数据结构。

## 3. 使用方法
### 在 C# 脚本中引用
```csharp
[SerializeField] private UCL_CSVEntry m_ConfigTable;

public void LoadConfig()
{
    CSVData data = m_ConfigTable.GetCSVData();
    if (data != null)
    {
        // 取得第一行第二列的数据
        string val = data.GetData(0, 1);
        Debug.Log($"Config Value: {val}");
    }
}
```

## 4. 数据结构
该资产内部封装了 `UCL_ModResourcesData`：
*   `m_ModuleID`：文件所属的 Mod 识别码。
*   `m_FolderPath`：相对于模块资源根目录的路径。
*   `m_FileName`：CSV 文件名称（包含 `.csv` 后缀）。

## 5. 注意事项
> [!TIP]
> 建议文件使用 `UTF-8` 编码以确保中文字符在各平台上都能正确解析。
