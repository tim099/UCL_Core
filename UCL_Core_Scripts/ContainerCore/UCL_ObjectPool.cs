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
        public ComponentPool() { }
        public ComponentPool(T iTemplate)
        {
            m_Template = iTemplate;
        }

        public string m_CreateName = typeof(T).ToString();
        Stack<T> m_ObjPool = new Stack<T>();
        /// <summary>
        /// All Object that Created
        /// </summary>
        List<T> m_AllObjs = new List<T>();
        T m_Template = null;
        public T Create(Transform iParent = null) {
            T aTarget = null;
            if(m_ObjPool.Count > 0) {
                aTarget = m_ObjPool.Pop();
            } else {
                GameObject aObj = null;
                if (m_Template != null)
                {
                    aTarget = GameObject.Instantiate(m_Template, iParent);
                    aObj = aTarget.gameObject;
                }
                else
                {
                    aObj = new GameObject(m_CreateName);
                    aTarget = aObj.AddComponent<T>();
                }

                if(iParent) {
                    aObj.layer = iParent.gameObject.layer;
                }
            }
            aTarget.gameObject.SetActive(true);
            if(iParent != aTarget.transform.parent) aTarget.transform.parent = iParent;

            m_AllObjs.Add(aTarget);
            return aTarget;
        }
        public void Delete(T iTarget) {

            iTarget.gameObject.SetActive(false);
            m_ObjPool.Push(iTarget);
            m_AllObjs.Remove(iTarget);
        }
        public void DeleteAll()
        {
            for(int i = 0; i < m_AllObjs.Count; i++)
            {
                var aTarget = m_AllObjs[i];
                if(aTarget != null)
                {
                    m_ObjPool.Push(aTarget);
                    aTarget.gameObject.SetActive(false);
                }
            }
            m_AllObjs.Clear();
        }
    }
}