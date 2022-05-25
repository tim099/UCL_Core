using UnityEngine;
using System;
using System.Reflection;
using System.Collections.Generic;

namespace UCL.Core.EditorLib
{
    public static class DrawATTR
    {
        public static void DrawAllMethods(UnityEngine.Object iTarget, Type iType, Type iClassType, UCL_ObjectDictionary iDic
            , System.Action iDrawDefaultInspector = null)
        {

            var aAllMethods = iType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            System.Action<IList<MethodInfo>> aDrawMethods = delegate (IList<MethodInfo> iMethods)
            {
                if (iMethods.Count > 0)
                {
                    var aClassName = iClassType.Name;

                    GUILayout.BeginVertical();
                    for (int i = 0; i < iMethods.Count; i++)
                    {
                        var aMethod = iMethods[i];
                        var aMethodDic = iDic.GetSubDic("Method", i);
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
                                        aAttr.DrawAttribute(iTarget, aMethod, aMethodDic.GetSubDic("Attr", j));
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
            };

            if (iDrawDefaultInspector != null)
            {
                List<MethodInfo> aPreMethods = new List<MethodInfo>();
                List<MethodInfo> aPostMethods = new List<MethodInfo>();
                for (int i = 0; i < aAllMethods.Length; i++)
                {
                    var aMethod = aAllMethods[i];
                    if (aMethod.GetCustomAttribute<ATTR.UCL_DrawBeforeDefaultInspectorAttribute>() != null)
                    {
                        aPreMethods.Add(aMethod);
                    }
                    else
                    {
                        aPostMethods.Add(aMethod);
                    }
                }
                aPreMethods.Sort((iA, iB) =>
                {
                    int aA = iA.GetCustomAttribute<ATTR.UCL_DrawBeforeDefaultInspectorAttribute>().m_SortOrder;
                    int aB = iB.GetCustomAttribute<ATTR.UCL_DrawBeforeDefaultInspectorAttribute>().m_SortOrder;
                    if (aA > aB)
                    {
                        return 1;
                    }
                    else if (aA < aB)
                    {
                        return -1;
                    }

                    return 0;
                });

                aDrawMethods(aPreMethods);
                iDrawDefaultInspector.Invoke();
                aDrawMethods(aPostMethods);
            }
            else
            {
                aDrawMethods(aAllMethods);
            }

        }
    }
}
