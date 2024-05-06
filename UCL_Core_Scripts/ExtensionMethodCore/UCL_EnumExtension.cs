using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UCL.Core
{
    public static partial class UCL_EnumExtension
    {
        /// <summary>
        /// return an array contains all enum value of iEnum
        /// </summary>
        /// <param name="iEnum"></param>
        /// <returns></returns>
        public static Array GetValues(this Enum iEnum)
        {
            if (iEnum == null) return null;
            return Enum.GetValues(iEnum.GetType());
        }
        /// <summary>
        /// Get next enum value
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iValue"></param>
        /// <returns></returns>
        public static T NextValue<T>(this T iValue) where T : System.Enum
        {
            try
            {
                var aValues = Enum.GetValues(iValue.GetType());
                int aIndex = System.Array.IndexOf(aValues, iValue);
                if (aIndex >= aValues.Length - 1)//last element
                {
                    return (T)aValues.GetValue(0);//first element
                }
                else
                {
                    return (T)aValues.GetValue(aIndex + 1);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }

            return iValue;
        }
        /// <summary>
        /// Get prev enum value
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iValue"></param>
        /// <returns></returns>
        public static T PrevValue<T>(this T iValue) where T : System.Enum
        {
            try
            {
                var aValues = Enum.GetValues(iValue.GetType());
                int aIndex = System.Array.IndexOf(aValues, iValue);//first element
                if (aIndex <= 0)
                {
                    return (T)aValues.GetValue(aValues.Length - 1);//last element
                }
                else
                {
                    return (T)aValues.GetValue(aIndex - 1);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }

            return iValue;
        }

        /// <summary>
        /// Get enum CustomAttributes of type T2
        /// </summary>
        public static T2[] GetCustomAttributes<T, T2>(this T iEnum, bool iInherit) where T : Enum where T2 : Attribute  
        {
            Type aEnumType = typeof(T);
            FieldInfo aFieldInfo = aEnumType.GetField(Enum.GetName(aEnumType, iEnum));
            return aFieldInfo.GetCustomAttributes(typeof(T2), iInherit) as T2[];
        }
        /// <summary>
        /// Get enum all CustomAttributes
        /// </summary>
        public static object[] GetCustomAttributes<T>(this T iEnum, bool iInherit) where T : Enum
        {
            Type aEnumType = typeof(T);
            FieldInfo aFieldInfo = aEnumType.GetField(Enum.GetName(aEnumType, iEnum));
            return aFieldInfo.GetCustomAttributes(iInherit);
        }
    }
}

