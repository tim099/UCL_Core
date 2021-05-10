using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UCL.Core.LocalizeLib
{
    public class UCL_LocalizeManager// : UCL.Core.UCL_Singleton<UCL_LocalizeManager>
    {
        static UCL_LocalizeManager m_Instance = null;
        /// <summary>
        /// Won't create new Instance if not exist!!
        /// </summary>
        /// <returns></returns>
        static public UCL_LocalizeManager GetInstance() { return m_Instance; }
        /// <summary>
        /// Create new Instance if not exist!!
        /// </summary>
        static public UCL_LocalizeManager Instance
        {
            get
            {
                if (m_Instance == null)
                {
                    m_Instance = new UCL_LocalizeManager();
                }

                return m_Instance;
            }
            set
            {
                m_Instance = value;
            }
        }

        /// <summary>
        /// Assign function to customize your loading flow,
        /// pass in Dir, Language, and return LanguageData
        /// </summary>
        public static System.Func<string, string, string> m_LoadLanguageFunc = null;

        static public event System.Action OnLanguageChanged = delegate () { };
        /// <summary>
        /// Root directory of languages files
        /// </summary>
        public string LangDir { get; protected set; } = "Language";
        /// <summary>
        /// Name of Language folder
        /// </summary>
        public string LangName { get; protected set; } = "English";

        protected LocalizeData m_LocalizeData = null;

        /// <summary>
        /// Load language, load from resource if m_LoadLanguageAct == null
        /// </summary>
        /// <param name="iDir">the directory contains language files</param>
        /// <param name="iLanguage">the language name</param>
        public void LoadLanguage(string iDir, string iLanguage)
        {
            LangDir = iDir;
            LangName = iLanguage;
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
            string aPath = LangName;
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
        /// if iKey not exist localize, then return iKey
        /// </summary>
        /// <param name="iKey"></param>
        /// <returns></returns>
        static public string Get(string iKey) {
            var aIns = m_Instance;
            if(aIns == null) return iKey;
            if(aIns.m_LocalizeData == null) {
                Debug.LogError("UCL_LocalizeManager not Init yet!!");
                return iKey;
            }
            return aIns.m_LocalizeData.GetLocalize(iKey);
        }
        /// <summary>
        /// Check if localize of iKey exist
        /// </summary>
        /// <param name="iKey"></param>
        /// <returns></returns>
        static public bool ContainsKey(string iKey)
        {
            var aIns = m_Instance;
            if (aIns == null) return false;
            if (aIns.m_LocalizeData == null)
            {
                Debug.LogError("UCL_LocalizeManager not Init yet!!");
                return false;
            }
            return aIns.m_LocalizeData.ContainsKey(iKey);
        }
        static public string Get(string key, params object[] objs) {
            string str = Get(key);
            return string.Format(str, objs);
        }
    }
}

