# UCL_SelectAssetPage

## 1. Overview
`UCL_SelectAssetPage` is the core navigation interface in the UCL framework for managing specific types of assets. It provides a structured list that allows developers to quickly search, preview, edit, or delete various data assets within a module.

## 2. Interface Features

### 2.1 Top Bar
*   **Create [Asset Name]**: Opens `UCL_CreateAssetPage` to create a new asset of the current type.
*   **RefreshData**: Rescans disk files to ensure the list is synchronized with the actual file system.
*   **OpenFolder**: Opens the storage directory for this asset type in the OS file explorer.
*   **Help Button (?)**: Displays a link button if the asset class defines a `[HelpURL]` attribute.
*   **Copy Class Name**: Copies the full C# class name of the current asset type to the clipboard.

### 2.2 Asset List & Search
*   **Search Bar**: Supports Regular Expressions (Regex) for fuzzy searching. Matching text is highlighted in red.
*   **Pagination**:
    *   Displays 10 assets per page by default.
    *   Provides "Prev/Next Page" buttons and a numeric input for direct page jumping.

### 2.3 List Item Actions
*   **Asset ID/Name**: Displays the unique ID or localized name of the asset.
*   **Edit**: Opens the dedicated editing page (`UCL_CommonEditPage`) for the asset.
*   **Preview**: Shows a content snapshot on the right side of the page without leaving the list.
*   **Delete**: Deletes the asset file after a confirmation prompt.
*   **Group Edit**: If Meta management is enabled, allows direct modification of asset group information in the list.

### 2.4 Preview Area
When the "Preview" button is clicked, the right side of the page displays detailed content (e.g., CSV table data, Sprite images) based on the asset's implemented `Preview` logic.

## 3. Developer Configuration

### 3.1 Linking Documentation (HelpURL)
Developers can link the help button to specific documentation by adding the `[HelpURL]` attribute to the asset class:

```csharp
[HelpURL("ucl_core:Docs~/{lang}/API/UCL_Asset/MyCustomAsset.md")]
public class MyCustomAsset : UCL_CSVAsset { ... }
```

## 4. Tips
> [!TIP]
> Use **Search** and **Pagination** to efficiently manage large numbers of assets. If a newly created file does not appear in the list, click **RefreshData**.
