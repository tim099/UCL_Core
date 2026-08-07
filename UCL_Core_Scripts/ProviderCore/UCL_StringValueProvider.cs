// 區塊職責：UCL_StringProvider 的預設實作 —— 回傳一個在 Inspector 指定的固定字串。
// 物理意義：它是 implicit operator 的落點 ——
//          `[SerializeReference] UCL_StringProvider s = "abc";` 生出來的就是這個類別的實例。
// 數值影響：純資料持有，GetString() 無副作用且每次結果相同（與可能隨機的其他子類不同）。
// 註：**不引入第三方 Inspector 套件的顯示用 attribute** —— UCL_Core 是跨專案共用模組，
//     消費端專案未必安裝那些套件；類別說明一律走 <summary> 與本註解。
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// 回傳指定 string 值。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/API/ProviderCore/UCL_StringValueProvider.md")]
    public class UCL_StringValueProvider : UCL_StringProvider, UCLI_ShortName
    {
        /// <summary>
        /// 指定 string 值
        /// </summary>
        [Header("指定string值")]
        [SerializeField]
        private string m_Value;

        override public string GetShortName() => ToString();

        public override string ToString()
        {
            // 物理意義：ToString() 是 UCL_ObjectFieldGUILayout 的顯示來源，空字串會畫成一片空白、
            //          看起來像「這個欄位壞了」。空值顯式標示出來，讓「沒填」與「填了空字串」
            //          在 UI 上仍分得出來（GetString() 本身不受影響，照樣回傳空字串）。
            return string.IsNullOrEmpty(m_Value) ? "(empty)" : m_Value;
        }

        public UCL_StringValueProvider() { }

        public UCL_StringValueProvider(string value)
        {
            m_Value = value;
        }

        public override string GetString()
        {
            // null 與空字串在使用端多半等價，且 Unity 反序列化未賦值的 string 欄位會是 null，
            // 統一收斂成空字串，免得每個呼叫點各自防一次。
            return m_Value ?? string.Empty;
        }

        /// <summary>
        /// 讓 UCL_StringProvider 支援直接這樣宣告
        /// [SerializeReference] public UCL_StringProvider test = "abc";
        /// </summary>
        public static implicit operator UCL_StringValueProvider(string data) { return new UCL_StringValueProvider(data); }
    }
}
