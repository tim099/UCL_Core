using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Container {

    public class ObjectPool<T> where T : new() {
        Queue<T> m_Pool = new Queue<T>();

        public T Create() {
            T target = default;
            if(m_Pool.Count > 0) {
                target = m_Pool.Dequeue();
            } else {
                target = new T();
            }
            return target;
        }
        public void Delete(T target) {
            m_Pool.Enqueue(target);
        }
    }

    public class ComponentPool<T> where T : Component
    {
        public string m_CreateName = typeof(T).ToString();
        Stack<T> m_ObjPool = new Stack<T>();

        public T Create(Transform parent = null) {
            T target = null;
            if(m_ObjPool.Count > 0) {
                target = m_ObjPool.Pop();
            } else {
                GameObject obj = new GameObject(m_CreateName);
                target = obj.AddComponent<T>();
                if(parent) {
                    obj.layer = parent.gameObject.layer;
                }
            }
            target.gameObject.SetActive(true);
            if(parent != target.transform.parent) target.transform.parent = parent;
            return target;
        }
        public void Delete(T target) {
            target.gameObject.SetActive(false);
            m_ObjPool.Push(target);
        }
    }
}