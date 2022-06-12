using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
namespace UCL.Core.JsonLib.Demo {
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_JsonDemo : MonoBehaviour {
        [System.Serializable]
        public class DemoClass1 {
            public int m_Int = 10;
            public float m_Float = 3.78f;
            public DemoClass2 m_DemoClass2;
        }
        [System.Serializable]
        public class DemoClass2 {
            public int m_Int = 10;
            public float m_Float = 3.78f;
        }
        public string m_SaveData = string.Empty;
        public Text m_DataText = null;
        public DemoClass1 m_DemoData = new DemoClass1();

        [UCL.Core.ATTR.UCL_FunctionButton]
        public void SaveToData() {
            m_SaveData = JsonLib.JsonConvert.ObjectToJson(m_DemoData).ToJson();
            m_DataText.text = m_SaveData;
        }
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void LoadFromData() {
            m_SaveData = m_DataText.text;
            var aObj = JsonLib.JsonConvert.JsonToObject(JsonData.ParseJson(m_SaveData));
            if(aObj != null) {
                Debug.LogWarning("aObj:" + aObj.UCL_ToString());
            } else {
                Debug.LogError("aObj == null");
            }
            m_DemoData = aObj as DemoClass1;
        }
    }
}

