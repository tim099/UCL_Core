using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.PA
{
    /// <summary>
    /// this attribute can only attach to string
    /// And the string value will be the Asset's path
    /// (use AssetDatabase.LoadAssetAtPath to load the real asset)
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false)]
    public class UCL_EditorOnlyAttribute : PropertyAttribute
    {
        public UCL_EditorOnlyAttribute()
        {

        }
    }
}