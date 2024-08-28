using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.ATTR
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class UCL_SortAttribute : Attribute
    {
        public int m_SortOrder;

        public UCL_SortAttribute(int iSortOrder) {
            m_SortOrder = iSortOrder;
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class UCL_GroupIDAttribute : Attribute
    {
        public string m_ID;

        public UCL_GroupIDAttribute(string iID)
        {
            m_ID = iID;
        }
    }

}
