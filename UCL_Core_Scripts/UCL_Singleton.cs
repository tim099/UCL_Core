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
                    CreateInstance();
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
        static public T CreateInstance() {
            if(_instance != null) {
                return _instance;
            }
            GameObject singleton = new GameObject(typeof(T).Name + "(UCL_Singleton_AutoGen)");
            singleton.SetActive(false);
            _instance = singleton.AddComponent<T>();//this trigger awake if gameobject enable!!
            DontDestroyOnLoad(singleton);

            singleton.SetActive(true);

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
        /// return true if set Instance success
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        static protected bool SetInstance(T value) {
            if(_instance != null) {
                if(value != _instance) {
                    Destroy(value.gameObject);
                    return false;
                }
                return true;//value == _instance
            }

            _instance = value;
            //Debug.LogWarning("_instance.name:" + _instance.name);
            _instance.name += "(UCL_Singleton)"; //typeof(T).Name + "(UCL_Singleton)";

            if(_instance.transform.parent == null) DontDestroyOnLoad(_instance.gameObject);
            
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
            if(_instance.transform.parent == null) DontDestroyOnLoad(_instance.gameObject);
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
            var ins = Instantiate(value);
            ins.name = ins.name.Replace("(Clone)", "");
            return SetInstance(ins);
        }
        virtual protected void OnDestroy() {
            if(_instance == this) {
                _instance = null;
                m_Destroyed = true;
            }
        }

    }
}

