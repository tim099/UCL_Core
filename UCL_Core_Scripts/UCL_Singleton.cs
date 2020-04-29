using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core {
    public class UCL_Singleton<T> : MonoBehaviour where T : MonoBehaviour {
        static T _instance;
        static protected bool m_Destroyed = false;
        /// <summary>
        /// return instance, and auto create one if instance not exsit!!
        /// </summary>
        static public T Instance {
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
        /// <summary>
        /// Auto create instance if not exist!!
        /// </summary>
        /// <returns></returns>
        static public T Get() {
            return Instance;
        }
        /// <summary>
        /// Won't auto create instance if not exist!!
        /// </summary>
        /// <returns></returns>
        static public T GetInstance() {
            return _instance;
        }
        /// <summary>
        /// Set instance manually!!
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
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
        /// <summary>
        /// Replace current instance
        /// </summary>
        /// <param name="value"></param>
        static protected void ReplaceInstance(T value) {
            if(value == _instance) return;

            if(_instance != null) {
                Destroy(_instance.gameObject);
            }

            _instance = value;
            _instance.name += "(UCL_Singleton)"; //typeof(T).Name + "(UCL_Singleton)";
            DontDestroyOnLoad(_instance.gameObject);
        }


        /// <summary>
        /// Create and set instance by value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        static protected bool CreateInstance(T value) {
            if(_instance != null) {
                return false;
            }
            if(value == null) {
                return false;
            }
            return SetInstance(Instantiate(value));
        }

        virtual protected void OnDestroy() {
            if(_instance == this) {
                _instance = null;
                m_Destroyed = true;
            }
        }

    }
}

