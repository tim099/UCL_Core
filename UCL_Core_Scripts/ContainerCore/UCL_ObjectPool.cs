using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Container {

    public class ObjectPool<T> where T : new() {
        Queue<T> m_Pool = new Queue<T>();

        public T Create() {
            T aTarget = default;
            if(m_Pool.Count > 0) {
                aTarget = m_Pool.Dequeue();
            } else {
                aTarget = new T();
            }
            return aTarget;
        }
        public void Delete(T target) {
            m_Pool.Enqueue(target);
        }
    }

    public class UnityComponentPool<T> where T : Component
    {
        public UnityComponentPool() { }
        public UnityComponentPool(T iTemplate, System.Action<T> iInitAction = null, System.Func<T, Transform, T> iCreateAction = null)
        {
            m_Template = iTemplate;
            m_InitAction = iInitAction;
            m_CreateAction = iCreateAction;
        }
        /// <summary>
        /// call when object first time created
        /// </summary>
        public System.Action<T> m_InitAction = null;
        /// <summary>
        /// if not null, will create object using this instead of GameObject.Instantiate(m_Template, iParent)
        /// </summary>
        public System.Func<T, Transform, T> m_CreateAction = null;
        public string m_CreateName = typeof(T).ToString();
        /// <summary>
        /// if not null, deleted object will set this as parent
        /// </summary>
        public Transform m_DeleteRoot = null;

        Stack<T> m_ObjPool = new Stack<T>();
        /// <summary>
        /// All Object that Created
        /// </summary>
        List<T> m_AllObjs = new List<T>();
        /// <summary>
        /// Template of created object
        /// (if not null, will use Instantiate(m_Template, iParent) to create GameObject
        /// </summary>
        public T m_Template = null;
        public void Prewarm(int iCount)
        {
            Transform aParent = null;
            if (m_DeleteRoot != null)
            {
                aParent = m_DeleteRoot;
            }

            for(int i = 0; i < iCount; i++)
            {
                T aTarget = null;
                GameObject aObj = null;
                if (m_Template != null)
                {
                    if (m_CreateAction != null)
                    {
                        aTarget = m_CreateAction(m_Template, aParent);
                    }
                    else
                    {
                        aTarget = GameObject.Instantiate(m_Template, aParent);
                    }
                    aObj = aTarget.gameObject;
                }
                else
                {
                    aObj = new GameObject(m_CreateName);
                    aTarget = aObj.AddComponent<T>();
                }
                if (aParent)
                {
                    aObj.layer = aParent.gameObject.layer;
                }

                if (m_InitAction != null)
                {
                    m_InitAction.Invoke(aTarget);
                }
                m_ObjPool.Push(aTarget);
            }

        }
        /// <summary>
        /// Create a object from pool
        /// </summary>
        /// <param name="iParent"></param>
        /// <returns></returns>
        public T Create(Transform iParent = null) {
            T aTarget = null;
            if(m_ObjPool.Count > 0) {
                aTarget = m_ObjPool.Pop();
            } else {
                GameObject aObj = null;
                if (m_Template != null)
                {
                    if(m_CreateAction != null)
                    {
                        aTarget = m_CreateAction(m_Template, iParent);
                    }
                    else
                    {
                        aTarget = GameObject.Instantiate(m_Template, iParent);
                    }
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

                if (m_InitAction != null)
                {
                    m_InitAction.Invoke(aTarget);
                }
            }
            aTarget.gameObject.SetActive(true);
            if(iParent != aTarget.transform.parent) aTarget.transform.SetParent(iParent);

            m_AllObjs.Add(aTarget);
            return aTarget;
        }
        /// <summary>
        /// delete object(return to pool so object will be reusable
        /// </summary>
        /// <param name="iTarget"></param>
        public void Delete(T iTarget) {

            iTarget.gameObject.SetActive(false);
            if (m_DeleteRoot != null)
            {
                iTarget.transform.SetParent(m_DeleteRoot);
            }
            m_ObjPool.Push(iTarget);
            m_AllObjs.Remove(iTarget);
        }
        /// <summary>
        /// delete all object created from pool
        /// </summary>
        public void DeleteAll()
        {
            for(int i = 0; i < m_AllObjs.Count; i++)
            {
                var aTarget = m_AllObjs[i];
                if(aTarget != null)
                {
                    if (m_DeleteRoot != null)
                    {
                        aTarget.transform.SetParent(m_DeleteRoot);
                    }
                    m_ObjPool.Push(aTarget);
                    aTarget.gameObject.SetActive(false);
                }
            }
            m_AllObjs.Clear();
        }
    }
}