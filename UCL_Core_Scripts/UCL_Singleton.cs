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
                if(!SetInstance(value)) {
                    //Debug.LogError("UCL_Singleton:" + typeof(T).Name + "Set Twice!! Destroy new Instance!!");
                    throw new System.Exception("UCL_Singleton:" + typeof(T).Name + "Set Twice!! Destroy new Instance!!");
                    //return;
                }
            }
        }
        static protected bool SetInstance(T value) {
            if(_instance != null) {
                if(value != _instance) {
                    Destroy(value.gameObject);
                }
                return false;
            }
            _instance = value;
            _instance.name += "(UCL_Singleton)"; //typeof(T).Name + "(UCL_Singleton)";
            DontDestroyOnLoad(_instance.gameObject);
            return true;
        }
        virtual protected void OnDestroy() {
            if(_instance == this) {
                _instance = null;
                m_Destroyed = true;
            }
        }

    }
}

