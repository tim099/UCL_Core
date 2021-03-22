using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.PA
{
    /// <summary>
    /// this attribute can only attach to string(using AssetDatabase.LoadAssetAtPath)
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false)]
    public class UCL_EditorOnlyAttribute : PropertyAttribute
    {
        public UCL_EditorOnlyAttribute()
        {

        }
    }
}