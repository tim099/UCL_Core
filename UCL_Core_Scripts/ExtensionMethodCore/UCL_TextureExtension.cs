using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static partial class TextureExtensionMethods {
    #region RenderTexture
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
    #endregion
}
