using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
#if UNITY_EDITOR
namespace UCL.Core.EditorLib {
    [InitializeOnLoad]
    static public class UCL_EditorUpdateManager {
        //[InitializeOnLoadMethod]
        //static public void Init() {}
        static System.Action m_EditorUpdateAction;
        static Dictionary<string, System.Action> m_EditorUpdateActionDic;


        static Queue<System.Tuple<int,System.Action> > m_DelayActQue;
        static Queue<System.Tuple<int, System.Action>> m_DelayActQueBuffer;
        /// <summary>
        /// Only trigger Once!!
        /// </summary>
        static Queue<System.Action> m_ActQue;
        static UCL_EditorUpdateManager() {
            Debug.Log("UCL_EditorUpdateManager() Init UnityEditor.EditorApplication.update += EditorUpdate");
            UnityEditor.EditorApplication.update += EditorUpdate;
            m_EditorUpdateAction = null;
            m_EditorUpdateActionDic = new Dictionary<string, System.Action>();
            m_ActQue = new Queue<Action>();
            m_DelayActQue = new Queue<Tuple<int, Action>>();
            m_DelayActQueBuffer = new Queue<Tuple<int, Action>>();
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
            if(m_DelayActQue != null) {
                foreach(var act in m_DelayActQue) {
                    if(act.Item1 > 0) {
                        m_DelayActQueBuffer.Enqueue(new Tuple<int, Action>(act.Item1 - 1, act.Item2));
                    } else {
                        AddAction(act.Item2);
                    }
                }
                m_DelayActQue.Clear();
                Core.UCL_GameObjectLib.swap(ref m_DelayActQue, ref m_DelayActQueBuffer);
            }
            //Debug.Log("EditorUpdate()!!");
        }
        static public void AddAction(System.Action act) {
            m_ActQue?.Enqueue(act);
        }
        static public void AddDelayAction(System.Action act,int delay_frame) {
            m_DelayActQue?.Enqueue(new Tuple<int, Action>(delay_frame, act));
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