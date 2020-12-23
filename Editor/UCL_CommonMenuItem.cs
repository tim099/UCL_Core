using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.EditorLib
{
    public static class UCL_CommonMenuItem
    {
        [UnityEditor.MenuItem("GameObject/3D Object/UCL/Curve")]
        private static void CreateUCL_Curve() {
            Object selectedObject = UnityEditor.Selection.activeObject;
            GameObject obj = selectedObject as GameObject;
            Transform p = null;
            if(obj != null) {
                p = obj.transform;
            }
            var curve = Core.GameObjectLib.Create<UCL.Core.MathLib.UCL_Curve>("UCL_Curve", p);
            UnityEditor.Selection.activeObject = curve;
        }
    }
}