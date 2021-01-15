using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.TextureLib {
    public enum ImageFormat {
        png = 0,
        jpg,
    }
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
        public static void SaveTexture(string path, Texture2D texture, ImageFormat format) {
            switch(format) {
                case ImageFormat.png: 
                    {
                        SavePNG(path, texture);
                        break;
                    }
                case ImageFormat.jpg: {
                        SaveJPG(path, texture);
                        break;
                    }
            }
        }
        public static void SavePNG(string path, Texture2D texture) {
            Core.FileLib.Lib.CreateDirectory(Core.FileLib.Lib.GetFolderPath(path));
            System.IO.File.WriteAllBytes(path + ".png", texture.EncodeToPNG());
#if UNITY_EDITOR
            UnityEditor.AssetDatabase.Refresh();
#endif
        }
        public static void SaveJPG(string path, Texture2D texture) {
            Core.FileLib.Lib.CreateDirectory(Core.FileLib.Lib.GetFolderPath(path));
            System.IO.File.WriteAllBytes(path + ".jpg", texture.EncodeToJPG());
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
                    var col = texture.GetPixelBilinear((x + 0.5f) * w_mult, (y + 0.5f) * h_mult);
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