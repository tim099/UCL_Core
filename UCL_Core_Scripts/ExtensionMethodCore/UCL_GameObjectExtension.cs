
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/27 2024 18:03
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core
{
    public static partial class UCL_GameObjectExtensions
    {
        public static void TrySetActive(this GameObject iObj, bool iValue)
        {
            if (iObj == null) return;
            if (iObj.activeSelf == iValue) return;
            iObj.SetActive(iValue);
        }
        public static void ToggleActiveState(this GameObject iObj)
        {
            if (iObj == null) return;
            iObj.SetActive(!iObj.activeSelf);
        }

        /// <summary>
        /// 取得 GameObject 上所有 Component 的 type 短名清單（依 GetComponents 順序）。
        /// 物理意義：給 hierarchy export / debug log / inspector 列 component 摘要用。
        /// 數值影響：純讀取，無副作用。
        /// </summary>
        /// <param name="iObj">目標 GameObject；null → 回空清單</param>
        /// <param name="iIncludeMissing">是否把 missing script (null component) 也列為 "(missing script)"；預設 true</param>
        /// <returns>component type 短名清單，順序與 GetComponents 一致</returns>
        public static List<string> GetComponentTypeNames(this GameObject iObj, bool iIncludeMissing = true)
        {
            // 區塊職責：把 GetComponents 結果轉成 type-name 清單，處理 missing script case
            // 物理意義：caller 只關心『這 GO 掛了什麼 Component』而不必拿到 Component 物件本身
            // 數值影響：無
            var aResult = new List<string>();
            if (iObj == null) return aResult;
            var aComps = iObj.GetComponents<Component>();
            foreach (var aComp in aComps)
            {
                if (aComp == null)
                {
                    if (iIncludeMissing) aResult.Add("(missing script)");
                    continue;
                }
                aResult.Add(aComp.GetType().Name);
            }
            return aResult;
        }
    }
}