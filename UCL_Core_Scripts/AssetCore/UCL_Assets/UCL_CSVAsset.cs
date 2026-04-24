
// AutoHeader
// to change the auto header please go to AutoHeader.cs

using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UnityEngine;
using UCL.Core.CsvLib;
using UCL.Core.UI;

namespace UCL.Core
{
    /// <summary>
    /// [職責] 定義 CSV 資產類別，透過 UCL_ModResourcesData 讀取外部 .csv 檔案並使用 UCL_CSVData 解析。
    /// [物理意義] 作為模組化 CSV 數據在系統中的代表，支援同步與異步讀取，並提供編輯器預覽。
    /// [數值影響] 影響遊戲內配置表、本地化數據或任何表格格式資源的加載效率與存取。
    /// </summary>
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
    [UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditDataType.UCL_CSVAsset)]
    [HelpURL("ucl_core:Docs~/{lang}/API/UCL_Asset/UCL_CSVAsset.md")]
    public class UCL_CSVAsset : UCL_Asset<UCL_CSVAsset>, UCL.Core.UI.UCLI_FieldOnGUI
    {
        #region OnGUI
        // [職責] 實作 UCLI_FieldOnGUI，提供整合式的 CSV 編輯器介面。
        // [物理意義] 在 Inspector 中直接顯示表格編輯格點，並支援與實體檔案的同步讀寫。
        // [參數說明] iFieldName: 欄位名稱。 iDataDic: 介面狀態快取。 iParams: 繪製參數。
        public object OnGUI(string iFieldName, UCL_ObjectDictionary iDataDic, UCL.Core.UI.UCL_GUILayout.DrawObjectParams iParams)
        {
            var result = UCL_GUILayout.DrawField(this, iParams);

            // [職責] 折疊選單控制。
            bool aIsShow = UCL.Core.UI.UCL_GUILayout.Toggle(iDataDic, "CSV_Editor_Show", iFieldName);
            if (!aIsShow) return result;
            void Read()
            {
                CSVData data = GetCSVData();
                if (data != null)
                {
                    iDataDic.SetData("CSV_Rows", data.Count);
                    int maxCols = 0;
                    for (int r = 0; r < data.Count; r++)
                    {
                        var row = data.GetRow(r);
                        maxCols = Mathf.Max(maxCols, row.Count);
                        for (int c = 0; c < row.Count; c++)
                        {
                            iDataDic.SetData($"CSV_Cell_{r}_{c}", row.Get(c));
                        }
                    }
                    iDataDic.SetData("CSV_Cols", maxCols);
                }
            }
            if (!iDataDic.ContainsKey("CSV_Rows"))
            {
                Read();
            }
            using (new GUILayout.VerticalScope("box"))
            {
                var width = GUILayout.Width(UCL_GUIStyle.GetScaledSize(400));
                // --- 工具列 ---
                using (new GUILayout.HorizontalScope(width))
                {
                    // [職責] 設定表格大小（行與列）。
                    int aRows = iDataDic.GetData("CSV_Rows", 1);
                    int aCols = iDataDic.GetData("CSV_Cols", 1);

                    aRows = UCL.Core.UI.UCL_GUILayout.NumField("Rows", aRows, iDataDic.GetSubDic("Rows"));
                    aCols = UCL.Core.UI.UCL_GUILayout.NumField("Cols", aCols, iDataDic.GetSubDic("Cols"));

                    iDataDic.SetData("CSV_Rows", aRows);
                    iDataDic.SetData("CSV_Cols", aCols);

                    // [職責] 從當前指向的實體檔案讀取數據。
                    if (GUILayout.Button(UCL_LocalizeManager.Get("Read"), UCL.Core.UI.UCL_GUIStyle.ButtonStyle)) Read();
                }

                // --- 輸出控制 ---
                using (new GUILayout.HorizontalScope(width))
                {
                    // [職責] 設定輸出檔案的 ID（檔名）。
                    string aExportID = iDataDic.GetData("CSV_ExportID", ID);
                    GUILayout.Label("Export ID", UCL.Core.UI.UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    aExportID = GUILayout.TextField(aExportID, UCL.Core.UI.UCL_GUIStyle.TextFieldStyle);
                    iDataDic.SetData("CSV_ExportID", aExportID);

                    // [職責] 將編輯器內容匯出為實體檔案，並更新資產引用。
                    if (GUILayout.Button(UCL_LocalizeManager.Get("Export"), UCL.Core.UI.UCL_GUIStyle.ButtonStyle))
                    {
                        int aRows = iDataDic.GetData("CSV_Rows", 1);
                        int aCols = iDataDic.GetData("CSV_Cols", 1);
                        CSVData data = new CSVData();
                        for (int r = 0; r < aRows; r++)
                        {
                            var row = data.AddRow();
                            for (int c = 0; c < aCols; c++)
                            {
                                row.AddColume(iDataDic.GetData($"CSV_Cell_{r}_{c}", string.Empty));
                            }
                        }

                        string csvContent = data.ToCSV();
                        string folder = m_ModResourcesData.FileSystemFolderPath;
                        if (!System.IO.Directory.Exists(folder)) System.IO.Directory.CreateDirectory(folder);

                        string fileName = $"{aExportID}.csv";
                        string fullPath = System.IO.Path.Combine(folder, fileName);
                        System.IO.File.WriteAllText(fullPath, csvContent);

                        m_ModResourcesData.m_FileName = fileName;
                        Debug.Log($"CSV Exported to {fullPath}");
                    }
                }

                // --- 資料格點 ---
                int rows = iDataDic.GetData("CSV_Rows", 1);
                int cols = iDataDic.GetData("CSV_Cols", 1);

                float aRowLabelWidth = UCL.Core.UI.UCL_GUIStyle.GetScaledSize(30);
                float aColWidth = UCL.Core.UI.UCL_GUIStyle.GetScaledSize(60);

                // [職責] 繪製頂部欄位代號（A, B, C...）。
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Space(aRowLabelWidth); // 預留行號空間
                    for (int c = 0; c < cols; c++)
                    {
                        GUILayout.Label(GetExcelColumnName(c), UCL.Core.UI.UCL_GUIStyle.LabelStyle, GUILayout.Width(aColWidth));
                    }
                }

                for (int r = 0; r < rows; r++)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        // [職責] 繪製行號（1, 2, 3...）。
                        GUILayout.Label((r + 1).ToString(), UCL.Core.UI.UCL_GUIStyle.LabelStyle, GUILayout.Width(aRowLabelWidth));

                        for (int c = 0; c < cols; c++)
                        {
                            string key = $"CSV_Cell_{r}_{c}";
                            string val = iDataDic.GetData(key, string.Empty);
                            val = GUILayout.TextField(val, UCL.Core.UI.UCL_GUIStyle.TextFieldStyle, GUILayout.Width(aColWidth));
                            iDataDic.SetData(key, val);
                        }
                    }
                }
            }

            return result;
        }
        // [職責] 將欄位索引轉換為 Excel 格式的字母（如 0->A, 25->Z, 26->AA）。
        private string GetExcelColumnName(int iCol)
        {
            string aName = string.Empty;
            while (iCol >= 0)
            {
                aName = (char)('A' + (iCol % 26)) + aName;
                iCol = (iCol / 26) - 1;
            }
            return aName;
        }
        #endregion
        // [職責] 存儲模組資源的核心定位資料。
        // [物理意義] 指向實體磁碟上的 .csv 檔案路徑與名稱。
        // [數值影響] 決定了 ReadAllText 等操作的目標檔案。
        public UCL_ModResourcesData m_ModResourcesData = new UCL_ModResourcesData();

        // [職責] 檢查資產是否為空。
        // [物理意義] 若未指定檔名則視為無效資產。
        public bool IsEmpty => m_ModResourcesData.IsEmpty;

        // [職責] 同步讀取並解析 CSV 資料。
        // [物理意義] 從檔案系統讀取原始字串並轉換為結構化的 CSVData 物件。
        // [計算邏輯] 呼叫 m_ModResourcesData.ReadAllText() 取得字串，若內容存在則實例化 CSVData。
        public CSVData GetCSVData()
        {
            string aCsvText = m_ModResourcesData.ReadAllText();// 從 Mod 目錄讀取原始文字 (string)
            if (string.IsNullOrEmpty(aCsvText)) return null;// 檢查檔案是否存在或為空 (CSVData)
            return new CSVData(aCsvText);// 執行解析並返回資料結構 (CSVData)
        }

        // [職責] 異步獲取 CSV 原始文字。
        // [物理意義] 透過異步磁碟讀取避免主執行緒阻塞。
        // [參數說明] iToken: 用於取消操作的權杖。
        public async UniTask<string> GetCSVTextAsync(CancellationToken iToken)
        {
            var aBytes = await m_ModResourcesData.ReadAllBytesAsync();// 異步讀取位元組數據 (UniTask<byte[]>)
            if (aBytes == null) return string.Empty;
            return System.Text.Encoding.UTF8.GetString(aBytes);// 將 UTF-8 位元組轉換為字串 (string)
        }

        // [職責] 在編輯器預覽視窗中繪製 CSV 內容摘要。
        // [物理意義] 顯示資產 ID 與前幾行數據，方便開發者快速確認內容。
        // [參數說明] iDataDic: 介面狀態字典。 iIsShowEditButton: 是否顯示編輯按鈕。
        override public void Preview(UCL.Core.UCL_ObjectDictionary iDataDic, bool iIsShowEditButton = false)
        {
            // [物理意義] 使用垂直盒子佈局包裹預覽資訊。
            using (var aScope = new GUILayout.VerticalScope("box", GUILayout.ExpandWidth(false)))
            {
                GUILayout.Label($"{UCL_LocalizeManager.Get("Preview")}({ID})", UCL.Core.UI.UCL_GUIStyle.LabelStyle);
                
                if (iIsShowEditButton)
                {
                    ShowEditButtonOnGUI();// 繪製編輯入口按鈕
                }

                // [職責] 讀取並顯示前 5 行內容作為摘要。
                if (!IsEmpty)
                {
                    try
                    {
                        string aText = m_ModResourcesData.ReadAllText();
                        if (!string.IsNullOrEmpty(aText))
                        {
                            var aLines = aText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                            int aDisplayCount = Mathf.Min(aLines.Length, 5);// 限制最大顯示行數為 5 (int)
                            for (int i = 0; i < aDisplayCount; i++)
                            {
                                GUILayout.Label(aLines[i], UCL.Core.UI.UCL_GUIStyle.LabelStyle);
                            }
                            if (aLines.Length > 5) GUILayout.Label("...", UCL.Core.UI.UCL_GUIStyle.LabelStyle);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
            }
        }

        // [職責] 預設建構子。
        public UCL_CSVAsset()
        {
            ID = "New CSVAsset";// 初始化資產標識 (string)
        }

        // [職責] 初始化資源定位資訊。
        // [參數說明] iPath: 相對資料夾路徑。 iName: 檔案名稱。
        public void Init(string iPath, string iName)
        {
            m_ModResourcesData.m_FolderPath = iPath;// 設定子目錄 (string)
            m_ModResourcesData.m_FileName = iName;// 設定目標檔名 (string)
        }
    }

    /// <summary>
    /// [職責] 定義 CSV 資產的條目 (Entry) 類別。
    /// [物理意義] 用於在其他 ScriptableObject 或資料結構中引用特定的 CSV 資產。
    /// </summary>
    [System.Serializable]
    public class UCL_CSVEntry : UCL_AssetEntryDefault<UCL_CSVAsset>
    {
        public const string DefaultID = "Default";
        public UCL_CSVEntry() { m_ID = DefaultID; }
        public UCL_CSVEntry(string iID) { m_ID = iID; }

        // [職責] 直接從條目獲取解析後的 CSV 資料。
        // [計算邏輯] 若資產存在則呼叫其 GetCSVData。
        public CSVData GetCSVData() => GetData()?.GetCSVData();
    }
}
