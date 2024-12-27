using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        private static Dictionary<System.Type, List<Type>> s_TypeListableDic = new();
        public static List<System.Type> GetAllITypes(System.Type iType)
        {
            if (!s_TypeListableDic.ContainsKey(iType))
            {
                var aTypeList = iType.GetAllITypesAssignableFrom();
                var orderResult = aTypeList.OrderBy(type =>
                {
                    var attr = type.GetCustomAttribute<UCL.Core.ATTR.UCL_SortAttribute>();
                    if (attr != null)
                    {
                        return attr.m_SortOrder;
                    }
                    return int.MaxValue;
                });
                List<Type> aAllTypeList = new List<Type>();
                foreach (var type in orderResult)
                {
                    if (type.GetCustomAttribute<UCL.Core.ATTR.UCL_IgnoreInTypeListableAttribute>(false) != null)
                    {
                        continue;
                    }
                    aAllTypeList.Add(type);
                }
                s_TypeListableDic[iType] = aAllTypeList;
            }


            return s_TypeListableDic[iType];
        }
    }


    public interface UCLI_GetTypeName
    {
        string GetTypeName(string iName);
    }

    public static partial class UCLI_TypeListableExtensions
    {

    }
}

namespace UCL.Core.ATTR
{
    /// <summary>
    /// Ignore in UCLI_TypeListable.GetAllITypes
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class UCL_IgnoreInTypeListableAttribute : Attribute
    {

    }
}

