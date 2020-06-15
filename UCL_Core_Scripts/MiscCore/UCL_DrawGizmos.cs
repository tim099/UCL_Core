using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core {
    static public class UCL_DrawGizmos {
        public static Vector3 TransformByPixel(Vector3 position, float x, float y) {
            return TransformByPixel(position, new Vector3(x, y));
        }

        public static Vector3 TransformByPixel(Vector3 position, Vector3 translateBy) {
            Camera cam = UnityEditor.SceneView.currentDrawingSceneView.camera;
            if(cam == null) return position;
            return cam.ScreenToWorldPoint(cam.WorldToScreenPoint(position) + translateBy);
        }
        public static Vector3 WorldToScreenPoint(Vector3 position) {
            Camera cam = UnityEditor.SceneView.currentDrawingSceneView.camera;
            if(cam == null) return position;
            return cam.WorldToScreenPoint(position);
        }
        public static void DrawCube(Vector3 pos, float size, Color? color = null, bool with_wire = true) {
            var restoreColor = Gizmos.color;
            if(color.HasValue) {
                Gizmos.color = color.Value;
            }
            if(with_wire) Gizmos.DrawWireCube(pos, size * Vector3.one);

            Gizmos.DrawCube(pos, size * Vector3.one);
            Gizmos.color = restoreColor;
        }
        public static void DrawLine(Vector3 start, Vector3 end, Color? color = null) {
            var restoreColor = Gizmos.color;
            if(color.HasValue) {
                Gizmos.color = color.Value;
            }
            Gizmos.DrawLine(start, end);
            Gizmos.color = restoreColor;
        }
        public static void DrawLine(Vector3 start, Vector3 end, float width, Color? color = null) {
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

        }
        public static void DrawConstSizeSphere(Vector3 pos, float size) {
            Gizmos.DrawSphere(pos, size * GetGizmoSize(pos));
        }
        public static float GetGizmoSize(Vector3 position) {
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
        }
        public static void DrawString(string text, Vector3 worldPos, int fontsize, Color? color = null, Color? outline_color = null) {
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
        }
        public static Vector2 WorldPosToGUI(Vector3 worldPos) {
            var view = UnityEditor.SceneView.currentDrawingSceneView;
            var cam = view.camera;

            if(cam == null) return Vector2.zero;
            //Debug.LogWarning("cam:" + cam.name);
            Vector3 sp = cam.WorldToViewportPoint(worldPos);
            sp.Scale(new Vector3(view.position.width, view.position.height, 1));
            return new Vector2(sp.x, view.position.height - sp.y);
        }
        public static bool DrawButtonGUI(string text, Vector3 worldPos, int fontsize, Vector2 but_size, Color text_col, Color but_col,
            Vector2? position_offset = null) {
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
        }
        public static void DrawStringGUI(string text, Vector3 worldPos, int fontsize, Color? color = null, Color? outline_color = null,
            float outline_offset = 0.08f) {
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
        }
        public static void DrawCircleAtPointWithRadius(Vector3 point, Quaternion orient, float radius) {
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
        }
    }
}


