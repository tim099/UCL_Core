
// ATS_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 02/20 2024 19:11
using System;
using System.Collections;
using System.Collections.Generic;
using UCL.Core;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;



namespace UCL.Core
{
    public interface UCLI_AssetEntry : UCLI_ID
    {
        /// <summary>
        /// Only for Asset Editor, not saved in UCL_Asset
        /// 供編輯器使用 可以選取分組 但存成Json時不保存
        /// </summary>
        string GroupID { get; set; }
        bool IsEmpty { get; }

        /// <summary>
        /// type of UCLI_Asset this UCLI_AssetEntry point to
        /// </summary>
        System.Type AssetType { get; }

        #region static

        public static System.Action<UCLI_AssetEntry> s_DeserializeAction = null;
        #endregion
    }

    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_AssetEntry<T> : UCL.Core.JsonLib.UnityJsonSerializable, UCLI_AssetEntry, UCLI_ShortName, UCLI_NameOnGUI, UCLI_FieldOnGUI
        , IEquatable<UCL_AssetEntry<T>> where T : class, UCLI_Asset, UCLI_Preview, new()
    {

        //[UCL.Core.PA.UCL_List(FuncKeyGetAllIDs)] public string m_ID = string.Empty;

        virtual public string GetShortName() => UCL.Core.LocalizeLib.UCL_LocalizeManager.Get(ID);
        virtual public string ID { get ; set ; }
        virtual public bool IsEmpty => string.IsNullOrEmpty(ID);

        public System.Type AssetType => typeof(T);
        /// <summary>
        /// 供編輯器使用 可以選取分組 但存成Json時不保存
        /// </summary>
        public string GroupID { get; set ; } = string.Empty;
        protected T Util => (UCL_Util<T>.Util);
        protected UCL_Asset<T> AssetUtil => Util as UCL_Asset<T>;
        /// <summary>
        /// 抓取道具資料(道具 裝備 卡牌等都算)
        /// </summary>
        /// <param name="iUseCache">是否使用緩存的資料 false會直接生成一份新的</param>
        /// <returns></returns>
        public T GetData(bool iUseCache = true)
        {
            try
            {
                return Util.GetAsset(ID, iUseCache) as T;
            }
            catch (Exception e)
            {
                Debug.LogError($"{nameof(T)}.GetData, ID:{ID},iUseCache:{iUseCache},Exception:{e}");
                Debug.LogException(e);
            }
            return null;
        }
        /// <summary>
        /// return true if asset exist
        /// </summary>
        /// <returns></returns>
        public bool Exist() => Util.GetAllIDs().Contains(ID);
        /// <summary>
        /// Get all ID of this UCL_Asset
        /// 抓取此類型中所有的ID
        /// </summary>
        /// <returns></returns>
        virtual public List<string> GetAllIDs(bool iUseCache = false) => Util.GetAllIDs(iUseCache);
        /// <summary>
        /// Get all ID of this UCL_Asset with cache
        /// 抓取此類型中所有的ID
        /// </summary>
        /// <returns></returns>
        virtual public List<string> GetAllIDsWithCache() => GetAllIDs(true);

        public IList<string> GetLocalizeIDs()
        {
            return GetLocalizeIDs(GetAllIDs());
        }
        public IList<string> GetLocalizeIDs(IList<string> iList)
        {
            string[] aDisplayList = new string[iList.Count];
            for (int i = 0; i < iList.Count; i++)
            {
                string aKey = iList[i];
                if (UCL.Core.LocalizeLib.UCL_LocalizeManager.ContainsKey(aKey))
                {
                    aDisplayList[i] = $"{UCL.Core.LocalizeLib.UCL_LocalizeManager.Get(aKey)}({aKey})";
                }
                else
                {
                    aDisplayList[i] = aKey;
                }
            }
            return aDisplayList;
        }
        virtual protected void SelectGroupIDOnGUI(UCL.Core.UCL_ObjectDictionary iDataDic, List<string> iGroupIDs = null)
        {
            var aUtil = AssetUtil;
            var aCommonDataMeta = aUtil.AssetMetaIns;
            List<string> aGroupIDs = null;
            if (!iGroupIDs.IsNullOrEmpty())
            {
                aGroupIDs = iGroupIDs.Clone();
                aGroupIDs.Insert(0, string.Empty);
            }
            else
            {
                var aGroups = aCommonDataMeta.m_Groups;
                if (!aGroups.IsNullOrEmpty())
                {
                    aGroupIDs = new List<string>();
                    aGroupIDs.Add(string.Empty);
                    foreach (var aGroup in aGroups.Keys)
                    {
                        aGroupIDs.Add(aGroup);
                    }
                }
            }
            if (!aGroupIDs.IsNullOrEmpty())
            {
                var aAt = UCL.Core.UI.UCL_GUILayout.PopupAuto(aGroupIDs.IndexOf(GroupID), aGroupIDs, iDataDic, "GroupID", 8, GUILayout.ExpandWidth(true));
                GroupID = aGroupIDs[aAt];
            }
        }
        public class SelectIDOnGUICache
        {
            public List<string> m_IDs = null;
            public IList<string> m_LocalizeIDs = null;
            public string m_GroupID = null;
            public System.DateTime? lastUpdateTime = null;
        }
        virtual protected void SelectIDOnGUI(UCL.Core.UCL_ObjectDictionary iDataDic)
        {
            SelectIDOnGUICache cache = null;
            void RefreshCache(SelectIDOnGUICache cache)
            {
                cache.m_IDs = GetAllIDs(true);
                cache.lastUpdateTime = System.DateTime.Now;
                if (!GroupID.IsNullOrEmpty())
                {
                    //if (cache.m_GroupID != GroupID)//Update Filter
                    {
                        cache.m_GroupID = GroupID;
                        //GUILayout.Label(GroupID);
                        var assetMeta = AssetUtil.AssetMetaIns;
                        cache.m_IDs = assetMeta.GetAllShowData(Util, cache.m_IDs, GroupID, UCL_AssetCommonMeta.PlayerPrefsData.FilterType.Dropdown);
                    }
                }

                if (!cache.m_IDs.IsNullOrEmpty())
                {
                    cache.m_LocalizeIDs = GetLocalizeIDs(cache.m_IDs);
                }
            }
            if (!iDataDic.ContainsKey(nameof(SelectIDOnGUICache)))
            {
                cache = new();
                iDataDic.Add(nameof(SelectIDOnGUICache), cache);
                RefreshCache(cache);
            }
            else
            {
                cache = iDataDic.GetData<SelectIDOnGUICache>(nameof(SelectIDOnGUICache));
            }

            if (!GroupID.IsNullOrEmpty())
            {
                if (cache.m_GroupID != GroupID)//Update Filter
                {
                    RefreshCache(cache);
                }
            }
            if((System.DateTime.Now - cache.lastUpdateTime.Value).TotalSeconds > 1.0f)//Refresh every second
            {
                RefreshCache(cache);
            }
            var aIDs = cache.m_IDs;
            var aLocalizeIDs = cache.m_LocalizeIDs;
            if (!aIDs.IsNullOrEmpty())
            {

                var aAt = UCL.Core.UI.UCL_GUILayout.PopupAuto(aIDs.IndexOf(ID), aLocalizeIDs, iDataDic, "ID", 8, GUILayout.ExpandWidth(true));
                if (aAt >= 0 && aAt < aIDs.Count)
                {
                    ID = aIDs[aAt];
                }
            }

            //var aIDs = GetAllIDs(true);

            //if (!GroupID.IsNullOrEmpty())
            //{
            //    //GUILayout.Label(GroupID);
            //    var assetMeta = AssetUtil.AssetMetaIns;
            //    aIDs = assetMeta.GetAllShowData(Util, aIDs, GroupID, UCL_AssetCommonMeta.PlayerPrefsData.FilterType.Dropdown);
            //}
            //if (!aIDs.IsNullOrEmpty())
            //{

            //    var aAt = UCL.Core.UI.UCL_GUILayout.PopupAuto(aIDs.IndexOf(ID), GetLocalizeIDs(aIDs), iDataDic, "ID", 8, GUILayout.ExpandWidth(true));
            //    if (aAt >= 0 && aAt < aIDs.Count)
            //    {
            //        ID = aIDs[aAt];
            //    }
            //}
        }
        virtual public void NameOnGUI(UCL.Core.UCL_ObjectDictionary iDataDic, string iDisplayName, UI.UCL_GUILayout.DrawObjectParams iParams)
        {
            string fieldName = iDisplayName;
            if(iParams != null)
            {
                fieldName = iParams.FieldName;
            }
            //else
            //{
            //    if(iParams != null)
            //    {
            //        fieldName = $"iParams.m_FieldInfo != null:{iParams.m_FieldInfo != null}";
            //        if(iParams.m_FieldInfo == null)
            //        {
            //            Debug.LogError($"{iDisplayName},iParams.m_FieldInfo == null");
            //        }
            //    }
            //    else
            //    {
            //        fieldName = $"iParams == null";
            //    }
                
            //}

            GUILayout.Label(fieldName, UCL.Core.UI.UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
            //篩選時採用所有模組內可選的分組ID
            //Filter whith all groupIDs in all modules
            var groupIDs = AssetUtil.AssetsCache.m_GroupIDs;
            if (!groupIDs.IsNullOrEmpty())
            {
                using (var aHorizontalScope = new GUILayout.HorizontalScope(GUILayout.MinWidth(140)))
                {
                    SelectGroupIDOnGUI(iDataDic.GetSubDic(nameof(groupIDs)), groupIDs);
                }
            }

            using (var aHorizontalScope = new GUILayout.HorizontalScope(GUILayout.MinWidth(200)))
            {
                SelectIDOnGUI(iDataDic.GetSubDic(nameof(SelectIDOnGUI)));
            }

        }
        virtual public object OnGUI(string iFieldName, UCL.Core.UCL_ObjectDictionary iDataDic, UI.UCL_GUILayout.DrawObjectParams iParams)
        {
            //UCL.Core.UI.UCL_GUILayout.DrawObjExSetting aSetting = new UCL_GUILayout.DrawObjExSetting();
            //aSetting.OnShowField = () =>
            //{
            //    using (var aVerticalScope = new GUILayout.VerticalScope())
            //    {
            //        if (!CommonDataUtil.CommonDataMetaIns.m_Groups.IsNullOrEmpty())
            //        {
            //            using (var aHorizontalScope = new GUILayout.HorizontalScope())
            //            {
            //                GUILayout.Label(UCL_LocalizeManager.Get("GroupID"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
            //                SelectGroupIDOnGUI(iDataDic);
            //            }
            //        }

            //        using (var aHorizontalScope = new GUILayout.HorizontalScope())
            //        {
            //            GUILayout.Label("ID", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
            //            SelectIDOnGUI(iDataDic);
            //        }
            //    }
            //};
            //var aParams = iParams.CreateChild(iDataDic.GetSubDic("Data"), iFieldName, iIsAlwaysShowDetail: false);
            //var aParams = new UI.UCL_GUILayout.DrawObjectParams(iDataDic.GetSubDic("Data"), iFieldName, false);
            UCL_GUILayout.DrawField(this, iParams);
            //UCL_GUILayout.DrawField(this, iDataDic.GetSubDic("Data"), iFieldName, false);//, iDrawObjExSetting : aSetting
            return this;
        }


        [UCL.Core.ATTR.UCL_DrawOnGUI]
        virtual public void Preview(UCL.Core.UCL_ObjectDictionary iDic)
        {
            GUILayout.BeginHorizontal();
            bool aIsPreview = UCL.Core.UI.UCL_GUILayout.Toggle(iDic, "PreviewToggle");
            //Debug.LogError($"aIsPreview:{aIsPreview}");
            GUILayout.Label(UCL_LocalizeManager.Get("Preview"), UCL_GUIStyle.LabelStyle);
            GUILayout.EndHorizontal();
            if (aIsPreview)
            {
                var aData = Util.GetAsset(ID);
                if (aData != null)
                {
                    aData.Preview(iDic.GetSubDic("Preview"), true);
                }
            }
        }

        public override JsonData SerializeToJson()
        {
            return new JsonData(ID);
        }
        public override void DeserializeFromJson(JsonData iJson)
        {
            if(iJson.IsString) ID = iJson.GetString();
            else
            {
                base.DeserializeFromJson(iJson);
            }
            UCLI_AssetEntry.s_DeserializeAction?.Invoke(this);
        }


        #region PA
        public override int GetHashCode()
        {
            return ID.GetHashCode();
        }
        public override bool Equals(object iObj)
        {
            return Equals(iObj as UCL_AssetEntry<T>);
        }

        public virtual bool Equals(UCL_AssetEntry<T> iObj)
        {
            if (iObj == null) return false;
            return iObj.ID == ID;
        }
        #endregion
    }

    [System.Serializable]
    public class UCL_AssetEntryDefault<T> : UCL_AssetEntry<T> where T : class, UCLI_Asset, UCLI_Preview, new()
    {
        override public string ID { get => m_ID; set => m_ID = value; }
        public override string ToString()
        {
            return $"{ID}({GetShortName()})";
        }

        [UCL.Core.PA.UCL_List(nameof(GetAllIDsWithCache))] //For component in editor, show all available IDs in the list
        [UCL.Core.ATTR.UCL_HideOnGUI]
        public string m_ID = "Default";

        override public object OnGUI(string iFieldName, UCL.Core.UCL_ObjectDictionary iDataDic, UCL_GUILayout.DrawObjectParams iParams)
        {
            UCL.Core.UI.UCL_GUILayout.DrawObjExSetting aSetting = new UCL_GUILayout.DrawObjExSetting();
            aSetting.OnShowField = () =>
            {
                using (var aVerticalScope = new GUILayout.VerticalScope())
                {
                    if (!AssetUtil.AssetMetaIns.m_Groups.IsNullOrEmpty())
                    {
                        using (var aHorizontalScope = new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label(UCL_LocalizeManager.Get("GroupID"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            SelectGroupIDOnGUI(iDataDic);
                        }
                    }

                    using (var aHorizontalScope = new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label("ID", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                        SelectIDOnGUI(iDataDic);
                    }
                }
            };
            //if (iParams != null)
            //{
            //    GUILayout.Label($"iFieldName:{iFieldName},DisplayName:{iParams.m_DisplayName},FieldInfo:{iParams.m_FieldInfo.AllFieldToString()}", UCL_GUIStyle.LabelStyle);
            //}
            var aParams = iParams.Copy();
            aParams.m_DataDic = iDataDic.GetSubDic("Data");
            aParams.m_DrawObjExSetting = aSetting;
            //var aParams = new UI.UCL_GUILayout.DrawObjectParams(iDataDic.GetSubDic("Data"), iFieldName, false, iDrawObjExSetting: aSetting);
            UCL_GUILayout.DrawField(this, aParams);
            //UCL_GUILayout.DrawField(this, iDataDic.GetSubDic("Data"), iFieldName, false, iDrawObjExSetting: aSetting);
            return this;
        }
    }
}

