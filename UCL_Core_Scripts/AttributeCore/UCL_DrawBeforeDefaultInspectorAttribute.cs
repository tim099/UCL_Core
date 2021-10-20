using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UCL.Core.ATTR
{
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class UCL_DrawBeforeDefaultInspectorAttribute : Attribute
    {
        public int m_SortOrder = 0;
        public UCL_DrawBeforeDefaultInspectorAttribute() { }
        public UCL_DrawBeforeDefaultInspectorAttribute(int iSortOrder)
        {
            m_SortOrder = iSortOrder;
        }
    }
}
