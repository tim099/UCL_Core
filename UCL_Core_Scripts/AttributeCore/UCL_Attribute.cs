using System;
using System.Reflection;

namespace UCL.Core.ATTR
{
    public class UCL_Attribute : Attribute
    {
        virtual public void DrawAttribute(UnityEngine.Object iTarget, MethodInfo iMethodInfo) { }
    }
}