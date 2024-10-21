
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 10/21 2023
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UCL.Core;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{
    public static class FileMetaExtensions
    {
        //public static string GetGroupID(this UCL_AssetCommonMeta.FileMeta iFileMeta)
        //{
        //    if(iFileMeta == null)
        //    {
        //        return string.Empty;
        //    }
        //    return iFileMeta.m_Group;
        //}
    }
    /// <summary>
    /// 模組內同類Asset共用的Meta(例如UCL_SpriteAsset)
    /// </summary>
    public class UCL_AssetCommonMeta : UnityJsonSerializable, UCLI_NameOnGUI
    {
        /// <summary>
        /// Save in PlayerPrefs
        /// </summary>
        public class PlayerPrefsData : UnityJsonSerializable
        {
            public enum FilterType
            {
                CheckBox = 0,
                Dropdown,
            }
            public class GroupData : UnityJsonSerializable
            {
                public bool m_IsEnable = true;
            }
            public FilterType m_FilterType = FilterType.CheckBox;
            #region CheckBox
            public bool m_ShowAll = true;
            public bool m_ShowOthers = true;
            #endregion
            #region Dropdown
            /// <summary>
            /// if m_SelectedGroup is string.Empty, then show all
            /// </summary>
            public string m_SelectedGroup = string.Empty;
            #endregion
            /// <summary>
            /// Show delete button
            /// </summary>
            public bool m_ShowDeleteButton = false;


            public Dictionary<string, GroupData> m_GroupDatas = new();

            public void OnGUI(UCL_ObjectDictionary iDic, UCL_AssetCommonMeta iCommonDataMeta)
            {
                var aExternalGroups = iCommonDataMeta.m_Groups;
                List<string> aDeletedKeys = new List<string>();
                foreach (var aKey in m_GroupDatas.Keys)
                {
                    if (!aExternalGroups.ContainsKey(aKey))
                    {
                        aDeletedKeys.Add(aKey);
                    }
                }
                foreach(var aKey in aDeletedKeys)//remove deleted keys
                {
                    m_GroupDatas.Remove(aKey);
                }
                foreach (var aKey in aExternalGroups.Keys)//add not exist keys
                {
                    if (!m_GroupDatas.ContainsKey(aKey))
                    {
                        m_GroupDatas.Add(aKey, new GroupData());
                    }

                    iCommonDataMeta.GroupsPopupID.Add(aKey);
                }
                const int MaxColumn = 8;
                const int ShowCount = MaxColumn - 2;
                int aRemainCheckBoxCount = 0;
                List<string> aKeys = m_GroupDatas.Keys.ToList();
                using (var aScope = new GUILayout.HorizontalScope("box"))
                {
                    GUILayout.Label(UCL_LocalizeManager.Get("FilterMode"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    m_FilterType = UCL_GUILayout.PopupAuto(m_FilterType, iDic, "FilterType", 6, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                    switch (m_FilterType)
                    {
                        case FilterType.CheckBox:
                            {
                                m_ShowAll = UCL_GUILayout.CheckBox(m_ShowAll);
                                GUILayout.Label("Show All", UCL_GUIStyle.LabelStyle, GUILayout.MinWidth(UCL_GUIStyle.GetScaledSize(60)), GUILayout.ExpandWidth(false));

                                if (!m_ShowAll)
                                {
                                    m_ShowOthers = UCL_GUILayout.CheckBox(m_ShowOthers);
                                    GUILayout.Label("Others", UCL_GUIStyle.LabelStyle, GUILayout.MinWidth(UCL_GUIStyle.GetScaledSize(60)), GUILayout.ExpandWidth(false));
                                    int aCurShowCount = Mathf.Min(aKeys.Count, ShowCount);
                                    aRemainCheckBoxCount = aKeys.Count - aCurShowCount;

                                    for(int i = 0; i < aCurShowCount; i++)
                                    {
                                        var aKey = aKeys[i];
                                        var aGroup = m_GroupDatas[aKey];
                                        aGroup.m_IsEnable = UCL_GUILayout.CheckBox(aGroup.m_IsEnable);
                                        GUILayout.Label(aKey, UCL_GUIStyle.LabelStyle, GUILayout.MinWidth(UCL_GUIStyle.GetScaledSize(60)), GUILayout.ExpandWidth(false));
                                    }

                                }



                                break;
                            }
                        case FilterType.Dropdown:
                            {
                                List<string> aList = new List<string>();
                                aList.Add(string.Empty);
                                aList.Append(aExternalGroups.Keys);
                                m_SelectedGroup = aList[UCL_GUILayout.PopupAuto(aList.IndexOf(m_SelectedGroup), aList, iDic, "SelectedGroup", 6, GUILayout.Width(UCL_GUIStyle.GetScaledSize(300)))];
                                break;
                            }
                    }
                }
                if (aRemainCheckBoxCount > 0)
                {
                    //GUILayout.Space(20);
                    int aRowCount = Mathf.CeilToInt((float)aRemainCheckBoxCount / MaxColumn);
                    //GUILayout.Label($"aRowCount:{aRowCount},aRemainCheckBoxCount:{aRemainCheckBoxCount}");
                    using (var aScope2 = new GUILayout.VerticalScope())
                    {
                        for (int i = 0; i < aRowCount; i++)
                        {
                            using (var aScope3 = new GUILayout.HorizontalScope())
                            {
                                GUILayout.Space(20);
                                for (int j = 0; j < MaxColumn; j++)
                                {
                                    int aIndex = i * MaxColumn + j + ShowCount;
                                    if (aIndex >= aKeys.Count)
                                    {
                                        break;
                                    }
                                    var aKey = aKeys[aIndex];
                                    var aGroup = m_GroupDatas[aKey];
                                    aGroup.m_IsEnable = UCL_GUILayout.CheckBox(aGroup.m_IsEnable);
                                    GUILayout.Label(aKey, UCL_GUIStyle.LabelStyle, GUILayout.MinWidth(UCL_GUIStyle.GetScaledSize(60)), GUILayout.ExpandWidth(false));
                                }
                            }
                        }
                    }

                }
            }
            public bool CheckShowData(UCLI_Asset iUtil, string iGroup, string iSelectedGroup, FilterType iFilterType)
            {
                switch (iFilterType)
                {
                    case FilterType.CheckBox:
                        {
                            if (m_ShowAll) return true;
                            if (string.IsNullOrEmpty(iGroup))//No group Data
                            {
                                //Debug.LogError("string.IsNullOrEmpty(aGroup)");
                                return m_ShowOthers;
                            }
                            if (m_GroupDatas.ContainsKey(iGroup))
                            {
                                return m_GroupDatas[iGroup].m_IsEnable;
                            }
                            break;
                        }
                    case FilterType.Dropdown:
                        {
                            return CheckShowGroup(iGroup, iSelectedGroup);
                            //if (string.IsNullOrEmpty(iGroup))//No group Data
                            //{
                            //    return true;
                            //}
                            //if (string.IsNullOrEmpty(iSelectedGroup) || iSelectedGroup == iGroup)
                            //{
                            //    return true;
                            //}
                            //return false;
                        }
                }

                return true;
            }
            public bool CheckShowGroup(string iGroup, string iSelectedGroup)
            {
                if (string.IsNullOrEmpty(iGroup))//No group Data
                {
                    return true;
                }
                if (string.IsNullOrEmpty(iSelectedGroup) || iSelectedGroup == iGroup)
                {
                    return true;
                }
                return false;
            }

            virtual public string HashKey
            {
                get
                {
                    switch (m_FilterType)
                    {
                        case PlayerPrefsData.FilterType.Dropdown:
                            {
                                return $"{m_FilterType},{m_SelectedGroup}";
                            }
                    }
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    //m_GroupDatas.Keys
                    sb.Append($"{m_FilterType},{m_SelectedGroup},");
                    sb.Append((m_ShowAll ? "1" : "0"));
                    sb.Append((m_ShowOthers ? "1" : "0"));
                    foreach(var group in m_GroupDatas.Values)
                    {
                        sb.Append((group.m_IsEnable ? "1" : "0"));
                    }
                    return sb.ToString();
                    
                    //$"{m_FilterType},{m_SelectedGroup}," +
                        //$"{(m_ShowAll ? "1" : "0")},{(m_ShowOthers ? "1" : "0")}";
                }
            }
        }
        public class Group : UnityJsonSerializable//, UCLI_IsEnable
        {
            public List<string> m_IDs = new List<string>();
            public string m_MD5 = "Tmp";
            //public bool IsEnable { get => m_IsEnable; set => m_IsEnable = value; }
            //public bool m_IsEnable = true;
        }
        public class FileMeta : UCL.Core.JsonLib.UnityJsonSerializable
        {
            public string m_ID;
            public string m_MD5;
            //public string m_Group;
        }
        public string PlayerPrefsKey => TypeName;
        public string TypeName { get; private set; }
        //public string m_SaveDate;

        //public string m_MD5;
        public PlayerPrefsData PlayerPrefsMeta { get; set; }
        public bool RequireClearDic { get; set; } = false;

        public Dictionary<string, Group> m_Groups = new();
        //public Dictionary<string, FileMeta> m_FileMetas = new();


        //public bool m_ShowAll = true;
        //public bool m_ShowOthers = true;
        public bool m_EditGroup = false;

        public bool ShowDeleteButton
        {
            get => PlayerPrefsMeta.m_ShowDeleteButton;
            set => PlayerPrefsMeta.m_ShowDeleteButton = value;
        }
        private System.Action<string> m_SaveAct = null;


        public void Init(string iTypeName, System.Action<string> iSaveAct)
        {
            //Debug.LogError($"iTypeName:{iTypeName}");
            TypeName = iTypeName;
            m_SaveAct = iSaveAct;
            PlayerPrefsMeta = new PlayerPrefsData();
            if (PlayerPrefs.HasKey(PlayerPrefsKey))
            {
                string aJson = PlayerPrefs.GetString(PlayerPrefsKey);
                JsonData aData = JsonData.ParseJson(aJson);
                PlayerPrefsMeta.DeserializeFromJson(aData);
            }
        }
        virtual public string HashKey
        {
            get
            {

                return PlayerPrefsMeta.HashKey;
            }
        }
        virtual public void NameOnGUI(UCL.Core.UCL_ObjectDictionary iDataDic, string iDisplayName)
        {
            {
                GUILayout.Label(iDisplayName, UCL_GUIStyle.LabelStyle);//, GUILayout.Width(180), GUILayout.ExpandWidth(true)
            }


            m_EditGroup = UCL_GUILayout.CheckBox(m_EditGroup);
            GUILayout.Label(UCL_LocalizeManager.Get("EditGroup"), UCL_GUIStyle.LabelStyle);
            float space = UCL_GUIStyle.GetScaledSize(10);
            GUILayout.Space(space);

            ShowDeleteButton = UCL_GUILayout.CheckBox(ShowDeleteButton);
            GUILayout.Label(UCL_LocalizeManager.Get("ShowDeleteButton"), UCL_GUIStyle.LabelStyle);
            

            GUILayout.Space(space);
            if (GUILayout.Button(UCL_LocalizeManager.Get("Save"), UCL_GUIStyle.ButtonStyle))
            {
                Save();
            }
        }

        public override void DeserializeFromJson(JsonData iJson)
        {
            base.DeserializeFromJson(iJson);
        }

        //const string MD5SaveKey = "##MD5##";
        public override JsonData SerializeToJson()
        {
            //m_SaveDate = System.DateTime.Now.ToString("yyyyy/MM/dd HH:mm:ss.ff");
            //m_MD5 = MD5SaveKey;
            var aJson = base.SerializeToJson();
            return aJson;
        }
        /// <summary>
        /// Write to File
        /// </summary>
        public void Save()
        {

            if(m_SaveAct != null)
            {
                string aMetaStr = SerializeToJson().ToJson();
                //FileDatas.SaveCommonDataMetaJson(aMetaStr);
                m_SaveAct.Invoke(aMetaStr);
            }
            PlayerPrefs.SetString(PlayerPrefsKey, PlayerPrefsMeta.SerializeToJson().ToJson());
            //string aMd5 = aMetaStr.ConvertToMD5();
            //FileDatas.SaveCommonDataMetaJson(aMetaStr.Replace(MD5SaveKey, aMd5));
        }
        private List<string> GroupsPopupID { get; set; } = new List<string>();
        private bool IsModified { get; set; } = false;
        public void OnGUI(UCL_ObjectDictionary iDic)
        {
            if (RequireClearDic)
            {
                RequireClearDic = false;
                iDic.Clear();
            }
            IsModified = false;

            

            int aGroupCount = m_Groups.Count;

            var aParams = new UCL_GUILayout.DrawObjectParams(iDic.GetSubDic("Data"), string.Empty);

            UCL_GUILayout.DrawObjectData(this, aParams);


            if (aGroupCount != m_Groups.Count)
            {
                IsModified = true;
            }

            GroupsPopupID.Clear();
            GroupsPopupID.Add(string.Empty);//No Group

            PlayerPrefsMeta.OnGUI(iDic.GetSubDic("PlayerPrefsMeta"), this);

        }
        /// <summary>
        /// 抓取全部要顯示的物品ID
        /// </summary>
        /// <returns></returns>
        public List<string> GetAllShowData(UCLI_Asset iUtil, IList<string> iIDs, PlayerPrefsData.FilterType? iFilterType = null)
        {
            return GetAllShowData(iUtil, iIDs, PlayerPrefsMeta.m_SelectedGroup, iFilterType);
        }

        /// <summary>
        /// 抓取全部要顯示的物品ID
        /// </summary>
        /// <param name="iIDs"></param>
        /// <param name="iTargetGroup">分組ID 空字串則顯示全部</param>
        /// <returns></returns>
        public List<string> GetAllShowData(UCLI_Asset iUtil, IList<string> iIDs, string iTargetGroup, PlayerPrefsData.FilterType? iFilterType = null)
        {
            List<string> aResult = new List<string>();
            for (int i = 0; i < iIDs.Count; i++)
            {
                string aID = iIDs[i];
                if (CheckShowData(iUtil, aID, iTargetGroup, iFilterType))
                {
                    aResult.Add(aID);
                    continue;
                }
            }
            try
            {
                aResult.Sort((iA, iB) =>
                {
                    string aGroupA = iUtil.GetCommonData(iA).GroupID;
                    string aGroupB = iUtil.GetCommonData(iB).GroupID;
                    if (aGroupA == aGroupB)
                    {
                        return iA.CompareTo(iB);
                    }
                    else if (aGroupA.IsNullOrEmpty())
                    {
                        if (aGroupB.IsNullOrEmpty())
                        {
                            return 0;
                        }
                        else if (iTargetGroup.IsNullOrEmpty())
                        {
                            return -1;
                        }
                        else
                        {
                            return 1;
                        }
                    }
                    else if (aGroupB.IsNullOrEmpty())
                    {
                        if (iTargetGroup.IsNullOrEmpty())
                        {
                            return 1;
                        }
                        else
                        {
                            return -1;
                        }
                    }

                    int aGroupCompare = aGroupA.CompareTo(aGroupB);
                    if (aGroupCompare != 0)
                    {
                        return aGroupCompare;
                    }
                    return iA.CompareTo(iB);
                });
            }
            catch(System.Exception e)
            {
                Debug.LogException(e);
            }

            return aResult;
        }
        public bool CheckShowData(UCLI_Asset iUtil, string iID, string iTargetGroup, PlayerPrefsData.FilterType? iFilterType = null)
        {
            var aAsset = iUtil.GetCommonData(iID);
            //var aFile = GetFileMeta(iID);
            //string aGroup = aFile.GetGroupID();
            string aGroup = aAsset.GroupID;
            PlayerPrefsData.FilterType aFilterType = PlayerPrefsMeta.m_FilterType;
            if (iFilterType.HasValue)
            {
                aFilterType = iFilterType.Value;
            }
            return PlayerPrefsMeta.CheckShowData(iUtil, aGroup, iTargetGroup, aFilterType);
        }
        public void OnGUI_ShowData(UCLI_Asset iUtil, string iID, UCL_ObjectDictionary iDic, int iWidth)
        {
            //var aFile = GetFileMeta(iID);
            var aGroupsPopupID = GroupsPopupID;//所有的分組ID
            if (!aGroupsPopupID.IsNullOrEmpty())
            {
                var aAsset = iUtil.GetCommonData(iID);
                int aOldIndex = aGroupsPopupID.IndexOf(aAsset.GroupID);
                var aIndex = UCL_GUILayout.PopupAuto(aOldIndex, aGroupsPopupID, iDic, "Group", 6, GUILayout.MinWidth(iWidth));//, GUILayout.MinWidth(80)
                if (aOldIndex != aIndex)
                {
                    IsModified = true;
                    aAsset.GroupID = aGroupsPopupID[aIndex];
                }
                //int aOldIndex = aGroupsPopupID.IndexOf(aFile.m_Group);
                //var aIndex = UCL_GUILayout.PopupAuto(aOldIndex, aGroupsPopupID, iDic, "Group", 6, GUILayout.MinWidth(iWidth));//, GUILayout.MinWidth(80)
                //if (aOldIndex != aIndex)
                //{
                //    IsModified = true;
                //    aFile.m_Group = aGroupsPopupID[aIndex];
                //}
            }
        }
        public void OnGUIEnd()
        {
            //Check modified and save
            if (IsModified) Save();
            IsModified = false;
        }

    }
}
