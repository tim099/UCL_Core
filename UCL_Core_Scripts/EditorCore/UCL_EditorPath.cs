public static class UCL_EditorPath
{

    private static string s_UCLCorePath = null;
    /// <summary>
    /// [職責] 自動定位 UCL_Core 模組在專案中的根目錄路徑。
    /// [物理意義] 用於解析相對於模組的路徑，支持模組在不同專案中的移植。
    /// </summary>
    public static string CorePath
    {
        get
        {
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(s_UCLCorePath))
            {
                // [計算邏輯] 透過搜尋特定的腳本檔案來定位模組路徑。
                string[] aGuids = UnityEditor.AssetDatabase.FindAssets("UCL_GUILayoutDrawObject t:Script");
                foreach (string aGuid in aGuids)
                {
                    string aPath = UnityEditor.AssetDatabase.GUIDToAssetPath(aGuid);
                    if (aPath.Contains("UCL_Core"))
                    {
                        // 範例路徑: Assets/UCL/UCL_Core/UCL_Core_Scripts/UICore/UCL_GUILayoutDrawObject.cs
                        int aIndex = aPath.IndexOf("UCL_Core");
                        s_UCLCorePath = aPath.Substring(0, aIndex + "UCL_Core".Length);
                        break;
                    }
                }
            }
#endif
            return s_UCLCorePath;
        }
    }

}
