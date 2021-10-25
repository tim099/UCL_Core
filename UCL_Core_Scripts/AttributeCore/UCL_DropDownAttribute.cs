using System;
using System.Collections;
using System.Collections.Generic;
using UCL.Core.ObjectReflectionExtension;
using UCL.Core.UI;
using UnityEngine;
namespace UCL.Core
{
    public static partial class AttributeExtension
    {
        public static object DrawOnGUI(this IStringArr iStrArr, object iObj, object iData, UCL_ObjectDictionary iDataDic, string iKey)
        {
            var aList = iStrArr.GetList(iObj);
            int aIndex = Mathf.Max(0, Array.IndexOf(aList, iData));
            aIndex = UCL_GUILayout.PopupAuto(aIndex, aList, iDataDic, iKey);
            return aList[aIndex];
        }
    }
    public interface IStringArr
    {
        string[] GetList(object iTarget);
    }
    public interface ITexture2D
    {
        Texture2D GetTexture(object iObj, object iValue);
    }
    public interface IShowInCondition
    {
        bool IsShow(object iObj);
    }
}
namespace UCL.Core.ATTR
{

    public class UCL_DropDownAttribute : Attribute ,IStringArr
    {
        string m_MethodName = null;
        object[] m_Params = null;
        /// <summary>
        /// 
        /// </summary>
        /// <param name="iMethodName">Member function that return a List</param>
        /// <param name="iParams"></param>
        public UCL_DropDownAttribute(string iMethodName, params object[] iParams)
        {
            m_MethodName = iMethodName;
            m_Params = iParams;
        }
        /// <summary>
        /// Get the string list from target
        /// </summary>
        /// <param name="iTarget"></param>
        /// <returns></returns>
        public string[] GetList(object iTarget)
        {
            return (string[])iTarget.Invoke(m_MethodName, m_Params);
        }
    }
}