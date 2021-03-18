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
        public void InvokeAct(string func_name,object obj,params object[] par) {
            //Debug.LogWarning("func_name:" + func_name + ",obj:" + obj.GetType().Name);
            if(!string.IsNullOrEmpty(m_FuncName)) {
                func_name = m_FuncName;
            }
            if(m_Type == null) {
                m_Type = obj.GetType();
            }
            var methods = m_Type.GetMethods();

            System.Reflection.MethodInfo method = null;
            int par_len = 0;
            int cur_len = 0;
            if(par != null) par_len = par.Length;
            //Debug.LogWarning("par_len:" + par_len);
            foreach(var m in methods) {
                if(m.Name == func_name) {
                    var m_pars = m.GetParameters();
                    int m_plen = m_pars.Length;
                    if(method == null) {
                        method = m;
                        cur_len = m_plen;
                    } else {
                        if(par_len == m_plen || (cur_len != m_plen && m_plen <= cur_len)) {
                            method = m;
                            cur_len = m_plen;
                        }
                    }

                    if(m_plen == par_len) {
                        bool check_flag = true;
                        for(int i = 0; i < m_plen; i++) {
                            var p = m_pars[i];
                            var pl = par[i];
                            if(pl != null && p.GetType() != pl.GetType()) {
                                check_flag = false;
                            }
                        }
                        if(check_flag) break;//find!!
                    }
                }
            }
            //var method = m_Type.GetMethod(func_name);
            if(method != null) {
                if(method.GetParameters().Length == 0) {
                    par = null;
                }
                try {
                    if(m_Type == obj.GetType()) {
                        
                        method.Invoke(obj, par);
                    } else {
                        method.Invoke(null, par);//static!!
                        /*
                        var obj_arr = new object[1];
                        obj_arr[0] = obj;
                        method?.Invoke(null, obj_arr);
                        */
                    }
                } catch(Exception e) {
                    Debug.LogError("UCL_ButtonProperty: " + m_Type.Name + "_" + func_name + ".Invoke Exception:" + e.ToString());
                }
            } else {
                string names = "";
                foreach(var m in methods) {
                    names += m.Name + "\n";
                }
                Debug.LogError(m_Type.Name+" func_name:\"" + func_name+"\" Not Exist!!,Functions:\n"+ names);

            }
        }
    }
}