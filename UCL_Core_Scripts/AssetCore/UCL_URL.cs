
// AutoHeader
// to change the auto header please go to AutoHeader.cs
using System;
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// [職責] 提供 URL 與路徑解析功能，支持自定義前綴與環境適配。
    /// [物理意義] 處理特定的 URL 前綴（如 ucl_core:）並轉換為實體路徑或網路連結，確保跨專案與跨環境（Editor/Build）的可用性。
    /// </summary>
    public static class UCL_URL
    {
        /// <summary>
        /// [職責] 解析 URL 字串，處理自定義前綴與本地路徑補全。
        /// [計算邏輯] 
        /// 1. 處理 "ucl_core:" 前綴：在 Editor 中導向本地 Docs~ 資料夾；在 Build 中導向 GitHub 鏡像。
        /// 2. 處理本地路徑：若不包含 "://" 則視為本地路徑並補全為絕對路徑。
        /// </summary>
        /// <param name="url">原始 URL 字串或相對路徑。</param>
        /// <returns>解析後的完整 URL 或路徑。</returns>
        public static string ResolveURL(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;

            // [計算邏輯] 處理相對於 UCL_Core 的路徑前綴 "ucl_core:"。
            // [物理意義] 確保核心模組文件連結在不同專案與發佈版本中均有效。
            if (url.StartsWith("ucl_core:", StringComparison.OrdinalIgnoreCase))
            {
                string aRelativePath = url.Substring("ucl_core:".Length);
#if UNITY_EDITOR
                // [物理意義] 在 Editor 環境下，優先讀取本地的 Docs~ 隱藏文件夾，確保開發時離線可用。
                // [注意] 使用 UCL_EditorPath.CorePath 自動定位模組根目錄。
                url = System.IO.Path.Combine(UCL_EditorPath.CorePath, aRelativePath);
#else
                // [物理意義] 在 Build 版本中，透過 GitHub 鏡像跳轉至雲端文件。
                // [計算邏輯] 將相對路徑轉換為 GitHub 的 blob 連結 (Dev 分支)。
                url = "https://github.com/tim099/UCL_Core/blob/Dev/" + aRelativePath;
#endif
            }

            // [物理意義] 如果字串中不包含 "://"（常見於 http:// 或 https:// 等協定），則視為本地檔案路徑。
            if (!url.Contains("://"))
            {
                // [計算邏輯] 透過 GetFullPath 將相對於專案根目錄的路徑轉換為絕對路徑。
                url = System.IO.Path.GetFullPath(url);
            }

            return url;
        }

        /// <summary>
        /// [職責] 解析 URL 並使用系統預設程式開啟。
        /// </summary>
        /// <param name="url">原始 URL 或路徑。</param>
        public static void OpenURL(string url)
        {
            url = ResolveURL(url);
            if (!string.IsNullOrEmpty(url))
            {
                // [職責] 調用 Unity API 開啟目標 URL 或本地檔案。
                Application.OpenURL(url);
            }
        }
    }
}
