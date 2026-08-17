public static class UCL_EditorPath
{

    /// <summary>
    /// [職責] UCL_Core 模組根的**專案相對**路徑（例：<c>Assets/Plugins/UCL_Core</c>）。
    /// [物理意義] 薄殼 —— 實作已收斂到 <see cref="UCL.Core.EditorLib.UCL_RepoPath.UCLCoreRelative"/>，
    ///           本屬性只為既有呼叫端相容而保留。要絕對路徑用 <c>UCL_RepoPath.UCLCoreDir</c>；
    ///           要 Tools~ 底下的腳本用 <c>UCL_RepoPath.CoreTool(name)</c>。
    /// [計算邏輯] 舊版走 <c>AssetDatabase.FindAssets("UCL_GUILayoutDrawObject t:Script")</c> ——
    ///           ① **main-thread only**（逼出 UCL_AwakeningService 那條「只能在主執行緒呼叫」）
    ///           ② 靠「特定腳本檔名 + 路徑含 UCL_Core」的啟發式，撞名不會叫
    ///           ③ 與絕對路徑那份是**兩個獨立解析器**，不一致時兩邊都不報錯
    ///           現改為單一來源，三個問題一起消失。
    /// [數值影響] **維持舊契約：解析失敗回 null，不 throw。**
    ///           🩸 上一版我讓它跟著 <c>UCL_RepoPath.UCLCoreRelative</c> 一起 throw，那是錯的 ——
    ///           現存 24 個呼叫端裡有數個寫著 <c>if (IsNullOrEmpty(core)) …</c> 的優雅降級，
    ///           其中 <c>UCL_CoreDocsBootstrap</c> 更是在**註冊期**（domain reload）跑的：
    ///           在那裡 throw 會把「找不到 core」升級成「Editor 初始化炸掉」。
    ///           薄殼的職責是**保持呼叫端行為不變**，不是順手改契約。
    ///           要「找不到就大聲停住」的語意 → 直接用 <c>UCL_RepoPath.UCLCoreRelative</c>（會 throw）。
    /// </summary>
    public static string CorePath
    {
        get
        {
            try { return UCL.Core.EditorLib.UCL_RepoPath.UCLCoreRelative; }
            catch (System.IO.DirectoryNotFoundException) { return null; }
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
