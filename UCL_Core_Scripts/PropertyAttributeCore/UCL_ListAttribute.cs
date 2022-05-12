using UnityEngine;
using System;
using System.Reflection;
using UCL.Core.ObjectReflectionExtension;
using System.Collections.Generic;

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
                        m_StrList = aMethodInfo.Invoke(null, new object[] { }) as IList<string>;
                        if(m_StrList != null) break;
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
            if (aResult is IList<string>) return aResult as IList<string>;

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
}