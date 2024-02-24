
// AutoHeader
// to change the auto header please go to AutoHeader.cs
// Create time : 09/25 2023
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.AddressableAssets;
//using UnityEngine.ResourceManagement.AsyncOperations;

namespace UCL.Core
{
    /// <summary>
    /// https://docs.unity3d.com/Packages/com.unity.addressables@1.3/manual/MemoryManagement.html
    /// </summary>
    [System.Serializable]
    public class UCL_AddressableData : UCL_Data
    {
        public class LoadedAddressable : IDisposable
        {
            public UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle m_Handle;
            public UnityEngine.Object m_Object;
            public bool m_Loaded = false;
            public void Dispose()
            {
                Addressables.Release(m_Handle);
            }
        }
        #region static
        public static void ReleaseAll()
        {
            foreach (var aKey in s_LoadedDic.Keys)
            {
                var aData = s_LoadedDic[aKey];
                aData.Dispose();
            }
            s_LoadedDic.Clear();
        }
        #endregion


        virtual protected List<string> GetAddressablePath() => UCL_Addressable.GetAddressablePath();

        [UCL.Core.PA.UCL_List("GetAddressablePath")]
        public string m_AddressablePath = string.Empty;

        virtual public List<string> GetAddressableKeys() => UCL_Addressable.GetAllAddressableKeys(m_AddressablePath);

        [UCL.Core.PA.UCL_List("GetAddressableKeys")]
        public string m_AddressableKey = string.Empty;

        /// <summary>
        /// 是否是空的(不考慮檔案格式不符)
        /// </summary>
        public bool IsEmpty => string.IsNullOrEmpty(m_AddressableKey);
        public string Key => m_AddressableKey;
        public string FileName => UCL.Core.FileLib.Lib.GetFileName(m_AddressableKey);
        private static Dictionary<string, LoadedAddressable> s_LoadedDic = new();
        //virtual public async UniTask<UnityEngine.Object> LoadAsync(CancellationToken iToken)
        //{
        //    var aObj = await Addressables.LoadAssetAsync<UnityEngine.Object>(m_AddressableKey);
        //    iToken.ThrowIfCancellationRequested();
        //    return aObj;
        //}
        virtual public async UniTask<UnityEngine.Object> LoadAsync(CancellationToken iToken)
        {
            return await LoadAsync<UnityEngine.Object>(iToken);
        }
        virtual public async UniTask<T> LoadComponentAsync<T>(CancellationToken iToken) where T : UnityEngine.Component
        {
            if (string.IsNullOrEmpty(m_AddressableKey)) return null;

            LoadedAddressable aData = await GetLoadedAddressable<GameObject>(iToken);


            if (aData.m_Object == null)
            {
                Debug.LogError($"UCL_AddressableData.LoadComponentAsync, aData.m_Object == null,m_AddressableKey:{m_AddressableKey}");
                return null;
            }

            GameObject aGameObject = aData.m_Object as GameObject;
            if (aGameObject == null)
            {
                Debug.LogError($"UCL_AddressableData.LoadAsync aGameObject == null,m_AddressableKey:{m_AddressableKey}");
                return null;
            }
            T aComponent = aGameObject.GetComponent<T>();
            if (aComponent == null)
            {
                Debug.LogError($"UCL_AddressableData.LoadAsync,aGameObject:{aGameObject.name},Component({typeof(T).FullName}) == null,m_AddressableKey:{m_AddressableKey}");
                return null;
            }
            return aComponent;
        }

        virtual public async UniTask<T> LoadAsync<T>(CancellationToken iToken) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(m_AddressableKey)) return null;

            LoadedAddressable aData = await GetLoadedAddressable<T>(iToken);
            var aObj = aData.m_Object;
            if (aObj == null)
            {
                Debug.LogError($"UCL_AddressableData.LoadAsync aData.m_Object == null,m_AddressableKey:{m_AddressableKey}");
                return null;
            }
            T aResult = aObj as T;
            if(aResult == null)//Type mismatch
            {
                Debug.LogError($"UCL_AddressableData.LoadAsync aObj.GetType():{aObj.GetType().FullName},TargetType:{typeof(T).FullName},m_AddressableKey:{m_AddressableKey}");
                return null;
            }
            return aResult;
        }
        private async UniTask<LoadedAddressable> GetLoadedAddressable<T>(CancellationToken iToken) where T : UnityEngine.Object
        {
            if (!s_LoadedDic.ContainsKey(m_AddressableKey))
            {
                var aHandle = Addressables.LoadAssetAsync<T>(m_AddressableKey);
                UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle aAsyncOperationHandle = aHandle;

                LoadedAddressable aData = new LoadedAddressable();
                aData.m_Handle = aAsyncOperationHandle;
                s_LoadedDic[m_AddressableKey] = aData;

                aData.m_Object = await aHandle;
                aData.m_Loaded = true;
            }
            {
                var aData = s_LoadedDic[m_AddressableKey];
                if(!aData.m_Loaded)
                {
                    await UniTask.WaitUntil(() => aData.m_Loaded, cancellationToken: iToken);
                }
                return aData;
            }
        }


    }
}
