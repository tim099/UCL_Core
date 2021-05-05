using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UCL.Core.LocalizeLib
{
    public class UCL_LocalizeManager : UCL.Core.UCL_Singleton<UCL_LocalizeManager>
    {
        /// <summary>
        /// Assign function to customize your loading flow,
        /// pass in Dir, Language, and return LanguageData
        /// </summary>
        public static System.Func<string, string, string> m_LoadLanguageFunc = null;

        static public event System.Action OnLanguageChanged = delegate () { };
        
        public string m_LangDir = "Language";
        public string m_LangName = "English";

        protected LocalizeData m_LocalizeData = null;

        /// <summary>
        /// Load language, load from resource if m_LoadLanguageAct == null
        /// </summary>
        /// <param name="iDir">the directory contains language files</param>
        /// <param name="iLanguage">the language name</param>
        public void LoadLanguage(string iDir, string iLanguage)
        {
            m_LangDir = iDir;
            m_LangName = iLanguage;
            string aLangData = string.Empty;
            if (m_LoadLanguageFunc != null)
            {
                aLangData = m_LoadLanguageFunc.Invoke(iDir, iLanguage);
            }
            else
            {
                aLangData = ResourceLoadLanguage(iDir, iLanguage);
            }
            LoadLanguageData(aLangData);
        }
        /// <summary>
        /// Load LanguageData from resource files
        /// </summary>
        /// <param name="iDir">the directory contains language files</param>
        /// <param name="iLanguage">the language name</param>
        string ResourceLoadLanguage(string iDir, string iLanguage)
        {
            string aPath = m_LangName;
            if (!string.IsNullOrEmpty(iDir)) aPath = Path.Combine(iDir, iLanguage);
            aPath = Path.Combine(aPath, "Lang");
            return ResourceLoadLanguage(aPath);
        }
        /// <summary>
        /// Load LanguageData from resource files
        /// </summary>
        /// <param name="iPath">the language file path</param>
        string ResourceLoadLanguage(string iPath)
        {
            TextAsset aTexts = Resources.Load(iPath) as TextAsset;
            if (aTexts == null)
            {
                Debug.LogError("UCL_LocalizeManager ResourceLoadLanguage path:" + iPath + ",not exist!!");
                return string.Empty;
            }
            Debug.Log("ResourceLoadLanguage:" + iPath);
            return aTexts.ToString();
            //LoadLanguageData(texts.ToString());
        }
        /// <summary>
        /// Load language from LanguageData(Data format "Key":"Value")
        /// </summary>
        /// <param name="iLanguageData"></param>
        void LoadLanguageData(string iLanguageData)
        {
            m_LocalizeData = new LocalizeData(iLanguageData);
            try
            {
                OnLanguageChanged.Invoke();
            }
            catch (System.Exception iE)
            {
                Debug.LogException(iE);
            }
        }
        /// <summary>
        /// Get localized of the key
        /// </summary>
        /// <param name="iKey"></param>
        /// <returns></returns>
        static public string Get(string iKey) {
            var aIns = GetInstance();
            if(aIns == null) return iKey;
            if(aIns.m_LocalizeData == null) {
                Debug.LogWarning("UCL_LocalizeManager not Init yet!!");
                return iKey;
            }
            return aIns.m_LocalizeData.GetLocalize(iKey);
        }
        static public string Get(string key, params object[] objs) {
            string str = Get(key);
            return string.Format(str, objs);
        }
    }
}

