using UnityEngine;
using System;
using System.Reflection;
using UCL.Core.ObjectReflectionExtension;
using System.Collections.Generic;
using System.Linq;

namespace UCL.Core.PA {
    public class UCL_StrListAttribute : PropertyAttribute, IStrList
    {
        public IList<string> m_StrList;
        public UCL_StrListAttribute(params string[] iList) {
            m_StrList = iList;
        }
        public UCL_StrListAttribute(Type iType, string iFuncName) {
            var aMethod = iType.GetMethod(iFuncName);
            if(aMethod != null) {
                try {
                    m_StrList = aMethod.Invoke(null, null) as IList<string>;
                } catch(Exception iE) {
                    Debug.LogException(iE);
                    Debug.LogError("UCL_ListProperty method.Invoke iFuncName:" + iFuncName + " Exception:" + iE.ToString());
                }
            } else { //might be accessor
                PropertyInfo aPropInfo = iType.GetProperty(iFuncName);
                if(aPropInfo == null) { // not accessor!!
                    Debug.LogError("UCL_ListProperty:" + iType.Name + ",func_name == null :" + iFuncName);
                    return;
                }
                MethodInfo[] aAccessors = aPropInfo.GetAccessors();
                for(int i = 0; i < aAccessors.Length; i++) {
                    MethodInfo aMethodInfo = aAccessors[i];
                    // Determine if this is the property getter or setter.
                    if (aMethodInfo.ReturnType == typeof(void)) {//setter
                        //m.Invoke(test, new object[] { "The Modified Caption" });
                    } else {//getter
                        var result = aMethodInfo.Invoke(null, new object[] { });
                        if (result is IList<string> list)
                        {
                            m_StrList = list;
                        }
                        else if (result is IEnumerable<string> enumerable)
                        {
                            m_StrList = enumerable.ToList();
                        }

                        if (m_StrList != null) break;
                    }
                }
            }
        }

        public IList<string> GetStrList(object iTarget)
        {
            return m_StrList;
        }
    }
    public class UCL_ListAttribute : PropertyAttribute, IStrList
    {
        //Type m_Type = null;
        string m_MethodName = null;
        object[] m_Params = null;
        /// <summary>
        /// 
        /// </summary>
        /// <param name="iMethodName">MethodName of member function that return a IList<string></param>
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
        public IList<string> GetStrList(object iTarget) {
            var aResult = iTarget.Invoke(m_MethodName, m_Params);
            if (aResult is IList<string> list) return list;
            if (aResult is IEnumerable<string> enumerable) return enumerable.ToList();

            return Array.Empty<string>();
        }
        /// <summary>
        /// Get the display list from target
        /// </summary>
        /// <param name="iTarget"></param>
        /// <returns></returns>
        public string[] GetDisplayList(object iTarget)
        {
            var aList = GetStrList(iTarget);
            string[] aDisplayList = new string[aList.Count];
            for (int i = 0; i < aList.Count; i++)
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

    public class UCL_ValueDropdownAttribute : PropertyAttribute, IValueDropdown
    {
        public MethodInfo methodInfo;

        public UCL_ValueDropdownAttribute(Type iType, string iFuncName)
        {
            var eType = typeof(IEnumerable<string>);
            var eType2 = typeof(IList<string>);
            var methods = iType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            methodInfo = methods.First(a => a.Name == iFuncName && 
                (eType.IsAssignableFrom(a.ReturnType) || eType2.IsAssignableFrom(a.ReturnType)));

            if (methodInfo == null) //might be accessor
            { 
                PropertyInfo aPropInfo = iType.GetProperty(iFuncName);
                if (aPropInfo == null)
                { // not accessor!!
                    Debug.LogError("UCL_ListProperty:" + iType.Name + ",func_name == null :" + iFuncName);
                    return;
                }
                MethodInfo[] accessors = aPropInfo.GetAccessors();
                methodInfo = accessors.First(a => a.Name == iFuncName &&
                    (eType.IsAssignableFrom(a.ReturnType) || eType2.IsAssignableFrom(a.ReturnType)));
            }
        }

        public IList<string> GetStrList(object iTarget)
        {
            if(methodInfo == null)
            {
                Debug.LogError("UCL_ValueDropdownAttribute methodInfo == null");
                return Array.Empty<string>();
            } 

            var result = methodInfo.Invoke(iTarget, new object[] { });
            if(result == null)
            {
                Debug.LogError("UCL_ValueDropdownAttribute result == null");
                return Array.Empty<string>();
            }
            if (result is IList<string> list) return list;
            if (result is IEnumerable<string> enumerable) return enumerable.ToList();

            Debug.LogError($"UCL_ValueDropdownAttribute result.GetType:{result.GetType()}");
            return Array.Empty<string>();
        }
    }
}