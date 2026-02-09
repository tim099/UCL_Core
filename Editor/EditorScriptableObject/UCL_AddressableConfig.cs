#if EDITOR_ADDRESSABLE_SUPPORT

using UnityEngine;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using System.IO;
using UCL.Core.ATTR;

namespace UCL.Core.EditorLib
{
    [UCL.Core.ATTR.EnableUCLEditor]
    [CreateAssetMenu(fileName = "AddressableConfig", menuName = "Tools/Addressable Config")]
    public class UCL_AddressableConfig : ScriptableObject
    {
        [Header("Settings")]
        public string groupName = "Default Local Group";
        public string labelName = "";

        public string addressFormat = "{0}";

        [Tooltip("Use the file name as the Address? (If false, uses the full asset path)")]
        public bool useFileNameAsAddress = true;

        [UCL_FunctionButton]
        [ContextMenu("Execute Sync")]
        public void SyncFolderAssets()
        {
            // 1. Get the directory path where this ScriptableObject is located
            string assetPath = AssetDatabase.GetAssetPath(this);
            string folderPath = Path.GetDirectoryName(assetPath);

            // 2. Retrieve Addressable Asset Settings
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                EditorUtility.DisplayDialog("Error", "Please create Addressable Settings first!", "OK");
                return;
            }

            // 3. Search for all ScriptableObjects in the same folder
            string[] guids = AssetDatabase.FindAssets("t:ScriptableObject", new[] { folderPath });
            int total = guids.Length;
            int processedCount = 0;

            try
            {
                // Ensure the Label exists in the settings
                if (!string.IsNullOrEmpty(labelName) && !settings.GetLabels().Contains(labelName))
                {
                    settings.AddLabel(labelName);
                }
                    

                // Ensure the Group exists; create it if not found
                AddressableAssetGroup group = settings.FindGroup(groupName);
                if (group == null) group = settings.CreateGroup(groupName, false, false, false, null);

                for (int i = 0; i < total; i++)
                {
                    string guid = guids[i];
                    string path = AssetDatabase.GUIDToAssetPath(guid);

                    // Skip the configuration file itself
                    if (path == assetPath) continue;

                    // Update progress bar: display current filename and progress ratio
                    // Returns true if the user clicks the "Cancel" button
                    float progress = (float)i / total;
                    if (EditorUtility.DisplayCancelableProgressBar("Syncing Addressables", $"Processing: {Path.GetFileName(path)}", progress))
                    {
                        Debug.LogWarning("[Addressable Tool] Operation cancelled by user.");
                        break;
                    }

                    // Core Logic: Create or move the entry to the specified group
                    AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group);

                    // Format the address string
                    string name = useFileNameAsAddress ? Path.GetFileNameWithoutExtension(path) : path;
                    name = string.Format(addressFormat, name);
                    entry.address = name;

                    // Assign the label
                    if (!string.IsNullOrEmpty(labelName)) entry.SetLabel(labelName, true);

                    processedCount++;
                }
            }
            finally
            {
                // IMPORTANT: Progress bar must be cleared in the finally block 
                // to ensure it closes even if an error occurs or the operation is cancelled.
                EditorUtility.ClearProgressBar();

                // Save changes and mark settings as dirty
                settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, null, true);
                AssetDatabase.SaveAssets();

                Debug.Log($"<color=green><b>[Addressable Tool]</b></color> Sync Completed. Processed {processedCount}/{total} files.");
            }
        }
    }
}

#endif