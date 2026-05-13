using System.Collections;
using System.Collections.Generic;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 本類別負責提供一個優雅的介面來管理 PlayerPrefs 數據。
    /// 透過 Windows 登錄檔 (Registry) 直接存取編輯器環境下的設定值，
    /// 並提供搜尋、修改與刪除功能，確保開發者能精準掌控持久化數據。
    /// </summary>
    public class UCL_PlayerPrefsEditPage : UCL.Core.EditorLib.Page.UCL_EditorPage
    {
        /* -------------------------------------------------------------------------
         * 成員變數定義區塊
         * 負責存儲頁面狀態、快取的鍵值清單以及搜尋過濾器。
         * 這些數值會影響 GUI 繪製的內容與資料檢索的範圍。
         * ------------------------------------------------------------------------- */

        /// <summary> 存儲從登錄檔讀取到的所有鍵名清單 </summary>
        private List<string> m_Keys = new List<string>();
        /// <summary> 搜尋過濾字串，用於篩選顯示的鍵名 </summary>
        private string m_SearchFilter = string.Empty;
        /// <summary> 用於存儲 GUI 狀態的字典，例如摺疊狀態 </summary>
        private UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();

        /* -------------------------------------------------------------------------
         * 初始化與生命週期管理
         * 在頁面開啟、恢復或初始化時觸發資料重新整理。
         * 確保使用者看到的數據永遠是最新的，不會出現過時的殘留。
         * ------------------------------------------------------------------------- */

        public override void Init(UCL_GUIPageController iGUIPageController)
        {
            // 呼叫基底初始化邏輯
            base.Init(iGUIPageController);
            // 首次進入時重新整理鍵值清單
            RefreshKeys();
        }

        public override void OnResume()
        {
            // 當頁面重新獲得焦點時，同步最新的登錄檔狀態
            RefreshKeys();
        }
        /* -------------------------------------------------------------------------
         * 數據處理核心邏輯
         * 負責與 Windows 作業系統底層通訊，獲取 Unity 存儲在登錄檔中的數據。
         * 物理意義：將二進位/數值化的系統設定轉化為可讀取的字串清單。
         * ------------------------------------------------------------------------- */

        /// <summary>
        /// 重新整理鍵名清單。
        /// 邏輯：根據 PlayerSettings 中的公司與產品名稱，定位到正確的登錄檔路徑，
        /// 並提取該路徑下所有的數值名稱。
        /// </summary>
        private void RefreshKeys()
        {
            m_Keys.Clear();
            try
            {
#if UNITY_EDITOR_WIN
                // 由於某些 Unity 專案環境未引用 Microsoft.Win32.Registry，
                // 我們改用更暴力但絕對有效的 'reg query' 命令來獲取鍵值。
                string registryPath = $@"HKEY_CURRENT_USER\Software\Unity\UnityEditor\{UnityEditor.PlayerSettings.companyName}\{UnityEditor.PlayerSettings.productName}";
                
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "reg",
                    Arguments = $"query \"{registryPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    // 解析 reg query 的輸出，每一行通常包含 [鍵名] [類型] [值]
                    string[] lines = output.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        // 跳過路徑標題與空行
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("HKEY_")) continue;
                        
                        // 提取第一部分作為鍵名
                        string[] parts = trimmed.Split(new[] { "    ", "\t" }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0)
                        {
                            string rawKey = parts[0];
                            // Unity 在 Windows 登錄檔中會幫 Key 加上 _h[數字] 的後綴
                            // 我們需要將其移除才能得到真正的 PlayerPrefs Key
                            string cleanKey = Regex.Replace(rawKey, @"_h\d+$", "");
                            m_Keys.Add(cleanKey);
                        }
                    }
                    // 移除重複項（如果有）
                    m_Keys = m_Keys.Distinct().ToList();
                }
