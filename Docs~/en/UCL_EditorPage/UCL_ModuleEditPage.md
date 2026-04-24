# UCL_ModuleEditPage

## 1. Overview
`UCL_ModuleEditPage` is a dedicated editor interface within the UCL framework designed for detailed configuration, asset maintenance, and lifecycle management of a single Module. It provides developers with intuitive access to the module's file system and allows editing of all asset data contained within it.

## 2. Interface Features

### 2.1 Top Bar
*   **Back/Close**: Ends the current editing session and returns to the main Module Service page.
*   **[ModuleID]**: Displays the ID of the module currently being edited.
*   **Open Module Root Folder**: Opens the source/resource root directory of the module in the OS file explorer.
*   **Open Module Install Folder**: Opens the directory where the module is installed in the system environment.
*   **RefreshAllDatas(With Reflection)**: Forces a rescan of all assets in the project using reflection to ensure data synchronization.

### 2.2 Lifecycle Controls
*   **Zip Module**: Packages the module into a `.zip` format for distribution or backup.
*   **Save Module**: Saves all current modifications on the interface back to the module's physical JSON configuration file.
*   **Load Module**: Discards current changes and reloads the module data from the disk.
*   **Install Module**: Mounts the module into the runtime environment (e.g., loading assets, registering services).
*   **UnInstall Module**: Unmounts the module from the runtime environment and releases resources.

### 2.3 Core Content Area
*   **CurEditModule (Foldout)**:
    *   **Settings**:
        *   `Version`: The version number of the module.
        *   `LastEditTime`: The timestamp of the last modification (automatically updated by the system on save).
        *   `UTC_TimeStamp`: Global timestamp used internally to determine if a re-install is required.
        *   `ID`: Unique ID of the module (typically matches the folder name).
        *   `Title`: Display title (primarily used for the Steam Workshop).
        *   `Description`: Detailed description of the module.
        *   `DependenciesModules`: Lists other modules this module depends on.
    *   **Logo Settings**:
        *   The system automatically looks for a file named **`Logo.png`** in the module's root directory.
        *   **How to Set**: Place a image file named `Logo.png` directly into the module's root folder.
        *   Expand the `Logo` section in the interface to preview it.
    *   **Module Content**:
        *   **Asset Grouping**: Automatically categorizes assets contained in the module by group (e.g., Data, Logic, UI).
        *   **Asset Editors**: Lists all created assets (e.g., `UCL_CSVAsset`). Clicking "Edit" opens the specific editor for that asset.

### 2.4 Footer
*   **Export Module**: Executes the module export process, typically used for preparing final release assets.

## 3. Important Notes
> [!IMPORTANT]
> Always click **Save Module** after making significant changes to ensure they are written to disk. If modifications involve asset definitions or path changes, it is recommended to execute **RefreshAllDatas** after saving to maintain system consistency.
