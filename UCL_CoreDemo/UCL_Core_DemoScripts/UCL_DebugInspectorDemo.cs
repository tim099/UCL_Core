using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Demo {
    [AddComponentMenu("UCL_CoreDemo/DebugInspectorDemo")]
    [Core.ATTR.EnableUCLEditor]
    public class UCL_DebugInspectorDemo : UCL_DebugInspectorParDemo {
        public enum DemoEnum {
            None = 0,
            a = 1,
            b = 1 << 1,
            c = 1 << 2,
        }
        [System.Serializable]
        public class DemoClass
        {
            public int m_Int = 30;
            public float m_Float = 20;
            public DemoClass2 m_DemoClass2 = new DemoClass2();
        }

        [System.Serializable]
        public class DemoClass2
        {
            public string m_Str = "Demo2 Str";
            public DemoClass3 m_DemoClass3 = new DemoClass3();
        }
        [System.Serializable]
        public class DemoClass3
        {
            public string m_Str = "Demo3 String";
        }
        //[UCL.Core.PA.UCL_EnumMask] 
        public DemoEnum m_Test;
        public DemoClass m_DemoClass;
        
        public int m_Val = 10;
        public float m_F = 0.731f;
        public string str = "Test String";
        public List<int> m_IntList = new List<int>();
        public float[] m_FloatArr = null;
        public Dictionary<string, string> m_TestDic = new Dictionary<string, string>();
        public Vector3 m_Vec3 = new Vector3(10.5f, 22.73f, 0.7321f);
        public Transform m_Transform;
        private void Start() {
            m_TestDic.Add("Dic e1", "ElementOne");
            m_TestDic.Add("Dic e2", "XXXXTwp");
            m_TestDic.Add("Dic element3", "QAQ");
            Show();
        }
        private void OnGUI() {
            if(!UCL_DebugInspector.GetShowing()) {
                if(UCL.Core.UI.UCL_GUILayout.ButtonAutoSize("Show")) {
                    UCL_DebugInspector.Show();
                }
            }
        }
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void Show() {
            UCL_DebugInspector.Show();
        }

        [UCL.Core.ATTR.UCL_FunctionButton]
        public void Hide() {
            UCL_DebugInspector.Hide();
        }
    }
}