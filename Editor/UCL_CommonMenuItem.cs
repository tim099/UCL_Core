using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.EditorLib
{
    public static class UCL_CommonMenuItem
    {
        [UnityEditor.MenuItem("GameObject/3D Object/UCL/Curve")]
        private static void CreateUCL_Curve() {
            Object aSelectedObject = UnityEditor.Selection.activeObject;
            GameObject aObj = aSelectedObject as GameObject;
            Transform aParent = null;
            if(aObj != null) {
                aParent = aObj.transform;
            }
            var aCurve = Core.GameObjectLib.Create<UCL.Core.MathLib.UCL_Curve>("UCL_Curve", aParent);
            UnityEditor.Selection.activeObject = aCurve;
        }
    }
}