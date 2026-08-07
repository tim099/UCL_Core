// 區塊職責：字串提供者的抽象基底 —— 讓「一個 string 欄位」可以被替換成任意求值策略
//          （固定值 / 查表 / 串接 / 隨機 / 依語系…），而使用端只認 GetString()。
// 物理意義：Provider 模式 —— 抽象基底 + 一個「回傳指定值」的預設實作 + 雙向 implicit operator，
//          讓 [SerializeReference] 欄位既能直接指派字面值、也能換成別的策略而不改使用端。
// 數值影響：本身不持有資料、不產生副作用；求值成本由各子類決定。
//          ⚠ GetString() **不保證每次回傳相同結果**（隨機／時間相關的子類是合法的），
//            所以呼叫端若需要同一幀內一致，請自行取一次存起來，不要重複呼叫當快取用。
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// 字串提供者基底。使用端宣告 <see cref="UCL_StringProvider"/> 欄位，
    /// 實際行為由子類決定（預設實作見 <see cref="UCL_StringValueProvider"/>）。
    /// </summary>
    [System.Serializable]
    public abstract class UCL_StringProvider : UCL.Core.JsonLib.UnityJsonSerializable, UCLI_ShortName, UCLI_TypeListable
    {
        public abstract string GetString();

        virtual public string GetShortName() => this.ToString();

        /// <summary>
        /// 可以直接對 UCL_StringProvider 取出 string 值。
        /// </summary>
        /// <remarks>
        /// ⚠ null provider 回傳 <see cref="string.Empty"/> 而不是 null —— 「null provider 回傳型別預設值」
        /// 是本 Provider 家族的共同慣例，讓使用端不必在每個取值點防 NullReferenceException。
        /// 代價是「沒有 provider」與「provider 回傳空字串」在這個轉換後**分不出來**；
        /// 需要分辨的呼叫端請不要走這個 operator，直接判 <c>provider == null</c> 再呼叫 GetString()。
        /// </remarks>
        public static implicit operator string(UCL_StringProvider data) => data == null ? string.Empty : data.GetString();

        /// <summary>
        /// 讓 UCL_StringProvider 支援直接這樣宣告
        /// [SerializeReference] public UCL_StringProvider test = "abc"; // 等同於 new UCL_StringValueProvider("abc");
        /// </summary>
        public static implicit operator UCL_StringProvider(string data) { return new UCL_StringValueProvider(data); }
    }
}
