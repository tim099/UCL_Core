using System;
using UnityEngine;

namespace UCL.Core.ATTR
{
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = true, Inherited = false)]
    public class UCL_FormerlySerializedAsAttribute : Attribute
    {
        private string m_oldName;

        //
        // Summary:
        //     The name of the field before the rename.
        public string oldName => m_oldName;

        //
        // Parameters:
        //   oldName:
        //     The name of the field before renaming.
        public UCL_FormerlySerializedAsAttribute(string oldName)
        {
            m_oldName = oldName;
        }
    }
}

