
using System.Collections;
using System.Collections.Generic;
using UCL.Core;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{

    /// <summary>
    /// Steam 支援的語言
    /// https://partner.steamgames.com/doc/store/localization/languages
    /// </summary>
    public enum SteamAPILangCode
    {
        /// <summary>
        /// 阿拉伯文 العربية
        /// </summary>
        arabic,

        /// <summary>
        /// 保加利亞文 български език
        /// </summary>
        bulgarian,

        /// <summary>
        /// 簡體中文 简体中文
        /// </summary>
        schinese,

        /// <summary>
        /// 繁體中文 繁體中文
        /// </summary>
        tchinese,

        /// <summary>
        /// 捷克文 čeština
        /// </summary>
        czech,

        /// <summary>
        /// 丹麥文 Dansk
        /// </summary>
        danish,

        /// <summary>
        /// 荷蘭文 Nederlands
        /// </summary>
        dutch,

        /// <summary>
        /// 英文 English
        /// </summary>
        english,

        /// <summary>
        /// 芬蘭文 Suomi
        /// </summary>
        finnish,

        /// <summary>
        /// 法文 Français
        /// </summary>
        french,

        /// <summary>
        /// 德文 Deutsch
        /// </summary>
        german,

        /// <summary>
        /// 希臘文 Ελληνικά
        /// </summary>
        greek,

        /// <summary>
        /// 匈牙利文 Magyar
        /// </summary>
        hungarian,

        /// <summary>
        /// 印尼文 Bahasa Indonesia
        /// </summary>
        indonesian,

        /// <summary>
        /// 義大利文 Italiano
        /// </summary>
        italian,

        /// <summary>
        /// 日文 日本語
        /// </summary>
        japanese,

        /// <summary>
        /// 韓文 한국어
        /// </summary>
        koreana,

        /// <summary>
        /// 挪威文 Norsk
        /// </summary>
        norwegian,

        /// <summary>
        /// 波蘭文 Polski
        /// </summary>
        polish,

        /// <summary>
        /// 葡萄牙文 Português
        /// </summary>
        portuguese,

        /// <summary>
        /// 葡萄牙文 - 巴西 Português-Brasil
        /// </summary>
        brazilian,

        /// <summary>
        /// 羅馬尼亞文 Română
        /// </summary>
        romanian,

        /// <summary>
        /// 俄文 Русский
        /// </summary>
        russian,

        /// <summary>
        /// 西班牙文 - 西班牙 Español-España
        /// </summary>
        spanish,

        /// <summary>
        /// 西班牙文 - 拉丁美洲 Español-Latinoamérica
        /// </summary>
        latam,

        /// <summary>
        /// 瑞典文 Svenska
        /// </summary>
        swedish,

        /// <summary>
        /// 泰文 ไทย
        /// </summary>
        thai,

        /// <summary>
        /// 土耳其文 Türkçe
        /// </summary>
        turkish,

        /// <summary>
        /// 烏克蘭文 Українська
        /// </summary>
        ukrainian,

        /// <summary>
        /// 越南文 Tiếng Việt
        /// </summary>
        vietnamese
    }
    [System.Serializable]
    public class UCL_LanguageCodeEntry : UCL_AssetEntryDefault<UCL_LanguageCodeAsset>
    {
        public const string DefaultID = "en";
        public static UCL_LanguageCodeEntry s_DefaultLang = new UCL_LanguageCodeEntry(DefaultID);
        public UCL_LanguageCodeEntry() { ID = DefaultID; }
        public UCL_LanguageCodeEntry(string iID) { ID = iID; }

    }

    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Config)]
    [UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditConfigType.UCL_LanguageCodeAsset)]
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
                GUILayout.Label($"SteamAPILangCode:{m_SteamAPILangCode}", UCL.Core.UI.UCL_GUIStyle.LabelStyle);
                if (!m_SupportedLanguages.IsNullOrEmpty())
                {
                    foreach(var lang in m_SupportedLanguages)
                    {
                        GUILayout.Label($"SupportedLanguage:{lang}", UCL.Core.UI.UCL_GUIStyle.LabelStyle);
                    }
                }
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
        public bool m_IsPolished = false;

        /// <summary>
        /// 排序用
        /// </summary>
        public int m_Order = 9999;
        /// <summary>
        /// 對應的SteamAPILangCode
        /// </summary>
        public SteamAPILangCode m_SteamAPILangCode = SteamAPILangCode.english;

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
