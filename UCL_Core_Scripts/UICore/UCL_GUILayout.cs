using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI {
    static public class UCL_GUILayout {
        #region property field
        static public int IntField(string label, int val,int min_width = 80) {
            GUILayout.BeginHorizontal();
            LabelAutoSize(label);
            string result = GUILayout.TextField(val.ToString(), GUILayout.MinWidth(min_width));
            GUILayout.EndHorizontal();

            int res_val = 0;
            if(int.TryParse(result, out res_val)) return res_val;
            return val;
        }
        static public float FloatField(string label, float val, int min_width = 80) {
            GUILayout.BeginHorizontal();
            LabelAutoSize(label);
            string result = GUILayout.TextField(val.ToString(), GUILayout.MinWidth(min_width));
            GUILayout.EndHorizontal();

            float res_val = 0;
            if(float.TryParse(result, out res_val)) return res_val;
            return val;
        }
        static public string TextField(string label, string val) {
            GUILayout.BeginHorizontal();
            LabelAutoSize(label);
            string result = GUILayout.TextField(val);
            GUILayout.EndHorizontal();
            return result;
        }
        static public Vector3 Vector3Field(string label, Vector3 val) {
            GUILayout.BeginHorizontal();
            LabelAutoSize(label);
            GUILayout.FlexibleSpace();
            LabelAutoSize("X");
            string x = GUILayout.TextField(val.x.ToString());
            float res_val = 0;
            if(float.TryParse(x, out res_val))val.x = res_val;

            LabelAutoSize("Y");
            string y = GUILayout.TextField(val.y.ToString());
            if(float.TryParse(y, out res_val)) val.y = res_val;

            LabelAutoSize("Z");
            string z = GUILayout.TextField(val.z.ToString());
            if(float.TryParse(z, out res_val)) val.z = res_val;
            GUILayout.EndHorizontal();
            return val;
        }
        static public string TextField(string label, string val, int min_width) {
            GUILayout.BeginHorizontal();
            LabelAutoSize(label);
            string result = GUILayout.TextField(val, GUILayout.MinWidth(min_width));
            GUILayout.EndHorizontal();
            return result;
        }
        #endregion
        static public void DrawSprite(Sprite sprite) {
            if(sprite == null) return;
            DrawSprite(sprite, sprite.rect.width, sprite.rect.height);
        }
        static public void DrawSpriteFixedWidth(Sprite sprite, float width) {
            if(sprite == null) return;
            DrawSprite(sprite, width, sprite.rect.height * (width / sprite.rect.width));
        }
        static public void DrawSpriteFixedHeight(Sprite sprite, float height) {
            if(sprite == null) return;
            DrawSprite(sprite, sprite.rect.width * (height / sprite.rect.height), height);
        }
        static public void DrawSprite(Sprite sprite, float width, float height) {
            if(sprite == null) return;
            DrawSprite(sprite, width, width, height, height);
        }
        static public void DrawSprite(Sprite sprite, float min_width, float max_width, float min_height, float max_height) {
            if(sprite == null) return;
            Rect sprite_rect = sprite.rect;
            Rect rect = GUILayoutUtility.GetRect(min_width, max_width, min_height, max_height);
            if(rect.width > max_width) rect.width = max_width;
            if(rect.height > max_height) rect.height = max_height;

            var tex = sprite.texture;
            sprite_rect.xMin /= tex.width;
            sprite_rect.xMax /= tex.width;
            sprite_rect.yMin /= tex.height;
            sprite_rect.yMax /= tex.height;
            GUI.DrawTextureWithTexCoords(rect, tex, sprite_rect);
        }

        #region Button
        static GUIStyle sButtonGuiStyle = new GUIStyle(GUI.skin.button);
        /// <summary>
        /// Draw a GUILayout Button fit the size of text
        /// </summary>
        /// <param name="name">the content of button</param>
        /// <param name="fontsize">font size</param>
        /// <returns></returns>
        public static bool ButtonAutoSize(string name, int fontsize = 22) {
            sButtonGuiStyle.fontSize = fontsize;
            sButtonGuiStyle.normal.textColor = Color.white;

            Vector2 size = sButtonGuiStyle.CalcSize(new GUIContent(name));
            bool flag = GUILayout.Button(name, style: sButtonGuiStyle, GUILayout.Width(size.x), GUILayout.Height(size.y));

            return flag;
        }
        public static bool ButtonAutoSize(string name, int fontsize, Color but_color) {
            sButtonGuiStyle.fontSize = fontsize;
            Color col_tmp = GUI.backgroundColor;
            GUI.backgroundColor = but_color;
            Vector2 size = sButtonGuiStyle.CalcSize(new GUIContent(name));
            bool flag = GUILayout.Button(name, style: sButtonGuiStyle, GUILayout.Width(size.x), GUILayout.Height(size.y));
            GUI.backgroundColor = col_tmp;
            return flag;
        }
        public static bool ButtonAutoSize(string name, int fontsize, Color but_color, Color text_color) {
            sButtonGuiStyle.fontSize = fontsize;
            sButtonGuiStyle.normal.textColor = text_color;

            Color col_tmp = GUI.backgroundColor;
            GUI.backgroundColor = but_color;
            Vector2 size = sButtonGuiStyle.CalcSize(new GUIContent(name));
            bool flag = GUILayout.Button(name, style: sButtonGuiStyle, GUILayout.Width(size.x), GUILayout.Height(size.y));
            GUI.backgroundColor = col_tmp;
            return flag;
        }
        #endregion

        #region Label
        static GUIStyle sLabelGuiStyle = new GUIStyle(GUI.skin.label);
        public static void LabelAutoSize(string name, int fontsize = 13) {
            sLabelGuiStyle.fontSize = fontsize;
            sLabelGuiStyle.normal.textColor = Color.white;

            Vector2 size = sLabelGuiStyle.CalcSize(new GUIContent(name));
            GUILayout.Label(name, style: sLabelGuiStyle, GUILayout.Width(size.x), GUILayout.Height(size.y));

        }
        #endregion

        #region Popup
        public static int Popup(int selectedIndex, string[] displayedOptions, ref bool opened) {
            string cur = displayedOptions[selectedIndex];
            if(opened) {
                GUILayout.BeginVertical();
                if(GUILayout.Button(cur)) {
                    opened = false;
                }
                using(var scope = new GUILayout.VerticalScope("box")) {
                    for(int i = 0; i < displayedOptions.Length; i++) {
                        if(GUILayout.Button(displayedOptions[i])) {
                            opened = false;
                            return i;
                        }
                    }
                }
                GUILayout.EndVertical();
            } else {
                if(GUILayout.Button(cur)) {
                    opened = true;
                }
            }
            return selectedIndex;
        }
        public static int Popup(int selectedIndex, List<string> displayedOptions, ref bool opened) {
            string cur = displayedOptions[selectedIndex];
            if(opened) {
                GUILayout.BeginVertical();
                if(GUILayout.Button(cur)) {
                    opened = false;
                }
                using(var scope = new GUILayout.VerticalScope("box")) {
                    for(int i = 0; i < displayedOptions.Count; i++) {
                        if(GUILayout.Button(displayedOptions[i])) {
                            opened = false;
                            return i;
                        }
                    }
                }
                GUILayout.EndVertical();
            } else {
                if(GUILayout.Button(cur)) {
                    opened = true;
                }
            }
            return selectedIndex;
        }
        #endregion
    }
}