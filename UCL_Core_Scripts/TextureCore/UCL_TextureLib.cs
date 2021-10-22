using System.Collections;
using System.Collections.Generic;
using System.IO;
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
            if(UCL.Core.EditorLib.AssetDatabaseMapper.Contains(Texture)) {
                UCL.Core.EditorLib.EditorUtilityMapper.CopySerialized(Texture, UCL.Core.EditorLib.AssetDatabaseMapper.LoadAssetAtPath<Texture>(path));
            } else {
                UCL.Core.EditorLib.AssetDatabaseMapper.CreateAsset(Texture, path);
            }
            UCL.Core.EditorLib.AssetDatabaseMapper.Refresh();
        }
    }
#endif
    static public class Lib {

        /// <summary>
        /// Create a Sprite from byte array
        /// </summary>
        /// <param name="iData"></param>
        /// <param name="iPixelsPerUnit"></param>
        /// <returns></returns>
        public static Sprite CreateSprite(byte[] iData, float iPixelsPerUnit = 100f)
        {
            Texture2D aTexture = UCL.Core.TextureLib.Lib.CreateTexture(iData);
            return Sprite.Create(aTexture, new Rect(0.0f, 0.0f, aTexture.width, aTexture.height), new Vector2(0.5f, 0.5f), iPixelsPerUnit);
        }
        /// <summary>
        /// Create a Texture2D from byte array
        /// </summary>
        /// <param name="iData"></param>
        /// <returns></returns>
        public static Texture2D CreateTexture(byte[] iData)
        {
            var aTex = new Texture2D(1, 1);
            aTex.LoadImage(iData); //..this will auto-resize the texture dimensions.
            return aTex;
        }
        /// <summary>
        ///  Create a Texture2D from texture Path
        /// </summary>
        /// <param name="iPath"></param>
        /// <returns></returns>
        public static Texture2D CreateTexture(string iPath)
        {
            var aData = File.ReadAllBytes(iPath);
            var aTex = new Texture2D(1, 1);
            aTex.LoadImage(aData); //..this will auto-resize the texture dimensions.
            return aTex;
        }
        /// <summary>
        /// Save texture to file
        /// </summary>
        /// <param name="iPath"></param>
        /// <param name="iTexture"></param>
        public static void SaveTexture(string iPath, Texture2D iTexture)
        {
            string aPath = iPath.ToLower();
            if (aPath.Contains(".png"))
            {
                SavePNG(iPath, iTexture);
            }else if (aPath.Contains(".jpg"))
            {
                SaveJPG(iPath, iTexture);
            }
        }
        public static void SaveTexture(string iPath, Texture2D iTexture, ImageFormat iFormat) {
            switch(iFormat) {
                case ImageFormat.png: 
                    {
                        SavePNG(iPath, iTexture);
                        break;
                    }
                case ImageFormat.jpg: {
                        SaveJPG(iPath, iTexture);
                        break;
                    }
            }
        }
        public static void SavePNG(string iPath, Texture2D texture) {
            Core.FileLib.Lib.CreateDirectory(Core.FileLib.Lib.GetFolderPath(iPath));
            if (iPath.Contains(".PNG"))
            {
                iPath = iPath.Replace(".PNG", ".png");
            }
            else
            {
                if (!iPath.Contains(".png"))
                {
                    iPath = iPath + ".png";
                }
            }

            System.IO.File.WriteAllBytes(iPath, texture.EncodeToPNG());
#if UNITY_EDITOR
            UCL.Core.EditorLib.AssetDatabaseMapper.Refresh();
#endif
        }
        public static void SaveJPG(string path, Texture2D texture) {
            Core.FileLib.Lib.CreateDirectory(Core.FileLib.Lib.GetFolderPath(path));
            System.IO.File.WriteAllBytes(path + ".jpg", texture.EncodeToJPG());
#if UNITY_EDITOR
            UCL.Core.EditorLib.AssetDatabaseMapper.Refresh();
#endif
        }

        /// <summary>
        /// Resize the target Texture if the texture bigger than iMaxSize
        /// </summary>
        /// <param name="iPath"></param>
        /// <param name="iMaxSize"></param>
        public static void ResizeTexture(string iPath, int iMaxSize)
        {
            if (!File.Exists(iPath))
            {
                Debug.LogError("ResizeTexture fail,iPath:" + iPath + ",!File.Exists");
                return;
            }
            try
            {
                var aFileData = File.ReadAllBytes(iPath);
                var aTex = new Texture2D(2, 2);
                aTex.LoadImage(aFileData); //..this will auto-resize the texture dimensions.

                bool aIsResized = false;
                int aWidth = aTex.width;
                int aHeight = aTex.height;
                //Debug.LogWarning("aIconPath:" + iPath + "Width:" + aWidth + ",Height:" + aHeight);
                if (aTex.width > aTex.height)
                {
                    if (aTex.width > iMaxSize)
                    {
                        float aScale = (float)iMaxSize / aTex.width;
                        aTex.ResizeTexture(iMaxSize, Mathf.RoundToInt(aTex.height * aScale));
                        aIsResized = true;
                    }
                }
                else
                {
                    if (aTex.height > iMaxSize)
                    {
                        float aScale = (float)iMaxSize / aTex.height;
                        aTex.ResizeTexture(Mathf.RoundToInt(aTex.width * aScale), iMaxSize);
                        aIsResized = true;
                    }
                }
                if (aIsResized)
                {
                    Debug.Log("Resize Texture:" + iPath + ",Origin Width:" + aWidth + ",Origin Height:" + aHeight
                        + ",new Width:" + aTex.width + ",new Height:" + aTex.height);
                    //SavePNG(iPath, aTex);
                    SaveTexture(iPath, aTex);
                }
                UnityEngine.Object.DestroyImmediate(aTex);
            }
            catch (System.Exception iE)
            {
                Debug.LogException(iE);
            }
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