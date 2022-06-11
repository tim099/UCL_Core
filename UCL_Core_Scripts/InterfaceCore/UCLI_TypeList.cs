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
    public interface UCLI_GetTypeName
    {
        string GetTypeName(string iName);
    }
}

