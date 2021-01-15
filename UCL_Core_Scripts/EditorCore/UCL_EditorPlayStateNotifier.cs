using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.EditorLib {
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    public static class UCL_EditorPlayStateNotifier {
        static List<System.Action> m_EnteredEditModeAct = new List<System.Action>();
        static List<System.Action> m_ExitingEditModeAct = new List<System.Action>();
        static List<System.Action> m_EnteredPlayModeAct = new List<System.Action>();
        static List<System.Action> m_ExitingPlayModeAct = new List<System.Action>();
        static UCL_EditorPlayStateNotifier() {
#if UNITY_EDITOR
            Debug.Log("UCL_EditorPlayStateNotifier() UnityEditor.EditorApplication.playModeStateChanged += PlayModeStateChanged");
            UnityEditor.EditorApplication.playModeStateChanged += PlayModeStateChanged;
#endif
        }
        public static void AddEnteredEditModeAct(System.Action act) {
#if UNITY_EDITOR
            m_EnteredEditModeAct.Add(act);
#endif
        }
        public static void RemoveEnteredEditModeAct(System.Action act) {
#if UNITY_EDITOR
            m_EnteredEditModeAct.Remove(act);
#endif
        }
        public static void AddExitingEditModeAct(System.Action act) {
#if UNITY_EDITOR
            m_ExitingEditModeAct.Add(act);
#endif
        }
        public static void RemoveExitingEditModeAct(System.Action act) {
#if UNITY_EDITOR
            m_ExitingEditModeAct.Remove(act);
#endif
        }
        public static void AddEnteredPlayModeAct(System.Action act) {
#if UNITY_EDITOR
            m_EnteredPlayModeAct.Add(act);
#endif
        }
        public static void RemoveEnteredPlayModeAct(System.Action act) {
#if UNITY_EDITOR
            m_EnteredPlayModeAct.Remove(act);
#endif
        }

        public static void AddExitingPlayModeAct(System.Action act) {
#if UNITY_EDITOR
            m_ExitingPlayModeAct.Add(act);
#endif
        }
        public static void RemoveExitingPlayModeAct(System.Action act) {
#if UNITY_EDITOR
            m_ExitingPlayModeAct.Remove(act);
#endif
        }
#if UNITY_EDITOR
        static void PlayModeStateChanged(UnityEditor.PlayModeStateChange state) {

            switch(state) {
                case UnityEditor.PlayModeStateChange.EnteredEditMode: {
                        List<System.Action> acts = m_EnteredEditModeAct;
                        for(int i = acts.Count-1 ; i >=0 ; i--) {
                            var act = acts[i];
                            if(act == null) {
                                acts.RemoveAt(i);
                            } else {
                                try {
                                    act.Invoke();
                                }catch(System.Exception e) {
                                    Debug.LogError("UCL_EditorPlayStateNotifier "+state.ToString()+" Exception:" + e);
                                    acts.RemoveAt(i);
                                }
                            }
                        }

                        break;
                    }
                case UnityEditor.PlayModeStateChange.ExitingEditMode: {
                        List<System.Action> acts = m_ExitingEditModeAct;
                        for(int i = acts.Count - 1; i >= 0; i--) {
                            var act = acts[i];
                            if(act == null) {
                                acts.RemoveAt(i);
                            } else {
                                try {
                                    act.Invoke();
                                } catch(System.Exception e) {
                                    Debug.LogError("UCL_EditorPlayStateNotifier " + state.ToString() + " Exception:" + e);
                                    acts.RemoveAt(i);
                                }
                            }
                        }
                        break;
                    }
                case UnityEditor.PlayModeStateChange.EnteredPlayMode: {
                        List<System.Action> acts = m_EnteredPlayModeAct;
                        for(int i = acts.Count - 1; i >= 0; i--) {
                            var act = acts[i];
                            if(act == null) {
                                acts.RemoveAt(i);
                            } else {
                                try {
                                    act.Invoke();
                                } catch(System.Exception e) {
                                    Debug.LogError("UCL_EditorPlayStateNotifier " + state.ToString() + " Exception:" + e);
                                    acts.RemoveAt(i);
                                }
                            }
                        }
                        break;
                    }
                case UnityEditor.PlayModeStateChange.ExitingPlayMode: {
                        List<System.Action> acts = m_ExitingPlayModeAct;
                        for(int i = acts.Count - 1; i >= 0; i--) {
                            var act = acts[i];
                            if(act == null) {
                                acts.RemoveAt(i);
                            } else {
                                try {
                                    act.Invoke();
                                } catch(System.Exception e) {
                                    Debug.LogError("UCL_EditorPlayStateNotifier " + state.ToString() + " Exception:" + e);
                                    acts.RemoveAt(i);
                                }
                            }
                        }
                        break;
                    }
            }
        }
#endif
    }
}