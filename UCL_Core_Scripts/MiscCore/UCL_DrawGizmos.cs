using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core {
    static public class UCL_DrawGizmos {

        public static Vector3 TransformByPixel(Vector3 position, float x, float y) {
#if UNITY_EDITOR
            return TransformByPixel(position, new Vector3(x, y));
#else
            return Vector3.zero;
#endif
        }

        public static Vector3 TransformByPixel(Vector3 position, Vector3 translateBy) {
#if UNITY_EDITOR
            Camera cam = UnityEditor.SceneView.currentDrawingSceneView.camera;
            if(cam == null) return position;
            return cam.ScreenToWorldPoint(cam.WorldToScreenPoint(position) + translateBy);
#else
            return Vector3.zero;
#endif
        }
        public static Vector3 WorldToScreenPoint(Vector3 position) {
#if UNITY_EDITOR
            Camera cam = UnityEditor.SceneView.currentDrawingSceneView.camera;
            if(cam == null) return position;
            return cam.WorldToScreenPoint(position);
#else
            return Vector3.zero;
#endif
        }
        public static void DrawCube(Vector3 pos, float size, Color? color = null, bool with_wire = true) {
#if UNITY_EDITOR
            var restoreColor = Gizmos.color;
            if(color.HasValue) {
                Gizmos.color = color.Value;
            }
            if(with_wire) Gizmos.DrawWireCube(pos, size * Vector3.one);

            Gizmos.DrawCube(pos, size * Vector3.one);
            Gizmos.color = restoreColor;
#endif
        }
        /// <summary>
        /// Not done yet!!
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="col"></param>
        /// <param name="size"></param>
        public static void DrawPoint(Vector3 pos, Color col ,float size) {
#if UNITY_EDITOR

#endif
        }


        public static void DrawLine(Vector3 start, Vector3 end, Color? color = null) {
#if UNITY_EDITOR
            var restoreColor = Gizmos.color;
            if(color.HasValue) {
                Gizmos.color = color.Value;
            }
            Gizmos.DrawLine(start, end);
            Gizmos.color = restoreColor;
#endif
        }
        public static void DrawLine(Vector3 start, Vector3 end, float width, Color? color = null) {
#if UNITY_EDITOR
            Vector3 del = WorldToScreenPoint(start) - WorldToScreenPoint(end);
            int half_width = (int)(width / 2);
            if(Mathf.Abs(del.x) < Mathf.Abs(del.y)) {
                for(int i = 0; i < width; i++) {
                    DrawLine(TransformByPixel(start, i - half_width, 0), TransformByPixel(end, i - half_width, 0), color);
                }
            } else {
                for(int i = 0; i < width; i++) {
                    DrawLine(TransformByPixel(start, 0, i - half_width), TransformByPixel(end, 0, i - half_width), color);
                }
            }
#endif
        }
        public static void DrawSphere(Vector3 pos, float size) {
#if UNITY_EDITOR
            Gizmos.DrawSphere(pos, size);
#endif
        }
        public static void DrawConstSizeSphere(Vector3 pos, float size) {
#if UNITY_EDITOR
            Gizmos.DrawSphere(pos, size * GetGizmoSize(pos));
#endif
        }
        public static float GetGizmoSize(Vector3 position) {
#if UNITY_EDITOR
            Camera cam = UnityEditor.SceneView.currentDrawingSceneView.camera;
            if(cam == null) return 1;

            //Camera cam = Camera.current;
            //if(cam == null) return 1;
            position = Gizmos.matrix.MultiplyPoint(position);

            Transform transform = cam.transform;
            Vector3 position2 = transform.position;
            float z = Vector3.Dot(position - position2, transform.TransformDirection(new Vector3(0f, 0f, 1f)));
            Vector3 a = cam.WorldToScreenPoint(position2 + transform.TransformDirection(new Vector3(0f, 0f, z)));
            Vector3 b = cam.WorldToScreenPoint(position2 + transform.TransformDirection(new Vector3(1f, 0f, z)));
            float magnitude = (a - b).magnitude;
            
            return 4 / Mathf.Max(magnitude, 0.0001f);
#else
            return 0;
#endif
        }
        public static void DrawString(string text, Vector3 worldPos, int fontsize, Color? color = null, Color? outline_color = null) {
#if UNITY_EDITOR
            var restoreColor = GUI.color;
            int oldsize = GUI.skin.label.fontSize;
            GUI.skin.label.fontSize = fontsize;
            Vector2 size = GUI.skin.label.CalcSize(new GUIContent(text));
            Vector3 newPos = TransformByPixel(worldPos, -0.5f * size.x, 0.5f * size.y);
            if(outline_color.HasValue) {
                float offset = 0.09f * fontsize;
                GUI.color = outline_color.Value;
                UnityEditor.Handles.Label(TransformByPixel(newPos, offset, 0), text);
                UnityEditor.Handles.Label(TransformByPixel(newPos, -offset, 0), text);
                UnityEditor.Handles.Label(TransformByPixel(newPos, 0, offset), text);
                UnityEditor.Handles.Label(TransformByPixel(newPos, 0, -offset), text);
            }
            if(color.HasValue) GUI.color = color.Value;
            UnityEditor.Handles.Label(newPos, text);
            GUI.skin.label.fontSize = oldsize;
            GUI.color = restoreColor;
#endif
        }
        /// <summary>
        /// convert the world space position to GUI space
        /// </summary>
        /// <param name="worldPos"></param>
        /// <returns></returns>
        public static Vector2 WorldPosToGUI(Vector3 worldPos) {
#if UNITY_EDITOR
            var view = UnityEditor.SceneView.currentDrawingSceneView;
            var cam = view.camera;

            if(cam == null) return Vector2.zero;
            //Debug.LogWarning("cam:" + cam.name);
            Vector3 sp = cam.WorldToViewportPoint(worldPos);
            sp.Scale(new Vector3(view.position.width, view.position.height, 1));
            
            return new Vector2(sp.x, view.position.height - sp.y);
#else
            return Vector2.zero;
#endif
        }

        public static Vector2 WorldPosToUI(Vector3 worldPos) {
#if UNITY_EDITOR
            var view = UnityEditor.SceneView.currentDrawingSceneView;
            var cam = view.camera;

            if(cam == null) return Vector2.zero;
            //Debug.LogWarning("cam:" + cam.name);
            Vector3 sp = cam.WorldToViewportPoint(worldPos);

            return sp.XY();
#else
            return Vector2.zero;
#endif
        }

        /// <summary>
        /// Draw EditorGUI.Popup at world pos
        /// </summary>
        /// <param name="world_pos"></param>
        /// <param name="size"></param>
        /// <param name="selected_index"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        public static int DrawPopup(Vector3 world_pos, Vector2 size, int selected_index, string[] options, Vector2? position_offset = null) {
#if UNITY_EDITOR
            UnityEditor.Handles.BeginGUI();
            var pos = WorldPosToGUI(world_pos);
            pos -= 0.5f * size;
            if(position_offset.HasValue) {
                pos += position_offset.Value;
            }
            Rect rect = new Rect(pos.x, pos.y, size.x, size.y);
            int at = UnityEditor.EditorGUI.Popup(rect, selected_index, options);
            UnityEditor.Handles.EndGUI();
            return at;
#else
            return 0;
#endif
        }

        /// <summary>
        /// Draw a GUI button at worldspace position
        /// </summary>
        /// <param name="text"></param>
        /// <param name="worldPos"></param>
        /// <param name="fontsize"></param>
        /// <param name="but_size"></param>
        /// <param name="text_col"></param>
        /// <param name="but_col"></param>
        /// <param name="position_offset"></param>
        /// <returns></returns>
        public static bool DrawButtonGUI(string text, Vector3 worldPos, int fontsize, Vector2 but_size, Color text_col, Color but_col,
            Vector2? position_offset = null) {
#if UNITY_EDITOR
            UnityEditor.Handles.BeginGUI();
            
            int oldsize = GUI.skin.label.fontSize;
            GUI.skin.label.fontSize = fontsize;

            Vector2 DrawPos = WorldPosToGUI(worldPos);// - 0.5f * (size.x) 0.5f * (size.y)
            DrawPos -= 0.5f*but_size;
            if(position_offset.HasValue) {
                DrawPos += position_offset.Value;
            }
            var restoreColor = GUI.contentColor;
            var res2 = GUI.backgroundColor;
            GUI.backgroundColor = but_col;
            GUI.contentColor = text_col;
            var result = GUI.Button(new Rect(DrawPos.x, DrawPos.y, but_size.x, but_size.y), text);
            //GUI.Label(new Rect(DrawPos.x, DrawPos.y, size.x, size.y), text);
            GUI.skin.label.fontSize = oldsize;

            GUI.contentColor = restoreColor;
            GUI.backgroundColor = res2;
            UnityEditor.Handles.EndGUI();
            
            return result;
#else
            return false;
#endif
        }

        public static bool DrawButtonGUI(string text, Vector2 pos, int fontsize, Vector2 but_size, Color text_col, Color but_col) {
#if UNITY_EDITOR
            UnityEditor.Handles.BeginGUI();

            int oldsize = GUI.skin.label.fontSize;
            GUI.skin.label.fontSize = fontsize;

            pos -= 0.5f * but_size;
            var restoreColor = GUI.contentColor;
            var res2 = GUI.backgroundColor;
            GUI.backgroundColor = but_col;
            GUI.contentColor = text_col;
            var result = GUI.Button(new Rect(pos.x, pos.y, but_size.x, but_size.y), text);
            GUI.skin.label.fontSize = oldsize;

            GUI.contentColor = restoreColor;
            GUI.backgroundColor = res2;
            UnityEditor.Handles.EndGUI();

            return result;
#else
            return false;
#endif
        }

        public static void DrawStringGUI(string text, Vector3 worldPos, int fontsize, Color? color = null, Color? outline_color = null,
            float outline_offset = 0.08f) {
#if UNITY_EDITOR
            UnityEditor.Handles.BeginGUI();
            var restoreColor = GUI.color;
            int oldsize = GUI.skin.label.fontSize;
            GUI.skin.label.fontSize = fontsize;
            Vector2 size = GUI.skin.label.CalcSize(new GUIContent(text));
            //GUI.matrix = Matrix4x4.identity;

            Vector2 DrawPos = WorldPosToGUI(worldPos);// - 0.5f * (size.x) 0.5f * (size.y)
            DrawPos -= 0.5f*size;

            //Debug.LogWarning("screenPos:" + screenPos+ ",cam.nearClipPlane:"+ cam.nearClipPlane+ ",cam.farClipPlane:"+ cam.farClipPlane
            // + "\n view.position.width:" + view.position.width + ",view.position.height:" + view.position.height
            // + "\n cam.transform.position:" + cam.transform.position);
            //Debug.LogWarning("view.position.width:" + view.position.width + ",view.position.height:" + view.position.height);
            /*
            {
                var view = UnityEditor.SceneView.currentDrawingSceneView;
                var cam = view.camera;

                if(cam == null) return;
                //Debug.LogWarning("cam:" + cam.name);
                Vector3 sp = cam.WorldToViewportPoint(worldPos);
                sp.Scale(new Vector3(view.position.width, view.position.height, 1));
                Matrix4x4 m_ProjectMatrix = Matrix4x4.Ortho(0, view.position.width, 0, view.position.height, 0, 1);
                GL.PushMatrix();

                Vector3 dp = new Vector3(sp.x, sp.y, 0);
                if(color.HasValue) GL.Color(color.Value);
                GL.LoadOrtho();
                GL.LoadProjectionMatrix(m_ProjectMatrix);
                GL.Begin(GL.LINES);
                GL.Vertex(dp);
                GL.Vertex(dp + new Vector3(0, 25));
                GL.End();
                GL.Begin(GL.LINES);
                GL.Vertex(dp);
                GL.Vertex(dp + new Vector3(25, 0));
                GL.End();
                GL.PopMatrix();
            }
            //*/
            if(outline_color.HasValue) {
                GUI.color = outline_color.Value;
                GUI.Label(new Rect(DrawPos.x + outline_offset * fontsize, DrawPos.y, size.x, size.y), text);
                GUI.Label(new Rect(DrawPos.x - outline_offset * fontsize, DrawPos.y, size.x, size.y), text);
                GUI.Label(new Rect(DrawPos.x, DrawPos.y + outline_offset * fontsize, size.x, size.y), text);
                GUI.Label(new Rect(DrawPos.x, DrawPos.y - outline_offset * fontsize, size.x, size.y), text);
            }
            if(color.HasValue) GUI.color = color.Value;
            GUI.Label(new Rect(DrawPos.x, DrawPos.y, size.x, size.y), text);
            GUI.skin.label.fontSize = oldsize;
            GUI.color = restoreColor;
            UnityEditor.Handles.EndGUI();
#endif
        }
        public static void DrawCircleAtPointWithRadius(Vector3 point, Quaternion orient, float radius) {
#if UNITY_EDITOR
            Matrix4x4 prevMatrix = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(point, orient, radius * Vector3.one);

            const int kNumPoints = 25;
            Vector3 currPoint = Vector3.forward;
            Quaternion rot = Quaternion.AngleAxis(360f / (float)kNumPoints, Vector3.up);
            for(int i = 0; i < kNumPoints + 1; ++i) {
                Vector3 nextPoint = rot * currPoint;
                Gizmos.DrawLine(currPoint, nextPoint);
                currPoint = nextPoint;
            }
            Gizmos.matrix = prevMatrix;
#endif
        }
    }

}


