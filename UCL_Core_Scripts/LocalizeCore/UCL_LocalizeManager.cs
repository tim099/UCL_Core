using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UCL.Core.LocalizeLib
{
    public class UCL_LocalizeManager : UCL.Core.UCL_Singleton<UCL_LocalizeManager>
    {
        static public event System.Action OnLanguageChanged = delegate () { };
        
        public string m_LangDir = "Language";
        public string m_LangName = "English";

        protected LocalizeData m_LocalizeData = null;
        /// <summary>
        /// Load language from resource files
        /// </summary>
        /// <param name="dir">the directory contains language files</param>
        /// <param name="lang_name">the language name</param>
        public void ResourceLoadLanguage(string dir, string lang_name) {
            m_LangDir = dir;
            m_LangName = lang_name;
            string path = m_LangName;
            if(!string.IsNullOrEmpty(dir)) path = Path.Combine(m_LangDir, m_LangName);
            path = Path.Combine(path, "Lang");
            ResourceLoadLanguage(path);
        }

        /// <summary>
        /// Load language from resource files
        /// </summary>
        /// <param name="path">the language file path</param>
        void ResourceLoadLanguage(string path) {
            TextAsset texts = Resources.Load(path) as TextAsset;
            if(texts == null) {
                Debug.LogError("UCL_LocalizeManager ResourceLoadLanguage path:" + path + ",not exist!!");
                return ;
            }
            string data = texts.ToString();
            m_LocalizeData = new LocalizeData(data);
            Debug.Log("ResourceLoadLanguage:" + path);
            try
            {
                OnLanguageChanged.Invoke();
            }catch(System.Exception iE)
            {
                Debug.LogException(iE);
            }

        }
        static public string Get(string key) {
            var ins = GetInstance();
            if(ins == null) return key;
            if(ins.m_LocalizeData == null) {
                Debug.LogWarning("UCL_LocalizeManager not Init yet!!");
                return key;
            }
            return ins.m_LocalizeData.GetLocalize(key);
        }
        static public string Get(string key, params object[] objs) {
            string str = Get(key);
            return string.Format(str, objs);
        }
    }
}

