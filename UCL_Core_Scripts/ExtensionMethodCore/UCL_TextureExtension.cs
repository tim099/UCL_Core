using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static partial class TextureExtensionMethods {
    #region RenderTexture
    public static void ReleaseTemporaryAndClearRef(this RenderTexture iRenderTexture)
    {
        if (iRenderTexture == null) return;
        if (RenderTexture.active == iRenderTexture) RenderTexture.active = null;
        iRenderTexture.Release();
        RenderTexture.ReleaseTemporary(iRenderTexture);
    }

    public static Texture2D ToTexture2D(this RenderTexture iRenderTexture)
    {
        Texture2D aTexture2D = new Texture2D(iRenderTexture.width, iRenderTexture.height, TextureFormat.RGBA32, false);
        var aPrev = RenderTexture.active;
        RenderTexture.active = iRenderTexture;
        aTexture2D.ReadPixels(new Rect(0, 0, iRenderTexture.width, iRenderTexture.height), 0, 0);
        aTexture2D.Apply();
        RenderTexture.active = aPrev;
        return aTexture2D;
    }
    #endregion

    #region Texture2D
    /// <summary>
    /// Resize texture but keep the content
    /// </summary>
    /// <param name="texture"></param>
    /// <param name="width"></param>
    /// <param name="height"></param>
    /// <returns></returns>
    public static Texture2D ResizeTexture(this Texture2D texture, int width, int height) {
        var cols = UCL.Core.TextureLib.Lib.GetPixels(texture, width, height);
        texture.Resize(width, height);
        texture.SetPixels(cols);
        return texture;
    }
    /// <summary>
    /// Create a resized texture but keep the content
    /// </summary>
    /// <param name="texture"></param>
    /// <param name="width"></param>
    /// <param name="height"></param>
    /// <returns></returns>
    public static Texture2D CreateResizeTexture(this Texture2D texture, int width, int height) {
        var cols = UCL.Core.TextureLib.Lib.GetPixels(texture, width, height);
        var new_texture = new Texture2D(width, height, texture.format, false);
        new_texture.SetPixels(cols);
        return new_texture;
    }
    /// <summary>
    /// Create a resized texture but keep the content
    /// </summary>
    /// <param name="texture"></param>
    /// <param name="width"></param>
    /// <param name="height"></param>
    /// <param name="format"></param>
    /// <returns></returns>
    public static Texture2D CreateResizeTexture(this Texture2D texture, int width, int height, TextureFormat format) {
        var cols = UCL.Core.TextureLib.Lib.GetPixels(texture, width, height);
        var new_texture = new Texture2D(width, height, format, false);
        new_texture.SetPixels(cols);
        return new_texture;
    }
    /// <summary>
    /// Set the whole texture to iColor
    /// </summary>
    /// <param name="iTexture"></param>
    /// <param name="iColor">Target color</param>
    public static void SetColor(this Texture2D iTexture, Color iColor)
    {
        var aColorArray = iTexture.GetPixels();
        for(int i = 0, aLen = aColorArray.Length; i < aLen; i++)
        {
            aColorArray[i] = iColor;
        }
        iTexture.SetPixels(aColorArray);
        iTexture.Apply();
    }
    public static void DrawPixel(this Texture2D iTexture, int iX, int iY, Color iCol)
    {
        int aWidth = iTexture.width;
        int aHeight = iTexture.height;
        
        if (iX < 0) iX = 0;
        if (iY < 0) iY = 0;
        if (iX >= aWidth) iX = aWidth - 1;
        if (iY >= aHeight) iY = aHeight - 1;
        if (iCol.a < 1f)//blend
        {
            Color aOriginCol = iTexture.GetPixel(iX, iY);
            Color aNewCol = Color.Lerp(aOriginCol, iCol, iCol.a);
            aNewCol.a = Mathf.Max(aOriginCol.a, aNewCol.a);
            iTexture.SetPixel(iX,iY, aNewCol);
        }
        else
        {
            iTexture.SetPixel(iX, iY, iCol);
        }
        //Debug.LogWarning(("Pos:") + (x + y * Width) + "Col:" + col);
    }
    public static void DrawLine(this Texture2D iTexture, Vector2Int iStartPos, Vector2Int iEndPos, Color iLineColor)
    {
        if(iStartPos == iEndPos)
        {
            return;
        }
        //Debug.LogError("iStartPos:" + iStartPos + ",iEndPos:" + iEndPos);
        if (iStartPos.x < 0) iStartPos.x = 0;
        if (iStartPos.x >= iTexture.width) iStartPos.x = iTexture.width - 1;
        if (iStartPos.y < 0) iStartPos.y = 0;
        if (iStartPos.y >= iTexture.height) iStartPos.y = iTexture.height - 1;

        if (iEndPos.x < 0) iEndPos.x = 0;
        if (iEndPos.x >= iTexture.width) iEndPos.x = iTexture.width - 1;
        if (iEndPos.y < 0) iEndPos.y = 0;
        if (iEndPos.y >= iTexture.height) iEndPos.y = iTexture.height - 1;
        var aDel = iEndPos - iStartPos;
        int aX = iStartPos.x;
        int aY = iStartPos.y;
        int aWidth = Mathf.Abs(aDel.x);
        int aHeight = Mathf.Abs(aDel.y);
        if (aDel.x == 0)
        {
            if(aDel.y > 0)
            {
                for (int i = 0; i < aHeight; i++)
                {
                    iTexture.DrawPixel(aX, aY + i, iLineColor);
                }
            }
            else
            {
                for (int i = 0; i < aHeight; i++)
                {
                    iTexture.DrawPixel(aX, aY - i, iLineColor);
                }
            }
        }
        else if(aDel.y == 0)
        {
            if(aDel.x > 0)
            {
                for (int i = 0; i < aWidth; i++)
                {
                    iTexture.DrawPixel(aX + i, aY, iLineColor);
                }
            }
            else
            {
                for (int i = 0; i < aWidth; i++)
                {
                    iTexture.DrawPixel(aX - i, aY, iLineColor);
                }
            }
        }
        else
        {

            int aDirX = aDel.x > 0 ? 1 : -1;
            int aDirY = aDel.y > 0 ? 1 : -1;
            if (aWidth >= aHeight)
            {
                int aPrevY = aY;
                float aSeg = 1.0f / aWidth;
                for (int i = 0; i < aWidth; i++)
                {
                    int aCurY = aY + Mathf.RoundToInt(i * aSeg * aDel.y);
                    int aCurX = aX + i * aDirX;

                    int aSY = Mathf.Min(aCurY, aPrevY);
                    int aEY = Mathf.Max(aCurY, aPrevY);
                    for (int j = aSY; j <= aEY; j++)
                    {
                        iTexture.DrawPixel(aCurX, j, iLineColor);
                    }
                    aPrevY = aCurY;
                }
            }
            else
            {
                int aPrevX = aX;
                float aSeg = 1.0f / aHeight;
                for (int i = 0; i < aHeight; i++)
                {
                    int aCurY = aY + i * aDirY;
                    int aCurX = aX + Mathf.RoundToInt(i * aSeg * aDel.x);
                    int aSX = Mathf.Min(aCurX, aPrevX);
                    int aEX = Mathf.Max(aCurX, aPrevX);
                    for (int j = aSX; j <= aEX; j++)
                    {
                        iTexture.DrawPixel(j, aCurY, iLineColor);
                    }
                    aPrevX = aCurX;
                }
            }
        }
        iTexture.Apply();
    }
    #endregion
}
