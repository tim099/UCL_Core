using System;
using System.Reflection;

namespace UCL.Core.ATTR
{
    public class UCL_Attribute : Attribute
    {
        virtual public void DrawAttribute(UnityEngine.Object iTarget, MethodInfo iMethodInfo, UCL_ObjectDictionary iDic)
        {
            Draw(iTarget, iMethodInfo, iDic);
        }
        virtual public void Draw(object iTarget, MethodInfo iMethodInfo, UCL_ObjectDictionary iDic) { }
        //virtual public void Draw(UCL_ObjectDictionary iDic, object iTarget, MethodInfo iMethodInfo) { }
    }
}