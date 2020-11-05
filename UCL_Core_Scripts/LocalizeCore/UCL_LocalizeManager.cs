using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.LocalizeLib
{
    public class UCL_LocalizeManager : UCL.Core.UCL_Singleton<UCL_LocalizeManager>
    {
        protected LocalizeData m_LocalizeData = null;

        /// <summary>
        /// Load language from resource files
        /// </summary>
        /// <param name="path">the language file path</param>
        virtual public void ResourceLoadLanguage(string path) {
            TextAsset texts = Resources.Load(path) as TextAsset;
            if(texts == null) {
                Debug.LogError("UCL_LocalizeManager ResourceLoadLanguage path:" + path + ",not exist!!");
                return ;
            }
            string data = texts.ToString();
            m_LocalizeData = new LocalizeData(data);
        }
        virtual public string GetLocalize(string key) {
            if(m_LocalizeData == null) {
                Debug.LogWarning("UCL_LocalizeManager not Init yet!!");
                return key;
            }
            return m_LocalizeData.GetLocalize(key);
        }
        virtual public string GetLocalize(string key,params object[] objs) {
            string str = GetLocalize(key);
            return string.Format(str, objs);
        }
    }
}

