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

    /// <summary>
    /// [職責] 將任一絕對 / 專案相對路徑，表達成「相對於 UCL_Core 根」的 forward-slash 相對路徑。
    /// [物理意義] 不同專案把 UCL_Core 掛在不同位置（Assets/Plugins/UCL_Core、Assets/UCL/UCL_Core…），
    ///           直接記實體路徑會隨 install 位置漂移。本 helper 把 install-path 那段剝掉，只留「相對 core 根」的穩定描述，
    ///           讓寫進共享 submodule 的資料（如 docs manifest header）跨專案完全一致、零 git churn。
    /// [計算邏輯] 把 CorePath 與輸入都 GetFullPath 正規化成絕對路徑（消弭分隔符 / 相對基準差異）後比對前綴，命中則截去。
    /// [數值影響] 不影響遊戲狀態；僅產生描述字串。
    /// </summary>
    /// <returns>相對 core 根的 forward-slash 路徑（path == core 根時為空字串）；輸入不在 core 之下或 CorePath 未解析時回 null。</returns>
    public static string ToCoreRelative(string iPath)
    {
#if UNITY_EDITOR
        if (string.IsNullOrEmpty(iPath)) return null;
        string aCore = CorePath;
        if (string.IsNullOrEmpty(aCore)) return null;

        // [正規化] GetFullPath 讓專案相對（Assets/…）與絕對路徑都落到同一絕對基準，再統一 forward-slash 比對，避免 Windows 反斜線 / 大小寫誤判。
        string aCoreFull = System.IO.Path.GetFullPath(aCore).Replace('\\', '/').TrimEnd('/');
        string aPathFull = System.IO.Path.GetFullPath(iPath).Replace('\\', '/');
        if (!aPathFull.StartsWith(aCoreFull, System.StringComparison.OrdinalIgnoreCase)) return null;
        return aPathFull.Substring(aCoreFull.Length).TrimStart('/');
#else
        return null;
#endif
    }

    /// <summary>
    /// [職責] 把路徑表達成 "ucl_core:&lt;rel&gt;" URL token — 與 UCL_URL 既有的 ucl_core: / repo: prefix 慣例同語言。
    /// [物理意義] 供「需要 install-independent 描述 UCL_Core 內某路徑」的場景使用（manifest header / HelpURL / agent 文件）。
    /// </summary>
    /// <returns>"ucl_core:&lt;rel&gt;"；輸入不在 core 之下時回 null。</returns>
    public static string ToCoreRelativeUrl(string iPath)
    {
        string aRel = ToCoreRelative(iPath);
        return aRel == null ? null : "ucl_core:" + aRel;
    }

}
