using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UCL.Core.LocalizeLib
{
    public class UCL_LocalizeManager// : UCL.Core.UCL_Singleton<UCL_LocalizeManager>
    {
        private static UCL_LocalizeManager s_Instance = null;
        /// <summary>
        /// Won't create new Instance if not exist!!
        /// </summary>
        /// <returns></returns>
        static public UCL_LocalizeManager GetInstance() { return s_Instance; }
        /// <summary>
        /// Create new Instance if not exist!!
        /// </summary>
        static public UCL_LocalizeManager Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    s_Instance = new UCL_LocalizeManager();
                }

                return s_Instance;
            }
            set
            {
                s_Instance = value;
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
        public static string s_LangName { get; protected set; } = "English";

        static protected bool m_NotInitializeErrorLogged = false;
        protected LocalizeData m_LocalizeData = null;

        /// <summary>
        /// Load language, load from resource if m_LoadLanguageAct == null
        /// </summary>
        /// <param name="iDir">the directory contains language files</param>
        /// <param name="iLanguage">the language name</param>
        public void LoadLanguage(string iDir, string iLanguage)
        {
            LangDir = iDir;
            s_LangName = LangName = iLanguage;
            string aLangData = string.Empty;
            if (m_LoadLanguageFunc != null)
            {
                try
                {
                    aLangData = m_LoadLanguageFunc.Invoke(iDir, iLanguage);
                }
                catch(System.Exception e)
                {
                    Debug.LogException(e);
                }
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
            if (s_Instance == null) return iKey;

            if (s_Instance.m_LocalizeData == null)
            {
                if (!m_NotInitializeErrorLogged)
                {
                    m_NotInitializeErrorLogged = true;
                    Debug.LogError("UCL_LocalizeManager.Get() fail, not Init yet!!");
                }
                return iKey;
            }
            return s_Instance.m_LocalizeData.GetLocalize(iKey);
        }


        static public string GetID(string iKey)
        {
            if (s_Instance == null) return iKey;
            string localizedId = Get(iKey);
            if(localizedId != iKey)
            {
                localizedId = $"{localizedId}({iKey})";
            }

            return localizedId;
        }
        /// <summary>
        /// Check if localize of iKey exist
        /// </summary>
        /// <param name="iKey"></param>
        /// <returns></returns>
        static public bool ContainsKey(string iKey)
        {
            var aIns = s_Instance;
            if (aIns == null) return false;
            if (aIns.m_LocalizeData == null)
            {
                if (!m_NotInitializeErrorLogged)
                {
                    m_NotInitializeErrorLogged = true;
                    Debug.LogError("UCL_LocalizeManager.ContainsKey() fail, not Init yet!!");
                }
                return false;
            }
            return aIns.m_LocalizeData.ContainsKey(iKey);
        }
        /// <summary>
        /// Get localized of the key
        /// if iKey not exist localize, then return iKey
        /// </summary>
        /// <param name="iKey"></param>
        /// <param name="iObjs"></param>
        /// <returns></returns>
        static public string Get(string iKey, params object[] iObjs) {
            string aStr = Get(iKey);
            if (iObjs.Length > 0)
            {
                try
                {
                    aStr = string.Format(aStr, iObjs);
                }
                catch (System.Exception iE)
                {
                    Debug.LogException(iE);
                    Debug.LogError("LocalizeGet Str:" + aStr + ",iObjs:" + iObjs.UCL_ToString());
                }
            }

            return aStr;
        }
    }
}

