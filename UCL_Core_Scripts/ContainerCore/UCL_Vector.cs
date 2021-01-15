using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Container {
    public class UCL_Vector<T> {
        public T[] m_Arr;
        public int Count { get; private set; }
        public int m_MaxSize;
        /*
        public IEnumerator<T> GetEnumerator() {
            return new CL_VectorEnumerator<T>();
        }
        */
        public T this[int i] {
            get { return m_Arr[i]; }
            set { m_Arr[i] = value; }
        }
        public UCL_Vector(int _Length = 2) {
            if(_Length <= 2) _Length = 2;

            Count = 0;
            m_MaxSize = _Length;
            m_Arr = new T[m_MaxSize];
        }
        public void Add(T val) {
            if(Count >= m_MaxSize) {
                m_MaxSize *= 2;
                Array.Resize(ref m_Arr, m_MaxSize);
            }
            m_Arr[Count++] = val;
        }
        public void Reverse() {
            T tmp;
            for(int i = 0; i < (Count / 2); i++) {
                tmp = m_Arr[i];
                m_Arr[i] = m_Arr[Count - i - 1];
                m_Arr[Count - i - 1] = tmp;
            }
        }
        public void Resize(int num) {
            Count = num;
            if(Count >= m_MaxSize) {
                while(Count >= m_MaxSize) {
                    m_MaxSize *= 2;
                }
                Array.Resize(ref m_Arr, m_MaxSize);
            }
        }
        public void AddCount(int num) {
            Count += num;
            if(Count >= m_MaxSize) {
                while(Count >= m_MaxSize) {
                    m_MaxSize *= 2;
                }
                Array.Resize(ref m_Arr, m_MaxSize);
            }
        }
        public T At(int i) {
            if(i < 0 || i >= m_MaxSize) {
                throw new IndexOutOfRangeException("UCL_Vector out of range!! m_MaxSize:" + m_MaxSize + ",i=" + i);
            }

            return m_Arr[i];
        }
        public T[] ToArray() {
            /*
            return new ArraySegment<T>(m_Arr, 0, Count).Array;
            */
            var _Arr = new T[Count];
            Array.Copy(m_Arr, _Arr, Count);
            return _Arr;
        }

        public T[] ResizeToCount() {
            m_MaxSize = Count;
            Array.Resize(ref m_Arr, m_MaxSize);
            return m_Arr;
        }
        public void Clear() {
            Count = 0;
        }
    }
}

