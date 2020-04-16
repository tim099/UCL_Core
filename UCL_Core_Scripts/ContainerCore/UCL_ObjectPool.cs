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
}