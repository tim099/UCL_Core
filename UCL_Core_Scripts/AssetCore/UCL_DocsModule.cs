using System;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// [職責] 描述一個「擁有自家多語系文件 + HelpURL prefix」的模組所需的全部配置。
    /// [物理意義] 將過去散落於 RCG_DocsResolver / UCL_URL static ctor / 各家 Manifest / Generator 的設定，集中為一個 plain data 物件，
    ///           讓任何下游模組只需填寫一份 config + 呼叫 <see cref="UCL_DocsModuleRegistry.Register"/> 即可同時得到：
    ///           1. UCL_URL 上的 prefix resolver（含 lang→en fallback）
    ///           2. Editor 與 Build 期共用的 manifest 讀取與產生
    ///           3. Build 前自動重產 manifest 的 PreprocessBuild hook
    /// [數值影響] 不影響任何遊戲狀態；僅作為文件解析與 manifest 工具鏈的設定來源。
    /// [設計取捨]
    ///   - 採「instance + descriptor」而非繼承：reader 是靜態查表 + lazy load，繼承會被 static 綁死，instance 反而更好擴充。
    ///   - DocsSubfolder 留空字串：用於專案層級的獨立 docs submodule（如 EmblemOfValorDocuments），其 manifest 條目沒有資料夾前綴；
    ///     非空（例如 "Docs~"）則用於 module 內附文件的場景（UCL_Core / UCL_Steam）。
    /// </summary>
    public sealed class UCL_DocsModule
    {
        /// <summary>
        /// [職責] HelpURL 的 prefix（不含結尾冒號）；UCL_URL 註冊表用此字串為 key。
        /// [物理意義] 例如 "ucl_core" 對應 [HelpURL("ucl_core:Docs~/{lang}/...")]。
        /// </summary>
        public string Prefix;

        /// <summary>
        /// [職責] Editor 端定位「文件解析根目錄」的提供者。
        /// [物理意義] resolver 會以 <see cref="System.IO.Path.Combine"/>(ResolveBaseProvider(), relPath) 取得本地檔案路徑；
        ///           manifest scan 也以此為起點計算相對路徑。
        /// [呼叫慣例] 僅在 Editor 端使用；Build 端走 <see cref="BuildBaseUrl"/>。為 null 或回傳空字串時對應模組的 Editor fallback 失效。
        /// </summary>
        public Func<string> ResolveBaseProvider;

        /// <summary>
        /// [職責] 真正掃描 .md 檔案的子資料夾（相對於 ResolveBase）。
        /// [物理意義]
        ///   - "Docs~"：模組內含的文件資料夾（Unity 會排除 ~ 結尾的 folder，但實體檔案仍存在於 repo）。
        ///   - 空字串：ResolveBase 自身就是 docs 根（例如外部 docs submodule）。
        /// [數值影響] 影響 manifest 條目的字首與 generator 的 scan root。
        /// </summary>
        public string DocsSubfolder = string.Empty;

        /// <summary>
        /// [職責] Editor 端 manifest 寫入目標 Resources 資料夾的提供者。
        /// [物理意義] 必須位於某個 Unity 認得的 Resources/ 之下，runtime 才能透過 Resources.Load 取得。
        ///           例如 UCL_Core 寫到 "<UCL_Core>/Resources/"，EoV 寫到 "<CardGame>/Assets/Resources/"。
        /// </summary>
        public Func<string> ResourcesFolderProvider;

        /// <summary>
        /// [職責] manifest 在 Resources 內的檔名（不含 .txt 副檔名）。
        /// [物理意義] 必須在所有註冊模組之間唯一；reader 以此 key 快取 HashSet。
        /// </summary>
        public string ManifestResourceName;

        /// <summary>
        /// [職責] Build 端的雲端文件根 URL。
        /// [物理意義] 例如 "https://github.com/tim099/UCL_Core/blob/Dev/"；resolver 在 Build 時直接做字串拼接。
        /// </summary>
        public string BuildBaseUrl;

        /// <summary>
        /// [職責] 顯示用名稱（log 與選單訊息）。可省略，預設為 Prefix。
        /// </summary>
        public string DisplayName;
    }

    /// <summary>
    /// [職責] 集中管理所有 <see cref="UCL_DocsModule"/> 的註冊；同時把 prefix resolver 註冊到 <see cref="UCL_URL"/>。
    /// [物理意義] 由各下游模組於 [InitializeOnLoadMethod] / [RuntimeInitializeOnLoadMethod] 階段呼叫 Register；
    ///           Editor 端的 manifest generator / build hook 會在執行時迭代此清單，做到「新增模組只要寫一份 bootstrap」。
    /// [數值影響] 不直接影響遊戲狀態，但決定了 HelpURL 解析、manifest 產生、build 期 fallback 的可用範圍。
    /// </summary>
    public static class UCL_DocsModuleRegistry
    {
        // [註冊表] 以 Prefix 為 key 去重；第一個註冊者勝出，後續覆寫會 LogWarning。
        // [物理意義] 註冊行為包含兩個副作用：(1) 加入此 List；(2) 透過 UCL_URL.RegisterResolver 接上 URL 解析鏈。
        private static readonly Dictionary<string, UCL_DocsModule> s_Modules
            = new Dictionary<string, UCL_DocsModule>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// [職責] 取得目前所有已註冊的模組（唯讀）。
        /// [使用情境] manifest generator 與 build hook 迭代用。
        /// </summary>
        public static IEnumerable<UCL_DocsModule> All => s_Modules.Values;

        /// <summary>
        /// [職責] 註冊一個 docs 模組。同 Prefix 重複呼叫時，後者覆寫前者並 LogWarning。
        /// [計算邏輯]
        ///   1. 驗證必要欄位（Prefix、ManifestResourceName、BuildBaseUrl）。
        ///   2. 寫入 s_Modules。
        ///   3. 建立並註冊 UCL_UrlPrefixResolver：
        ///      - Editor 端：以 ResolveBaseProvider 為根做 Path.Combine + File.Exists。
        ///      - Build 端：以 BuildBaseUrl 拼接 + 查 manifest。
        /// [覆寫策略] 重複註冊時，後者完全取代前者於 s_Modules 與 UCL_URL 中的條目。
        /// </summary>
        /// <param name="iModule">要註冊的模組描述；任何必填欄位為空時跳過註冊並 LogError。</param>
        public static void Register(UCL_DocsModule iModule)
        {
            // [輸入防護] 拒絕 null 或欄位缺漏的註冊，避免在後續查表時噴未對齊的 NullRef。
            if (iModule == null)
            {
                Debug.LogError("[UCL_DocsModuleRegistry] Register called with null module.");
                return;
            }
            if (string.IsNullOrEmpty(iModule.Prefix))
            {
                Debug.LogError("[UCL_DocsModuleRegistry] Register failed: Prefix is empty.");
                return;
            }
            if (string.IsNullOrEmpty(iModule.ManifestResourceName))
            {
                Debug.LogError($"[UCL_DocsModuleRegistry] Register failed: ManifestResourceName is empty for prefix '{iModule.Prefix}'.");
                return;
            }
            if (string.IsNullOrEmpty(iModule.BuildBaseUrl))
            {
                Debug.LogError($"[UCL_DocsModuleRegistry] Register failed: BuildBaseUrl is empty for prefix '{iModule.Prefix}'.");
                return;
            }

            // [覆寫提示] 同 prefix 已存在 → LogWarning 但允許覆寫，便於下游臨時替換實作測試。
            if (s_Modules.ContainsKey(iModule.Prefix))
            {
                Debug.LogWarning($"[UCL_DocsModuleRegistry] Prefix '{iModule.Prefix}' already registered; overriding.");
            }
            s_Modules[iModule.Prefix] = iModule;

            // 區塊職責：建立並註冊 UCL_URL 上的 prefix resolver。
            // 物理意義：Editor 接本地檔案路徑（含 lang→en fallback 的 File.Exists），Build 接雲端 URL（fallback 查 manifest）。
            // 數值影響：影響 [HelpURL("<prefix>:...")] 點擊後的目標位置。
            UCL_DocsModule aModuleRef = iModule;
            UCL_URL.RegisterResolver(new UCL_UrlPrefixResolver(
                prefix: iModule.Prefix,
#if UNITY_EDITOR
                resolver: (aRelativePath) =>
                {
                    string aBase = aModuleRef.ResolveBaseProvider?.Invoke();
                    return string.IsNullOrEmpty(aBase) ? null : System.IO.Path.Combine(aBase, aRelativePath);
                },
                existsChecker: (aRelativePath) =>
                {
                    string aBase = aModuleRef.ResolveBaseProvider?.Invoke();
                    return !string.IsNullOrEmpty(aBase) && System.IO.File.Exists(System.IO.Path.Combine(aBase, aRelativePath));
                }
#else
                resolver: (aRelativePath) => aModuleRef.BuildBaseUrl + aRelativePath,
                existsChecker: (aRelativePath) => UCL_DocsModuleManifest.Contains(aModuleRef.ManifestResourceName, aRelativePath)
#endif
            ));
        }

        /// <summary>
        /// [職責] 以 prefix 取出已註冊的模組描述。
        /// </summary>
        /// <returns>命中時為對應的 module；找不到時為 null。</returns>
        public static UCL_DocsModule Get(string iPrefix)
        {
            if (string.IsNullOrEmpty(iPrefix)) return null;
            s_Modules.TryGetValue(iPrefix, out var aModule);
            return aModule;
        }
    }

    /// <summary>
    /// [職責] 提供 Build 期所有已註冊模組的 manifest 查詢，使 prefix resolver 在 Build 模式也能做「lang→en」fallback。
    /// [物理意義] manifest 為純文字檔（每行一個相對路徑），由 generator 在 build 前掃描各模組的 docs 資料夾產生並寫入該模組設定的 Resources 路徑。
    ///             Runtime 透過 Resources.Load 讀回，組成 HashSet 後 O(1) 查詢路徑是否存在。
    /// [數值影響] 不影響任何遊戲狀態；僅作為 Build 模式 fallback 的判斷依據。
    /// [設計取捨] 改為以 ManifestResourceName 為 key 的快取字典，多模組共用同一份 reader，避免每個模組各自寫一份 static class。
    /// </summary>
    public static class UCL_DocsModuleManifest
    {
        // [快取] resourceName → 路徑集合；lazy load，第一次查詢時才從 Resources 載入。
        private static readonly Dictionary<string, HashSet<string>> s_Cache
            = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// [職責] 查詢指定 manifest 內是否包含某個相對路徑。
        /// [物理意義] 路徑格式應與 manifest 內條目一致（forward-slash、相對於該模組 ResolveBase）。
        /// [呼叫時機] 由 UCL_URL 的 prefix resolver 在 Build 模式呼叫。
        /// </summary>
        /// <param name="iManifestResourceName">module 的 ManifestResourceName（不含 .txt）。</param>
        /// <param name="iRelativePath">與 manifest 條目一致的相對路徑。</param>
        /// <returns>true 表示存在；false 表示缺失或 manifest 載入失敗。</returns>
        public static bool Contains(string iManifestResourceName, string iRelativePath)
        {
            if (string.IsNullOrEmpty(iManifestResourceName) || string.IsNullOrEmpty(iRelativePath)) return false;
            var aSet = GetOrLoad(iManifestResourceName);
            string aNormalized = iRelativePath.Replace('\\', '/');
            return aSet != null && aSet.Contains(aNormalized);
        }

        /// <summary>
        /// [職責] 強制重新載入指定 manifest（清快取後重讀）。
        /// [使用情境] Editor 工具產生新 manifest 之後可呼叫此方法刷新；runtime 一般不需要。
        /// </summary>
        public static void Reload(string iManifestResourceName)
        {
            if (string.IsNullOrEmpty(iManifestResourceName)) return;
            s_Cache.Remove(iManifestResourceName);
            GetOrLoad(iManifestResourceName);
        }

        /// <summary>
        /// [職責] 回傳指定 manifest 的條目數，除錯用。
        /// </summary>
        public static int Count(string iManifestResourceName)
        {
            if (string.IsNullOrEmpty(iManifestResourceName)) return 0;
            var aSet = GetOrLoad(iManifestResourceName);
            return aSet?.Count ?? 0;
        }

        // [計算邏輯] 取出快取，無則 Resources.Load 並逐行解析；空檔 / 缺檔回空 HashSet 並 LogWarning 一次。
        private static HashSet<string> GetOrLoad(string iName)
        {
            if (s_Cache.TryGetValue(iName, out var aSet)) return aSet;

            var aAsset = Resources.Load<TextAsset>(iName);
            aSet = new HashSet<string>();
            if (aAsset == null)
            {
                Debug.LogWarning($"[UCL_DocsModuleManifest] manifest TextAsset not found at Resources/{iName}; lang fallback disabled for this module.");
            }
            else
            {
                var aLines = aAsset.text.Split('\n');
                foreach (var aLineRaw in aLines)
                {
                    string aLine = aLineRaw.Trim();
                    if (string.IsNullOrEmpty(aLine)) continue;
                    if (aLine[0] == '#') continue;
                    aSet.Add(aLine);
                }
            }
            s_Cache[iName] = aSet;
            return aSet;
        }
    }
}
