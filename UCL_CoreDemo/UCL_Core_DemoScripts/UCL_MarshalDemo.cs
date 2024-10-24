﻿using System;
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
        public struct Demo {//public unsafe struct Demo 
            //public int m_Int;
            //public float m_F;
            //public long m_L;
            //[MarshalAs(UnmanagedType.ByValArray, SizeConst = 1, ArraySubType = UnmanagedType.I1)]

            //public fixed sbyte TestArr[1]; 
            public string m_Str;
            public float[] m_F;
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

        public class ValT {
            public ValT() {
                m_Int = Core.MathLib.UCL_Random.Instance.Next(30);
                m_F = Core.MathLib.UCL_Random.Instance.NextFloat(100f);
                test = "Hi:" + m_Int + ",QWQ";
            }
            public string test;
            public int m_Int;
            public float m_F;
        }

        public class Demo3 {
            public Demo3() {
                m_Demo = new Demo4();
                m_Demo.Init();
            }
            public int m_Int;
            public float m_F;
            public long m_L;
            public Demo4 m_Demo;
            public ValT[] m_Vals;
            public Int64[] TestArr;
            
        }

        public struct Demo4 {
            public void Init() {
                TestArr = new long[5];
                for(int i = 0; i < TestArr.Length; i++) {
                    TestArr[i] = 11 + i * 8 + i * i * 3;
                }
            }
            public int m_Int;
            public float m_F;
            public long m_L;

            public Int64[] TestArr;
        }
        public byte[] m_Data;
        public Demo m_Demo;
        public Demo m_DemoTest;
        public Demo2 m_Demo2;

        enum ET {
            a=0,
            b,
            c,
        }
        List<string> rec;
        List<string> rec2;
        [ATTR.UCL_FunctionButton]
        public void Test2() {
            rec = new List<string>();
            rec2 = new List<string>();
            int end_count = 0;
            for(int i = 0; i < 10; i++) {
                int at = i;
                Core.ThreadLib.UCL_ThreadManager.Instance.Run(delegate () {
                    Debug.LogWarning("Hi Hi:"+at);
                    lock(rec) {
                        rec.Add("Hi Hi:" + at);
                        Core.ThreadLib.UCL_ThreadManager.Instance.RunOnUpdate(delegate () {
                            Debug.LogWarning("Ho Ho:" + at);
                            rec2.Add("Ho Ho:" + at);
                            if(++end_count >= 10) {
                                Core.ThreadLib.UCL_ThreadManager.Instance.RunOnUpdate(delegate () {
                                    for(int j = 0; j < 10; j++) {
                                        Debug.LogWarning("rec:" + rec[j] + ",rec2:" + rec2[j]);
                                    }
                                });
                            }
                        });
                    }
                });
            }
        }

        [ATTR.UCL_FunctionButton]
        public void Test() {

            /*
            unsafe {
                m_Demo = new Demo();
                //m_Demo.TestArr = new sbyte[1];
                m_Demo.TestArr[0] = 37;
                m_Data = m_Demo.ToByteArray();

                m_DemoTest = m_Demo.ToByteArray().ToStructure<Demo>();
            }
            */
            var test = new Demo3();
            test.TestArr = new long[5];
            for(int i = 0; i < test.TestArr.Length; i++) {
                test.TestArr[i] = 37 + i * 5 + i * i * 2;
            }
            test.m_Vals = new ValT[3];
            for(int i = 0; i < test.m_Vals.Length; i++) {
                test.m_Vals[i] = new ValT();
            }
            test.m_F = Core.MathLib.UCL_Random.Instance.NextFloat(2.0f);
            Dictionary<string, int> dic = new Dictionary<string, int>();

            dic.Add("hii", 3);
            dic.Add("No QAQ", 142);
            Debug.LogWarning(dic.AllFieldToString());
            ET e = ET.b;
            Debug.LogWarning(e.AllFieldToString());
            int a = 17;
            Debug.LogWarning(a.AllFieldToString());
            Debug.LogWarning(test.AllFieldToString());

            Debug.LogWarning(test.GetMember("m_F").AllFieldToString());
            Debug.LogWarning(test.GetMember("m_Demo").AllFieldToString());
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

