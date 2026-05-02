using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// [職責] 提供 Build 期 UCL_Core/Docs~/ 的 manifest 查詢，使 `ucl_core:` prefix 在 Build 模式也能做「lang→en」fallback。
    /// [物理意義] manifest 是純文字檔（每行一個相對路徑），由 UCL_LocalizedDocsManifestGenerator 在 build 前掃描 UCL_Core/Docs~/ 產生並寫入 UCL_Core/Resources/。
    ///             Runtime 透過 Resources.Load 讀回，組成 HashSet 後 O(1) 查詢路徑是否存在。
    /// [數值影響] 不影響任何遊戲狀態；僅作為 UCL_URL 的 ucl_core resolver 在 Build 模式做 lang fallback 的判斷依據。
    /// [設計呼應] 與 RCG_LocalizedDocsManifest 為同形雙生 — UCL_Core 自家管自家的 manifest，下游模組（如 EoV）各自管各自的。
    /// </summary>
    public static class UCL_LocalizedDocsManifest
    {
        // [常數] manifest 檔在 Resources 內的名稱（不含 .txt 副檔名 — 因為 Resources.Load 不要副檔名）。
        // [物理意義] 對應 UCL_Core/Resources/UCL_LocalizedDocsManifest.txt；改動時請同步 Generator 與 Build hook。
        public const string ResourcesPath = "UCL_LocalizedDocsManifest";

        // [快取] 第一次查詢時從 Resources 載入並建表，後續 O(1) 查詢。
        // [初始化策略] lazy load：若 Build 沒附 manifest（資料缺失或 Generator 沒跑），s_Paths 會是空的 HashSet，所有 Contains 查詢回 false → 沒有 fallback 行為（與沒 manifest 等效）。
        private static HashSet<string> s_Paths = null;

        // [狀態] 是否已嘗試載入；用於避免重複 Resources.Load 失敗時的 LogWarning 噪音。
        private static bool s_Loaded = false;

        /// <summary>
        /// [職責] 查詢給定的相對路徑是否存在於 manifest。
        /// [物理意義] 路徑格式應與 manifest 內一致 — 從 UCL_Core 根起算的 forward-slash 路徑（例如 "Docs~/zh-Hant/Workflows/HelpURL_Workflow.md"）。
        /// [使用情境] 由 UCL_URL 內建的 ucl_core resolver 在 Build 模式呼叫；若回 false，UCL_URL 會嘗試把 lang 換成 "en" 再查一次。
        /// </summary>
        /// <param name="relativePath">與 manifest 紀錄一致的相對路徑（Docs~/{lang}/.../*.md）。</param>
        /// <returns>true 表示 manifest 中含此路徑；false 表示缺失或 manifest 未載入。</returns>
        public static bool Contains(string relativePath)
        {
            // [防護] 空字串直接回 false。
            if (string.IsNullOrEmpty(relativePath)) return false;

            // [Lazy 初始化] 第一次查詢才載入；避免遊戲啟動就吃 Resources I/O。
            if (!s_Loaded) Load();

            // [路徑正規化] Windows 路徑分隔符 \ 統一成 /，與 manifest 內格式一致。
            string aNormalized = relativePath.Replace('\\', '/');
            return s_Paths != null && s_Paths.Contains(aNormalized);
        }

        /// <summary>
        /// [職責] 強制重新載入 manifest（清快取後重讀）。
        /// [使用情境] Editor 工具產生新 manifest 之後可呼叫此方法刷新；runtime 一般不需要。
        /// </summary>
        public static void Reload()
        {
            s_Loaded = false;
            s_Paths = null;
            Load();
        }

        /// <summary>
        /// [職責] 從 Resources 載入 manifest，逐行加入 HashSet。
        /// [計算邏輯] 以 \n 切行 → 去除空白 → 跳過空行與 '#' 開頭的註解行 → 加入集合。
        /// [錯誤處理] 找不到 TextAsset 時 s_Paths 為空 HashSet，後續 Contains 一律回 false（無 fallback、與沒有 manifest 行為一致）。
        /// </summary>
        private static void Load()
        {
            s_Loaded = true;

            // 區塊職責：從 Resources 取出 manifest TextAsset。
            // 物理意義：build 前由 UCL_LocalizedDocsManifestGenerator 寫入；runtime 透過 Resources.Load 取回。
            // 數值影響：若取不到（Generator 漏跑、Resources 路徑錯）則整個 fallback 機制等同未啟用。
            var aAsset = Resources.Load<TextAsset>(ResourcesPath);
            if (aAsset == null)
            {
                // [警告] 找不到 manifest 時印一次 Debug.LogWarning，方便整合期發現遺漏。
                // [非致命] 不 throw — 沒 manifest 只是失去 fallback，遊戲仍能執行。
                Debug.LogWarning($"[UCL_LocalizedDocsManifest] manifest TextAsset not found at Resources/{ResourcesPath}; ucl_core lang fallback disabled.");
                s_Paths = new HashSet<string>();
                return;
            }

            // 區塊職責：解析 manifest 內容。
            // 物理意義：每行一個相對路徑，路徑格式為 forward-slash；'#' 開頭視為註解，空行忽略。
            // 數值影響：HashSet 大小 = 文件總數，Build 時應在數百筆內，記憶體佔用可忽略。
            s_Paths = new HashSet<string>();
            var aLines = aAsset.text.Split('\n');
            foreach (var aLineRaw in aLines)
            {
                string aLine = aLineRaw.Trim();
                if (string.IsNullOrEmpty(aLine)) continue;
                if (aLine[0] == '#') continue;
                s_Paths.Add(aLine);
            }
        }

        /// <summary>
        /// [職責] 回傳當前 manifest 內含路徑數量；除錯 / 工具用。
        /// </summary>
        public static int Count
        {
            get
            {
                if (!s_Loaded) Load();
                return s_Paths?.Count ?? 0;
            }
        }
    }
}
