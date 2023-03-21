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
        public const string CurLangKey = "CurLang";
        #endregion
        public static string LoadLangPath => "Install/Language";
        public static string CurLang
        {
            get
            {
                if (Ins != null) return Ins.m_CurLang;
                string aCurLang = PlayerPrefs.GetString(CurLangKey);
                if (string.IsNullOrEmpty(aCurLang)) aCurLang = "Chinese";
                return aCurLang;
            }
            set
            {
                if (Ins != null) Ins.m_CurLang = value;
                PlayerPrefs.SetString(CurLangKey, value);
            }
        }
        static public UCL_LocalizeService Ins = null;

        public string m_LoadLangPath = "Install/Language";
        public string m_CurLang = "Chinese";

        protected LocalizeData m_LocalizeSetting = null;
        public override void Init() {
            base.Init();
            Ins = this;
            string aPath = Path.Combine(m_LoadLangPath, "LanguageSetting");
            TextAsset aTexts = Resources.Load(aPath) as TextAsset;
            if(aTexts != null) {
                m_LocalizeSetting = new LocalizeData(aTexts.ToString());
                Debug.Log(m_LocalizeSetting.GetDic().UCL_ToString());
            }
        }
        /// <summary>
        /// Save current localize setting
        /// </summary>
        /// <param name="iDir"></param>
        public override void Save(string iDir) {
            string aPath = Path.Combine(iDir, "LocalizeSetting.txt");
            JsonLib.JsonData aData = new JsonLib.JsonData();
            //aData["m_LoadLangPath"] = m_LoadLangPath;
            aData["m_CurLang"] = m_CurLang;
#if UNITY_EDITOR
            //Debug.LogError("m_LoadLangPath:" + m_LoadLangPath + ",m_CurLang:" + m_CurLang);
            //Editor下 PlayerPrefs紀錄起來以便非執行期使用
            PlayerPrefs.SetString(CurLangKey, m_CurLang);
#endif
            FileLib.Lib.WriteToFile(aData.ToJson(), aPath);
        }
        public override void Load(string iDir) {
            string aPath = Path.Combine(iDir, "LocalizeSetting.txt");
            if(File.Exists(aPath)) {
                var aStr = File.ReadAllText(aPath);
                JsonLib.JsonData aData = JsonLib.JsonData.ParseJson(aStr);
                //m_LoadLangPath = aData.GetString("m_LoadLangPath", m_LoadLangPath);
                m_CurLang = aData.GetString("m_CurLang", m_CurLang);
            }
            LoadLanguage(m_CurLang);
        }
        
        public void LoadLanguage(string iLang) {
            m_CurLang = iLang;
            UCL_LocalizeManager.Instance.LoadLanguage(m_LoadLangPath, m_CurLang);
        }
    }
}