
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 12/11 2024
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;
using UnityEngine.Networking;

namespace UCL.Core
{
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    public static class UCL_ModResourcesService
    {
        public enum DataType
        {
            Default = 0,
            Sprite,
        }

        private static Dictionary<string, LoadedData> s_LoadedDatas = new Dictionary<string, LoadedData>();
        public static void OnGUI(UCL_ObjectDictionary iDataDic)
        {
            if (GUILayout.Button(UCL_LocalizeManager.Get("Release All"), UCL_GUIStyle.ButtonStyle))
            {
                ReleaseAll();
            }
            using (var aScope = new GUILayout.VerticalScope())
            {
                foreach (var aKey in s_LoadedDatas.Keys.ToList())
                {
                    GUILayout.BeginHorizontal();
                    bool aRelease = false;
                    if (GUILayout.Button(UCL_LocalizeManager.Get("Release"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        aRelease = true;
                    }
                    var aData = s_LoadedDatas[aKey];
                    GUILayout.Label($"{aKey}", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    aData.OnGUI();
                    if (aRelease)
                    {
                        Release(aKey);
                    }
                    GUILayout.EndHorizontal();
                }
            }

        }
        #region LoadedData
        public class LoadedData : IDisposable
        {
            public string m_FilePath;

            public List<UnityEngine.Object> m_CreatedAssets = new List<UnityEngine.Object>();
            virtual public bool IsValid => true;
            virtual public DataType DataType => DataType.Default;
            virtual public UniTask InitAsync(string iPath)
            {
                throw new System.NotImplementedException();
            }
            virtual public void Init(string iPath)
            {
                m_FilePath = iPath;
            }
            virtual public void Dispose()
            {
                if (Application.isPlaying)
                {
                    foreach (var aAsset in m_CreatedAssets)
                    {
                        if (aAsset != null)
                        {
                            UnityEngine.Object.Destroy(aAsset);
                        }
                    }
                }
                else
                {
                    foreach (var aAsset in m_CreatedAssets)
                    {
                        if (aAsset != null)
                        {
                            UnityEngine.Object.DestroyImmediate(aAsset);
                        }
                    }
                }

                m_CreatedAssets.Clear();
            }

            virtual public void OnGUI()
            {
                GUILayout.Label($"{GetType().FullName}", UCL_GUIStyle.LabelStyle);
            }
        }
        public class LoadedSpriteData : LoadedData
        {
            const int TextureIndex = 0;
            const int SpriteIndex = TextureIndex + 1;
            public bool IsLoading = true;

            override public DataType DataType => DataType.Sprite;
            override public bool IsValid 
            {
                get
                {
                    if(IsLoading) return true;
                    if (m_CreatedAssets.Count == 0) return true;//null texture
                    return Texture2D != null;//Texture destroyed or not
                }
            }
            override public void Init(string iPath)
            {
                //Debug.LogError($"Init:{iPath}");
                base.Init(iPath);
                if (!File.Exists(iPath))
                {
                    Debug.LogError($"LoadedSpriteData.Init !File.Exists,iPath:{iPath}");
                    return;
                }

                var aBytes = File.ReadAllBytes(iPath);
                Texture2D aTexture = UCL.Core.TextureLib.Lib.CreateTexture(aBytes);
                if(aTexture != null)
                {
                    m_CreatedAssets.Add(aTexture);
                    Sprite aSprite = UCL.Core.TextureLib.Lib.CreateSprite(aTexture);
                    m_CreatedAssets.Add(aSprite);
                }

                IsLoading = false;
            }
            public async UniTask InitAsync(string iPath, UCLI_LoadTextureConfig config)
            {
                //Debug.LogError($"InitAsync:{iPath}");
                base.Init(iPath);
                Texture2D aTexture = await UCL.Core.TextureLib.Lib.LoadTextureFromFile(iPath, config);

                if(aTexture != null)
                {
                    if (config != null)
                    {
                        aTexture.filterMode = config.FilterMode;
                    }
                    m_CreatedAssets.Add(aTexture);
                }

                //Sprite aSprite = UCL.Core.TextureLib.Lib.CreateSprite(aTexture);
                //m_CreatedAssets.Add(aSprite);
                IsLoading = false;
            }
            public Sprite Sprite
            {
                get
                {
                    if (m_CreatedAssets.Count <= TextureIndex)
                    {
                        return null;
                    }
                    if (m_CreatedAssets.Count <= SpriteIndex)
                    {
                        Sprite aSprite = UCL.Core.TextureLib.Lib.CreateSprite(Texture2D);
                        m_CreatedAssets.Add(aSprite);
                    }

                    return m_CreatedAssets[SpriteIndex] as Sprite;
                }
            }
            public Texture2D Texture2D
            {
                get
                {
                    if (m_CreatedAssets.Count > TextureIndex)
                    {
                        return m_CreatedAssets[TextureIndex] as Texture2D;
                    }
                    return null;
                }
            }
            public override void OnGUI()
            {
                var aTexture = Texture2D;
                if (aTexture != null)
                {
                    float aSize = UCL_GUIStyle.GetScaledSize(16);
                    GUILayout.Box(aTexture, GUILayout.Height(aSize), GUILayout.ExpandWidth(false));//GUILayout.Height((aSize * aTexture.height)/aTexture.width)
                }
                GUILayout.Label($"{GetType().FullName}", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
            }
        }
        #endregion

        public static async UniTask<LoadedSpriteData> LoadTextureAsync(string iPath, UCLI_LoadTextureConfig config)
        {
            RemoveInvalidData(iPath);
            //Debug.LogError($"LoadSpriteAsync:{iPath}");
            if (!s_LoadedDatas.ContainsKey(iPath))
            {
                try
                {
                    LoadedSpriteData aLoadedData = new LoadedSpriteData();
                    s_LoadedDatas[iPath] = aLoadedData;
                    await aLoadedData.InitAsync(iPath, config);
                }
                catch (System.Exception ex)
                {
                    Debug.LogException(ex);
                    Debug.LogError($"LoadSprite iPath:{iPath},Exception:{ex}");
                    return null;
                }
            }

            var aData = s_LoadedDatas[iPath];

            if (aData is LoadedSpriteData aSpriteData)
            {
                if (aSpriteData.IsLoading)
                {
                    await UniTask.WaitUntil(() => !aSpriteData.IsLoading);
                }
                return aSpriteData;
            }
            else
            {
                Debug.LogError($"UCL_ModResourcesService.LoadSprite aData.GetType().FullName:{aData.GetType().FullName}");
                return null;
            }
        }
        /// <summary>
        /// Remove invalid data from s_LoadedDatas
        /// </summary>
        /// <param name="iPath"></param>
        private static void RemoveInvalidData(string iPath)
        {
            if (s_LoadedDatas.TryGetValue(iPath, out var aData))
            {
                if (!aData.IsValid)
                {
                    aData.Dispose();
                    s_LoadedDatas.Remove(iPath);
                }
            }
        }
        public static Sprite LoadSprite(string iPath)
        {
            //Debug.LogError($"LoadSprite iPath:{iPath},s_LoadedDatas.Keys:{s_LoadedDatas.Keys.ConcatString()}");
            RemoveInvalidData(iPath);

            if (!s_LoadedDatas.ContainsKey(iPath))
            {
                try
                {
                    LoadedSpriteData aLoadedData = new LoadedSpriteData();
                    s_LoadedDatas[iPath] = aLoadedData;
                    aLoadedData.Init(iPath);
                }
                catch (System.Exception ex)
                {
                    Debug.LogException(ex);
                    Debug.LogError($"LoadSprite iPath:{iPath},Exception:{ex}");
                    return null;
                }
            }


            var aData = s_LoadedDatas[iPath];
            if (aData is LoadedSpriteData aSpriteData)
            {
                return aSpriteData.Sprite;
            }
            else
            {
                Debug.LogError($"UCL_ModResourcesService.LoadSprite aData.GetType().FullName:{aData.GetType().FullName}");
                return null;
            }
        }

        static UCL_ModResourcesService()
        {
            //Debug.LogError("UCL_ModResourcesService.InitializeOnLoad");
            EditorLib.EditorApplicationMapper.playModeStateChanged += (iPlayModeStateChangeMapper) =>
            {
                //Debug.LogError($"playModeStateChanged iPlayModeStateChangeMapper:{iPlayModeStateChangeMapper}");
                switch (iPlayModeStateChangeMapper)
                {
                    case EditorLib.PlayModeStateChangeMapper.ExitingPlayMode:
                        {
                            ReleaseAll();
                            break;
                        }
                }
            };
        }

        public static void ReleaseAll()
        {
            //Debug.LogError("UCL_ModResourcesService.ReleaseAll()");
            //UnityEditor.EditorApplication.playModeStateChanged
            foreach (var aData in s_LoadedDatas.Values)
            {
                aData.Dispose();
            }
            s_LoadedDatas.Clear();
        }
        public static void Release(string iKey)
        {
            if (s_LoadedDatas.ContainsKey(iKey))
            {
                s_LoadedDatas[iKey].Dispose();
                s_LoadedDatas.Remove(iKey);
            }
        }

    }
}
