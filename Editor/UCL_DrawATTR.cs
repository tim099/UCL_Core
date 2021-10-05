using UnityEngine;
using System;
using System.Reflection;

namespace UCL.Core.EditorLib
{
    public static class DrawATTR
    {
        public static void Draw(UnityEngine.Object iTarget, Type iType, Type iClassType)
        {
            var aMethods = iType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            //.Where(m => m.GetCustomAttributes(typeof(ATTR.UCL_FunctionButtonAttribute), false).Length > 0).ToArray();
            //Debug.LogWarning("type:" + type.Name + ",methods:" + methods.Length);
            if (aMethods.Length > 0)
            {
                var aClassName = iClassType.Name;

                GUILayout.BeginVertical();
                for (int i = 0; i < aMethods.Length; i++)
                {
                    var aMethod = aMethods[i];

                    {
                        var aAttrType = typeof(ATTR.UCL_Attribute);
                        var aAttrs = aMethod.GetCustomAttributes(aAttrType, false);
                        if (aAttrs.Length > 0)
                        {
                            for (int j = 0; j < aAttrs.Length; j++)
                            {
                                var aAttr = (ATTR.UCL_Attribute)aAttrs[j];
                                try
                                {
                                    aAttr.DrawAttribute(iTarget, aMethod);
                                }
                                catch (Exception iE)
                                {
                                    Debug.LogException(iE);
                                    Debug.LogWarning(aAttr.GetType().Name + ":"
                                        + aClassName + " " + aAttrType.Name + " Exception:" + iE);
                                }
                            }
                        }
                    }
                }
                GUILayout.EndVertical();
            }
        }
    }
}
