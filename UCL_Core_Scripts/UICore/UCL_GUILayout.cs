using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI {
    static public class UCL_GUILayout {
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
    }
}