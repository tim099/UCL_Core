using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// return all types inherit base class
    /// </summary>
    public interface UCLI_TypeList
    {
        IList<System.Type> GetAllTypes();
    }
    /// <summary>
    /// new version of UCLI_TypeList
    /// </summary>
    public interface UCLI_TypeListable
    {

    }


    public interface UCLI_GetTypeName
    {
        string GetTypeName(string iName);
    }
}

namespace UCL.Core.ATTR
{
    /// <summary>
    /// Ignore in UCLI_TypeListable
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class UCL_IgnoreInTypeListableAttribute : Attribute
    {

    }
}

