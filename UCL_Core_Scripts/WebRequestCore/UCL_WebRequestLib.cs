using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace UCL.Core
{
    public static class WebRequestLib
    {
        public static IEnumerator Download(string iDownloadPath, System.Action<byte[]> DownloadCallback)
        {
            var www = UnityEngine.Networking.UnityWebRequest.Get(iDownloadPath);
            UnityWebRequestAsyncOperation request_opt = www.SendWebRequest();
            yield return request_opt;

            if (www.isNetworkError || www.isHttpError)
            {
                Debug.LogError("LoadByWebRequest Error:" + iDownloadPath + ",Error:" + www.error);
                if (DownloadCallback != null) DownloadCallback.Invoke(null);
            }
            else
            {
                var results = www.downloadHandler.data;
                if (DownloadCallback != null) DownloadCallback.Invoke(results);
            }
        }
        public static IEnumerator CheckHeaderAndDownload(string iDownloadPath, System.Action<byte[]> DownloadCallback)
        {
            long aFileSize = 0;
            ///Get Header
            using (var headRequest = UnityWebRequest.Head(iDownloadPath))
            {
                yield return headRequest.SendWebRequest();
                if (headRequest.responseCode == 200)
                {
                    var contentLength = headRequest.GetResponseHeader("CONTENT-LENGTH");
                    long.TryParse(contentLength, out aFileSize);
                }
                else
                {
                    Debug.LogError("WebRequestLoadBundle headRequest.error:" + headRequest.error);
                    yield break;
                }
            }
            Debug.LogWarning("WebRequestLoad file_size:" + aFileSize);

            var www = UnityEngine.Networking.UnityWebRequest.Get(iDownloadPath);
            UnityWebRequestAsyncOperation request_opt = www.SendWebRequest();
            yield return request_opt;

            if (www.isNetworkError || www.isHttpError)
            {
                Debug.LogError("LoadByWebRequest Error:" + iDownloadPath + ",Error:" + www.error);
            }
            else
            {
                var results = www.downloadHandler.data;
                if (DownloadCallback != null) DownloadCallback.Invoke(results);
            }
        }
    }
}

