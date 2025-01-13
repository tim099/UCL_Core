using System;
using UCL.Core.ObjectReflectionExtension;
using UnityEngine;

namespace UCL.Core.PA {
    /// <summary>
    /// reference https://github.com/Deadcows/MyBox/blob/master/Attributes/ConditionalAttribute.cs
    /// this is an alter version of ConditionalAttribute
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class ConditionalAttribute : PropertyAttribute, UCL.Core.IShowInCondition {
        public enum ConditionalMode
        {
            Field = 0,
            Function
        }
        public readonly string m_FieldName;
        public readonly object[] m_CompareValues;       
        public readonly bool m_Inverse;

        public object[] FunctionParams => m_CompareValues;
        public string FunctionName => m_FieldName;

        public ConditionalMode m_ConditionalMode;
        /// <param name="iFieldName">String name of field to check value</param>
        /// <param name="iInverse">Inverse check result</param>
        /// <param name="iCompareValues">On which values field will be shown in inspector</param>
        public ConditionalAttribute(string iFieldName, bool iInverse, params object[] iCompareValues) {
            m_ConditionalMode = ConditionalMode.Field;
            m_FieldName = iFieldName;
            m_Inverse = iInverse;
            m_CompareValues = iCompareValues;
        }
        /// <summary>
        /// Invoke mamber function and use the return value
        /// if return true then show the field
        /// </summary>
        /// <param name="iFunctionName"></param>
        /// <param name="iFunctionParams"></param>
        public ConditionalAttribute(string iFunctionName, params object[] iFunctionParams)
        {
            m_ConditionalMode = ConditionalMode.Function;
            m_FieldName = iFunctionName;
            m_CompareValues = iFunctionParams;
        }
        public bool IsShow(object iObj)
        {
            switch (m_ConditionalMode)
            {
                case ConditionalMode.Field:
                    {
                        var aObj = iObj.GetMember(m_FieldName);
                        if (m_CompareValues != null)
                        {
                            for (int i = 0; i < m_CompareValues.Length; i++)
                            {
                                var aComp = m_CompareValues[i];
                                if (aComp.Equals(aObj))
                                {
                                    return !m_Inverse;
                                }
                            }
                        }
                        return m_Inverse;
                    }
                case ConditionalMode.Function:
                    {

                        var aResult = iObj.Invoke(FunctionName, FunctionParams);
                        if(aResult is bool)
                        {
                            return (bool)aResult;
                        }
                        return true;
                    }
            }

            return true;
        }
    }
}