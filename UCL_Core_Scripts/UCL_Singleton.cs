using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core {
    public class UCL_Singleton<T> : MonoBehaviour where T : MonoBehaviour {
        static T _instance;
        static protected bool m_Destroyed = false;
        public static T Instance {
            get {
                if(m_Destroyed) return null;

                if(_instance == null) {
                    GameObject singleton = new GameObject(typeof(T).Name+ "(AutoGen_UCL_Singleton)");
                    _instance = singleton.AddComponent<T>();
                    DontDestroyOnLoad(singleton);
                }

                return _instance;
            }
            set {
                if(_instance != null) {
                    Debug.LogError("UCL_Singleton:" + typeof(T).Name + "Set Twice!!");
                    return;
                }
                _instance = value;
                _instance.name += "(UCL_Singleton)"; //typeof(T).Name + "(UCL_Singleton)";
                DontDestroyOnLoad(_instance.gameObject);
            }
        }
        virtual protected void OnDestroy() {
            m_Destroyed = true;
            _instance = null;
        }

    }
}