#elif UNITY_EDITOR_OSX
                // macOS 上的 PlayerPrefs 存儲在 Library/Preferences/ 下的 .plist 檔案中
                // 格式通常為 com.unity.UnityEditor.[CompanyName].[ProductName].plist
                string fileName = $"com.unity.UnityEditor.{UnityEditor.PlayerSettings.companyName}.{UnityEditor.PlayerSettings.productName}.plist";
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library/Preferences", fileName);
                if (File.Exists(path))
                {
                    // 這裡僅作路徑提示，實際解析 .plist 可能需要額外工具，
                    // 但為了「跨平台」意圖，我們至少標註了位置。
                    // 註：macOS 的 plist 可能是二進位格式，讀取較為複雜。
                    UnityEngine.Debug.LogWarning($"[UCL_PlayerPrefsEditPage] macOS 檔案路徑: {path} (暫不支持直接解析二進位 plist)");
                }
#elif UNITY_EDITOR_LINUX
                // Linux 上的 PlayerPrefs 存儲在 ~/.config/unity3d/[CompanyName]/[ProductName]/prefs
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), 
                    ".config/unity3d", UnityEditor.PlayerSettings.companyName, UnityEditor.PlayerSettings.productName, "prefs");
                if (File.Exists(path))
                {
                    // Linux 的 prefs 是 XML 格式，可以使用 Regex 提取鍵名
                    string content = File.ReadAllText(path);
                    MatchCollection matches = Regex.Matches(content, "name=\"([^\"]+)\"");
                    foreach (Match match in matches)
                    {
                        m_Keys.Add(match.Groups[1].Value);
                    }
                }
