using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
namespace UCL.Core.MathLib {
    [CustomEditor(typeof(UCL_Curve))]
    public class UCL_CurveEditor : Core.EditorLib.UCL_MonobehaviorEditor {
        UCL_Curve path;
        GUIStyle style = new GUIStyle();
        GUIStyle outline_style = new GUIStyle();
        void OnEnable() {
            outline_style.fontStyle = style.fontStyle = FontStyle.Bold;

            style.normal.textColor = Color.black;
            outline_style.normal.textColor = Color.white;
            style.fontSize = 28;
            outline_style.fontSize = 29;
            //style.border.top = style.border.bottom = style.border.left = style.border.right = 2;

        }

        void OnSceneGUI() {
            path = target as UCL_Curve;
            int new_node_at = -1;
            int delete_node_at = -1;
            if(path.enabled && path.gameObject.activeInHierarchy) {
                if(path.m_Points != null && path.m_Points.Count > 0) {
                    //allow path adjustment undo
                    Undo.RecordObject(path, "Alter Path");
                    //node handle display:
                    Color but_col = new Color(1, 1, 1, 0.8f);
                    Vector2 but_offset = new Vector2(0, 30);
                    for(int i = 0; i < path.m_Points.Count; i++) {
                        var p = path.m_Points[i];
                        Vector3 pos = path.transform.TransformPoint(p);
                        var new_pos = path.transform.InverseTransformPoint(Handles.PositionHandle(pos, Quaternion.identity));
                        if((new_pos - p).magnitude > 0.001f) {
                            //Debug.LogWarning("(new_pos - p).magnitude:" + (new_pos - p).magnitude);
                            path.SetPoint(i, new_pos);
                        }
                        string str = "node " + i;
                        UCL_DrawGizmos.DrawStringGUI(str, pos, 28, Color.black, Color.white);
                        if(UCL_DrawGizmos.DrawButtonGUI("delete", pos, 28, new Vector2(50, 22), Color.red, but_col, but_offset)) {
                            delete_node_at = i;
                        }

                        if(i > 0 && path.m_Points.Count >= 2) {
                            var at = ((float)(i-0.5f) / (path.m_Points.Count - 1));
                            Vector3 m_pos = path.GetPoint(at);//0.5f*(prev_pos + pos); 

                            if(UCL_DrawGizmos.DrawButtonGUI("Insert node", m_pos, 28, new Vector2(80, 22), Color.black, but_col)) {
                                new_node_at = i;
                                //Debug.LogWarning("Pressed:" + str);
                            }
                        }
                        //Handles.Label(pos, str, outline_style);// - Vector3.one
                        //Handles.Label(pos, str, style);
                        /*
                        if(Handles.Button(pos, Quaternion.LookRotation(Camera.current.transform.forward, Camera.current.transform.up),
                            20.0f, 20.0f, Handles.RectangleHandleCap)) {
                            Debug.LogWarning("Add Button:" + i);
                        }
                        */
                    }
                }
            }
            if(new_node_at >= 0) {
                path.InsertPoint(new_node_at);
            }
            if(delete_node_at >= 0) {
                path.RemoveAt(delete_node_at);
            }
        }
    }
}