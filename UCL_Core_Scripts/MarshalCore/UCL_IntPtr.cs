using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using System;
namespace UCL.Core.MarshalLib {
    public class UCL_IntPtr {
        public int m_Size { get; protected set; }
        public IntPtr m_Ptr { get; protected set; } = IntPtr.Zero;
        public UCL_IntPtr() {

        }
        public UCL_IntPtr(int _size) {
            Alloc(_size);
        }
        ~UCL_IntPtr() {
            Free();
        }

        virtual public UCL_IntPtr Alloc(int _size) {
            Free();
            m_Size = _size;
            m_Ptr = Marshal.AllocHGlobal(m_Size);
            return this;
        }
        virtual public void Free() {
            if(m_Ptr == IntPtr.Zero) return;
            Marshal.FreeHGlobal(m_Ptr);
            m_Ptr = IntPtr.Zero;
            m_Size = 0;
        }
        public T ToStructure<T>() {
            if(m_Ptr == IntPtr.Zero) return default(T);
            return (T)Marshal.PtrToStructure(m_Ptr, typeof(T));
        }
        #region To Ptr
        /// <summary>
        /// Load array to m_Ptr
        /// </summary>
        /// <param name="arr"></param>
        public void ToPtr(float[] arr) {
            if(m_Ptr == IntPtr.Zero) {
                return;
            }
            Marshal.Copy(arr, 0, m_Ptr , arr.Length);
        }
        /// <summary>
        /// Load array to m_Ptr
        /// </summary>
        /// <param name="arr"></param>
        public void ToPtr(int[] arr) {
            if(m_Ptr == IntPtr.Zero) {
                return;
            }
            Marshal.Copy(arr, 0, m_Ptr, arr.Length);
        }
        /// <summary>
        /// Load array to m_Ptr
        /// </summary>
        /// <param name="arr"></param>
        public void ToPtr(byte[] arr) {
            if(m_Ptr == IntPtr.Zero) {
                return;
            }
            Marshal.Copy(arr, 0, m_Ptr, arr.Length);
        }
        /// <summary>
        /// Load array to m_Ptr
        /// </summary>
        /// <param name="arr"></param>
        public void ToPtr(short[] arr) {
            if(m_Ptr == IntPtr.Zero) {
                return;
            }
            Marshal.Copy(arr, 0, m_Ptr, arr.Length);
        }
        /// <summary>
        /// Load array to m_Ptr
        /// </summary>
        /// <param name="arr"></param>
        public void ToPtr(long[] arr) {
            if(m_Ptr == IntPtr.Zero) {
                return;
            }
            Marshal.Copy(arr, 0, m_Ptr, arr.Length);
        }
        #endregion


        #region To Array
        public void ToArray(float[] arr) {
            if(m_Ptr == IntPtr.Zero) {
                return;
            }
            Marshal.Copy(m_Ptr, arr, 0, arr.Length);
        }
        public void ToArray(byte[] arr) {
            if(m_Ptr == IntPtr.Zero) {
                return;
            }
            Marshal.Copy(m_Ptr, arr, 0, arr.Length);
        }
        public void ToArray(int[] arr) {
            if(m_Ptr == IntPtr.Zero) {
                return;
            }
            Marshal.Copy(m_Ptr, arr, 0, arr.Length);
        }
        public void ToArray(short[] arr) {
            if(m_Ptr == IntPtr.Zero) {
                return;
            }
            Marshal.Copy(m_Ptr, arr, 0, arr.Length);
        }
        public void ToArray(long[] arr) {
            if(m_Ptr == IntPtr.Zero) {
                return;
            }
            Marshal.Copy(m_Ptr, arr, 0, arr.Length);
        }
        /*
        public void ToArray<T>(ref T[] arr) where T : IComparable {
            Marshal.Copy(m_Ptr, arr, 0, 0);
            //return ToStructure<T[]>();
        }
        */
        #endregion
    }
}