#endif
                // 依字母排序以利閱讀
                m_Keys.Sort();
            }
            catch (Exception e)
            {
                // 若發生權 her 權限或路徑錯誤，記錄於控制台以利排查
                UnityEngine.Debug.LogError($"[UCL_PlayerPrefsEditPage] 讀取設定失敗: {e.Message}");
            }
        }

        /* -------------------------------------------------------------------------
         * GUI 繪製核心
         * 負責渲染整個編輯頁面的視覺內容。
         * 數值影響：直接控制編輯器視窗的佈局與互動反饋。
         * ------------------------------------------------------------------------- */

        protected override void ContentOnGUI()
        {
            // 標題區域，使用專案統一的 BoxStyle
            //GUILayout.Box("PlayerPrefs Manager", UCL_GUIStyle.BoxStyle, GUILayout.ExpandWidth(true));
            // 工具列：包含重新整理按鈕與搜尋框
            using (new GUILayout.HorizontalScope())
            {
                // 重新整理按鈕：手動觸發資料同步
                if (GUILayout.Button(UCL_CodeLocalize.Get("PlayerPrefs.Btn.Refresh"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(80)))
                {
                    RefreshKeys();
                }

                // 搜尋標籤
                GUILayout.Label(UCL_CodeLocalize.Get("AgentCmd.Search"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                // 搜尋輸入框：動態過濾清單內容
                m_SearchFilter = GUILayout.TextField(m_SearchFilter, UCL_GUIStyle.TextFieldStyle);

                // 清除搜尋按鈕
                if (GUILayout.Button("X", UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(20))))
                {
                    m_SearchFilter = string.Empty;
                    GUI.FocusControl(null); // 移除焦點以更新畫面
                }
            }

            GUILayout.Space(UCL_GUIStyle.GetScaledSize(10));
            // 資料顯示區域 — 不再開 BeginScrollView（UCL_EditorPage.OnGUI 已包外層 ScrollViewScope；
            // 巢狀 ScrollView 在 Unity 2021 IMGUI 會拋 InvalidCastException）
            {
                // 遍歷所有鍵，並根據搜尋關鍵字進行篩選
                foreach (string keyName in m_Keys)
                {
                    // 若關鍵字不為空且鍵名不包含關鍵字（忽略大小寫），則跳過
                    if (!string.IsNullOrEmpty(m_SearchFilter) &&
                        keyName.IndexOf(m_SearchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    DrawKeyEntry(keyName);
                }
            }

            // 底部危險操作區域
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(UCL_CodeLocalize.Get("PlayerPrefs.Btn.DeleteAll"), GUILayout.Height(30)))
            {
                // 增加二次確認彈窗，防止手滑導致災難
                UCL.Core.Page.UCL_OptionPage.Create(UCL_CodeLocalize.Get("PlayerPrefs.Dialog.DeleteAll.Title"),
                    UCL_CodeLocalize.Get("PlayerPrefs.Dialog.DeleteAll.Body"),
                    new UCL.Core.Page.ButtonData(UCL_CodeLocalize.Get("Confirm"), () =>
                    {
                        PlayerPrefs.DeleteAll();
                        PlayerPrefs.Save();
                        RefreshKeys();
                    }, UCL_GUIStyle.GetButtonStyle(Color.red, 20)),
                    new UCL.Core.Page.ButtonData(UCL_CodeLocalize.Get("Cancel"), iStyle: UCL_GUIStyle.GetButtonStyle(Color.white, 20))
                );
            }
        }

        /// <summary>
        /// 繪製單個鍵值的編輯條目。
        /// 參數意義：keyName 代表登錄檔中的鍵名。
        /// 邏輯：判斷資料類型（Int, Float, 或 String），繪製對應的編輯欄位與刪除按鈕。
        /// </summary>
        private void DrawKeyEntry(string iKeyName)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                // 這裡採用最通用的字串讀取作為主要編輯手段
                string oldVal = PlayerPrefs.GetString(iKeyName, "");
                string newVal = oldVal;

                using (new GUILayout.HorizontalScope())
                {
                    // 刪除按鈕：移除特定項目
                    if (GUILayout.Button(UCL_CodeLocalize.Get("Delete"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        UCL.Core.Page.UCL_OptionPage.Create(UCL_CodeLocalize.Get("PlayerPrefs.Dialog.DeleteOne.Title"),
                            string.Format(UCL_CodeLocalize.Get("PlayerPrefs.Dialog.DeleteOne.BodyFmt"), iKeyName),
                            new UCL.Core.Page.ButtonData(UCL_CodeLocalize.Get("Delete"), () =>
                            {
                                PlayerPrefs.DeleteKey(iKeyName);
                                PlayerPrefs.Save();
                                RefreshKeys();
                            }, UCL_GUIStyle.GetButtonStyle(Color.red, 20)),
                            new UCL.Core.Page.ButtonData(UCL_CodeLocalize.Get("Cancel"), iStyle: UCL_GUIStyle.GetButtonStyle(Color.white, 20))
                        );
                    }
                    // 嘗試將字串解析為布林值 (支援 "True"/"False" 或 "true"/"false")
                    // 如果解析成功，則顯示 CheckBox 供快速切換
                    if (bool.TryParse(oldVal, out bool boolVal))
                    {
                        bool newBoolVal = UCL_GUILayout.CheckBox(boolVal);
                        if (newBoolVal != boolVal)
                        {
                            newVal = newBoolVal.ToString();
                        }
                    }
                    // 顯示鍵名，使用專案風格的 LabelStyle
                    GUILayout.Label(iKeyName, UCL_GUIStyle.LabelStyle);


                }

                // 嘗試讀取並繪製值。由於 PlayerPrefs 無法得知原始類型，我們需進行嘗試性讀取
                // 注意：Unity 的 PlayerPrefs 其實只支持三種基本類型
                newVal = GUILayout.TextField(newVal, UCL_GUIStyle.TextFieldStyle);



                if (newVal != oldVal)
                {
                    // 若數值發生變動，即時寫回 PlayerPrefs
                    PlayerPrefs.SetString(iKeyName, newVal);
                    PlayerPrefs.Save();
                }
            }
        }
    }
}
