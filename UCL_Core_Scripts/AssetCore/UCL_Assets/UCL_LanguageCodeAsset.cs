
using System.Collections;
using System.Collections.Generic;
using UCL.Core;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{
    [System.Serializable]
    public class UCL_LanguageCodeEntry : UCL_AssetEntryDefault<UCL_LanguageCodeAsset>
    {
        public const string DefaultID = "en";
        public static UCL_LanguageCodeEntry s_DefaultLang = new UCL_LanguageCodeEntry(DefaultID);
        public UCL_LanguageCodeEntry() { ID = DefaultID; }
        public UCL_LanguageCodeEntry(string iID) { ID = iID; }

    }

    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Config)]
    //[UCL.Core.ATTR.UCL_Sort((int)AssetGroup.EditLocalizeSettingType.RCG_LanguageCodeData)]
    public class UCL_LanguageCodeAsset : UCL_Asset<UCL_LanguageCodeAsset>
    {
        #region must override 一定要override的部份
        /// <summary>
        /// 預覽
        /// </summary>
        /// <param name="iIsShowEditButton">是否顯示編輯按鈕</param>
        override public void Preview(UCL.Core.UCL_ObjectDictionary iDataDic, bool iIsShowEditButton = false)
        {
            GUILayout.BeginHorizontal();
            using (var aScope = new GUILayout.VerticalScope("box", GUILayout.MinWidth(130)))
            {
                GUILayout.Label($"{UCL_LocalizeManager.Get("Preview")}({ID})[{LanguageName}]", UCL.Core.UI.UCL_GUIStyle.LabelStyle);
                //UCL.Core.UI.UCL_GUILayout.LabelAutoSize(UCL_LocalizeManager.Get("Preview"));
                //UCL.Core.UI.UCL_GUILayout.LabelAutoSize(ItemName);

                //using (var aScope2 = new GUILayout.HorizontalScope())
                //{
                //}

                if (iIsShowEditButton)
                {
                    ShowEditButtonOnGUI();
                }
            }
            GUILayout.EndHorizontal();
        }

        public UCL_LanguageCodeAsset()
        {
            ID = UCL_LanguageCodeEntry.DefaultID;
        }
        public UCL_LanguageCodeAsset(string iID)
        {
            Init(iID);
        }
        #endregion
        public string LanguageName
        {
            get
            {
                if (!string.IsNullOrEmpty(m_LanguageName))
                {
                    return m_LanguageName;
                }
                var aInfo = CultureInfo;
                if (aInfo == null) return ID;

                return $"{aInfo.NativeName}";
            }
        }
        System.Globalization.CultureInfo CultureInfo
        {
            get
            {
                if (!s_CultureInfoDic.ContainsKey(ID))
                {
                    System.Globalization.CultureInfo aInfo = null;
                    try
                    {
                        aInfo = new System.Globalization.CultureInfo(ID);
                    }
                    catch (System.Globalization.CultureNotFoundException e)
                    {
                        Debug.LogWarning(e);
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogException(e);
                    }

                    s_CultureInfoDic[ID] = aInfo;
                }
                return s_CultureInfoDic[ID];
            }
        }
        public string m_LanguageName;

        public List<SystemLanguage> m_SupportedLanguages = new List<SystemLanguage>();

        public static Dictionary<string, System.Globalization.CultureInfo> s_CultureInfoDic = new Dictionary<string, System.Globalization.CultureInfo>();

        /// <summary>
        /// 在選擇語言UI標註 翻譯完整度
        /// </summary>
        public bool isPolished = false;

        public bool CheckSupported(SystemLanguage iSystemLanguage)
        {
            foreach (var aLang in m_SupportedLanguages)
            {
                if (aLang == iSystemLanguage) return true;
            }
            return false;
        }
        public void SetLanguage()
        {
            UCL.Core.Game.UCL_LocalizeService.SetLanguage(ID);
        }
    }
}
