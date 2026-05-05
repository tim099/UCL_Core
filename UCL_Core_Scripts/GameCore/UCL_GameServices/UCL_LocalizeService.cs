using System.Collections;
using System.Collections.Generic;
using System.IO;
using UCL.Core.LocalizeLib;
using UnityEngine;

namespace UCL.Core.Game {
    /// <summary>
    /// this service is outdated and will be replace
    /// </summary>
    public class UCL_LocalizeService : UCL_GameService {
        #region key
        public const string DefaultLangKey = "en";

        /// <summary>
        /// 預設 PlayerPrefs key（保留 const 提供向後相容；未覆寫時 <see cref="CurLangKey"/> 會回傳此值）。
        /// </summary>
        public const string DefaultCurLangKey = "CurLang";

        // 區塊職責：當前語言用的 PlayerPrefs key 儲存欄位（可被上層專案覆寫）
        // 物理意義：UCL_Core 預設用 "CurLang" 當 key，但有些第三方 plugin（例：Utage）也用同名 key →
        //          跨專案撞 key 會互相覆蓋。讓 key 變成 settable，上層專案可在 bootstrap 改成自己的命名空間
        // 數值影響：影響後續 PlayerPrefs.GetString / SetString 的 key；改動可由：
        //          (a) 程式設定 (UCL_LocalizeService.CurLangKey = "MyKey")
        //          (b) UCL_Config 持久化（自動於 ModuleService 就緒後第一次讀 CurLang 時 load）
        private static string s_CurLangKey = DefaultCurLangKey;

        /// <summary>
        /// 用來持久化 CurLangKey 的 UCL_Config key（meta-key — 存「key 的 key」）。
        /// </summary>
        public const string ConfigKey_CurLangKey = "CurLangKey";

        // 區塊職責：標記是否已嘗試從 UCL_ConfigAsset 載入 CurLangKey（lazy + 一次性）
        // 物理意義：第一次讀 CurLangKey 時若 ModuleService 已 init，就試著從 UCL_Config 拿，
        //          找到就覆寫 s_CurLangKey；找不到 / 還沒 init 都退回預設值。重複嘗試浪費，
        //          所以用 flag 鎖一次。後續呼叫 ResetConfigLoadFlag 可重新嘗試（測試用）
        // 數值影響：純效能優化，不影響行為
        private static bool s_LoadedFromConfig = false;

        /// <summary>
        /// 用來存「當前語言」的 PlayerPrefs key。預設 <see cref="DefaultCurLangKey"/> = "CurLang"。
        ///
        /// <para><b>三層解析優先序</b>（高到低）：</para>
        /// <list type="number">
        ///   <item>程式 setter（最高）：<c>UCL_LocalizeService.CurLangKey = "MyKey"</c></item>
        ///   <item>UCL_ConfigAsset 持久化值（透過 UCL_Config）— 第一次讀此屬性時 lazy load</item>
        ///   <item>編譯期常數 <see cref="DefaultCurLangKey"/> = "CurLang"</item>
        /// </list>
        ///
        /// <para><b>跨專案撞 key 場景</b>（例：另一專案同時使用 Utage，Utage 也用 "CurLang"）：</para>
        /// 推薦在 UCL_LocalizeEditPage 透過 UI 改 + Apply 持久化 — 不必動程式碼且設定會跟著 git。
        /// 或在專案 bootstrap 程式中改寫（純 session 內有效）：
        /// <code>
        /// UCL_LocalizeService.CurLangKey = "MyProj_CurLang";
        /// </code>
        ///
        /// 設為 <c>null</c> 或空字串會被忽略（保留先前的值）。
        /// </summary>
        public static string CurLangKey
        {
            get
            {
                // 區塊職責：lazy 從 UCL_Config 載入持久化值；一次性
                // 物理意義：避免每次讀 CurLangKey 都重 IO；ModuleService 還沒就緒時退回預設、
                //          但會留 flag false 讓下次 init 後仍有機會嘗試
                if (!s_LoadedFromConfig)
                {
                    string aFromConfig = UCL_Config.GetString(ConfigKey_CurLangKey, null);
                    // 注意：UCL_Config.GetString 內部會檢查 ModuleService.Initialized；未就緒時回 null（=default 值）
                    if (UCL_ModuleService.Initialized)
                    {
                        s_LoadedFromConfig = true;
                        if (!string.IsNullOrEmpty(aFromConfig))
                        {
                            s_CurLangKey = aFromConfig;
                        }
                    }
                }
                return s_CurLangKey;
            }
            set
            {
                if (string.IsNullOrEmpty(value)) return;
                s_CurLangKey = value;
                // 一旦程式 setter 改過，就不需要再從 Config 載入（避免下次讀又被 Config 值覆蓋）
                s_LoadedFromConfig = true;
            }
        }

