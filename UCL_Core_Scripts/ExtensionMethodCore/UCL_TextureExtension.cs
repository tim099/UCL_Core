using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static partial class TextureExtensionMethods {
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
