
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 11/27 2024
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UnityEngine;
using UnityEngine.UI;
namespace UCL.Core
{
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
    [UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditDataType.UCL_ModResourceAsset)]
    public class UCL_ModResourceAsset : UCL_Asset<UCL_ModResourceAsset>, IDisposable
    {

        public UCL_ModResourcesData m_ModResourcesData = new UCL_ModResourcesData();



        public bool IsEmpty => Data.IsEmpty;
        private UCL_Data Data => m_ModResourcesData;

        public async UniTask<Sprite> GetSpriteAsync(CancellationToken iToken)
        {
            await Data.LoadAsync(iToken);
            return Data.GetSprite();
        }
        public async UniTask<Texture2D> GetTextureAsync(CancellationToken iToken)
        {
            await Data.LoadAsync(iToken);
            iToken.ThrowIfCancellationRequested();
            return Data.GetSprite().texture;
        }

        //public override UCL_ModResourceAsset CreateData(string iID)
        //{
        //    var aConfig = GetAssetConfig(iID);
        //    if (!aConfig.Exist)
        //    {
        //        string log = $"CreateData Type:{nameof(UCL_ModResourceAsset)}, ID:{iID}, !Config.Exist";
        //        Debug.LogError(log);
        //        //return null;
        //        throw new Exception(log);
        //    }

        //    var aData = new UCL_ModResourceAsset();
        //    UCLI_Asset.s_CurCreateData = aData;

        //    try
        //    {
        //        aData.ID = iID;
        //        aData.DeserializeFromJson(aConfig.GetJsonData());
        //        var module = aConfig.p_Module;
        //        if (module != null)
        //        {
        //            aData.m_ModResourcesData.m_ModuleID = module.ID;//Set ModuleID!!
        //        }
        //    }
        //    catch (Exception e)
        //    {
        //        Debug.LogException(e);
        //        throw e;
        //    }
        //    finally
        //    {
        //        UCLI_Asset.s_CurCreateData = null;
        //    }

            
        //    return aData;
        //}

        /// <summary>
        /// Preview(OnGUI)
        /// </summary>
        /// <param name="iIsShowEditButton">Show edit button in preview window?</param>
        override public void Preview(UCL.Core.UCL_ObjectDictionary iDataDic, bool iIsShowEditButton = false)
        {
            //GUILayout.BeginHorizontal();
            using (var aScope = new GUILayout.VerticalScope("box", GUILayout.ExpandWidth(false)))
            {

                GUILayout.Label($"{UCL_LocalizeManager.Get("Preview")}({ID})", UCL.Core.UI.UCL_GUIStyle.LabelStyle);
                
                if (iIsShowEditButton)
                {
                    ShowEditButtonOnGUI();
                }
            }
            //GUILayout.EndHorizontal();
        }
        public UCL_ModResourceAsset()
        {
            ID = "New SpriteAsset";
        }

        public void Dispose()
        {
            Data.Release();
            //if (m_Sprite != null)
            //{
            //    Data.Release(m_Sprite);
            //    //GameObject.Destroy(m_Sprite);
            //    m_Sprite = null;
            //}
        }
        public void Init(string iPath, string iName)
        {
            m_ModResourcesData.m_FolderPath = iPath;
            m_ModResourcesData.m_FileName = iName;
        }

    }

    [System.Serializable]
    public class UCL_ModResourceEntry : UCL_AssetEntryDefault<UCL_ModResourceAsset>
    {
        public const string DefaultID = "Default";
        public UCL_ModResourceEntry() { m_ID = DefaultID; }
        public UCL_ModResourceEntry(string iID) { m_ID = iID; }

        public UCL_ModResourcesData Data => GetData().m_ModResourcesData;
        public override bool IsEmpty 
        {
            get
            {
                if (base.IsEmpty) return true;
                try
                {
                    var data = GetData();
                    return data.IsEmpty;
                }
                catch //Data not exist!!
                {
                    return true;
                }
            }
        }
    }
}