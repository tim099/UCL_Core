
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 01/07 2025
using UnityEngine;

namespace UCL.Core
{
    [UCL.Core.ATTR.UCL_Sort((int)ParseStringSort.SortEnum.UCL_ParseStringReplace)]
    public class UCL_ParseStringReplace : UCL_ParseString
    {
        public string m_OldValue;
        public string m_NewValue;
        public override string Parse(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }
            return value.Replace(m_OldValue, m_NewValue);
        }
    }
}