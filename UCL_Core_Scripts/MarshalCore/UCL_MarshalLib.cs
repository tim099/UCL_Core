using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace UCL.Core.MarshalLib {
    static public class Lib {

        #region Hex
        /// <summary>
        /// Convert byte array into Hexadecimal string
        /// </summary>
        /// <param name="iBytes"></param>
        /// <returns></returns>
        public static string ByteArrayToHexString(byte[] iBytes)
        {
            if (iBytes.IsNullOrEmpty()) return string.Empty;
            System.Text.StringBuilder aSB = new System.Text.StringBuilder();
            for(int i = 0; i < iBytes.Length; i++)
            {
                byte aByte = iBytes[i];            
                aSB.Append(ByteToHexChar(aByte >> 4));
                aSB.Append(ByteToHexChar(aByte & 0b0000_1111));
            }
            return aSB.ToString();
        }
        /// <summary>
        /// Convert Hexadecimal string into byte array 
        /// </summary>
        /// <param name="iHexString"></param>
        /// <returns></returns>
        public static byte[] HexStringToByteArray(string iHexString)
        {
            byte[] aBytes = new byte[iHexString.Length / 2];
            System.Text.StringBuilder aSB = new System.Text.StringBuilder();
            for (int i = 0; i < aBytes.Length; i++)
            {
                aBytes[i] = HexToByte(iHexString[2 * i], iHexString[2 * i + 1]);
            }
            return aBytes;
        }
        /// <summary>
        /// Convert Hex char to byte
        /// </summary>
        /// <param name="iChar"></param>
        /// <returns></returns>
        public static byte HexCharToByte(char iChar)
        {
            switch (iChar)
            {
                case '0': return 0;
                case '1': return 1;
                case '2': return 2;
                case '3': return 3;
                case '4': return 4;
                case '5': return 5;
                case '6': return 6;
                case '7': return 7;
                case '8': return 8;
                case '9': return 9;
                case 'A': return 10;
                case 'B': return 11;
                case 'C': return 12;
                case 'D': return 13;
                case 'E': return 14;
                case 'F': return 15;
            }
            return 0;
        }
        /// <summary>
        /// Convert Hex to byte
        /// </summary>
        /// <param name="iHeigh"></param>
        /// <param name="iLow"></param>
        /// <returns></returns>
        public static byte HexToByte(char iHeigh, char iLow)
        {
            return (byte)(HexCharToByte(iHeigh) << 4 | HexCharToByte(iLow));
        }
        /// <summary>
        /// Convert Hex string (00~FF) to byte
        /// </summary>
        /// <param name="iHex"></param>
        /// <returns></returns>
        public static byte HexToByte(string iHex)
        {
            return (byte)(HexCharToByte(iHex[0]) << 4 | HexCharToByte(iHex[1]));
        }
        /// <summary>
        /// Convert iByte to string (00~FF)
        /// </summary>
        /// <param name="iByte"></param>
        /// <returns></returns>
        public static string ByteToHexString(byte iByte)
        {
            return new string(new char[2] { ByteToHexChar(iByte >> 4), ByteToHexChar(iByte & 0b0000_1111) });
        }
        /// <summary>
        /// Convert iByte to Char(0~F)
        /// </summary>
        /// <param name="iByte"></param>
        /// <returns></returns>
        public static char ByteToHexChar(byte iByte)
        {
            switch (iByte)
            {
                case 0: return '0';
                case 1: return '1';
                case 2: return '2';
                case 3: return '3';
                case 4: return '4';
                case 5: return '5';
                case 6: return '6';
                case 7: return '7';
                case 8: return '8';
                case 9: return '9';
                case 10: return 'A';
                case 11: return 'B';
                case 12: return 'C';
                case 13: return 'D';
                case 14: return 'E';
                case 15: return 'F';
            }

            return '0';
        }
        /// <summary>
        /// Convert iByte to Char(0~F)
        /// </summary>
        /// <param name="iByte"></param>
        /// <returns></returns>
        public static char ByteToHexChar(int iByte)
        {
            switch (iByte)
            {
                case 0: return '0';
                case 1: return '1';
                case 2: return '2';
                case 3: return '3';
                case 4: return '4';
                case 5: return '5';
                case 6: return '6';
                case 7: return '7';
                case 8: return '8';
                case 9: return '9';
                case 10: return 'A';
                case 11: return 'B';
                case 12: return 'C';
                case 13: return 'D';
                case 14: return 'E';
                case 15: return 'F';
            }

            return '0';
        }
        #endregion
        public static byte[] ToByteArray(object iObj)
        {
            //var type = obj.GetType();
            //Debug.LogWarning("Marshal.SizeOf(obj):" + Marshal.SizeOf(obj));
            try
            {
                byte[] aArr = new byte[Marshal.SizeOf(iObj)];
                GCHandle aHandle = GCHandle.Alloc(iObj, GCHandleType.Pinned);
                try
                {
                    Marshal.Copy(aHandle.AddrOfPinnedObject(), aArr, 0, aArr.Length);
                    return aArr;
                }
                finally
                {
                    aHandle.Free();
                }
            }catch(System.Exception iE)
            {
                Debug.LogException(iE);
                return null;
            }

        }
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
        public static T ToStructure<T>(byte[] iByteArray) {
            int aSize = Marshal.SizeOf(typeof(T));
            IntPtr aPtr = Marshal.AllocHGlobal(aSize);
            //unsafe {}
            Marshal.Copy(iByteArray, 0, aPtr, aSize);

            T aData = (T)Marshal.PtrToStructure(aPtr, typeof(T));
            Marshal.FreeHGlobal(aPtr);
            return aData;
        }
        public static object ToStructure(byte[] iByteArray, Type iType)
        {
            int aSize = Marshal.SizeOf(iType);
            IntPtr aPtr = Marshal.AllocHGlobal(aSize);
            Marshal.Copy(iByteArray, 0, aPtr, aSize);

            object aData = Marshal.PtrToStructure(aPtr, iType);
            Marshal.FreeHGlobal(aPtr);
            return aData;
        }
    }
}

