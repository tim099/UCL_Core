using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL {
    public class UCL_Singleton<T> : MonoBehaviour where T : MonoBehaviour {
        static T _instance;
        static bool m_Destroyed = false;
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
                _instance = value;
                _instance.name = typeof(T).Name + "(UCL_Singleton)";
                DontDestroyOnLoad(_instance.gameObject);
            }
        }
        private void OnDestroy() {
            m_Destroyed = true;
            _instance = null;
        }

    }
}

