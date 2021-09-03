using System;
using System.Collections;
using System.Collections.Generic;
using UCL.Core.ObjectReflectionExtension;
using UnityEngine;

namespace UCL.Core.ATTR
{
    public class UCL_DropDownAttribute : Attribute
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