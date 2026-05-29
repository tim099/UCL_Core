using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;


namespace UCL.Core {
    public static class GameObjectLib {
        /// <summary>
        /// Clone object using refelction
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iSourceObject"></param>
        /// <returns></returns>
        public static T ReflectionCloneObject<T>(this T iSourceObject) {
            System.Type aType = iSourceObject.GetType();
            PropertyInfo[] aProperties = aType.GetProperties();
            System.Object aObj = aType.InvokeMember("", System.Reflection.BindingFlags.CreateInstance, null, iSourceObject, null);
            foreach(PropertyInfo aPropertie in aProperties) {
                if(aPropertie.CanWrite) {
                    aPropertie.SetValue(aObj, aPropertie.GetValue(iSourceObject, null), null);
                }
            }
            return (T)System.Convert.ChangeType(aObj, typeof(T));
        }
        public static void Swap<type>(ref type a, ref type b) {
            type c = a; a = b; b = c;
        }
        public static GameObject CreateByName(string TypeName, Transform t) {
            System.Type type = System.Type.GetType(TypeName);
            GameObject obj = CreateGameObject(TypeName, t);
            obj.AddComponent(type);
            return obj;
        }
        public static T Create<T>(string name, Transform parent) where T : Component {
            GameObject Obj = CreateGameObject(name, parent);
            return Obj.AddComponent<T>();
        }
        public static T Create<T>(Transform parent) where T : Component {
            GameObject Obj = CreateGameObject(typeof(T).Name, parent);
            return Obj.AddComponent<T>();
        }
        public static Transform SetParent(Transform t, Transform parent) {
            t.SetParent(parent);
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
            t.localPosition = Vector3.zero;
            return t;
        }
        public static GameObject CreateGameObject(string name, Transform parent) {
            GameObject Obj = new GameObject(name);
            if(parent) {
                var rt = parent.GetComponent<RectTransform>();
                if(rt != null) {
                    Obj.AddComponent<RectTransform>();
                }
            }
            SetParent(Obj.transform, parent);
            return Obj;
        }
        public static void SearchChildExcludeParent<T>(Transform parent, List<T> result) where T : Component
        {
            foreach(Transform child in parent) {
                SearchChild(child, result);
            }
        }
        public static GameObject SearchChild(Transform iParent, string iName)
        {
            if (iParent.name == iName)
            {
                return iParent.gameObject;
            }

            foreach (Transform aChild in iParent)
            {
                var aRes = SearchChild(aChild, iName);
                if (aRes != null) return aRes;
            }
            return null;
        }


