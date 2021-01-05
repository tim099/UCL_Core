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

        public event System.Action OnLocalizationChanged = delegate() { };

        protected LocalizeData m_LocalizeData = null;
        protected LocalizeData m_LocalizeSetting = null;
        public override void Init() {
            base.Init();
            ins = this;
            string path = Path.Combine(m_LoadLangPath, "LanguageSetting");
            TextAsset texts = Resources.Load(path) as TextAsset;
            if(texts != null) {
                m_LocalizeSetting = new LocalizeData(texts.ToString());
                Debug.LogWarning(m_LocalizeSetting.GetDic().UCL_ToString());
            }
        }
        public override void Save(string dir) {
            string path = Path.Combine(dir, "LocalizeSetting");
            JsonLib.JsonData data = new JsonLib.JsonData();
            data["m_LoadLangPath"] = m_LoadLangPath;
            data["m_CurLang"] = m_CurLang;
            FileLib.Lib.WriteToFile(data.ToJson(), path);
        }
        public override void Load(string dir) {
            string path = Path.Combine(dir, "LocalizeSetting");
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
            string path = Path.Combine(Path.Combine(m_LoadLangPath, m_CurLang), "Lang");
            TextAsset texts = Resources.Load(path) as TextAsset;
            //Debug.Assert(texts != null);
            if(texts == null) {
                Debug.LogError("ResourceLoadLanguage path:" + path + ",not exist!!");
                return;
            }
            string data = texts.ToString();
            m_LocalizeData = new LocalizeData(data);

            OnLocalizationChanged.Invoke();
        }
        virtual public string Get(string key) {
            if(m_LocalizeData == null) {
                Debug.LogWarning("UCL_LocalizeManager not Init yet!!");
                return key;
            }
            return m_LocalizeData.GetLocalize(key);
        }
        virtual public string GetLocalize(string key, params object[] objs) {
            string str = Get(key);
            return string.Format(str, objs);
        }
    }
}