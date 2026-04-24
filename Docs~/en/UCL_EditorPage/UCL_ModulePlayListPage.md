# UCL_ModulePlayListPage (Module Playlist Management Page)

## 1. Overview
The `UCL_ModulePlayListPage` is a dedicated editor interface for managing `UCL_ModulePlaylist`. It allows developers to define different sets of modules (playlists) to load, facilitating testing, environment switching, or distributing different versions of mod collections.

## 2. Core Features
*   **Playlist Selection**: Lists all `.json` playlists stored in the `PersistentDataPath`.
*   **Dynamic Creation**: Quickly create new playlists, which include the mandatory `Core` module by default.
*   **Runtime Loading**: Provides a "Load current playlist" button to immediately re-initialize the module service via `LoadModulePlaylistAsync`.
*   **Edit Mode Switching**:
    *   **Select Mode**: Choose or create a playlist.
    *   **Edit Mode**: Edit playlist content, including loading order and activation state.

## 3. Workflow
1.  **Access**: Typically accessed from the `UCL_ModuleService` main page via the "Playlist Management" entry.
2.  **Edit & Save**: Select modules and adjust their order in the list, then click "Save" to persist the configuration.
3.  **Apply**: Click "Load current playlist"; the system will clear currently loaded modules and reload according to the new playlist.

## 4. Key Components
*   **`UCL_ModulePlaylist`**: Data entity class defining module ID lists and loading logic.
*   **`UCL_ModuleService.LoadModulePlaylistAsync`**: Async method executing the core loading pipeline.

> [!IMPORTANT]
> Changing the playlist will cause all currently loaded `UCL_Module` instances to be released and recreated. Ensure no critical logic is running before switching.
