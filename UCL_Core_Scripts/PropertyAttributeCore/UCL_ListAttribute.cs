using UnityEngine;
using System;
using System.Reflection;
using UCL.Core.ObjectReflectionExtension;

namespace UCL.Core.PA {
    public class UCL_StrListAttribute : PropertyAttribute {
        public string[] m_List;
        public UCL_StrListAttribute(params string[] list) {
            m_List = list;
        }
        public UCL_StrListAttribute(Type type, string func_name) {
            var method = type.GetMethod(func_name);
            if(method != null) {
                try {
                    m_List = method.Invoke(null, null) as string[];
                } catch(Exception e) {
                    Debug.LogError("UCL_ListProperty method.Invoke Exception:" + e.ToString());
                }

            } else { //might be accessor
                PropertyInfo propInfo = type.GetProperty(func_name);
                if(propInfo == null) { // not accessor!!
                    Debug.LogError("UCL_ListProperty:" + type.Name + ",func_name == null :" + func_name);
                    return;
                }
                MethodInfo[] methInfos = propInfo.GetAccessors();
                for(int i = 0; i < methInfos.Length; i++) {
                    MethodInfo m = methInfos[i];
                    //Console.WriteLine("Accessor #{0}:", ctr + 1);
                    //Console.WriteLine("   Name: {0}", m.Name);
                    //Console.WriteLine("   Visibility: {0}", GetVisibility(m));
                    //Console.Write("   Property Type: ");
                    // Determine if this is the property getter or setter.
                    if(m.ReturnType == typeof(void)) {//setter
                        //Console.WriteLine("Setter");
                        //Console.WriteLine("   Setting the property value.");
                        //  Set the value of the property.
                        //m.Invoke(test, new object[] { "The Modified Caption" });
                    } else {//getter
                        //Console.WriteLine("Getter");
                        // Get the value of the property.
                        //Console.WriteLine("   Property Value: {0}", m.Invoke(test, new object[] { }));
                        m_List = m.Invoke(null , new object[] { }) as string[];
                        if(m_List != null) break;
                    }
                }
            }
        }
    }
    public class UCL_ListAttribute : PropertyAttribute {
        string m_MethodName = null;
        object[] m_Params = null;
        public UCL_ListAttribute(string _MethodName, params object[] para) {
            m_MethodName = _MethodName;
            m_Params = para;
        }
        public string[] GetList(object target) {
            return (string[]) target.Invoke(m_MethodName, m_Params);
        }
        //public UCL_ListAttribute (System.Action<List<string>> get)
    }
}