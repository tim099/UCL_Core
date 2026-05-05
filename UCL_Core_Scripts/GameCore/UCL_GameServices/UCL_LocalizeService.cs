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
        // 數值影響：影響後續 PlayerPrefs.GetString / SetString 的 key；**改動必須在第一次讀寫 CurLang
        //          之前完成**，否則用舊 key 寫入的值讀不回來
        private static string s_CurLangKey = DefaultCurLangKey;

        /// <summary>
        /// 用來存「當前語言」的 PlayerPrefs key。預設 <see cref="DefaultCurLangKey"/> = "CurLang"。
        ///
        /// **跨專案撞 key 場景**（例如另一個專案同時使用 Utage，Utage 也用 "CurLang"）→
        /// 在專案 bootstrap 程式中改寫：
        /// <code>
        /// UCL_LocalizeService.CurLangKey = "MyProj_CurLang";
        /// </code>
        /// 必須在第一次存取 <see cref="CurLang"/> 之前設定，否則會讀不到先前用舊 key 寫入的值。
        /// 設為 <c>null</c> 或空字串會被忽略（保留先前的值）。
        /// </summary>
        public static string CurLangKey
        {
            get => s_CurLangKey;
            set
            {
                if (string.IsNullOrEmpty(value)) return;
                s_CurLangKey = value;
            }
        }
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