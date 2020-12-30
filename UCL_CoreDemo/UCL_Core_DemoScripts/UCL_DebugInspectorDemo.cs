﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Demo {
    [Core.ATTR.EnableUCLEditor]
    public class UCL_DebugInspectorDemo : MonoBehaviour {
        public enum Test {
            None = 0,
            a = 1,
            b = 1 << 1,
            c = 1 << 2,
        }

        [UCL.Core.PA.UCL_EnumMask] public Test m_Test;
        public int m_Val = 10;
        public float m_F = 0.731f;
        public string str = "Test String";
        public List<int> m_IntList = new List<int>();
        public float[] m_FloatArr = null;
        public Dictionary<string, string> m_TestDic = new Dictionary<string, string>();
        public Vector3 m_Vec3 = new Vector3(10.5f, 22.73f, 0.7321f);
        private void Start() {
            m_TestDic.Add("Dic e1", "ElementOne");
            m_TestDic.Add("Dic e2", "XXXXTwp");
            m_TestDic.Add("Dic element3", "QAQ");
            Show();
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