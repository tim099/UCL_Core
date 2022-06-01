using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;


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
    }
}

