
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 01/13 2025
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UCL.Core;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{


    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
    [UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditDataType.UCL_TMPFontAsset)]
    public class UCL_TMPFontAsset : UCL_Asset<UCL_TMPFontAsset>
    {
        #region must override 一定要override的部份
        /// <summary>
        /// 預覽
        /// </summary>
        /// <param name="iIsShowEditButton">是否顯示編輯按鈕</param>
        override public void Preview(UCL.Core.UCL_ObjectDictionary iDataDic, bool iIsShowEditButton = false)
        {
            GUILayout.BeginHorizontal();
            using (var aScope = new GUILayout.VerticalScope("box"))//, GUILayout.MinWidth(130)
            {
                GUILayout.Label($"{UCL_LocalizeManager.Get("Preview")}({ID})", UCL.Core.UI.UCL_GUIStyle.LabelStyle);


                if (iIsShowEditButton)
                {
                    ShowEditButtonOnGUI();
                }
            }
            GUILayout.EndHorizontal();
        }

        public UCL_TMPFontAsset()
        {
            ID = "new font asset";
        }
        public UCL_TMPFontAsset(string iID)
        {
            Init(iID);
        }
        #endregion

        virtual public bool IsEmpty => m_FontAsset.IsEmpty;


        /// <summary>
        /// TMP_FontAsset
        /// </summary>
        public UCL_AddressableData m_FontAsset = new UCL_AddressableData();


        public async UniTask SetFontAsset()
        {
            //TMP_FontAsset
            TMP_FontAsset fontAsset = null;
            if (!m_FontAsset.IsEmpty)
            {
                fontAsset = await m_FontAsset.LoadObjectAsync<TMP_FontAsset>(default);
            }
            UCL_TMPUtil.SetFontAsset(fontAsset);
        }
    }

    [System.Serializable]
    public class UCL_TMPFontEntry : UCL_AssetEntryDefault<UCL_TMPFontAsset>
    {
        public const string DefaultID = "Default";
        public static UCL_LanguageCodeEntry s_DefaultLang = new UCL_LanguageCodeEntry(DefaultID);

        public override bool IsEmpty
        {
            get
            {
                if (string.IsNullOrEmpty(ID)) return true;
                try
                {
                    if (!GetData().IsEmpty)//not Empty
                    {
                        return false;
                    }
                }
                catch (Exception)
                {
                    //data not exist!!
                }
                return true;
            }
        }

        public UCL_TMPFontEntry() { ID = DefaultID; }
        public UCL_TMPFontEntry(string iID) { ID = iID; }

        public async UniTask SetFontAsset()
        {
            if (IsEmpty)
            {
                UCL_TMPUtil.SetFontAsset(null);//set to default
                return;
            }
            await GetData().SetFontAsset();
        }
    }


    public static class UCL_TMPUtil
    {
        private static TMP_Settings s_DefaultSetting = null;
        private static Type s_SettingsType = null;
        private static FieldInfo s_InstanceFieldInfo = null;

        private static TMP_FontAsset s_DefaultFontAsset = null;
        public static void SetTMPSettings(TMP_Settings setting)
        {
            if (s_DefaultSetting == null)
            {
                s_DefaultSetting = TMP_Settings.instance;
                s_SettingsType = typeof(TMP_Settings);
                s_InstanceFieldInfo = s_SettingsType.GetField("s_Instance", BindingFlags.Static | BindingFlags.NonPublic);//get 
            }

            if (setting == null)//Set to default
            {
                setting = s_DefaultSetting;
            }

            s_InstanceFieldInfo.SetValue(null, setting);
        }
        public static void SetFontAsset(TMP_FontAsset fontAsset)
        {
            if (s_DefaultFontAsset == null)//Save defaultFontAsset
            {
                s_DefaultFontAsset = TMP_Settings.defaultFontAsset;
            }

            if (fontAsset == null)//set to default!!
            {
                fontAsset = s_DefaultFontAsset;
            }

            TMP_Settings.defaultFontAsset = fontAsset;
        }
    }
}