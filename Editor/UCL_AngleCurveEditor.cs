using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
namespace UCL.Core.MathLib {
    [CustomEditor(typeof(UCL_AngleCurve))]
    public class UCL_AngleCurveEditor : Core.EditorLib.UCL_MonobehaviorEditor {
        UCL_AngleCurve m_Path;
        void OnSceneGUI() {
            m_Path = target as UCL_AngleCurve;
            int new_node_at = -1;
            int delete_node_at = -1;
            if(m_Path.enabled && m_Path.gameObject.activeInHierarchy) {
                if(m_Path.m_Points != null && m_Path.m_Points.Count > 0) {
                    //allow path adjustment undo
                    Undo.RecordObject(m_Path, "Alter Path");
                    //node handle display:
                    Color but_col = new Color(1, 1, 1, 0.8f);
                    Vector2 but_offset = new Vector2(0, 30);
                    int draw_count = m_Path.m_Points.Count;
                    //if(path.m_Loop) --draw_count;
                    for(int i = 0; i < draw_count; i++) {
                        var p = m_Path.m_Points[i];

                        if(!m_Path.m_Loop || i < draw_count - 1) {
                            Vector3 pos = m_Path.transform.TransformPoint(p);
                            var new_pos = m_Path.transform.InverseTransformPoint(Handles.PositionHandle(pos, Quaternion.identity));
                            if((new_pos - p).magnitude > 0.001f) {
                                //Debug.LogWarning("(new_pos - p).magnitude:" + (new_pos - p).magnitude);
                                m_Path.SetPoint(i, new_pos);
                            }

                            string str = "node " + i;
                            UCL_DrawGizmos.DrawStringGUI(str, pos, 28, Color.black, Color.white);
                            if(UCL_DrawGizmos.DrawButtonGUI("delete", pos, 28, new Vector2(50, 22), Color.red, but_col, but_offset)) {
                                delete_node_at = i;
                            }
                        }


                        if(i > 0 && m_Path.m_Points.Count >= 2) {
                            var at = ((float)(i-0.5f) / (m_Path.m_Points.Count - 1));
                            Vector3 m_pos = m_Path.GetPoint(at);//0.5f*(prev_pos + pos); 

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
                m_Path.InsertPoint(new_node_at);
            }
            if(delete_node_at >= 0) {
                m_Path.RemoveAt(delete_node_at);
            }
        }
    }
}