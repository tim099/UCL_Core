
namespace UCL.Core.LocalizeLib
{
    /// <summary>
    /// [職責] 透過 Switch 分支管理硬編碼的多國語言。
    /// [物理意義] 提供零延遲、零分配的譯詞查詢服務，作為 UCL_LocalizeManager 的獨立補充。
    /// [關聯檔案] 具體語系詞條表實作於以下 partial 檔案中：
    /// - UCL_CodeLocalize.zh-Hant.cs (繁體中文)
    /// - UCL_CodeLocalize.zh-Hans.cs (簡體中文)
    /// - UCL_CodeLocalize.en.cs (英文)
    /// - UCL_CodeLocalize.ja.cs (日文)
    /// </summary>
    public static partial class UCL_CodeLocalize
    {
        /// <summary>
        /// [職責] 根據當前語系獲取翻譯。
        /// [數值影響] 依序查詢當前語系分支 -> 英文分支 -> 回傳 Key 本身。
        /// </summary>
        public static string Get(string iKey)
        {
            string aLang = UCL_LocalizeManager.s_LangName;
            string aResult = null;

            // [計算邏輯] 根據語系進行一級分發
            switch (aLang)
            {
                case "zh-Hant": aResult = Get_zhHant(iKey); break;
                case "zh-Hans": aResult = Get_zhHans(iKey); break;
                case "en":      aResult = Get_en(iKey); break;
                case "ja":      aResult = Get_ja(iKey); break;
            }

            if (aResult != null) return aResult;

            // [Fallback] 若當前語系找不到，優先嘗試英文分支
            if (aLang != "en")
            {
                aResult = Get_en(iKey);
                if (aResult != null) return aResult;
            }

            return iKey; // 最後的最後，回傳 Key 本身
        }
    }
}
