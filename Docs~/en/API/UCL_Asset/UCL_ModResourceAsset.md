# UCL_ModResourceAsset (Module Resource Asset)

## 1. System Overview
`UCL_ModResourceAsset` is the core asset class in the Ringworld project for handling "modular external resources". It allows developers to reference external images (Sprite or Texture2D) stored within Mod folders via an ID system, providing asynchronous loading and automatic resource release mechanisms.

## 2. Core Features
*   **Dynamic Path Mapping**: Automatically locates physical file paths based on `ModuleID` and asset configuration.
*   **Async Loading Mechanism**: Supports `UniTask` asynchronous reading of terrain, item, or character textures to avoid main thread stuttering.
*   **Lifecycle Management**: Implements the `IDisposable` interface to ensure texture resources are correctly released from memory when the asset is no longer in use.
*   **Preview Functionality**: Provides real-time preview and editing entry points in the editor interface.

## 3. Data Structure (UCL_ModResourcesData)
Core data is stored in the `m_ModResourcesData` member, which includes:
*   `m_ModuleID`: Unique identifier of the owning module.
*   `m_FolderPath`: Subfolder path relative to the module resources root.
*   `m_FileName`: Target file name (including extension).

## 4. Usage Example (C#)
```csharp
// Asynchronously retrieve Sprite from a resource entry
public async UniTask SetupIcon(UCL_ModResourceEntry iEntry, CancellationToken iToken)
{
    Sprite icon = await iEntry.GetData().GetSpriteAsync(iToken);
    m_IconImage.sprite = icon;
}
```

## 5. Notes
> [!IMPORTANT]
> Ensure that module resources are stored in the correct `ModResources/` subdirectory; otherwise, the system will not be able to locate the files.
