
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/21 2024 10:36

using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace UCL.Core
{
    public static partial class UCL_StreamingAssets
    {
        public const string ReflectKeyStreamingAssetsPath = "StreamingAssetsPath";
        /// <summary>
        /// Path to RoolFolder of streamingAssetsPath
        /// 根目錄位置
        /// </summary>
        public static string StreamingAssetsPath
        {
            get
            {
                if(s_StreamingAssetsPath == null)
                {
                    //if (Application.platform == RuntimePlatform.WindowsEditor)//Editor環境不安裝 直接抓Application.streamingAssetsPath
                    //{
                    //    s_StreamingAssetsPath = Application.streamingAssetsPath;
                    //}
                    //else//其他平台安裝到 Application.dataPath/Install
                    //{
                    //    s_StreamingAssetsPath = Application.streamingAssetsPath;//暫時沒有安裝功能 先用舊的
                    //    //s_StreamingAssetsPath = Application.dataPath + "/Install";
                    //}

                    s_StreamingAssetsPath = Application.streamingAssetsPath;
                }

                return s_StreamingAssetsPath;
            }
        }
        static string s_StreamingAssetsPath = null;

        public static async Task<string> LoadString(string iPath)
        {
            try
            {
                var aLoadingRequest = UnityWebRequest.Get(Path.Combine(Application.streamingAssetsPath, iPath));
                var aOperation = aLoadingRequest.SendWebRequest();

                var aResult = await aOperation;

                if (aResult.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"ATS_StreamingAssets.LoadString Fail,iPath:{iPath},result:{aResult.result}");
                    return string.Empty;
                }

                return aLoadingRequest.downloadHandler.text;
            }
            catch(Exception e)
            {
                Debug.LogException(e);
                Debug.LogError($"UCL_StreamingAssets.LoadString iPath:{iPath},Exception:{e}");
            }
            return string.Empty;
        }
        public static async Task<byte[]> LoadBytes(string iPath)
        {

            try
            {
                var aLoadingRequest = UnityWebRequest.Get(Path.Combine(Application.streamingAssetsPath, iPath));
                var aOperation = aLoadingRequest.SendWebRequest();
                await aOperation;

                var aResult = await aOperation;

                if (aResult.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"ATS_StreamingAssets.LoadBytes Fail,iPath:{iPath},result:{aResult.result}");
                    return Array.Empty<byte>();
                }

                return aLoadingRequest.downloadHandler.data;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Debug.LogError($"UCL_StreamingAssets.LoadBytes iPath:{iPath},Exception:{e}");
            }

            return Array.Empty<byte>();
        }

        public static string GetFileSystemPath(string iPath)
        {
            return Path.Combine(Application.streamingAssetsPath, iPath);
        }

        public static string ReadAllText(string iPath)
        {
            return File.ReadAllText(GetFileSystemPath(iPath));
            //return BetterStreamingAssets.ReadAllText(iPath);
        }

        public static byte[] ReadAllBytes(string iPath)
        {
            var aPath = GetFileSystemPath(iPath);
            if (!File.Exists(aPath))
            {
                Debug.LogError("ReadAllBytes !File.Exists aPath:" + aPath);
                return null;
            }
            return File.ReadAllBytes(aPath);
            //return BetterStreamingAssets.ReadAllBytes(iPath);
        }
        public static async Task<byte[]> ReadAllBytesAsync(string iPath, CancellationToken iToken = default)
        {
            var aPath = GetFileSystemPath(iPath);
            if (!File.Exists(aPath))
            {
                Debug.LogError("ReadAllBytes !File.Exists aPath:" + aPath);
                return null;
            }
            return await File.ReadAllBytesAsync(aPath, iToken);
        }
        //public static async UniTask<byte[]> ReadAllBytesAsyncThreaded(string iPath, CancellationToken iToken = default)
        //{
        //    bool aLoaded = false;
        //    byte[] aBytes = null;
        //    System.Threading.ThreadPool.QueueUserWorkItem(o =>
        //    {
        //        try
        //        {
        //            aBytes = UCL_StreamingAssets.ReadAllBytes(iPath);
        //        }
        //        catch (System.Exception e)
        //        {
        //            Debug.LogException(e);
        //        }
        //        finally
        //        {
        //            aLoaded = true;
        //        }
        //    });
        //    await UniTask.WaitUntil(() => aLoaded, cancellationToken: iToken);
        //    iToken.ThrowIfCancellationRequested();
        //    return aBytes;
        //}
        public static bool DirectoryExists(string iPath)
        {
            return Directory.Exists(GetFileSystemPath(iPath));
        }
        /// <summary>
        /// Create Directory if not exist
        /// </summary>
        /// <param name="iPath"></param>
        public static void CheckAndCreateDirectory(string iPath)
        {
            string aPath = GetFileSystemPath(iPath);
            if (!Directory.Exists(aPath))
            {
                Directory.CreateDirectory(aPath);
            }
        }

        public static List<string> GetFilesName(string iPath, string iSearchPattern, bool iIsRemoveExtension = true)
        {
            if (!DirectoryExists(iPath))
            {
                return new List<string>();
            }
            var aFolderPath = GetFileSystemPath(iPath);
            var aFiles = Directory.GetFiles(aFolderPath, iSearchPattern, SearchOption.TopDirectoryOnly);
            var aFileNames = new List<string>();
            if (aFiles != null)
            {
                int aDiscardLen = aFolderPath.Length + 1;
                for (int i = 0; i < aFiles.Length; i++)
                {
                    var aPath = aFiles[i];
                    if(aPath.Substring(aPath.Length - 5, 5) != ".meta")
                    {
                        string aFileName = aPath.Substring(aDiscardLen, aPath.Length - aDiscardLen);
                        if(iIsRemoveExtension) aFileName = UCL.Core.FileLib.Lib.RemoveFileExtension(aFileName);
                        aFileNames.Add(aFileName);
                    }
                }
            }
            return aFileNames;
            //return BetterStreamingAssets.GetFiles(iPath, iSearchPattern, iSearchOption);
        }
    }
}

