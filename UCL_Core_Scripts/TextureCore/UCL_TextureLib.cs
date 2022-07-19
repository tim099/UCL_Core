using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
namespace UCL.Core.TextureLib {
    public class TextureAtlasData : System.IDisposable
    {
        public Texture2D m_Texture;
        /// <summary>
        /// m_Seg = Sqrt(m_TextureCount)
        /// m_Seg * m_Size = m_AtlasSize
        /// etc. m_Seg = 2 ,m_Size = 32, size of m_Texture will be 64 X 64 (32*2 X 32*2)
        /// </summary>
        public int m_Seg = 0;
        /// <summary>
        /// Texture count of Textures combine to this Atlas
        /// </summary>
        public int m_TextureCount = 1;
        /// <summary>
        /// Texture that size not fit m_Size will be scale to fit m_Size
        /// </summary>
        public int m_Size = 64;

        /// <summary>
        /// Interval of texture (Pixels)
        /// </summary>
        public int m_Interval = 0;

        /// <summary>
        /// 1f / m_Seg
        /// </summary>
        public float SegSize => 1f / m_Seg;
        public float AtlasMult => m_Seg < 1? 0 : m_Seg - 1;

        public void Dispose()
        {
            if(m_Texture != null)
            {
                GameObject.DestroyImmediate(m_Texture);
                m_Texture = null;
            }
        }

        Dictionary<int, Color> m_TextureUVDic = new Dictionary<int, Color>();
        /// <summary>
        /// Convert TextureID into UV color
        /// </summary>
        /// <param name="iID"></param>
        /// <returns></returns>
        public Color GetAtlasColor(int iID)
        {
            if (iID < 0 || iID >= m_TextureCount) return Color.clear;
            if (m_Seg <= 1 || iID == 0) return Color.black;//Color.black == (0,0,0,1)

            if (!m_TextureUVDic.ContainsKey(iID))
            {
                float aSize = 1f / (m_Seg - 1);
                int aX = iID % m_Seg;
                int aY = (iID - aX) / m_Seg;
                m_TextureUVDic.Add(iID, new Color(aSize * aX, aSize * aY, 0, 1));
            }

            return m_TextureUVDic[iID];
        }
    }


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
        public static void SaveTextureAsset(string iPath, Texture Texture) {
            if(UCL.Core.EditorLib.AssetDatabaseMapper.Contains(Texture)) {
                UCL.Core.EditorLib.EditorUtilityMapper.CopySerialized(Texture, UCL.Core.EditorLib.AssetDatabaseMapper.LoadAssetAtPath<Texture>(iPath));
            } else {
                UCL.Core.EditorLib.AssetDatabaseMapper.CreateAsset(Texture, iPath);
            }
            UCL.Core.EditorLib.AssetDatabaseMapper.Refresh();
        }
    }
#endif
    static public class Lib {
        /// <summary>
        /// Create TextureAtlas from input iTextList
        /// </summary>
        /// <param name="iTextList"></param>
        /// <param name="iSize">Size per Texture</param>
        /// <param name="iTextureFormat"></param>
        /// <returns></returns>
        public static TextureAtlasData CreateTextureAtlas(List<Texture2D> iTextList, int iSize, TextureFormat iTextureFormat, int iInterval = 0)
        {
            TextureAtlasData aData = new TextureAtlasData();
            
            if (iTextList.IsNullOrEmpty())
            {
                Debug.LogError("CreateTextureAtlas iTextList.IsNullOrEmpty()!!");
                return aData;
            }
            aData.m_Interval = iInterval;
            aData.m_Size = iSize;
            aData.m_Seg = Core.MathLib.Lib.SqrtInt(iTextList.Count);
            aData.m_TextureCount = iTextList.Count;
            int aSeg = aData.m_Seg;
            int aAtlasSize = aSeg * iSize + (aSeg-1) * iInterval;
            var aTexture = new Texture2D(aAtlasSize, aAtlasSize, iTextureFormat, false);

            for (int i = 0; i < iTextList.Count; i++)
            {
                var aTex = iTextList[i];
                int aX = i % aSeg;
                int aY = (i - aX) / aSeg;
                Color[] aCols = null;
                if (iSize != aTex.width || iSize != aTex.height)
                {
                    aCols = TextureLib.Lib.GetPixels(aTex, iSize, iSize);
                }
                else
                {
                    aCols = aTex.GetPixels(0);
                }
                aTexture.SetPixels(aX * (iSize + iInterval), aY * (iSize + iInterval), iSize, iSize, aCols);
            }
            aTexture.Apply();
            aData.m_Texture = aTexture;
            return aData;
        }
        /// <summary>
        /// Create a Sprite from byte array
        /// </summary>
        /// <param name="iData"></param>
        /// <param name="iPixelsPerUnit"></param>
        /// <returns></returns>
        public static Sprite CreateSprite(byte[] iData, float iPixelsPerUnit = 100f, bool iIsInverse = false)
        {
            Texture2D aTexture = UCL.Core.TextureLib.Lib.CreateTexture(iData, iIsInverse);
            return Sprite.Create(aTexture, new Rect(0.0f, 0.0f, aTexture.width, aTexture.height), new Vector2(0.5f, 0.5f), iPixelsPerUnit);
        }
        /// <summary>
        /// Create a Texture2D from byte array
        /// </summary>
        /// <param name="iData"></param>
        /// <returns></returns>
        public static Texture2D CreateTexture(byte[] iData, bool iIsInverse = false)
        {
            var aTex = new Texture2D(1, 1);
            aTex.LoadImage(iData); //..this will auto-resize the texture dimensions.
            if (iIsInverse)
            {
                int aX = aTex.width / 2;
                int aY = aTex.height;
                int aW = aTex.width;
                for (int i = 0; i < aX; i++)
                {
                    for (int j = 0; j < aY; j++)
                    {
                        var aA = aTex.GetPixel(i, j);
                        var aB = aTex.GetPixel(aW - i - 1, j);
                        aTex.SetPixel(i, j, aB);
                        aTex.SetPixel(aW - i - 1, j, aA);
                    }
                }
                aTex.Apply();
            }

            return aTex;
        }
        /// <summary>
        ///  Create a Texture2D from texture Path
        /// </summary>
        /// <param name="iPath"></param>
        /// <returns></returns>
        public static Texture2D CreateTexture(string iPath)
        {
            if (!File.Exists(iPath))
            {
                return null;
            }
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
        public static Texture2D ToGrayScale(this Texture2D iTexture2D)
        {
            Texture2D aGrayScaleTexture = new Texture2D(iTexture2D.width, iTexture2D.height);
            for (int y = 0; y < iTexture2D.height; y++)
            {
                for (int x = 0; x < iTexture2D.width; x++)
                {
                    Color aCol = iTexture2D.GetPixel(x, y);

                    float aR = aCol.r;
                    float aG = aCol.g;
                    float aB = aCol.b;

                    float aGrayCol = (aCol.r * 0.299f) + (aCol.g * 0.587f) + (aCol.b * 0.114f);

                    aGrayScaleTexture.SetPixel(x, y, new Color(aGrayCol, aGrayCol, aGrayCol, aCol.a));
                }
            }
            aGrayScaleTexture.Apply();

            return aGrayScaleTexture;
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

        public static Sprite CreateSprite(Texture2D iTexture) {
            if (iTexture == null) return null;
            return Sprite.Create(iTexture, new Rect(0, 0, iTexture.width, iTexture.height), Vector2.zero);
        }
    }
}