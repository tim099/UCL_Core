using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Assertions;

namespace UCL.Core.MarshalLib.Demo {
    [ATTR.EnableUCLEditor]
    public class UCL_MarshalDemo : MonoBehaviour {
        [System.Serializable]
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public unsafe struct Demo {
            //public int m_Int;
            //public float m_F;
            //public long m_L;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1, ArraySubType = UnmanagedType.I1)]

            public fixed sbyte TestArr[1]; 
            //public string m_Str;
            //public float[] m_F;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        //[StructLayout(LayoutKind.Sequential)]
        [System.Serializable]
        public class Demo2 {
            public int m_Int;
            public float m_F;
            public long m_L;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1, ArraySubType = UnmanagedType.I8)]
            public Int64[] TestArr;
            //public string m_Str;
            //public float[] m_F;
        }
        public byte[] m_Data;
        public Demo m_Demo;
        public Demo m_DemoTest;
        public Demo2 m_Demo2;
        [ATTR.UCL_FunctionButton]
        public void Test() {
            unsafe {
                m_Demo = new Demo();
                //m_Demo.TestArr = new sbyte[1];
                m_Demo.TestArr[0] = 37;
                m_Data = m_Demo.ToByteArray();

                m_DemoTest = m_Demo.ToByteArray().ToStructure<Demo>();
            }

        }

        [ATTR.UCL_FunctionButton]
        public void ToByteArr() {
            m_Data = m_Demo.ToByteArray();
        }
        [ATTR.UCL_FunctionButton]
        public void ToByteArr2() {
            m_Data = m_Demo2.ToByteArray();
        }
        [ATTR.UCL_FunctionButton]
        public void ToStructure() {
            m_Demo = m_Data.ToStructure<Demo>();
        }
        [ATTR.UCL_FunctionButton]
        public void ToStructure2() {
            m_Demo2 = m_Data.ToStructure<Demo2>();
        }
        // Start is called before the first frame update
        void Start() {
            //https://dotblogs.com.tw/hatelove/2014/06/06/how-to-assert-two-collection-equal
            //Assert.AreEqual(c.Equals(c1), false);
        }

        // Update is called once per frame
        void Update() {

        }
    }
}

