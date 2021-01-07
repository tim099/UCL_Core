using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.TextureLib {

    #region Interface
    public interface UCLI_Texture {
        Texture GetTexture();
    }
    public interface UCLI_Texture2D {
        Texture2D GetTexture();
    }
    #endregion


#if UNITY_EDITOR
    static public class EditorLib {
        public static void SaveTextureAsset(string _path, Texture Texture) {
            string path = _path;
            if(UnityEditor.AssetDatabase.Contains(Texture)) {
                UnityEditor.EditorUtility.CopySerialized(Texture, UnityEditor.AssetDatabase.LoadAssetAtPath<Texture>(path));
            } else {
                UnityEditor.AssetDatabase.CreateAsset(Texture, path);
            }
            UnityEditor.AssetDatabase.Refresh();
        }
    }
#endif
    static public class Lib {
        public static void SavePNG(string path, Texture2D Texture) {
            System.IO.File.WriteAllBytes(path + ".png", Texture.EncodeToPNG());
#if UNITY_EDITOR
            UnityEditor.AssetDatabase.Refresh();
#endif
        }
        public static void SaveJPG(string path, Texture2D Texture) {
            System.IO.File.WriteAllBytes(path + ".jpg", Texture.EncodeToJPG());
#if UNITY_EDITOR
            UnityEditor.AssetDatabase.Refresh();
#endif
        }
        /// <summary>
        /// Scale the target texture to target size!!
        /// </summary>
        /// <param name="texture"></param>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <returns></returns>
        public static Color[] GetPixels(Texture2D texture, int width, int height) {
            float w_mult = 1.0f / width;
            float h_mult = 1.0f / height;
            var cols = new Color[height * width];
            for(int y = 0; y < height; y++) {
                for(int x = 0; x < width; x++) {
                    var col = texture.GetPixelBilinear(x * w_mult, y * h_mult);
                    cols[x + y * width] = col;
                }
            }
            return cols;
        }

        public static Sprite CreateSprite(Texture2D tex) {
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), Vector2.zero);
        }
    }
}