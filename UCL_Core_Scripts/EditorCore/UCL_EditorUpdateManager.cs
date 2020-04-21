using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
#if UNITY_EDITOR
namespace UCL.Core.Editor {
    [InitializeOnLoad]
    static public class UCL_EditorUpdateManager {
        //[InitializeOnLoadMethod]
        //static public void Init() {}
        static System.Action m_EditorUpdateAction;
        static Dictionary<string, System.Action> m_EditorUpdateActionDic;
        /// <summary>
        /// Only trigger Once!!
        /// </summary>
        static Queue<System.Action> m_ActQue;
        static UCL_EditorUpdateManager() {
            Debug.LogWarning("UCL_EditorUpdateManager() Init!!");
            UnityEditor.EditorApplication.update += EditorUpdate;
            m_EditorUpdateAction = null;
            m_EditorUpdateActionDic = new Dictionary<string, System.Action>();
            m_ActQue = new Queue<Action>();
        }
        static void EditorUpdate() {
            try {
                m_EditorUpdateAction?.Invoke();
            }catch(Exception e) {
                Debug.LogError(e);
            }
            foreach(System.Action act in m_EditorUpdateActionDic.Values) {
                try {
                    act?.Invoke();
                } catch(Exception e) {
                    Debug.LogError(e);
                }
                
            }
            if(m_ActQue != null) {
                while(m_ActQue.Count > 0) {
                    try {
                        m_ActQue.Dequeue()?.Invoke();
                    } catch(Exception e) {
                        Debug.LogError(e);
                    }
                }
            }

            //Debug.Log("EditorUpdate()!!");
        }
        static public void AddAction(System.Action act) {
            m_ActQue?.Enqueue(act);
        }
        static public void AddEditorUpdateAct(System.Action act) {
            m_EditorUpdateAction += act;
            Debug.LogWarning("AddEditorUpdateAct count:" + m_EditorUpdateAction.GetInvocationList().GetLength(0));
        }
        static public void RemoveEditorUpdateAct(System.Action act) {
            m_EditorUpdateAction -= act;
            int count = 0;
            if(m_EditorUpdateAction != null) {
                count = m_EditorUpdateAction.GetInvocationList().GetLength(0);
            }
            Debug.LogWarning("RemoveEditorUpdateAct count:" + count);
        }
        static public void AddEditorUpdateAct(string key, System.Action act) {
            if(m_EditorUpdateActionDic.ContainsKey(key)) {
                Debug.LogError("UCL_EditorUpdateManager AddEditorUpdateAct Fail!!key:" + key + ",already Exist!!");
                return;
            }
            m_EditorUpdateActionDic.Add(key, act);
            Debug.Log("m_EditorUpdateActionDic.Count:" + m_EditorUpdateActionDic.Count);
        }
        static public void RemoveEditorUpdateAct(string key) {
            if(!m_EditorUpdateActionDic.ContainsKey(key)) {
                Debug.LogError("UCL_EditorUpdateManager AddEditorUpdateAct Fail!!key:" + key + ",not Exist!!");
                return;
            }
            m_EditorUpdateActionDic.Remove(key);
            Debug.Log("m_EditorUpdateActionDic.Count:" + m_EditorUpdateActionDic.Count);
        }
        static public void Clear() {
            m_EditorUpdateActionDic?.Clear();
            m_EditorUpdateAction = null;
        }

    }
}

#endif