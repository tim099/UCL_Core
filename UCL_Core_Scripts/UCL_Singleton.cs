using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core {
#if UNITY_EDITOR
    // 區塊職責：非泛型 singleton static 重置登錄處
    // 物理意義：每個被使用過的 UCL_Singleton<T> 會把自身的 static 重置動作登錄進來，
    //          供「進入 Play 模式」時統一呼叫，模擬 Domain Reload 的 static 歸零行為。
    // 數值影響：在 Enter Play Mode Options 關閉 Domain Reload 的設定下（本專案 m_EnterPlayModeOptions:3），
    //          清掉殘留的 s_Instance / _Destroyed，使第二次進 Play 仍能正常重建 Instance。
    internal static class UCL_SingletonResetRegistry {
        // 各 closed-generic 型別登錄的重置動作。此 List 本身為 static —— 在 Domain Reload 關閉時跨 Play 持續存在，
        // 正是我們需要的：session 1 登錄過的型別，session 2 進 Play 時仍記得要重置它。
        static readonly List<System.Action> s_Resets = new List<System.Action>();

        /// <summary>
        /// 登錄一個型別專屬的 static 重置動作。
        /// 由 UCL_Singleton&lt;T&gt; 的 static 建構子呼叫，每個 closed-generic 型別只會登錄一次。
        /// </summary>
        /// <param name="iReset">該型別的 static 歸零動作（清 s_Instance / _Destroyed）。</param>
        public static void Register(System.Action iReset) {
            // 防重複登錄：同一型別的 static ctor 只跑一次，但保險起見仍去重，避免重置動作被呼叫多次。
            if(!s_Resets.Contains(iReset)) s_Resets.Add(iReset);
        }

        // 每次進入 Play 模式於最早期（SubsystemRegistration）觸發。
        // 此方法為「非泛型 static method」，故即使 Domain Reload 關閉也會被 Unity 正常呼叫
        //（RuntimeInitializeOnLoadMethod 無法掛在 generic 型別的 static method 上，這正是需要本登錄處的原因）。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetAll() {
            // 逐一呼叫所有登錄過的型別重置動作，把上一個 Play session 殘留的 static 狀態歸零。
            foreach(var aReset in s_Resets) aReset();
        }
    }
#endif


    public class UCL_Singleton<T> : MonoBehaviour where T : MonoBehaviour {
#if UNITY_EDITOR
        // 型別初始化時把自身的 static 重置動作登錄進非泛型登錄處。
        // static 建構子在該 closed-generic 型別首次被存取時跑一次；登錄後即使本型別之後不再重新初始化
        //（Domain Reload 關閉 → 型別不會二次初始化），登錄處仍記得它，故下次進 Play 能被正確重置。
        static UCL_Singleton() {
            UCL_SingletonResetRegistry.Register(ResetStatics);
        }
#endif
        /// <summary>
        /// 清空本型別的 singleton static 狀態（模擬 Domain Reload 歸零）。
        /// 由 UCL_SingletonResetRegistry 於每次進入 Play 模式時呼叫。
        /// </summary>
        static void ResetStatics() {
            s_Instance = null;   // 清掉可能殘留的 instance 參考（指向已被銷毀的 GameObject）。
            s_Destroyed = false;  // 關鍵：把 destroyed 旗標歸位，否則第二次進 Play 取 Instance 會卡在 return null。
        }

        static T s_Instance = null;
        static protected bool s_Destroyed = false;
        //{
        //    get => _Destroyed;
        //    set {
        //        Debug.LogError($"UCL_Singleton:{typeof(T).Name} destroyed!!");
        //        _Destroyed = value;
        //    }
        //}
        //static protected bool _Destroyed = false;
        /// <summary>
        /// return instance, and auto create one if instance not exsit!!
        /// </summary>
        static public T Instance {
            get {
                if(s_Destroyed)
                {
                    Debug.LogError("UCL_Singleton:" + typeof(T).Name + " is destroyed!!");
                    return null;
                }

                if(s_Instance == null) {
                    CreateInstance();
                }

                return s_Instance;
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
        /// Ensure Instance Exist
        /// </summary>
        /// <returns></returns>
        static public T CheckInstance()
        {
            return Instance;
        }
        /// <summary>
        /// Auto create instance if not exist!!
        /// </summary>
        /// <returns></returns>
        static public T CreateInstance() {
            if(s_Instance != null) {
                return s_Instance;
            }
            GameObject singleton = new GameObject(typeof(T).Name + "(UCL_Singleton_AutoGen)");
            singleton.SetActive(false);
            s_Instance = singleton.AddComponent<T>();//this trigger awake if gameobject enable!!
            DontDestroyOnLoad(singleton);

            singleton.SetActive(true);
            return Instance;
        }
        /// <summary>
        /// Won't auto create instance if not exist!!
        /// </summary>
        /// <returns></returns>
        static public T GetInstance() {
            return s_Instance;
        }
        /// <summary>
        /// Set instance manually!!
        /// return true if set Instance success
        /// </summary>
        /// <param name="iInstance"></param>
        /// <returns></returns>
        static protected bool SetInstance(T iInstance) {
            if(s_Instance != null) {
                if(iInstance != s_Instance) {
                    if(iInstance != null) Destroy(iInstance.gameObject);
                    return false;
                }
                return true;//value == _instance
            }

            s_Instance = iInstance;
            //Debug.LogWarning("_instance.name:" + _instance.name);
            s_Instance.name += "(UCL_Singleton)"; //typeof(T).Name + "(UCL_Singleton)";

            if(s_Instance.transform.parent == null) DontDestroyOnLoad(s_Instance.gameObject);
            
            return true;
        }
        /// <summary>
        /// Replace current instance
        /// </summary>
        /// <param name="value"></param>
        static protected void ReplaceInstance(T value) {
            if(value == s_Instance) return;

            if(s_Instance != null) {
                Destroy(s_Instance.gameObject);
            }

            s_Instance = value;
            s_Instance.name += "(UCL_Singleton)"; //typeof(T).Name + "(UCL_Singleton)";
            if(s_Instance.transform.parent == null) DontDestroyOnLoad(s_Instance.gameObject);
        }


        /// <summary>
        /// Create and set instance by value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        static protected bool CreateInstance(T value) {
            if(s_Instance != null) {
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
            if(s_Instance == this) {
                s_Instance = null;
                s_Destroyed = true;
            }
        }

    }
}

