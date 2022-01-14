using UnityEngine;
using System;
using System.Reflection;
using UCL.Core.ObjectReflectionExtension;
using System.Collections.Generic;

namespace UCL.Core.PA {
    public class UCL_StrListAttribute : PropertyAttribute, IStringArr
    {
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
                    // Determine if this is the property getter or setter.
                    if (aMethodInfo.ReturnType == typeof(void)) {//setter
                        //m.Invoke(test, new object[] { "The Modified Caption" });
                    } else {//getter
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

        public string[] GetList(object iTarget)
        {
            return m_StrArr;
        }
    }
    public class UCL_ListAttribute : PropertyAttribute, IStringArr
    {
        //Type m_Type = null;
        string m_MethodName = null;
        object[] m_Params = null;
        /// <summary>
        /// 
        /// </summary>
        /// <param name="iMethodName">Member function that return a List</param>
        /// <param name="iParams"></param>
        public UCL_ListAttribute(string iMethodName, params object[] iParams) {
            m_MethodName = iMethodName;
            m_Params = iParams;
        }
        /// <summary>
        /// Get the string list from target
        /// </summary>
        /// <param name="iTarget"></param>
        /// <returns></returns>
        public string[] GetList(object iTarget) {
            var aResult = iTarget.Invoke(m_MethodName, m_Params);
            if (aResult is string[]) return (string[])aResult;
            else if (aResult is List<string>) return ((List<string>)aResult).ToArray();

            return new string[0];
        }
        /// <summary>
        /// Get the display list from target
        /// </summary>
        /// <param name="iTarget"></param>
        /// <returns></returns>
        public string[] GetDisplayList(object iTarget)
        {
            string[] aList = GetList(iTarget);
            string[] aDisplayList = new string[aList.Length];
            for (int i = 0; i < aList.Length; i++)
            {
                string aKey = aList[i];
                if (LocalizeLib.UCL_LocalizeManager.ContainsKey(aKey))
                {
                    aDisplayList[i] = string.Format("{0}({1})", LocalizeLib.UCL_LocalizeManager.Get(aKey), aKey);
                }
                else
                {
                    aDisplayList[i] = aKey;
                }
            }
            return aDisplayList;
        }
        //public UCL_ListAttribute (System.Action<List<string>> get)
    }
}