        /// <summary>重置「已從 Config 載入」旗標 — 給測試 / 編輯器強制 reload 用。一般不必呼叫。</summary>
        public static void ResetConfigLoadFlag() => s_LoadedFromConfig = false;
        #endregion
        public static string LoadLangPath => "Install/.Language";
        public static string CurLang
        {
            get
            {
                string lang = PlayerPrefs.GetString(CurLangKey, DefaultLangKey);
                //Debug.LogError($"Get CurLang:{lang}");
                //if (Ins != null) return Ins.m_CurLang;
                return lang;
            }
            set
            {
                //Debug.LogError($"Set CurLang:{value}");
                //if (Ins != null) Ins.m_CurLang = value;
                PlayerPrefs.SetString(CurLangKey, value);
            }
        }
        static public UCL_LocalizeService Ins = null;

        public string m_LoadLangPath = "Install/Language";

        protected LocalizeData m_LocalizeSetting = null;
        public override void Init() {
            base.Init();
            Ins = this;
            string aPath = Path.Combine(m_LoadLangPath, "LanguageSetting");
            TextAsset aTexts = Resources.Load(aPath) as TextAsset;
            if(aTexts != null) {
                m_LocalizeSetting = new LocalizeData(aTexts.ToString());
                Debug.Log(m_LocalizeSetting.GetDic().AllFieldToString());
            }
        }
        /// <summary>
        /// Save current localize setting
        /// </summary>
        /// <param name="iDir"></param>
        public override void Save(string iDir) {
            //string aPath = Path.Combine(iDir, "LocalizeSetting.txt");
            //JsonLib.JsonData aData = new JsonLib.JsonData();
            //aData["m_LoadLangPath"] = m_LoadLangPath;
            //aData["m_CurLang"] = CurLang;
//#if UNITY_EDITOR
            //Debug.LogError("m_LoadLangPath:" + m_LoadLangPath + ",m_CurLang:" + m_CurLang);
            //Editor下 PlayerPrefs紀錄起來以便非執行期使用
            //PlayerPrefs.SetString(CurLangKey, m_CurLang);
//#endif
            //FileLib.Lib.WriteToFile(aData.ToJson(), aPath);
        }
        public override void Load(string iDir) {
            //string aPath = Path.Combine(iDir, "LocalizeSetting.txt");
            //if(File.Exists(aPath)) {
            //    var aStr = File.ReadAllText(aPath);
            //    JsonLib.JsonData aData = JsonLib.JsonData.ParseJson(aStr);
            //    //m_LoadLangPath = aData.GetString("m_LoadLangPath", m_LoadLangPath);
            //    //m_CurLang = PlayerPrefs.GetString(CurLangKey, m_CurLang);//aData.GetString("m_CurLang", m_CurLang);
            //}
            LoadLanguage(CurLang);
        }
        
        public void LoadLanguage(string iLang) {
            //m_CurLang = iLang;
            CurLang = iLang;
            UCL_LocalizeManager.Instance.LoadLanguage(m_LoadLangPath, CurLang);
        }

        public static void SetLanguage(string iLangCode)
        {
            CurLang = iLangCode;

            UCL_LocalizeManager.Instance.LoadLanguage(LoadLangPath, iLangCode);
        }
    }
}