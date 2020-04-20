using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace UCL.Core.MarshalLib {
    static public class Lib {
        public static byte[] ToByteArray<T>(T obj) {
            Byte[] arr = new Byte[Marshal.SizeOf(typeof(T))];
            GCHandle handle = GCHandle.Alloc(obj, GCHandleType.Pinned);
            try {
                Marshal.Copy(handle.AddrOfPinnedObject(), arr, 0, arr.Length);
                return arr;
            } finally {
                handle.Free();
            }
        }

        public static T ToStructure<T>(byte[] buffer) {
            int size = Marshal.SizeOf(typeof(T));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            //unsafe {}
            Marshal.Copy(buffer, 0, ptr, size);

            T data = (T)Marshal.PtrToStructure(ptr, typeof(T));
            Marshal.FreeHGlobal(ptr);
            return data;
        }
    }
}

