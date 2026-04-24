
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
    /// <summary>
    /// [職責] 定義模組化外部資源資產，繼承自 UCL_Asset。
    /// [物理意義] 作為 Mod 資源（如圖片、貼圖）在資產系統中的代表，負責資源的生命週期管理與異步加載。
    /// [數值影響] 影響模組資源的讀取路徑與記憶體釋放行為。
    /// </summary>
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
    [UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditDataType.UCL_ModResourceAsset)]
    [HelpURL("ucl_core:Docs~/{lang}/API/UCL_Asset/UCL_ModResourceAsset.md")]
    public class UCL_ModResourceAsset : UCL_Asset<UCL_ModResourceAsset>, IDisposable
    {
        // [職責] 存儲模組資源的核心資料。
        // [物理意義] 包含檔案路徑、名稱與所屬模組 ID 等關鍵資訊。
        // [數值影響] 決定了實體檔案的定位邏輯。
        public UCL_ModResourcesData m_ModResourcesData = new UCL_ModResourcesData();

        // [職責] 檢查資產內容是否為空。
        // [物理意義] 如果沒有指定檔案名稱，則視為空資產。
        // [數值影響] 返回布林值 (bool)。
        public bool IsEmpty => Data.IsEmpty;

        // [職責] 取得內部的資源資料實例。
        // [物理意義] 簡化對 m_ModResourcesData 的訪問。
        private UCL_Data Data => m_ModResourcesData;

        // [職責] 異步獲取 Sprite 資源。
        // [物理意義] 先執行加載邏輯，再提取 Sprite 物件。
        // [參數說明] iToken: 用於取消異步操作的權杖。
        // [計算邏輯] 呼叫 Data.LoadAsync 確保資源就緒後返回 Sprite。
        public async UniTask<Sprite> GetSpriteAsync(CancellationToken iToken)
        {
            await Data.LoadAsync(iToken);// 異步加載原始數據
            return Data.GetSprite();// 返回對應的 Sprite 物件
        }

        // [職責] 異步獲取 Texture2D 資源。
        // [物理意義] 將加載後的 Sprite 轉換為 Texture2D。
        // [參數說明] iToken: 用於取消異步操作的權杖。
        // [計算邏輯] 加載後檢查取消狀態，並提取 Sprite 的 texture 屬性。
        public async UniTask<Texture2D> GetTextureAsync(CancellationToken iToken)
        {
            await Data.LoadAsync(iToken);// 執行異步讀取
            iToken.ThrowIfCancellationRequested();// 檢查是否已要求取消
            return Data.GetSprite().texture;// 取得貼圖物件
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
        // [職責] 在編輯器中繪製資產預覽。
        // [物理意義] 顯示資產 ID 與預覽標籤，並可選顯示編輯按鈕。
        // [參數說明] iDataDic: 存儲 GUI 狀態的字典。 iIsShowEditButton: 是否顯示編輯按鈕。
        override public void Preview(UCL.Core.UCL_ObjectDictionary iDataDic, bool iIsShowEditButton = false)
        {
            // [物理意義] 使用垂直區塊包裹預覽內容。
            using (var aScope = new GUILayout.VerticalScope("box", GUILayout.ExpandWidth(false)))
            {
                // [計算邏輯] 從本地化系統取得 "Preview" 文字並串接 ID。
                GUILayout.Label($"{UCL_LocalizeManager.Get("Preview")}({ID})", UCL.Core.UI.UCL_GUIStyle.LabelStyle);
                
                if (iIsShowEditButton)
                {
                    ShowEditButtonOnGUI();// 繪製跳轉至編輯頁面的按鈕
                }
            }
        }

        // [職責] 預設建構子。
        // [物理意義] 初始化新資產的預設 ID。
        public UCL_ModResourceAsset()
        {
            ID = "New SpriteAsset";// 設定預設 ID 名稱 (string)
        }

        // [職責] 實作 IDisposable 介面，釋放資源。
        // [物理意義] 呼叫底層資料的 Release，清空記憶體中的貼圖快取。
        public void Dispose()
        {
            Data.Release();// 執行資源釋放邏輯
        }

        // [職責] 初始化資源路徑與名稱。
        // [物理意義] 直接設定內部的資料路徑資訊。
        // [參數說明] iPath: 資料夾路徑。 iName: 檔案名稱。
        public void Init(string iPath, string iName)
        {
            m_ModResourcesData.m_FolderPath = iPath;// 設定資料夾路徑 (string)
            m_ModResourcesData.m_FileName = iName;// 設定檔案名稱 (string)
        }
    }

    /// <summary>
    /// [職責] 定義模組資源的條目 (Entry) 類別。
    /// [物理意義] 用於在其他資產中引用 UCL_ModResourceAsset，支援預設 ID 與空值檢查。
    /// </summary>
    [System.Serializable]
    public class UCL_ModResourceEntry : UCL_AssetEntryDefault<UCL_ModResourceAsset>
    {
        // [物理意義] 定義預設的資產 ID 常數。
        public const string DefaultID = "Default";

        // [職責] 建構子，初始化為預設 ID。
        public UCL_ModResourceEntry() { m_ID = DefaultID; }

        // [職責] 帶 ID 參數的建構子。
        public UCL_ModResourceEntry(string iID) { m_ID = iID; }

        // [職責] 取得對應資產的內部資料。
        public UCL_ModResourcesData Data => GetData().m_ModResourcesData;

        // [職責] 檢查條目是否為空。
        // [物理意義] 同時檢查條目本身是否未設定，以及指向的資產是否內容為空。
        // [計算邏輯] 若基底檢查為空或資產不存在/為空，則返回 true。
        public override bool IsEmpty 
        {
            get
            {
                if (base.IsEmpty) return true;// 檢查基底條目狀態
                try
                {
                    var data = GetData();// 獲取資產實例
                    return data.IsEmpty;// 檢查資產內部內容
                }
                catch // 若資產不存在則捕捉例外並視為空
                {
                    return true;
                }
            }
        }
    }
}
