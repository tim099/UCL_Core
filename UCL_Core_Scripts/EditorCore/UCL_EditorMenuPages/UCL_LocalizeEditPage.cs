using System.Collections;
using System.Collections.Generic;
using UCL.Core.UI;
using UnityEngine;
using System.Text.RegularExpressions;
using UCL.Core.LocalizeLib;
using System.IO;
using System.Text;

namespace UCL.Core.EditorLib.Page
{
    public class UCL_LocalizeEditPage : UCL.Core.EditorLib.Page.UCL_EditorPage
    {
        UCL_LocalizeSetting m_LocalizeSetting = null;
        UCL.Core.UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();
        const string LocalizeSettingKey = "LocalizeEditPageSetting";
        bool m_IsDownloading = false;
        public override void Init(UCL_GUIPageController iGUIPageController)
        {
            base.Init(iGUIPageController);
            LoadLocalizeSetting();
        }
        ~UCL_LocalizeEditPage()
        {
            SaveLocalizeSetting();
        }
        public override void OnPause()
        {
            SaveLocalizeSetting();
        }
        public override void OnResume()
        {
            LoadLocalizeSetting();
        }
        public override void OnClose()
        {
            SaveLocalizeSetting();
        }
        protected void LoadLocalizeSetting()
        {
            if (m_LocalizeSetting == null) m_LocalizeSetting = new UCL_LocalizeSetting();
            if (PlayerPrefs.HasKey(LocalizeSettingKey))
            {
                string aJson = PlayerPrefs.GetString(LocalizeSettingKey);
                UCL.Core.JsonLib.JsonConvert.LoadDataFromJson(m_LocalizeSetting, JsonLib.JsonData.ParseJson(aJson), JsonLib.JsonConvert.SaveMode.Unity);
            }
        }
        protected  void SaveLocalizeSetting()
        {
            if (m_LocalizeSetting == null) return;
            var aData = UCL.Core.JsonLib.JsonConvert.SaveDataToJson(m_LocalizeSetting, JsonLib.JsonConvert.SaveMode.Unity);
            PlayerPrefs.SetString(LocalizeSettingKey, aData.ToJson());
        }
        protected override void ContentOnGUI()
        {
            GUILayout.Box("Localize");
            UCL.Core.UI.UCL_GUILayout.DrawObjectData(m_LocalizeSetting,
                m_DataDic, iIsAlwaysShowDetail: true, iFieldNameFunc: UCL.Core.UCL_StaticFunctions.LocalizeFieldName);
            if (!m_IsDownloading)
            {
                if (GUILayout.Button("Download"))
                {
                    m_LocalizeSetting.StartDownload((iSuccess) =>
                    {
                        m_IsDownloading = false;
                    });
                }
            }
        }
    }
}