# UCL_ModResourceAsset (模块资源资产)

## 1. 系统概览
`UCL_ModResourceAsset` 是 Ringworld 项目中用于处理“模块化外部资源”的核心资产类别。它允许开发者通过 ID 系统引用存放于 Mod 文件夹内的外部图片（Sprite 或 Texture2D），并提供异步加载与自动资源释放机制。

## 2. 核心功能
*   **动态路径对应**：自动根据 `ModuleID` 与资产配置定位实体文件路径。
*   **异步加载机制**：支持 `UniTask` 异步读取地表、道具或角色贴图，避免主线程卡顿。
*   **生命周期管理**：实现 `IDisposable` 接口，确保当资产不再使用时能正确释放内存中的贴图资源。
*   **预览功能**：在编辑器界面中提供实时预览与编辑入口。

## 3. 数据结构 (UCL_ModResourcesData)
资产核心数据存放于 `m_ModResourcesData` 成员中，包含：
*   `m_ModuleID`：所属模块的唯一识别码。
*   `m_FolderPath`：相对于模块资源根目录的子文件夹路径。
*   `m_FileName`：目标文件名称（包含后缀）。

## 4. 使用范例 (C#)
```csharp
// 从资源条目中异步获取 Sprite
public async UniTask SetupIcon(UCL_ModResourceEntry iEntry, CancellationToken iToken)
{
    Sprite icon = await iEntry.GetData().GetSpriteAsync(iToken);
    m_IconImage.sprite = icon;
}
```

## 5. 注意事项
> [!IMPORTANT]
> 确保模块资源存放于正确的 `ModResources/` 子目录下，否则系统将无法根据路径定位文件。
