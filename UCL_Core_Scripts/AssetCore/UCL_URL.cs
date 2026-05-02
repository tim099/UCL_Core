
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
        /// </summary>
        /// <param name="relativePath">prefix 後方的相對路徑（不含 "prefix:" 段落）。</param>
        /// <returns>解析後的完整 URL 或本地路徑；不支援解析時可回傳 null。</returns>
        string Resolve(string relativePath);
    }

    /// <summary>
    /// [職責] 以 Lambda 委派為基礎的 Resolver 輕量實作，方便下游不必為了一個 prefix 多寫一個 class。
    /// [物理意義] 將「解析策略」以 Func 注入，行為上等同於實作 IUCL_UrlPrefixResolver。
    /// [使用情境] 大部分下游模組（含 UCL_Core 自己）只需要簡單的 Path.Combine / 字串拼接，使用此類別即可。
    /// </summary>
    public sealed class UCL_UrlPrefixResolver : IUCL_UrlPrefixResolver
    {
        // [欄位] 解析委派；建構後不可變，由建構式注入。可為 null（視為 Resolver 對當前環境不提供解析）。
        private readonly Func<string, string> m_Resolver;

        /// <summary>
        /// [職責] 提供此 Resolver 對應的 prefix 字串。
        /// [物理意義] 由建構式注入，建立後不可變，作為 UCL_URL 註冊表的 key。
        /// </summary>
        public string Prefix { get; }

        /// <summary>
        /// [職責] 建構一個以委派為策略的 Resolver。
        /// [使用慣例] 註冊端應使用 #if UNITY_EDITOR 切換要傳入哪一個委派（Editor 用本地路徑、Build 用雲端 URL）。
        /// </summary>
        /// <param name="prefix">prefix 字串（不含結尾冒號），例如 "ucl_core"。</param>
        /// <param name="resolver">解析委派；負責將相對路徑轉換為當前編譯目標適用的 URL / 路徑。</param>
        public UCL_UrlPrefixResolver(string prefix, Func<string, string> resolver)
        {
            Prefix = prefix;
            m_Resolver = resolver;
        }

        /// <inheritdoc />
        public string Resolve(string relativePath) => m_Resolver?.Invoke(relativePath);
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

        // [常數] UCL_Core 預設 prefix 與雲端文件根 URL。
        // [物理意義] 集中此處避免散落於程式各處；若 Dev 分支日後改名請於此修改。
        private const string CORE_PREFIX = "ucl_core";
        private const string CORE_BUILD_BASE_URL = "https://github.com/tim099/UCL_Core/blob/Dev/";

        /// <summary>
        /// [職責] 型別初始化時，將 UCL_Core 自己的 "ucl_core:" Resolver 註冊進 s_Resolvers。
        /// [物理意義] 讓核心模組與下游模組走同一條註冊流程，避免在 ResolveURL 中存在「核心 prefix 特例」的硬編碼分支。
        /// </summary>
        static UCL_URL()
        {
            // 區塊職責：註冊 UCL_Core 的預設 Resolver。
            // 物理意義：Editor 端使用 UCL_EditorPath.CorePath 自動定位模組根目錄；Build 端拼接 GitHub Dev 分支 URL。
            // 數值影響：影響 [HelpURL("ucl_core:...")] 在開發與發佈兩種版本中的目標位置。
            RegisterResolver(new UCL_UrlPrefixResolver(
                prefix: CORE_PREFIX,
#if UNITY_EDITOR
                // [Editor 解析] 將相對路徑接於 UCL_Core 模組根目錄之後，形成本地檔案路徑。
                resolver: (aRelativePath) => System.IO.Path.Combine(UCL_EditorPath.CorePath ?? string.Empty, aRelativePath)
#else
                // [Build 解析] 將相對路徑接於 GitHub Dev 分支 URL 之後，形成可瀏覽器開啟的雲端連結。
                resolver: (aRelativePath) => CORE_BUILD_BASE_URL + aRelativePath
#endif
            ));
        }

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
        /// [職責] 解析 URL 字串，依序處理「自定 prefix → {lang} 替換 → 本地路徑補全 → en fallback」。
        /// [計算邏輯]
        /// 1. 嘗試從字串拆出 "prefix:"（排除 "://" 形式的 protocol）並查詢 s_Resolvers。
        /// 2. 命中時呼叫 Resolver.Resolve（Editor / Build 差異由註冊端在編譯期決定）。
        /// 3. 將 {lang} 替換為當前語系代碼。
        /// 4. 若結果不含 "://"，視為本地路徑並轉為絕對路徑；Editor 下若檔案不存在則嘗試以 en 替換語系碼。
        /// </summary>
        /// <param name="url">原始 URL 字串或相對路徑。</param>
        /// <returns>解析後的完整 URL 或本地絕對路徑。</returns>
        public static string ResolveURL(string url)
        {
            // [快速返回] 空字串或 null 直接回傳，避免後續判斷誤觸 IndexOf。
            if (string.IsNullOrEmpty(url)) return url;

            // 區塊職責：從 URL 切出 "prefix:" 並查表。
            // 物理意義：prefix 為「不含 ://」的命名空間標識；http/https 等 protocol 因為冒號後接 "//" 而被排除。
            // 數值影響：唯一決定哪一個 Resolver 會被派發，從而決定後續路徑/URL 的根。
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
                        // [計算邏輯] 取出 "prefix:" 之後的相對路徑，交給對應 Resolver。
                        // [編譯期分流] Editor / Build 差異由 Resolver 註冊端在 #if UNITY_EDITOR 中決定，此處無需感知。
                        string aRelativePath = url.Substring(aColonIdx + 1);
                        string aResolved = aResolver.Resolve(aRelativePath);
                        if (!string.IsNullOrEmpty(aResolved)) url = aResolved;
                    }
                }
            }

            // 區塊職責：本地化佔位符替換。
            // 物理意義：將 {lang} 替換為當前語系代碼（如 en、zh-Hans、ja），實現多語系文件切換。
            // 數值影響：影響最終目標檔案的語系版本；Resolver 端不必各自處理 {lang}。
            if (url.Contains("{lang}"))
            {
                string aLangCode = UCL.Core.Game.UCL_LocalizeService.CurLang;
                url = url.Replace("{lang}", aLangCode);
            }

            // 區塊職責：本地路徑補全與 en 回退。
            // 物理意義：若解析結果非雲端 URL，則視為相對於專案根的路徑並轉為絕對路徑；Editor 下若當前語系檔案缺失，回退到 en。
            // 數值影響：避免因尚未翻譯的語系版本造成 404；不影響非 Editor 流程。
            if (!url.Contains("://"))
            {
                // [計算邏輯] 透過 GetFullPath 將相對路徑轉換為絕對路徑。
                url = System.IO.Path.GetFullPath(url);

#if UNITY_EDITOR
                if (!System.IO.File.Exists(url) && url.Contains(UCL.Core.Game.UCL_LocalizeService.CurLang))
                {
                    string aFallbackUrl = url.Replace(UCL.Core.Game.UCL_LocalizeService.CurLang, "en");
                    if (System.IO.File.Exists(aFallbackUrl)) url = aFallbackUrl;
                }
#endif
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
