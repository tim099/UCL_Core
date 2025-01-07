
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 01/07 2025
using UnityEngine;

namespace UCL.Core
{
    public static class ParseStringSort
    {
        public enum SortEnum
        {
            UCL_ParseStringReplace = 0,
        }
    }
    [UCL.Core.ATTR.UCL_IgnoreInTypeListable]
    public class UCL_ParseString : UCL.Core.JsonLib.UnityJsonSerializable, UCLI_TypeListable
    {
        virtual public string Parse(string value)
        {
            return value;
        }
    }

}
