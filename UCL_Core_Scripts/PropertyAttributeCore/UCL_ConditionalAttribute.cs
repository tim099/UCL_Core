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

        /// <param name="field_name">String name of field to check value</param>
        /// <param name="inverse">Inverse check result</param>
        /// <param name="compareValues">On which values field will be shown in inspector</param>
        public ConditionalAttribute(string field_name, bool inverse = false, params object[] compareValues) {
            m_FieldName = field_name;
            m_Inverse = inverse;
            m_CompareValues = compareValues;
        }
    }
}