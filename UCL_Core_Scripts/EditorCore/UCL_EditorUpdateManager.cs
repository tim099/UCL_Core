using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.EditorLib {
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    static public class UCL_EditorUpdateManager {
#if UNITY_EDITOR
        static event System.Action m_EditorUpdateAction = null;
        static Dictionary<string, System.Action> m_EditorUpdateActionDic = new Dictionary<string, System.Action>();

        static Queue<System.Tuple<int,System.Action> > m_DelayActQue = new Queue<Tuple<int, Action>>();
        static Queue<System.Tuple<int, System.Action>> m_DelayActQueBuffer = new Queue<Tuple<int, Action>>();
        /// <summary>
        /// Action trigger once!!
        /// </summary>
        static Queue<System.Action> m_ActQue = new Queue<Action>();
#endif
        static UCL_EditorUpdateManager() {
#if UNITY_EDITOR
            //Debug.Log("UCL_EditorUpdateManager() Init UnityEditor.EditorApplication.update += EditorUpdate");
            UnityEditor.EditorApplication.update += UpdateAction;
#endif
        }
        /// <summary>
        /// Add action that only invoke once
        /// </summary>
        /// <param name="act"></param>
        static public void AddAction(System.Action act) {
#if UNITY_EDITOR
            m_ActQue?.Enqueue(act);
#endif
        }
        /// <summary>
        /// Add action that invoke after delay_frame
        /// </summary>
        /// <param name="act"></param>
        /// <param name="delay_frame"></param>
        static public void AddDelayAction(System.Action act, int delay_frame) {
#if UNITY_EDITOR
            m_DelayActQue?.Enqueue(new Tuple<int, Action>(delay_frame, act));
#endif
        }
        static public void AddEditorUpdateAct(System.Action act) {
#if UNITY_EDITOR
            m_EditorUpdateAction += act;
            Debug.Log("AddEditorUpdateAct count:" + m_EditorUpdateAction.GetInvocationList().GetLength(0));
#endif
        }
        static public void RemoveEditorUpdateAct(System.Action act) {
#if UNITY_EDITOR
            m_EditorUpdateAction -= act;
            int count = 0;
            if(m_EditorUpdateAction != null) {
                count = m_EditorUpdateAction.GetInvocationList().GetLength(0);
            }
            Debug.Log("RemoveEditorUpdateAct count:" + count);
#endif
        }
        static public void AddEditorUpdateAct(string key, System.Action act) {
#if UNITY_EDITOR
            if(m_EditorUpdateActionDic.ContainsKey(key)) {
                Debug.LogError("UCL_EditorUpdateManager AddEditorUpdateAct Fail!!key:" + key + ",already Exist!!");
                return;
            }
            m_EditorUpdateActionDic.Add(key, act);
            Debug.Log("m_EditorUpdateActionDic.Count:" + m_EditorUpdateActionDic.Count);
#endif
        }
        static public void RemoveEditorUpdateAct(string key) {
#if UNITY_EDITOR
            if(!m_EditorUpdateActionDic.ContainsKey(key)) {
                Debug.LogError("UCL_EditorUpdateManager AddEditorUpdateAct Fail!!key:" + key + ",not Exist!!");
                return;
            }
            m_EditorUpdateActionDic.Remove(key);
            Debug.Log("m_EditorUpdateActionDic.Count:" + m_EditorUpdateActionDic.Count);
#endif
        }
        static public void Clear() {
#if UNITY_EDITOR
            m_EditorUpdateActionDic?.Clear();
            m_EditorUpdateAction = null;
#endif
        }

        /// <summary>
        /// Main update loop
        /// </summary>
        static void UpdateAction() {
#if UNITY_EDITOR
            try {
                m_EditorUpdateAction?.Invoke();
            } catch(Exception e) {
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
                Core.GameObjectLib.Swap(ref m_DelayActQue, ref m_DelayActQueBuffer);
            }
            //Debug.Log("EditorUpdate()!!");
#endif
        }
    }
}

