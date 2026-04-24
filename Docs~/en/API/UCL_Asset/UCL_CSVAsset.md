# UCL_CSVAsset (CSV Data Asset)

## 1. System Overview
`UCL_CSVAsset` is a specialized asset class for handling modular CSV files. It inherits from the `UCL_Asset` system, using `UCL_ModResourcesData` to locate physical files within Mod folders, and integrates `UCL.Core.CsvLib` to provide structured tabular data access.

## 2. Core Features
*   **Modular File Reading**: Supports reading `.csv` files from the `ModResources` directory of a specific Mod.
*   **Real-time Parsing**: Provides the `GetCSVData()` method to convert raw CSV text into a `CSVData` object with row and column operation interfaces.
*   **Async Support**: Built-in `GetCSVTextAsync` supports large text reading on background threads to avoid UI freezing.
*   **Content Summary Preview**: Automatically displays the first 5 lines of the CSV file in the Unity Inspector or UCL Editor pages for quick data structure verification.

## 3. Usage
### Reference in C# Scripts
```csharp
[SerializeField] private UCL_CSVEntry m_ConfigTable;

public void LoadConfig()
{
    CSVData data = m_ConfigTable.GetCSVData();
    if (data != null)
    {
        // Get data from the first row, second column
        string val = data.GetData(0, 1);
        Debug.Log($"Config Value: {val}");
    }
}
```

## 4. Data Structure
The asset encapsulates `UCL_ModResourcesData`:
*   `m_ModuleID`: The unique identifier of the Mod the file belongs to.
*   `m_FolderPath`: Path relative to the module resources root.
*   `m_FileName`: Name of the CSV file (including the `.csv` extension).

## 5. Notes
> [!TIP]
> It is recommended to use `UTF-8` encoding for files to ensure characters are correctly parsed across all platforms.
