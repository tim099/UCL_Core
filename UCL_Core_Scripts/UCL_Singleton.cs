using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core {
    public class UCL_Singleton<T> : MonoBehaviour where T : MonoBehaviour {
        static T m_Instance;
        static protected bool m_Destroyed = false;
        /// <summary>
        /// return instance, and auto create one if instance not exsit!!
        /// </summary>
        static public T Instance {
            get {
                if(m_Destroyed) return null;

                if(m_Instance == null) {
                    CreateInstance();
                }

                return m_Instance;
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
            if(m_Instance != null) {
                return m_Instance;
            }
            GameObject singleton = new GameObject(typeof(T).Name + "(UCL_Singleton_AutoGen)");
            singleton.SetActive(false);
            m_Instance = singleton.AddComponent<T>();//this trigger awake if gameobject enable!!
            DontDestroyOnLoad(singleton);

            singleton.SetActive(true);
            return Instance;
        }
        /// <summary>
        /// Won't auto create instance if not exist!!
        /// </summary>
        /// <returns></returns>
        static public T GetInstance() {
            return m_Instance;
        }
        /// <summary>
        /// Set instance manually!!
        /// return true if set Instance success
        /// </summary>
        /// <param name="iInstance"></param>
        /// <returns></returns>
        static protected bool SetInstance(T iInstance) {
            if(m_Instance != null) {
                if(iInstance != m_Instance) {
                    if(iInstance != null) Destroy(iInstance.gameObject);
                    return false;
                }
                return true;//value == _instance
            }

            m_Instance = iInstance;
            //Debug.LogWarning("_instance.name:" + _instance.name);
            m_Instance.name += "(UCL_Singleton)"; //typeof(T).Name + "(UCL_Singleton)";

            if(m_Instance.transform.parent == null) DontDestroyOnLoad(m_Instance.gameObject);
            
            return true;
        }
        /// <summary>
        /// Replace current instance
        /// </summary>
        /// <param name="value"></param>
        static protected void ReplaceInstance(T value) {
            if(value == m_Instance) return;

            if(m_Instance != null) {
                Destroy(m_Instance.gameObject);
            }

            m_Instance = value;
            m_Instance.name += "(UCL_Singleton)"; //typeof(T).Name + "(UCL_Singleton)";
            if(m_Instance.transform.parent == null) DontDestroyOnLoad(m_Instance.gameObject);
        }


        /// <summary>
        /// Create and set instance by value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        static protected bool CreateInstance(T value) {
            if(m_Instance != null) {
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
            if(m_Instance == this) {
                m_Instance = null;
                m_Destroyed = true;
            }
        }

    }
}

