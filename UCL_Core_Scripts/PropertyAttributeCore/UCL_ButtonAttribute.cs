using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System;
using System.Reflection;

namespace UCL.Core.PA {
    public class UCL_ButtonAttribute : PropertyAttribute {
        public Type m_Type;
        public string m_FuncName;
        public UCL_ButtonAttribute() {

        }
        public UCL_ButtonAttribute(params object[] par) {
            for(int i = 0; i < par.Length; i++) {
                SetParam(par[i]);
            }
        }
        void SetParam(object obj) {
            var tt = obj as Type;
            if(tt != null) {
                m_Type = tt;
            }else{
                var type = obj.GetType();
                if(type == typeof(string)) {
                    m_FuncName = obj as string;
                }
            }
        }
        public void InvokeAct(string iFuncName, object iObj, params object[] iParams) {
            //Debug.LogWarning("func_name:" + func_name + ",obj:" + obj.GetType().Name);
            if(!string.IsNullOrEmpty(m_FuncName)) {
                iFuncName = m_FuncName;
            }
            if(m_Type == null) {
                m_Type = iObj.GetType();
            }
            var aMethods = m_Type.GetMethods();

            MethodInfo aSelectedMethod = null;
            int par_len = 0;
            int cur_len = 0;
            if(iParams != null) par_len = iParams.Length;
            //Debug.LogWarning("par_len:" + par_len);
            foreach(var aMethod in aMethods) {
                if(aMethod.Name == iFuncName) {
                    var m_pars = aMethod.GetParameters();
                    int m_plen = m_pars.Length;
                    if(aSelectedMethod == null) {
                        aSelectedMethod = aMethod;
                        cur_len = m_plen;
                    } else {
                        if(par_len == m_plen || (cur_len != m_plen && m_plen <= cur_len)) {
                            aSelectedMethod = aMethod;
                            cur_len = m_plen;
                        }
                    }

                    if(m_plen == par_len) {
                        bool check_flag = true;
                        for(int i = 0; i < m_plen; i++) {
                            var p = m_pars[i];
                            var pl = iParams[i];
                            if(pl != null && p.GetType() != pl.GetType()) {
                                check_flag = false;
                            }
                        }
                        if(check_flag) break;//find!!
                    }
                }
            }
            //var method = m_Type.GetMethod(func_name);
            if(aSelectedMethod != null) {
                if(aSelectedMethod.GetParameters().Length == 0) {
                    iParams = null;
                }
                try {
                    if(m_Type == iObj.GetType()) {
                        
                        aSelectedMethod.Invoke(iObj, iParams);
                    } else {
                        aSelectedMethod.Invoke(null, iParams);//static!!
                        /*
                        var obj_arr = new object[1];
                        obj_arr[0] = obj;
                        method?.Invoke(null, obj_arr);
                        */
                    }
                } catch(Exception e) {
                    Debug.LogError("UCL_ButtonProperty: " + m_Type.Name + "_" + iFuncName + ".Invoke Exception:" + e.ToString());
                }
            } else {
                string names = "";
                foreach(var m in aMethods) {
                    names += m.Name + "\n";
                }
                Debug.LogError(m_Type.Name+" func_name:\"" + iFuncName+"\" Not Exist!!,Functions:\n"+ names);

            }
        }
    }
}