
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
            public List<UnityEngine.Object> m_CreatedAssets = new List<UnityEngine.Object>();
            public bool m_Loaded = false;
            private Sprite m_Sprite = null;
            public void Dispose()
            {
                if (!m_CreatedAssets.IsNullOrEmpty())
                {
                    foreach(var aObj in m_CreatedAssets)
                    {
                        if(aObj != null)
                        {
                            UnityEngine.Object.Destroy(aObj);
                        }
                    }
                }
                Addressables.Release(m_Handle);
            }

            public Sprite Sprite
            {
                get
                {
                    if (!m_Loaded || m_Object == null)
                    {
                        return null;
                    }
                    if (m_Sprite == null)
                    {
                        if (m_Object is Sprite aSprite)
                        {
                            m_Sprite = aSprite;
                        }
                        else if (m_Object is Texture2D aTexture)
                        {
                            m_Sprite = UCL.Core.TextureLib.Lib.CreateSprite(aTexture);
                            m_CreatedAssets.Add(m_Sprite);
                        }
                        else
                        {
                            //Type Error!!
                            Debug.LogError($"UCL_AddressableData m_Object.Type:{m_Object.GetType().FullName},Not Sprite!!");
                        }
                    }



                    return m_Sprite;
                }
            }
        }
        #region static
        /// <summary>
        /// Release all loaded addressables
        /// </summary>
        public static void ReleaseAll()
        {
            foreach (var aKey in s_LoadedDic.Keys)
            {
                var aData = s_LoadedDic[aKey];
                aData.Dispose();
            }
            s_LoadedDic.Clear();
        }
        public static void Release(string aKey)
        {
            if (s_LoadedDic.ContainsKey(aKey))
            {
                s_LoadedDic[aKey].Dispose();
                s_LoadedDic.Remove(aKey);
            }
        }
        private static Dictionary<string, LoadedAddressable> s_LoadedDic = new();
        #endregion


        virtual protected List<string> GetAddressablePath() => UCL_Addressable.GetAddressablePath();

        [UCL.Core.PA.UCL_List("GetAddressablePath")]
        public string m_AddressablePath = string.Empty;

        virtual public List<string> GetAddressableKeys() => UCL_Addressable.GetAllAddressableKeys(m_AddressablePath);

        [UCL.Core.PA.UCL_List("GetAddressableKeys")]
        public string m_AddressableKey = string.Empty;


        #region interface
        /// <summary>
        /// Is the Empty(Null)
        /// 是否是空的(不考慮檔案格式不符)
        /// </summary>
        public override bool IsEmpty => string.IsNullOrEmpty(m_AddressableKey);
        public override string Key => m_AddressableKey;

        public override string Name => UCL.Core.FileLib.Lib.GetFileName(m_AddressableKey);
        public override async UniTask<UnityEngine.Object> LoadAsync(CancellationToken iToken)
        {
            return await LoadAsync<UnityEngine.Object>(iToken);
        }
        public override Sprite GetSprite()
        {
            var aLoadedAddressable = GetLoadedAddressable();
            if (aLoadedAddressable == null)//Not Loaded yet start loading!
            {
                LoadSpriteAsync(default).Forget();
                return null;
            }

            return aLoadedAddressable.Sprite;
        }
        public override async UniTask<Sprite> LoadSpriteAsync(CancellationToken iToken)
        {
            await LoadAsync<UnityEngine.Object>(iToken);
            var aLoadedAddressable = GetLoadedAddressable();
            if(aLoadedAddressable == null)
            {
                Debug.LogError($"UCL_AddressableData.LoadSpriteAsync aLoadedAddressable == null,m_AddressableKey:{m_AddressableKey}");
                return null;
            }
            return aLoadedAddressable.Sprite;
        }
        #endregion

        //virtual public async UniTask<UnityEngine.Object> LoadAsync(CancellationToken iToken)
        //{
        //    var aObj = await Addressables.LoadAssetAsync<UnityEngine.Object>(m_AddressableKey);
        //    iToken.ThrowIfCancellationRequested();
        //    return aObj;
        //}

        private async UniTask<T> LoadAsync<T>(CancellationToken iToken) where T : UnityEngine.Object
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
        private bool CheckLoaded() => s_LoadedDic.ContainsKey(m_AddressableKey);
        private LoadedAddressable GetLoadedAddressable()
        {
            if (!s_LoadedDic.ContainsKey(m_AddressableKey))
            {
                return null;
            }
            return s_LoadedDic[m_AddressableKey];
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
        /// <summary>
        /// Release Object load from UCL_Data
        /// </summary>
        /// <param name=""></param>
        public override void Release()
        {
            Release(m_AddressableKey);
        }

    }
}
