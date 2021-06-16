using UnityEngine;
using System;
using System.Reflection;
using UCL.Core.ObjectReflectionExtension;
using System.Collections.Generic;

namespace UCL.Core.PA {
    public class UCL_StrListAttribute : PropertyAttribute {
        public string[] m_StrArr;
        public UCL_StrListAttribute(params string[] iList) {
            m_StrArr = iList;
        }
        public UCL_StrListAttribute(Type iType, string iFuncName) {
            var aMethod = iType.GetMethod(iFuncName);
            if(aMethod != null) {
                try {
                    var aResult = aMethod.Invoke(null, null);
                    m_StrArr = aResult as string[];
                    if (m_StrArr == null)
                    {
                        if (aResult is List<string>)
                        {
                            var aList = aResult as List<string>;
                            m_StrArr = new string[aList.Count];
                            for (int j = 0; j < aList.Count; j++)
                            {
                                m_StrArr[j] = aList[j];
                            }
                        }

                    }
                } catch(Exception e) {
                    Debug.LogError("UCL_ListProperty method.Invoke Exception:" + e.ToString());
                }

            } else { //might be accessor
                PropertyInfo aPropInfo = iType.GetProperty(iFuncName);
                if(aPropInfo == null) { // not accessor!!
                    Debug.LogError("UCL_ListProperty:" + iType.Name + ",func_name == null :" + iFuncName);
                    return;
                }
                MethodInfo[] aMethodInfos = aPropInfo.GetAccessors();
                for(int i = 0; i < aMethodInfos.Length; i++) {
                    MethodInfo aMethodInfo = aMethodInfos[i];
                    //Debug.LogWarning(string.Format("Name: {0}", aMethodInfo.Name));
                    //Debug.LogWarning(string.Format("aMethodInfo.IsPrivate: {0}", aMethodInfo.IsPrivate));
                    // Determine if this is the property getter or setter.
                    if (aMethodInfo.ReturnType == typeof(void)) {//setter
                        //Console.WriteLine("Setter");
                        //Console.WriteLine("   Setting the property value.");
                        //  Set the value of the property.
                        //m.Invoke(test, new object[] { "The Modified Caption" });
                    } else {//getter
                        //Console.WriteLine("Getter");
                        // Get the value of the property.
                        //Console.WriteLine("   Property Value: {0}", m.Invoke(test, new object[] { }));
                        var aResult = aMethodInfo.Invoke(null, new object[] { });
                        m_StrArr = aResult as string[];
                        if(m_StrArr == null)
                        {
                            if(aResult is List<string>)
                            {
                                var aList = aResult as List<string>;
                                m_StrArr = new string[aList.Count];
                                for (int j = 0; j < aList.Count; j++)
                                {
                                    m_StrArr[j] = aList[j];
                                }
                            }

                        }
                        if(m_StrArr != null) break;
                    }
                }
            }
        }
    }
    public class UCL_ListAttribute : PropertyAttribute {
        string m_MethodName = null;
        object[] m_Params = null;
        /// <summary>
        /// 
        /// </summary>
        /// <param name="iMethodName">Member function that return a List</param>
        /// <param name="para"></param>
        public UCL_ListAttribute(string iMethodName, params object[] para) {
            m_MethodName = iMethodName;
            m_Params = para;
        }
        public string[] GetList(object target) {
            return (string[]) target.Invoke(m_MethodName, m_Params);
        }
        //public UCL_ListAttribute (System.Action<List<string>> get)
    }
}