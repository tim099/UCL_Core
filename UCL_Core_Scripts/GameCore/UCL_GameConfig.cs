using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Game {
    public class UCL_GameConfig : UCL_IConfig {
        protected Dictionary<string, object> m_ConfigData;
        virtual public void Init() {
            m_ConfigData = new Dictionary<string, object>();
        }
        #region Save & Load
        virtual public string Save() {
            JsonLib.JsonData data = new JsonLib.JsonData();
            data["config"] = "Config test:" + System.DateTime.Now.ToString();
            data["m_ConfigData"] = new JsonLib.JsonData(m_ConfigData);

            //"Config test:" + System.DateTime.Now.ToString()
            return data.ToJson();
        }
        virtual public void Load(string config) {
            Debug.LogWarning("Config:" + config);
            var data = JsonLib.JsonData.ParseJson(config);
            m_ConfigData = data.Get("m_ConfigData").GetDic();
            foreach(var v in m_ConfigData) {
                Debug.LogWarning("Dic key:" + v.Key + ",v:" + v.Value);
            }
            Debug.LogWarning("cc:" + data["config"]);
        }
        #endregion
        #region Set & Get Value
        virtual public void SetString(string key, string value) {
            m_ConfigData[key] = value;
        }
        virtual public void SetInt(string key, int value) {
            m_ConfigData[key] = value;
        }
        virtual public void SetFloat(string key, float value) {
            m_ConfigData[key] = value;
        }
        virtual public void SetDouble(string key, double value) {
            m_ConfigData[key] = value;
        }
        virtual public void SetValue(string key, object value) {
            m_ConfigData[key] = value;
        }
        virtual public object GetValue(string key) {
            if(!m_ConfigData.ContainsKey(key)) {
                return null;
            }
            return m_ConfigData[key];
        }
        virtual public string GetString(string key) {
            var val = GetValue(key);
            if(val == null || !(val is string)) return "";
            return (string)val;
        }
        virtual public int GetInt(string key) {
            var val = GetValue(key);
            if(val == null || !(val is int)) return 0;
            return (int)val;
        }
        virtual public float GetFloat(string key) {
            var val = GetValue(key);
            if(val == null || !(val is float)) return 0;
            return (float)val;
        }
        virtual public double GetDouble(string key) {
            var val = GetValue(key);
            if(val == null || !(val is double)) return 0;
            return (double)val;
        }

        #endregion
        virtual public void DeleteKey(string key) {
            //PlayerPrefs.DeleteKey
            if(m_ConfigData.ContainsKey(key)) {
                m_ConfigData.Remove(key);
            }
        }
        virtual public void DeleteAll() {
            //PlayerPrefs.DeleteAll();
            m_ConfigData.Clear();
        }

    }
}