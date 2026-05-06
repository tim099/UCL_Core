// 文件關聯：對應的計畫文件
// 繁體中文: Docs~/zh-Hant/Plan/SerializeReference_Symmetry_Plan.md
//
// 區塊職責：本檔提供「多型欄位 / 多型元素 / 子類列舉」的單一事實來源（SSOT），
//          供 GUI 編輯路徑（UCL_GUILayoutDrawObject）與 JSON 序列化路徑
//          （UCL_JsonLib SaveFieldsToJson / LoadFieldFromJson）共同使用。
// 物理意義：把「這個欄位該不該被當成多型？」「這個型別當作集合元素時該不該多型？」
//          「base 型對應的合法具體子類有哪些？」這三個原本散落在多個檔案的判斷
//          收斂到一個地方，避免 GUI 與 JSON 兩邊判定不一致。
// 數值影響：純讀取 / 純判斷；不修改任何狀態。
//
// Step 1（本檔）：純新增，無現有 caller — 零行為變更，可獨立合併。
// Step 2/3 才會把 GUI 與 JSON 的呼叫點改用本 helper。
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// UCL_Asset 多型支援的單一事實來源（SSOT）。
    /// <para>
    /// UCL_Core 對「polymorphic field」的判定原本散落在三處（GUI dropdown 啟用條件、
    /// JSON Save 包 ClassName 條件、JSON Load 還原條件），且各自的條件不對稱
    /// （詳見 <c>Docs~/zh-Hant/Plan/SerializeReference_Symmetry_Plan.md</c>）。
    /// 本 helper 把判定收斂為三個 API：
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="IsPolymorphicField(FieldInfo)"/> — 「這個 <b>欄位</b>本身要多型？」</item>
    /// <item><see cref="IsPolymorphicElement(Type)"/> — 「這個<b>型別</b>當作集合元素時要多型？」</item>
    /// <item><see cref="GetConcreteSubtypes(Type)"/> — base 型對應的合法具體子類清單</item>
    /// </list>
    /// </summary>
    public static class UCL_PolymorphicHelper
    {
        /// <summary>
        /// 判定「此欄位被設計為多型」。
        /// <para>
        /// 主要訊號：Unity 內建 <see cref="SerializeReference"/> attribute — 設計者明確聲明
        /// 此處要存子類資訊。GUI dropdown 與 JSON ClassName 包裝皆以此為觸發。
        /// </para>
        /// <para>
        /// 物理意義：欄位上有 <c>[SerializeReference]</c> ⇒ 欄位的執行期值可能是
        /// <c>fieldInfo.FieldType</c>（宣告型）的子類別實例，序列化 / 編輯時必須帶上型別資訊。
        /// </para>
        /// </summary>
        /// <param name="iField">要檢查的欄位</param>
        /// <returns>true：欄位需要多型處理；false：當作宣告型直接處理即可</returns>
        public static bool IsPolymorphicField(FieldInfo iField)
        {
            // 防呆：null 欄位視為「不多型」
            if (iField == null) return false;
            // Unity 內建 [SerializeReference] — 唯一觸發訊號（向後相容用 UCL_SerializeReference 已 obsolete）
            return iField.GetCustomAttribute<SerializeReference>() != null;
        }

        /// <summary>
        /// 判定「此型別當作集合元素時要多型」。
        /// <para>
        /// 主要訊號：型別實作 <see cref="UCLI_TypeList"/> 或 <see cref="UCLI_TypeListable"/>
        /// — UCL_Core 既有的「集合元素多型」標記。
        /// </para>
        /// <para>
        /// 物理意義：<c>List&lt;Base&gt;</c> 的 <c>Base</c>、<c>Dictionary&lt;K,V&gt;</c> 的 <c>V</c>
        /// 等，若實作這兩個介面之一，UCL_JsonLib 的 list / dict 序列化路徑會 per-item 包 ClassName
        /// 以保留每個元素的子類資訊。
        /// </para>
        /// <para>
        /// 注意：<see cref="UnityJsonSerializableObject"/> 子類由其自身的 ClassNameID 機制處理
        /// （見 <c>UCL_JsonLib.cs</c> SaveDataToJson 約 line 375 的 OR 條件），不需要本 helper 介入。
        /// </para>
        /// </summary>
        /// <param name="iElementType">集合元素的宣告型別</param>
        /// <returns>true：元素需要多型 wrap；false：直接以宣告型序列化</returns>
        public static bool IsPolymorphicElement(Type iElementType)
        {
            // 防呆：null 型別不多型
            if (iElementType == null) return false;
            // UCLI_TypeList（舊介面）或 UCLI_TypeListable（新介面）兩者皆視為多型訊號
            return typeof(UCLI_TypeList).IsAssignableFrom(iElementType)
                || typeof(UCLI_TypeListable).IsAssignableFrom(iElementType);
        }

        /// <summary>
        /// 拉出 base 型對應的合法具體子類清單，供 GUI dropdown / JSON 反序列化共用。
        /// <para>
        /// 目前直接 delegate 到 <see cref="UCLI_TypeListable.GetAllITypes(Type)"/>，保持與既有
        /// GUI 行為完全一致：
        /// </para>
        /// <list type="bullet">
        /// <item>filter：跳過 abstract、跳過 <c>[UCL_IgnoreInTypeListable]</c></item>
        /// <item>sort：依 <c>[UCL_Sort].m_SortOrder</c> 升冪，無屬性者排最後</item>
        /// <item>cache：靜態 dict，型別查詢結果整批快取</item>
        /// </list>
        /// <para>
        /// 物理意義：把「dropdown 顯示哪些選項」「JSON ClassName 反查時哪些是合法目標」
        /// 收斂到同一函式 — 避免兩邊清單不一致。
        /// </para>
        /// </summary>
        /// <param name="iBaseType">基底型別（abstract class / interface）</param>
        /// <returns>
        /// 合法的具體子類清單（已 sort、已 filter）；找不到回空 list 不為 null。
        /// 回傳型別刻意用 <see cref="List{Type}"/>（非 IList&lt;Type&gt;）以與
        /// <see cref="UCLI_TypeListable.GetTypeNames(List{Type})"/> 等既有 API 簽章相容。
        /// </returns>
        public static List<Type> GetConcreteSubtypes(Type iBaseType)
        {
            // 防呆：null 基底回空清單
            if (iBaseType == null) return s_Empty;
            // 直接走既有 cache 與 filter 邏輯，行為等價
            return UCLI_TypeListable.GetAllITypes(iBaseType);
        }

        // 區塊職責：null 輸入時返回的共享空清單，避免每次都 new
        // 物理意義：caller 拿到的是 List<Type> 型空清單，與 GetTypeNames 等下游 API 簽章相容
        // 數值影響：節省 null-input case 的 GC 壓力
        private static readonly List<Type> s_Empty = new List<Type>(0);
    }
}
