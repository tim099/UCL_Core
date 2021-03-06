﻿using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;


namespace UCL.Core {
    public static class GameObjectLib {
        public static T CloneObject<T>(this T sourceObject) {
            System.Type t = sourceObject.GetType();
            PropertyInfo[] properties = t.GetProperties();
            System.Object p = t.InvokeMember("", System.Reflection.BindingFlags.CreateInstance, null, sourceObject, null);
            foreach(PropertyInfo pi in properties) {
                if(pi.CanWrite) {
                    pi.SetValue(p, pi.GetValue(sourceObject, null), null);
                }
            }
            return (T)System.Convert.ChangeType(p, typeof(T));
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
        public static void SearchChildNotIncludeParent<T>(Transform parent, List<T> result) where T : Component
        {
            foreach(Transform child in parent) {
                SearchChild(child, result);
            }
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
        public static void SearchChild<T>(Transform parent, List<T> result) where T : Component
        {
            var res = parent.GetComponents<T>();
            for(int i = 0; i < res.Length; i++) {
                result.Add(res[i]);
            }
            foreach(Transform child in parent) {
                SearchChild(child, result);
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

