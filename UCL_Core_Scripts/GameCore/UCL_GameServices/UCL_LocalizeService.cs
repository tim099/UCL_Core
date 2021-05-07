using System.Collections;
using System.Collections.Generic;
using System.IO;
using UCL.Core.LocalizeLib;
using UnityEngine;

namespace UCL.Core.Game {
    public class UCL_LocalizeService : UCL_GameService {
        static public UCL_LocalizeService ins = null;

        public string m_LoadLangPath = "Language";
        public string m_CurLang = "English";

        protected LocalizeData m_LocalizeSetting = null;
        public override void Init() {
            base.Init();
            ins = this;
            string path = Path.Combine(m_LoadLangPath, "LanguageSetting");
            TextAsset texts = Resources.Load(path) as TextAsset;
            if(texts != null) {
                m_LocalizeSetting = new LocalizeData(texts.ToString());
                Debug.Log(m_LocalizeSetting.GetDic().UCL_ToString());
            }
        }
        public override void Save(string iDir) {
            string path = Path.Combine(iDir, "LocalizeSetting.txt");
            JsonLib.JsonData data = new JsonLib.JsonData();
            data["m_LoadLangPath"] = m_LoadLangPath;
            data["m_CurLang"] = m_CurLang;
#if UNITY_EDITOR
            //Debug.LogError("m_LoadLangPath:" + m_LoadLangPath + ",m_CurLang:" + m_CurLang);
            //Editor下 PlayerPrefs紀錄起來以便非執行期使用
            PlayerPrefs.SetString("CurLang", m_CurLang);
            PlayerPrefs.SetString("LoadLangPath", m_LoadLangPath);
#endif
            FileLib.Lib.WriteToFile(data.ToJson(), path);
        }
        public override void Load(string iDir) {
            string path = Path.Combine(iDir, "LocalizeSetting.txt");
            if(File.Exists(path)) {
                var str = File.ReadAllText(path);
                JsonLib.JsonData data = JsonLib.JsonData.ParseJson(str);
                m_LoadLangPath = data.Get("m_LoadLangPath").GetString(m_LoadLangPath);
                m_CurLang = data.Get("m_CurLang").GetString(m_CurLang);
            }
            LoadLanguage(m_CurLang);
        }
        
        public void LoadLanguage(string lang) {
            m_CurLang = lang;
            UCL_LocalizeManager.Instance.LoadLanguage(m_LoadLangPath, m_CurLang);
        }
    }
}