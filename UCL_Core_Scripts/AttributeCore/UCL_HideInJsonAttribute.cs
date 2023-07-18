using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.ATTR
{
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class UCL_HideInJsonAttribute : Attribute
    {

    }
}