        public static T SearchChild<T>(Transform iParent, string iName) where T : Component
        {
            if(iParent.name == iName)
            {
                var aRes = iParent.GetComponent<T>();
                if (aRes != null) return aRes;
            }
            
            foreach (Transform child in iParent)
            {
                var aRes = SearchChild<T>(child, iName);
                if (aRes != null) return aRes;
            }
            return null;
        }
        /// <summary>
        /// Search child contains T(Include parent)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iParent"></param>
        /// <param name="iResult"></param>
        public static void SearchChild<T>(Transform iParent, List<T> iResult) where T : Component
        {
            var res = iParent.GetComponents<T>();
            for(int i = 0; i < res.Length; i++) {
                iResult.Add(res[i]);
            }
            foreach(Transform child in iParent) {
                SearchChild(child, iResult);
            }
        }
        public static T SearchChild<T>(Transform parent) where T : Component
        {
            var res = parent.GetComponent<T>();
            if(res != null) return res;
            foreach(Transform child in parent) {
                var result = SearchChild<T>(child);
                if(result != null) return result;
            }
            return default;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Scene / Hierarchy traversal — 通用 hierarchy 讀取與走訪工具
        // 區塊職責：給 Cmd_ReadHierarchy / UCL_GameObjectInspectorPage 等上層共用的
        //          Scene 根 GameObject 列舉 + DFS hierarchy 遍歷器。
        // 物理意義：純讀取；不修改任何 transform / GameObject 狀態。
        // 數值影響：無，僅讀取場景結構並回傳資料 / 回呼。
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 取得指定 Scene 的所有根 GameObject。
        /// </summary>
        /// <param name="iScene">目標 Scene；若 invalid 或未載入則回空清單。</param>
        /// <param name="iIncludeInactive">是否包含 activeSelf=false 的根 GO，預設 true。</param>
        /// <returns>該場景的根 GameObject 清單（不影響 Unity 內部 list）。</returns>
        public static List<GameObject> GetRootGameObjects(Scene iScene, bool iIncludeInactive = true)
        {
            // 區塊職責：薄包裝 Scene.GetRootGameObjects + 可選 inactive 過濾
            // 物理意義：把 Unity 內建 API 整理成可直接餵給 caller 的 list
            // 數值影響：無
            var aRoots = new List<GameObject>();
            if (!iScene.IsValid() || !iScene.isLoaded) return aRoots;
            iScene.GetRootGameObjects(aRoots);
            if (!iIncludeInactive)
            {
                aRoots.RemoveAll(go => go == null || !go.activeSelf);
            }
            return aRoots;
        }

        /// <summary>
        /// 取得當前 active Scene 的所有根 GameObject。
        /// </summary>
        /// <param name="iIncludeInactive">是否包含 activeSelf=false 的根 GO，預設 true。</param>
        public static List<GameObject> GetActiveSceneRootGameObjects(bool iIncludeInactive = true)
        {
            return GetRootGameObjects(SceneManager.GetActiveScene(), iIncludeInactive);
        }

        /// <summary>
        /// DFS 走訪一棵 Transform 樹，對每個節點呼叫 iVisit(transform, depth)。
        /// 物理意義：通用 hierarchy 遍歷器；給 hierarchy export / 統計收集 / 條件搜尋共用底座。
        /// 數值影響：純走訪不改 transform；若 iVisit 改 transform 屬性則責任在 caller。
        /// </summary>
        /// <param name="iRoot">根節點 Transform；null 直接返回</param>
        /// <param name="iVisit">每節點呼叫 (transform, depth)；depth=0 表示 iRoot 自身</param>
        /// <param name="iMaxDepth">最大遞迴深度，-1 = 無限；0 = 只訪問 iRoot</param>
        /// <param name="iIncludeInactive">是否進入 activeSelf=false 的子樹，預設 true</param>
        public static void WalkHierarchy(Transform iRoot, System.Action<Transform, int> iVisit, int iMaxDepth = -1, bool iIncludeInactive = true)
        {
            if (iRoot == null || iVisit == null) return;
            WalkHierarchyImpl(iRoot, iVisit, 0, iMaxDepth, iIncludeInactive);
        }

        /// <summary>
        /// WalkHierarchy 內部遞迴實作；切開以便 caller 介面乾淨無 depth 起始參數。
        /// </summary>
        private static void WalkHierarchyImpl(Transform iCurrent, System.Action<Transform, int> iVisit, int iDepth, int iMaxDepth, bool iIncludeInactive)
        {
            // 區塊職責：對 iCurrent 觸發 visit，再依條件遞迴到所有 child
            // 物理意義：DFS 深度優先，先 root 再 children；inactive 子樹依 iIncludeInactive 決定是否進入
            // 數值影響：無
            if (iCurrent == null) return;
            if (!iIncludeInactive && !iCurrent.gameObject.activeSelf) return;
            iVisit(iCurrent, iDepth);
            if (iMaxDepth >= 0 && iDepth >= iMaxDepth) return;
            int aChildCount = iCurrent.childCount;
            for (int i = 0; i < aChildCount; ++i)
            {
                WalkHierarchyImpl(iCurrent.GetChild(i), iVisit, iDepth + 1, iMaxDepth, iIncludeInactive);
            }
        }
    }
}

