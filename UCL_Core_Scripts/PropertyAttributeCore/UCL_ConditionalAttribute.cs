using System;
using UnityEngine;

namespace UCL.Core.PA {
    /// <summary>
    /// reference https://github.com/Deadcows/MyBox/blob/master/Attributes/ConditionalAttribute.cs
    /// this is an alter version of ConditionalAttribute
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class ConditionalAttribute : PropertyAttribute {
        public readonly string m_FieldName;
        public readonly object[] m_CompareValues;
        public readonly bool m_Inverse;

        /// <param name="iFieldName">String name of field to check value</param>
        /// <param name="iInverse">Inverse check result</param>
        /// <param name="iCompareValues">On which values field will be shown in inspector</param>
        public ConditionalAttribute(string iFieldName, bool iInverse = false, params object[] iCompareValues) {
            m_FieldName = iFieldName;
            m_Inverse = iInverse;
            m_CompareValues = iCompareValues;
        }
    }
}