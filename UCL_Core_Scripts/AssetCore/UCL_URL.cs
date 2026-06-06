
// AutoHeader
// to change the auto header please go to AutoHeader.cs
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// [職責] 定義「URL 前綴解析器」的契約。每一個下游模組可以實作此介面，註冊自己專屬的 prefix（例如 "ucl_core"、"eov_docs"）。
    /// [物理意義] 將「prefix → 本地檔案路徑 / 雲端 URL」的映射策略，從 UCL_URL 主流程外部化，避免核心程式碼認識任何特定下游專案。
    /// [數值影響] 不直接影響數值，但決定了 HelpURL 連結在當前編譯目標下會被導向到哪一份實體文件。
    /// [設計備註] Editor / Build 的差異留給「註冊端」用 #if UNITY_EDITOR 自行決定，Resolver 介面本身只負責單一解析行為，不重複攜帶兩種模式的策略。
    /// </summary>
    public interface IUCL_UrlPrefixResolver
    {
        /// <summary>
        /// [職責] 此 Resolver 對應的 prefix 字串（不含結尾的冒號）。
        /// [物理意義] 例如 "ucl_core" 對應 URL "ucl_core:Docs~/..."，比對時忽略大小寫。
        /// </summary>
        string Prefix { get; }

        /// <summary>
        /// [職責] 將相對路徑轉換為「當前編譯目標適用」的目標字串。
        /// [物理意義]
        ///   - Editor 編譯：通常回傳本地絕對路徑，便於以系統預設應用程式直接開啟本地 Markdown。
        ///   - Build 編譯：通常回傳雲端 URL（例如 GitHub blob 連結），讓玩家透過瀏覽器存取。
        /// [呼叫前置] UCL_URL 會先把 {lang} 佔位符替換為當前語系代碼，再把替換後的相對路徑交給此方法 — 因此 relativePath 不會再含有 {lang}。
        /// </summary>
        /// <param name="relativePath">prefix 後方的相對路徑（不含 "prefix:" 段落）；{lang} 已被替換。</param>
        /// <returns>解析後的完整 URL 或本地路徑；不支援解析時可回傳 null。</returns>
        string Resolve(string relativePath);

        /// <summary>
        /// [職責] 檢查指定的相對路徑（{lang} 已替換）對應的文件是否實際存在。
        /// [物理意義] 用於驅動「找不到當前語系時自動回退到 en」的 fallback 機制：
        ///   - Editor：通常以 File.Exists 檢查本地 submodule 文件。
        ///   - Build：通常查詢 build-time 產生的 manifest。
        /// [預設行為] 回傳 true（不啟用 fallback，維持原始 URL）。要啟用 fallback 請覆寫此方法。
        /// [呼叫時機] UCL_URL 在 Resolve 之前呼叫；命中失敗時會嘗試把 lang 換成 "en" 再呼叫一次。
        /// </summary>
        /// <param name="relativePath">與 Resolve 同一份相對路徑（{lang} 已替換）。</param>
        /// <returns>true 表示該路徑指向的文件存在；false 觸發 fallback。</returns>
        bool Exists(string relativePath) => true;
    }

    /// <summary>
    /// [職責] 以 Lambda 委派為基礎的 Resolver 輕量實作，方便下游不必為了一個 prefix 多寫一個 class。
    /// [物理意義] 將「解析策略」與「存在檢查策略」以 Func 注入，行為上等同於實作 IUCL_UrlPrefixResolver。
    /// [使用情境] 大部分下游模組（含 UCL_Core 自己）只需要簡單的 Path.Combine / 字串拼接，使用此類別即可。
    /// </summary>
    public sealed class UCL_UrlPrefixResolver : IUCL_UrlPrefixResolver
    {
        // [欄位] 解析委派；建構後不可變，由建構式注入。可為 null（視為 Resolver 對當前環境不提供解析）。
        private readonly Func<string, string> m_Resolver;

        // [欄位] 存在檢查委派；可為 null（代表此 Resolver 不參與 fallback，預設視為「永遠存在」）。
        private readonly Func<string, bool> m_ExistsChecker;

        /// <summary>
        /// [職責] 提供此 Resolver 對應的 prefix 字串。
        /// [物理意義] 由建構式注入，建立後不可變，作為 UCL_URL 註冊表的 key。
        /// </summary>
        public string Prefix { get; }

        /// <summary>
        /// [職責] 建構一個以委派為策略的 Resolver。
        /// [使用慣例] 註冊端應使用 #if UNITY_EDITOR 切換要傳入哪一個解析委派（Editor 用本地路徑、Build 用雲端 URL）。
        /// [選用功能] 若要啟用「找不到當前語系時 fallback 到 en」，請傳入 existsChecker；不傳則維持原本「不檢查存在」的行為。
        /// </summary>
        /// <param name="prefix">prefix 字串（不含結尾冒號），例如 "ucl_core"。</param>
        /// <param name="resolver">解析委派；負責將相對路徑轉換為當前編譯目標適用的 URL / 路徑。</param>
        /// <param name="existsChecker">（選用）存在檢查委派。Editor 端常用 File.Exists；Build 端常查 build-time manifest。為 null 時 Exists 永遠回傳 true（無 fallback）。</param>
        public UCL_UrlPrefixResolver(string prefix, Func<string, string> resolver, Func<string, bool> existsChecker = null)
        {
            Prefix = prefix;
            m_Resolver = resolver;
            m_ExistsChecker = existsChecker;
        }

        /// <inheritdoc />
        public string Resolve(string relativePath) => m_Resolver?.Invoke(relativePath);

        /// <inheritdoc />
        public bool Exists(string relativePath) => m_ExistsChecker?.Invoke(relativePath) ?? true;
    }

    /// <summary>
    /// [職責] 提供 URL 與路徑解析功能，支援由下游模組註冊的自定義前綴與多語系 fallback。
    /// [物理意義] 處理 "prefix:" 形式的 URL，依當前執行環境（Editor / Build）派發給對應的 Resolver；同時統一處理 {lang} 替換與 en 回退。
    /// [數值影響] 不修改任何遊戲狀態，僅決定 HelpURL / OpenURL 最終會打開的目標檔案或網址。
    /// </summary>
    public static class UCL_URL
    {
        // [註冊表] prefix → resolver 對應表；key 比對忽略大小寫。
        // [初始化時機] 在 type initializer (static ctor) 階段一次填入 UCL_Core 自身的預設 Resolver；下游模組可於任意時機透過 RegisterResolver 追加。
        private static readonly Dictionary<string, IUCL_UrlPrefixResolver> s_Resolvers
            = new Dictionary<string, IUCL_UrlPrefixResolver>(StringComparer.OrdinalIgnoreCase);

        // [常數] fallback 目標語系。en 文件作為「保底文件」，其他語系若缺檔則回退到此。
        // [物理意義] 與 UCL_LocalizeAsset 的 DefaultLang 對齊；改動時請同步檢查。
        private const string FALLBACK_LANG = "en";

        // [註記] UCL_Core 自家的 "ucl_core:" prefix 由 UCL_CoreDocsBootstrap 透過 UCL_DocsModuleRegistry 註冊，
        //       與其他下游模組走同一條路徑，不再於此 static ctor 中做硬編碼註冊。

        /// <summary>
        /// [職責] 註冊（或覆寫）一個自定 prefix 的 Resolver。
        /// [物理意義] 下游模組（例如非開源遊戲專案）可藉此將自家文件路徑接入 UCL_URL，而不需修改 UCL_Core 程式碼。
        /// [覆寫策略] 同 prefix 後註冊勝出，便於下游覆寫核心預設行為；覆寫時會輸出 Warning 以利除錯。
        /// </summary>
        /// <param name="resolver">欲註冊的 Resolver 實例；resolver 為 null 或 Prefix 為空時直接忽略。</param>
        public static void RegisterResolver(IUCL_UrlPrefixResolver resolver)
        {
            // [輸入防護] 拒絕 null 或無效 prefix，避免污染註冊表。
            if (resolver == null || string.IsNullOrEmpty(resolver.Prefix)) return;

            // [覆寫提示] 已存在不同實例時記 Warning，但仍允許覆寫。
            if (s_Resolvers.TryGetValue(resolver.Prefix, out var aExisting) && !ReferenceEquals(aExisting, resolver))
            {
                Debug.LogWarning($"[UCL_URL] Prefix '{resolver.Prefix}' resolver overridden by {resolver.GetType().FullName}.");
            }

            s_Resolvers[resolver.Prefix] = resolver;
        }

        /// <summary>
        /// [職責] 移除指定 prefix 的 Resolver。
        /// </summary>
        /// <param name="prefix">欲移除的 prefix。</param>
        /// <returns>是否實際從註冊表移除一筆條目。</returns>
        public static bool UnregisterResolver(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return false;
            return s_Resolvers.Remove(prefix);
        }

        /// <summary>
        /// [職責] 查詢指定 prefix 是否已被註冊。
        /// </summary>
        /// <param name="prefix">prefix 字串（不含冒號）。</param>
        public static bool HasResolver(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return false;
            return s_Resolvers.ContainsKey(prefix);
        }

        /// <summary>
        /// [職責] 解析 URL 字串，依序處理「自定 prefix → {lang} 替換 → 存在檢查 / fallback → Resolve → 本地路徑補全」。
        /// [計算邏輯]
        /// 1. 嘗試從字串拆出 "prefix:"（排除 "://" 形式的 protocol）並查詢 s_Resolvers。
        /// 2. 命中時把 {lang} 在相對路徑內替換為當前語系。
        /// 3. 呼叫 Resolver.Exists 檢查；失敗且非 en 時把 lang 換成 en 再 Exists；仍失敗則維持原值（最終由 Resolve 回傳的 URL 自行處理 404）。
        /// 4. 呼叫 Resolver.Resolve 取得最終路徑 / URL。
        /// 5. 若結果不含 "://"，視為本地路徑並轉為絕對路徑；Editor 下若檔案不存在則嘗試以 en 替換語系碼（針對「無 prefix」的舊式 URL 仍保留此行為）。
        /// </summary>
        /// <param name="url">原始 URL 字串或相對路徑。</param>
        /// <returns>解析後的完整 URL 或本地絕對路徑。</returns>
        public static string ResolveURL(string url)
        {
            // [快速返回] 空字串或 null 直接回傳，避免後續判斷誤觸 IndexOf。
            if (string.IsNullOrEmpty(url)) return url;

            // [預先取出] 後續多處共用，避免重複 property 存取。
            string aLang = UCL.Core.Game.UCL_LocalizeService.CurLang;

            // 區塊職責：從 URL 切出 "prefix:" 並查表 → {lang} 替換 → Exists 檢查 / fallback → Resolve。
            // 物理意義：prefix 為「不含 ://」的命名空間標識；http/https 等 protocol 因為冒號後接 "//" 而被排除。
            // 數值影響：唯一決定哪一個 Resolver 會被派發，從而決定後續路徑/URL 的根；fallback 機制讓非 en 玩家在缺檔時自動取得 en 文件。
            int aColonIdx = url.IndexOf(':');
            if (aColonIdx > 0)
            {
                // [protocol 排除] 若冒號後緊跟 "//"（如 https://、file://），視為標準 URL，不視為自定 prefix。
                bool aIsProtocol = url.Length > aColonIdx + 2
                                   && url[aColonIdx + 1] == '/'
                                   && url[aColonIdx + 2] == '/';

                if (!aIsProtocol)
                {
                    string aPrefix = url.Substring(0, aColonIdx);
                    if (s_Resolvers.TryGetValue(aPrefix, out var aResolver))
                    {
                        // [計算邏輯] 取出 "prefix:" 之後的相對路徑。
                        string aRelativePath = url.Substring(aColonIdx + 1);

                        // [{lang} 前置替換] 在交給 Resolver 之前先把 {lang} 替換為當前語系，使 Exists / Resolve 拿到的 relativePath 已是「最終的語系版本」。
                        if (aRelativePath.Contains("{lang}"))
                        {
                            aRelativePath = aRelativePath.Replace("{lang}", aLang);
                        }

                        // [存在檢查 + fallback] 當前語系檔不存在 → 嘗試以 en 取代後再檢查一次。
                        // 條件：Exists 檢查失敗（Resolver 預設都是 true，除非主動覆寫）、且當前語系非 fallback 目標自身、且 relativePath 確實含有當前語系字串。
                        if (aLang != FALLBACK_LANG && aRelativePath.Contains(aLang) && !aResolver.Exists(aRelativePath))
                        {
                            string aFallbackPath = aRelativePath.Replace(aLang, FALLBACK_LANG);
                            if (aResolver.Exists(aFallbackPath))
                            {
                                aRelativePath = aFallbackPath;
                            }
                        }

                        // [編譯期分流] Editor / Build 差異由 Resolver 註冊端在 #if UNITY_EDITOR 中決定，此處無需感知。
                        string aResolved = aResolver.Resolve(aRelativePath);
                        if (!string.IsNullOrEmpty(aResolved)) url = aResolved;
                    }
                }
            }

            // 區塊職責：本地化佔位符替換（後備）。
            // 物理意義：若 URL 沒被任何 prefix resolver 處理，{lang} 仍可能殘留於原 URL 中，於此補上替換。
            // 數值影響：影響無 prefix 的舊式 URL 的最終目標檔案。
            if (url.Contains("{lang}"))
            {
                url = url.Replace("{lang}", aLang);
            }

            // 區塊職責：本地路徑補全與 en 回退（針對無 prefix 的舊式 URL）。
            // 物理意義：若解析結果非雲端 URL，則視為相對於專案根的路徑並轉為絕對路徑；Editor 下若當前語系檔案缺失，回退到 en。
            // 數值影響：避免因尚未翻譯的語系版本造成 404；不影響非 Editor 流程。
            if (!url.Contains("://"))
            {
                // 區塊職責：".claude/" 開頭的相對路徑特例解析（Claude Code skill / 設定目錄）。
                // 物理意義：".claude/" 是 Claude Code 的通用約定，固定位於 git repo 根；而 process CWD（Unity Editor 下為 Unity 專案目錄）
                //          對「Unity 專案是 repo 子目錄」的結構（如 EOV 的 CardGame/）而言並非 repo 根，直接 GetFullPath 會把路徑接到專案目錄 → 檔案不存在。
                // 數值影響：往 CWD 的祖先目錄尋找含 ".claude" 資料夾者當 repo 根再接；找不到則回退原 CWD 行為（不影響其他無 prefix 路徑）。
                string aNormalized = url.Replace('\\', '/');
                if (aNormalized.StartsWith(".claude/"))
                {
                    string aRepoRoot = FindRepoRoot();
                    url = string.IsNullOrEmpty(aRepoRoot)
                        ? System.IO.Path.GetFullPath(url)
                        : System.IO.Path.GetFullPath(System.IO.Path.Combine(aRepoRoot, aNormalized));
                }
                else
                {
                    // [計算邏輯] 透過 GetFullPath 將相對路徑轉換為絕對路徑。
                    url = System.IO.Path.GetFullPath(url);
                }

#if UNITY_EDITOR
                if (!System.IO.File.Exists(url) && url.Contains(aLang))
                {
                    string aFallbackUrl = url.Replace(aLang, FALLBACK_LANG);
                    if (System.IO.File.Exists(aFallbackUrl)) url = aFallbackUrl;
                }
#endif
            }

            return url;
        }

        /// <summary>
        /// [職責] 定位當前 git repo 根目錄 — 供 "repo:" prefix resolver 與 ".claude/" 相對路徑特例共用的單一錨點來源。
        /// [物理意義] 從 process CWD 逐層往上找：優先回傳第一個含 ".claude" 資料夾的祖先 (agent-facing root, 與 install_skills.py 同慣例);
        ///           退而求其次回傳第一個含 ".git" (資料夾或 submodule/worktree 的 .git 檔) 的祖先。
        /// [數值影響] 純路徑查找, 不觸碰遊戲狀態; 兩種標記都找不到時回傳 null, 由呼叫端 (resolver / 特例) 決定回退行為。
        /// [為何共用] 讓 "repo:docs/..." 與 ".claude/skills/..." 解析到完全相同的 repo 根, 避免兩套 root-finding 邏輯漂移。
        /// </summary>
        /// <returns>repo 根的絕對路徑; 找不到任何標記時為 null。</returns>
        public static string FindRepoRoot()
        {
            // 區塊職責：自 CWD 逐層往上, 優先 .claude (agent root), 次選 .git (repo root)。
            // 物理意義：.claude 為 Claude Code 約定的 agent-facing 根; .git 為 git 物理根 (submodule/worktree 下為檔案而非資料夾)。
            // 數值影響：先記下首個 .git 當 fallback, 但持續往上找 .claude — 命中 .claude 立即勝出 (與 install_skills.py 偏好一致)。
            var aDir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            string aGitFallback = null;
            while (aDir != null)
            {
                if (System.IO.Directory.Exists(System.IO.Path.Combine(aDir.FullName, ".claude")))
                {
                    return aDir.FullName;
                }
                if (aGitFallback == null)
                {
                    // [.git 偵測] 資料夾 (一般 repo 根) 或檔案 (submodule / worktree 的 gitdir 指標) 皆算命中。
                    string aGitPath = System.IO.Path.Combine(aDir.FullName, ".git");
                    if (System.IO.Directory.Exists(aGitPath) || System.IO.File.Exists(aGitPath))
                    {
                        aGitFallback = aDir.FullName;
                    }
                }
                aDir = aDir.Parent;
            }
            return aGitFallback;
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
