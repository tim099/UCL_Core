using Cysharp.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.JsonLib;
using UCL.Core.UI;
using UnityEngine;


namespace UCL.Core
{
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Editor)]
    [UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditEditorType.UCL_ImageEditor)]
    public class UCL_ImageEditor : UCL_Asset<UCL_ImageEditor>
    {
        public class CompressImageSetting : UnityJsonSerializable, UCLI_FieldOnGUI
        {
            public static float s_CompressProgress = -1;
            public static bool Processing { get; private set; } = false;

            public string m_InputFolder = string.Empty;
            public string m_OutputFolder = string.Empty;
            public float m_DownScaleRate = 0.5f;

            public int m_AddPixelToLeft = 0;
            public int m_AddPixelToRight = 0;
            public int m_AddPixelToUp = 0;
            public int m_AddPixelToDown = 0;
            /// <summary>
            /// 多少alpha值以下判定為透明
            /// </summary>
            [UCL.Core.PA.UCL_Slider(0f, 1.0f)]
            public float m_AlpahClipThreshold = 0f;
            public List<string> GetAllImageNames()
            {
                var aFileDatas = UCL.Core.FileLib.Lib.GetFilesName(m_InputFolder, "*");
                List<string> aIconPaths = new List<string>() { string.Empty };//可選空的
                aIconPaths.Append(aFileDatas);
                return aIconPaths;
            }
            [UCL.Core.PA.UCL_List(nameof(GetAllImageNames))]
            public string m_FlipImageName = string.Empty;
            public object OnGUI(string iFieldName, UCL_ObjectDictionary iDataDic, UCL_GUILayout.DrawObjectParams iParams)
            {
                var aDic = iDataDic.GetSubDic("RCG_CompressImageSetting");
                UCL.Core.UI.UCL_GUILayout.DrawField(this, aDic, iFieldName, false);
                if (aDic.GetData(UCL_GUILayout.IsShowFieldKey, false))
                {
                    if (Processing)
                    {
                        GUILayout.Label($"Processing, Progress: {(100.0f * s_CompressProgress).ToString("0.0")} %", UCL_GUIStyle.LabelStyle);
                    }
                    else
                    {
                        if (Directory.Exists(m_InputFolder))
                        {
                            if (GUILayout.Button("Compress images", UCL_GUIStyle.ButtonStyle))
                            {
                                CompressImageAsync(m_InputFolder, m_OutputFolder).Forget();
                            }
                            if (GUILayout.Button("Clip images alpha", UCL_GUIStyle.ButtonStyle))
                            {
                                ClipImageAlphaAsync(m_InputFolder, m_AlpahClipThreshold).Forget();
                            }
                            if (!string.IsNullOrEmpty(m_FlipImageName))
                            {
                                if (GUILayout.Button("Flip Image", UCL_GUIStyle.ButtonStyle))
                                {
                                    FlipImageAsync(m_InputFolder, m_OutputFolder, m_FlipImageName).Forget();
                                }
                            }
                            if (GUILayout.Button("Flip All Image", UCL_GUIStyle.ButtonStyle))
                            {
                                var files = UCL.Core.FileLib.Lib.GetFilesName(m_InputFolder);
                                if (!files.IsNullOrEmpty())
                                {
                                    FlipAll();
                                    async void FlipAll()
                                    {
                                        //Debug.LogError($"files:{files.ConcatToString()}");
                                        foreach (var fileName in files)
                                        {
                                            //Debug.LogError($"Flip:{fileName}");
                                            await FlipImageAsync(m_InputFolder, m_OutputFolder, fileName);
                                        }
                                    }


                                }
                            }
                        }
                    }


                }
                return this;
            }
            public async UniTaskVoid CompressImageAsync(string iInputFolder, string iOutputFolder)
            {
                if (Processing)
                {
                    return;
                }
                Processing = true;
                s_CompressProgress = 0f;
                if (!Directory.Exists(iInputFolder))
                {
                    Debug.LogError($"CompressImageAsync() !Directory.Exists(m_InputFolder) m_InputFolder:{iInputFolder}");
                    return;
                }
                if (!Directory.Exists(iOutputFolder))
                {
                    UCL.Core.FileLib.Lib.CreateDirectory(iOutputFolder);
                }

                var aFiles = UCL.Core.FileLib.Lib.GetFilesName(iInputFolder, "*.png");
                List<UniTask> aTasks = new List<UniTask>();
                foreach (var aFileName in aFiles)
                {
                    try
                    {
                        var aPath = Path.Combine(iInputFolder, aFileName);
                        if (!File.Exists(aPath))
                        {
                            Debug.LogError($"LoadImage() File.Exists(aPath) aPath:{aPath}");
                            continue;
                        }
                        var aBytes = File.ReadAllBytes(aPath);
                        var texture = UCL.Core.TextureLib.Lib.CreateTexture(aBytes);
                        int width = Mathf.RoundToInt(texture.width * m_DownScaleRate);
                        int height = Mathf.RoundToInt(texture.height * m_DownScaleRate);
                        if (width < 1) width = 1;
                        if (height < 1) height = 1;

                        int newWidth = width + m_AddPixelToLeft + m_AddPixelToRight;
                        int newHeight = height + m_AddPixelToUp + m_AddPixelToDown;

                        var resizeTexture = new Texture2D(newWidth, newHeight, texture.format, false);

                        float w_mult = 1.0f / width;
                        float h_mult = 1.0f / height;
                        var cols = new Color[newWidth * newHeight];
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                int rX = x + m_AddPixelToLeft;
                                int rY = y + m_AddPixelToDown;
                                var col = texture.GetPixelBilinear((x + 0.5f) * w_mult, (y + 0.5f) * h_mult);
                                //resizeTexture.SetPixel(rX, rY, col);

                                cols[rX + rY * newWidth] = col;
                            }
                        }
                        resizeTexture.SetPixels(cols);
                        //resizeTexture.Apply();

                        //var resizeTexture = texture.CreateResizeTexture(width, height);

                        var aOutputBytes = resizeTexture.EncodeToPNG();
                        var aOutputPath = Path.Combine(iOutputFolder, aFileName);
                        aTasks.Add(File.WriteAllBytesAsync(aOutputPath, aOutputBytes).AsUniTask());
                        GameObject.DestroyImmediate(texture);
                        GameObject.DestroyImmediate(resizeTexture);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }

                await UniTask.WhenAll(aTasks);

                s_CompressProgress = -1;
                Processing = false;
            }
            public static async UniTaskVoid ClipImageAlphaAsync(string iInputFolder, float iAlpahClipThreshold)
            {
                if (Processing)
                {
                    return;
                }
                Processing = true;
                s_CompressProgress = 0f;
                if (!Directory.Exists(iInputFolder))
                {
                    Debug.LogError($"ClipImageAlphaAsync() !Directory.Exists(m_InputFolder) m_InputFolder:{iInputFolder}");
                    return;
                }

                var aFiles = Directory.GetFiles(iInputFolder, "*.png", SearchOption.AllDirectories);
                //var aFiles = UCL.Core.FileLib.Lib.GetFilesName(iInputFolder, "*.png", SearchOption.AllDirectories);
                List<UniTask> aTasks = new List<UniTask>();
                foreach (var aFileName in aFiles)
                {
                    try
                    {
                        //var aPath = Path.Combine(iInputFolder, aFileName);
                        var aPath = aFileName;//改用Directory.GetFiles 直接獲取完整路徑
                        if (!File.Exists(aPath))
                        {
                            Debug.LogError($"LoadImage() File.Exists(aPath) aPath:{aPath}");
                            continue;
                        }
                        var aBytes = File.ReadAllBytes(aPath);
                        var aTexture = UCL.Core.TextureLib.Lib.CreateTexture(aBytes);
                        Color[] pixels = aTexture.GetPixels();
                        bool aFlag = false;//至少要有一個pixel變動才存檔
                        for (int i = 0; i < pixels.Length; i++)
                        {
                            float alpha = pixels[i].a;
                            if (alpha <= iAlpahClipThreshold || Mathf.Approximately(alpha, iAlpahClipThreshold))
                            {
                                pixels[i] = Color.clear;
                                aFlag = true;
                            }
                        }
                        if (aFlag)
                        {
                            aTexture.SetPixels(pixels);
                            var aOutputBytes = aTexture.EncodeToPNG();
                            aTasks.Add(File.WriteAllBytesAsync(aPath, aOutputBytes).AsUniTask());
                        }

                        GameObject.DestroyImmediate(aTexture);

                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }

                await UniTask.WhenAll(aTasks);

                s_CompressProgress = -1;
                Processing = false;
            }
            public static async UniTask FlipImageAsync(string iInputFolder, string iOutputFolder, string iFlipImageName)
            {
                if (Processing)
                {
                    return;
                }
                Processing = true;
                s_CompressProgress = 0f;
                if (!Directory.Exists(iInputFolder))
                {
                    Debug.LogError($"CompressImageAsync() !Directory.Exists(m_InputFolder) m_InputFolder:{iInputFolder}");
                    return;
                }
                if (!Directory.Exists(iOutputFolder))
                {
                    UCL.Core.FileLib.Lib.CreateDirectory(iOutputFolder);
                }

                //var aFiles = Path.Combine(iInputFolder, iFlipImageName);
                //List<UniTask> aTasks = new List<UniTask>();
                //foreach (var aFileName in aFiles)
                {
                    try
                    {
                        var aPath = Path.Combine(iInputFolder, iFlipImageName);
                        if (!File.Exists(aPath))
                        {
                            Debug.LogError($"FlipImageAsync() File.Exists(aPath) aPath:{aPath}");
                        }
                        var aBytes = File.ReadAllBytes(aPath);
                        var aTexture = UCL.Core.TextureLib.Lib.CreateTexture(aBytes, true);

                        var aOutputBytes = aTexture.EncodeToPNG();
                        var aOutputPath = Path.Combine(iOutputFolder, iFlipImageName);
                        await File.WriteAllBytesAsync(aOutputPath, aOutputBytes);
                        GameObject.DestroyImmediate(aTexture);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }

                s_CompressProgress = -1;
                Processing = false;
            }
        }


        public CompressImageSetting m_CompressImageSetting = new();
    }
}