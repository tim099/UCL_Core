using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.ATTR
{
    /// <summary>
    /// Ignore in UCLI_Asset.GetAllAssetTypes()
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class UCL_IgnoreAssetAttribute : Attribute
    {

    }
}
