using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core
{
    public static partial class UCL_EnumExtension
    {
        /// <summary>
        /// return an array contains all enum value of iEnum
        /// </summary>
        /// <param name="iEnum"></param>
        /// <returns></returns>
        public static Array GetValues(this Enum iEnum)
        {
            if (iEnum == null) return null;
            return Enum.GetValues(iEnum.GetType());
        }
    }
}

