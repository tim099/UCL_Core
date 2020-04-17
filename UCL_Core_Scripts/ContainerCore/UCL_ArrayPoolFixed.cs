using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace UCL.Core.Container {
    public class UCL_ArrayPoolFixed<T> {
        public int m_Size { get; protected set; }
        Queue<T[]> m_Pool;

        public UCL_ArrayPoolFixed(int size) {
            m_Size = size;
            m_Pool = new Queue<T[]>();
        }
        public T[] Rent() {
            //Debug.LogWarning("Rent m_Pool.Count:" + m_Pool.Count);
            lock(m_Pool) {
                if(m_Pool.Count > 0) {
                    return m_Pool.Dequeue();
                }
            }

            return new T[m_Size];
        }
        public void Return(T[] arr) {
            lock(m_Pool) {
                m_Pool.Enqueue(arr);
            }
            //Debug.LogWarning("Return m_Pool.Count:" + m_Pool.Count);
        }
    }
}