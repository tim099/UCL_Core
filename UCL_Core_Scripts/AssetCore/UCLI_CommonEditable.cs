using System.Collections;
using System.Collections.Generic;
using UCL.Core;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// 預覽介面
    /// Preview interface
    /// </summary>
    public interface UCLI_Preview
    {
        /// <summary>
        /// 預覽
        /// Preview OnGUI
        /// </summary>
        /// <param name="iIsShowEditButton">是否顯示編輯按鈕</param>
        void Preview(UCL.Core.UCL_ObjectDictionary iDataDic, bool iIsShowEditButton = false);
    }
    public interface UCLI_CommonEditable : IJsonSerializable, UCLI_ID
    {
        /// <summary>
        /// 路徑
        /// Save path in file system
        /// </summary>
        string SavePath { get; }
        /// <summary>
        /// 存檔
        /// </summary>
        JsonData Save();
        /// <summary>
        /// 根據ID刪除資料
        /// </summary>
        /// <param name="iID"></param>
        void Delete(string iID);
        /// <summary>
        /// 繪製編輯器
        /// </summary>
        void OnGUI(UCL.Core.UCL_ObjectDictionary iDataDic);
        /// <summary>
        /// 複製一份(避免編輯時改到原本的資料
        /// </summary>
        /// <returns></returns>
        UCLI_CommonEditable CloneInstance();

        /// <summary>
        /// 清除緩存
        /// </summary>
        void ClearCache();
    }
}
