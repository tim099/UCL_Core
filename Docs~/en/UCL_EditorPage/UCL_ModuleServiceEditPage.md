# UCL_ModuleServiceEditPage (Module Service Edit Page)

## 1. Overview
The `UCL_ModuleServiceEditPage` is the central management interface for the UCL module system. Developers can switch edit modes, manage configurations, create new modules, and access the playlist management workflow here.

## 2. Core Functional Blocks

### 2.1 Global Management
*   **Edit Mode**: Toggle the resource environment for the system.
    *   `Builtin`: Handle internal modules (typically located in `StreamingAssets`).
    *   `Runtime`: Handle runtime modules (located in `PersistentDataPath`, supports Modding).
*   **Configuration Operations**: Provides "Save Config" and "Load Config" functions.
*   **Path Access**: "Open Module Folder" button for quick navigation to the physical file location.

### 2.2 Module Maintenance
*   **Create New Module**: Establish a brand new module folder and configuration.
*   **Module Editing Entry**:
    *   Select the target Module ID (e.g., `Core`) via a popup menu.
    *   Click the **"Edit"** button to enter the detailed settings page for that module (`UCL_ModuleEditPage`).

### 2.3 Playlist Management
*   **Edit Playlist**: Click to open the `UCL_ModulePlayListPage`.
*   **Logic Note**: To enable specific modules, they must be added to a playlist. Modules are loaded in the order defined in the playlist; **modules loaded later will override assets with the same ID from earlier modules**.

### 2.4 Runtime Status Inspection
*   **Currently Loaded Modules**: Expand to view active module instances in the system.
*   **AssetsCache**: Displays the status of asset cache dictionaries, useful for debugging path resolutions.

## 3. Precautions
> [!WARNING]
> Direct edits to `Builtin(Core)` may be lost after a system update. It is recommended to create new modules to add or override content.

> [!TIP]
> Use the "Copy" button to quickly retrieve the data path of the current page for easier debugging in scripts.
