using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UCL.Core.UI;
using UnityEngine;
using System.Text.RegularExpressions;
using UCL.Core.LocalizeLib;
using System.IO;
using System.Text;
using UCL.Core.Game;

namespace UCL.Core.EditorLib.Page
{
    public class UCL_LocalizeEditPage : UCL_CommonEditorPage
    {
        UCL_LocalizeSetting m_LocalizeSetting = null;
        UCL.Core.UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();
        const string LocalizeSettingKey = "LocalizeEditPageSetting";
        //bool m_IsDownloading = false;
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
                UCL.Core.JsonLib.JsonConvert.LoadDataFromJsonUnityVer(m_LocalizeSetting, JsonLib.JsonData.ParseJson(aJson));
            }
        }
        protected void SaveLocalizeSetting()
        {
            if (m_LocalizeSetting == null) return;
            var aData = UCL.Core.JsonLib.JsonConvert.SaveDataToJsonUnityVer(m_LocalizeSetting);
            PlayerPrefs.SetString(LocalizeSettingKey, aData.ToJson());
        }
        // 區塊職責：暫存 PlayerPrefs key 編輯欄的 in-flight 字串
        // 物理意義：使用者正在輸入新 key 時，先暫存 → 按 Apply 按鈕才 commit 到
        //          UCL_LocalizeService.CurLangKey + 觸發重新讀取對應 PlayerPrefs 值
        // 數值影響：未按 Apply 不影響系統行為
        string m_PendingCurLangKey = null;

        protected override void ContentOnGUI()
        {
            GUILayout.Box("Localize", UI.UCL_GUIStyle.BoxStyle);

            DrawLanguagePicker();
            DrawCurLangKeyEditor();

            //UCL_GUILayout.DrawObjectData(m_LocalizeSetting,
            //    m_DataDic, iIsAlwaysShowDetail: false, iFieldNameFunc: UCL_StaticFunctions.LocalizeFieldName);
            //if (!m_IsDownloading)
            //{
            //    if (GUILayout.Button(UCL_LocalizeManager.Get("UCL_Download")))
            //    {
            //        SaveLocalizeSetting();
            //        m_LocalizeSetting.StartDownload((iSuccess) =>
            //        {
            //            m_IsDownloading = false;
            //        });
            //    }
            //}
        }

        // ===========================================================
        // 區塊：Language picker — 用 PopupSearchCache 提供可搜尋下拉選單
        // 職責：列出 UCL_LanguageCodeAsset 全部 ID，搜尋選定後立即套用 SetLanguage
        // 物理意義：與 UCL_AgentCommandsPage 的 Cmd 下拉同一風格，避免 DrawObjectData
        //          預設 layout 在語言多時佔版面 + 沒搜尋
        // 數值影響：選擇變更時呼叫 UCL_LocalizeService.SetLanguage → 寫 PlayerPrefs + reload
        // ===========================================================
        void DrawLanguagePicker()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("Language", UCL_GUIStyle.LabelStyle);

                // 取所有語言 ID（會走 UCL_ModuleService → 跨模組）
                var aAllIDs = UCL_LanguageCodeAsset.Util.GetAllIDs(true);
                if (aAllIDs == null || aAllIDs.Count == 0)
                {
                    GUILayout.Label("(no UCL_LanguageCodeAsset registered)", UCL_GUIStyle.LabelStyle);
                    return;
                }

                string aCurLang = UCL_LocalizeService.CurLang;
                int aCurIdx = aAllIDs.IndexOf(aCurLang);
                if (aCurIdx < 0) aCurIdx = 0;

                // 用 ID + 顯示名稱組成易讀選項（DisplayName 取自 LanguageCodeAsset）
                var aDisplayOptions = aAllIDs
                    .Select(id =>
                    {
                        var aAsset = UCL_LanguageCodeAsset.Util.GetData(id);
                        string aName = aAsset != null && !string.IsNullOrEmpty(aAsset.LanguageName) ? aAsset.LanguageName : id;
                        return aName == id ? id : $"{id} — {aName}";
                    })
                    .ToList();

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Current:", UCL_GUIStyle.LabelStyle, GUILayout.Width(80));
                    int aNewIdx = UCL_GUILayout.PopupSearchCache(aCurIdx, aDisplayOptions, m_Dic, "LangPicker");
                    if (aNewIdx != aCurIdx && aNewIdx >= 0 && aNewIdx < aAllIDs.Count)
                    {
                        string aNewLang = aAllIDs[aNewIdx];
                        UCL_LocalizeService.SetLanguage(aNewLang);
                        Debug.Log($"[UCL_LocalizeEditPage] Language switched: {aCurLang} → {aNewLang}");
                    }
                }
            }
        }

        // ===========================================================
        // 區塊：CurLangKey 編輯器 — 讓上層專案可即時改 PlayerPrefs key
        // 職責：顯示當前 key、提供文字輸入 + Apply 按鈕；Apply 時呼叫 SetLanguage 重新觸發 GetCurLang
        //       讀新 key 對應的值
        // 物理意義：跨專案撞 key（例：Utage 也用 "CurLang"）時不必改原始碼，從 UI 改即可。
        //          但這個 UI 改動只活在當前 process / Editor session — 永久化還是要在 bootstrap
        //          程式內 set static property（按按鈕後本 Page 會貼提示）
        // 數值影響：Apply 後 UCL_LocalizeService.CurLangKey 變更，後續讀寫走新 key
        // ===========================================================
        void DrawCurLangKeyEditor()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label("PlayerPrefs Key (CurLangKey)", UCL_GUIStyle.LabelStyle);

                string aCurKey = UCL_LocalizeService.CurLangKey;
                if (m_PendingCurLangKey == null) m_PendingCurLangKey = aCurKey;

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Key:", UCL_GUIStyle.LabelStyle, GUILayout.Width(80));
                    m_PendingCurLangKey = GUILayout.TextField(m_PendingCurLangKey ?? "", UCL_GUIStyle.TextFieldStyle);

                    GUI.enabled = !string.IsNullOrEmpty(m_PendingCurLangKey) && m_PendingCurLangKey != aCurKey;
                    // 區塊職責：Apply 按鈕 — 同時更新 session 內的 static 欄位 + 透過 UCL_Config 持久化到 ConfigAsset
                    // 物理意義：兩段式更新確保「session 立即生效」且「下次啟動仍記得」。順序很重要：
                    //          1. 先寫 session 值（CurLangKey setter）→ 後續呼叫立刻看到新 key
                    //          2. 再寫 UCL_Config + Save → 落地到 ConfigAsset JSON（git-tracked）
                    //          3. 最後呼叫 SetLanguage 觸發用新 key 重讀對應 PlayerPrefs 值並 reload
                    // 數值影響：寫一筆 UCL_ConfigAsset/Default.json 到 disk
                    if (GUILayout.Button("Apply (Persist)", UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                    {
                        UCL_LocalizeService.CurLangKey = m_PendingCurLangKey;
                        UCL_Config.SetString(UCL_LocalizeService.ConfigKey_CurLangKey, m_PendingCurLangKey);
                        UCL_Config.Save();
                        UCL_LocalizeService.SetLanguage(UCL_LocalizeService.CurLang);
                        Debug.Log($"[UCL_LocalizeEditPage] CurLangKey: '{aCurKey}' → '{m_PendingCurLangKey}' (persisted via UCL_Config)");
                    }
                    GUI.enabled = true;

                    if (GUILayout.Button("Reset", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_PendingCurLangKey = UCL_LocalizeService.DefaultCurLangKey;
                    }
                }

                // 區塊職責：清除 ConfigAsset 內的 override，讓系統回到 DefaultCurLangKey
                // 物理意義：若使用者過去設過 override 想退回預設，按此即可（會同時清 session + persist）
                // 數值影響：刪 UCL_ConfigAsset 內 ConfigKey_CurLangKey 那筆 entry + Save
                using (new GUILayout.HorizontalScope())
                {
                    bool aHasOverride = UCL_Config.HasKey(UCL_LocalizeService.ConfigKey_CurLangKey);
                    GUI.enabled = aHasOverride;
                    if (GUILayout.Button("Clear Persisted Override", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        UCL_Config.DeleteKey(UCL_LocalizeService.ConfigKey_CurLangKey);
                        UCL_Config.Save();
                        UCL_LocalizeService.CurLangKey = UCL_LocalizeService.DefaultCurLangKey;
                        m_PendingCurLangKey = UCL_LocalizeService.DefaultCurLangKey;
                        UCL_LocalizeService.SetLanguage(UCL_LocalizeService.CurLang);
                        Debug.Log("[UCL_LocalizeEditPage] CurLangKey override cleared, reverted to default.");
                    }
                    GUI.enabled = true;

                    GUILayout.Label(aHasOverride ? "  (override 存在於 ConfigAsset)" : "  (目前用預設值)",
                        UCL_GUIStyle.LabelStyle);
                }

                GUILayout.Label($"  Default: \"{UCL_LocalizeService.DefaultCurLangKey}\"", UCL_GUIStyle.LabelStyle);
                GUILayout.Label("  💾 Apply 會持久化到 UCL_ConfigAsset/Default.json，下次啟動自動載入。", UCL_GUIStyle.LabelStyle);
                GUILayout.Label("  ⌨ 程式控制（純 session）：UCL_LocalizeService.CurLangKey = \"<your_key>\";", UCL_GUIStyle.LabelStyle);
            }
        }
    }
}