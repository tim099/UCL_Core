// 區塊職責：UCL_Asset 化的單值設定容器（PlayerPrefs 的 UCL_Asset 版）
// 物理意義：一個 asset = 一個 K-V 對；asset 的 ID 即為 Key、m_Value 為 Value 字串。
//          多 key 用多檔（CurLangKey.json / BGM_Volume.json / ...）。
//          避免單一大檔的 git merge 衝突；每筆 key 獨立 commit / diff
// 數值影響：純資料層；外部存取請走 UCL_Config facade，不要直接操作 asset
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// 通用單值設定 asset（UCL_Asset 版的 PlayerPrefs 條目）。
    /// **一個檔案 = 一個 Key**，asset 的 <see cref="UCL_Asset{T}.ID"/> 就是 Key、<see cref="m_Value"/> 是 Value。
    ///
    /// <para>典型用法：透過 <see cref="UCL_Config"/> facade 取用</para>
    /// <code>
    /// string lang = UCL_Config.GetString("CurLangKey", "en");
    /// UCL_Config.SetString("CurLangKey", "MyProj_CurLang");
    /// UCL_Config.Save();   // 把所有 dirty key 寫到 disk
    /// </code>
    ///
    /// <para>磁碟結構：</para>
    /// <code>
    /// &lt;module&gt;/UCL_Assets/UCL_ConfigAsset/
    ///   ├── CurLangKey.json   { "m_Value": "MyProj_CurLang" }
    ///   ├── BGM_Volume.json   { "m_Value": "80" }
    ///   └── ...
    /// </code>
    ///
    /// <para>vs PlayerPrefs：</para>
    /// 跨機器同步（git）/ Editor 與 Player 共用 / 每筆 key 獨立檔便於 git diff /
    /// 不撞第三方 plugin 的 PlayerPrefs key
    /// </summary>
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Config)]
    [UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditConfigType.UCL_ConfigAsset)]
    public class UCL_ConfigAsset : UCL_Asset<UCL_ConfigAsset>
    {
        public const string DefaultID = "Default";

        // 區塊職責：當前 key 對應的 value 字串
        // 物理意義：所有型別（int / bool / float）都以字串形式儲存，由 UCL_Config facade 用 TryParse 轉換。
        //          這樣設計避免「同一 key 在不同地方被當不同型別讀寫」的混亂
        // 數值影響：直接編輯此欄位需手動呼叫 Save() 才落地
        public string m_Value = string.Empty;

        public UCL_ConfigAsset() { ID = DefaultID; }
        public UCL_ConfigAsset(string iID) { Init(iID); }
    }

    [System.Serializable]
    public class UCL_ConfigAssetEntry : UCL_AssetEntryDefault<UCL_ConfigAsset>
    {
        public UCL_ConfigAssetEntry() { ID = UCL_ConfigAsset.DefaultID; }
        public UCL_ConfigAssetEntry(string iID) { ID = iID; }
    }
}
