// 區塊職責：提供 PlayerPrefs-like 靜態 facade 給 UCL_ConfigAsset（每個 key 對應一個獨立 asset）
// 物理意義：上層程式 99% 走這個 facade。Key = UCL_ConfigAsset 的 ID，Value 存於 m_Value。
//          找不到對應 asset 時 Get* 回傳 default 值（不丟例外）；Set* 會 lazy create 對應 asset。
//          Save() 一次寫所有 dirty key 到 disk
// 數值影響：純資料層；Set* 不會自動 Save，需顯式呼叫 Save() 才落地
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// PlayerPrefs-like facade for <see cref="UCL_ConfigAsset"/>。
    ///
    /// <para>每個 key 對應一個獨立的 UCL_ConfigAsset 檔（asset ID = Key、m_Value = Value）。</para>
    ///
    /// <para>典型用法：</para>
    /// <code>
    /// // 讀（找不到 asset 回 default，不丟例外）
    /// string lang = UCL_Config.GetString("CurLangKey", "en");
    /// int  volume = UCL_Config.GetInt("BGM_Volume", 80);
    ///
    /// // 寫（Set 加入 dirty 集合，不會立即落地）
    /// UCL_Config.SetString("CurLangKey", "zh-Hant");
    /// UCL_Config.SetInt("BGM_Volume", 50);
    ///
    /// // Save 把所有 dirty key 各自寫成獨立 .json 檔
    /// UCL_Config.Save();
    /// </code>
    ///
    /// <para>vs PlayerPrefs：</para>
    /// 跨機器同步（git）/ Editor 與 Player 共用 / 每筆 key 獨立檔便於 git diff /
    /// 不撞第三方 plugin 的 PlayerPrefs key
    /// </summary>
    public static class UCL_Config
    {
        // 區塊職責：in-memory 暫存區，存「已被 Set 但還沒 Save」的 key/value
        // 物理意義：每個 key 是獨立 asset 檔，新 key 在 Save 之前 disk 上根本沒檔（aConfig.Exist=false）。
        //          直接靠 asset cache 不行 — TryGetAsset 看 Exist=false 就回 null，會讓「Set 後立刻 Get」
        //          回到 default 值（不符 PlayerPrefs 預期）。所以額外維護這個 map：
        //          - SetString → 寫進 s_PendingValues
        //          - GetString → 優先查 s_PendingValues，沒有才走 disk asset
        //          - Save() → flush 所有 pending key 各自 .Save()，成功則從 map 移除
        //          - DeleteKey → 同步從 map 移除
        // 數值影響：Save() 失敗的 key 留在 map（下次 Save 會再試）；HasKey 也會檢查 map
        private static readonly Dictionary<string, string> s_PendingValues = new Dictionary<string, string>();

        // ===========================================================
        // Asset 取用 — 找不到不丟例外，回 null
        // ===========================================================

        /// <summary>
        /// 取得 key 對應的 ConfigAsset 實例。**asset 不存在或 ModuleService 未就緒時回 null**。
        /// 由各 Get* 方法在 null 時回傳 default 值。
        /// </summary>
        private static UCL_ConfigAsset TryGetAsset(string iKey)
        {
            if (string.IsNullOrEmpty(iKey)) return null;
            try
            {
                if (!UCL_ModuleService.Initialized) return null;
                var aConfig = UCL_ModuleService.Ins.GetAssetConfig(typeof(UCL_ConfigAsset), iKey);
                if (aConfig == null) return null;

                // 區塊職責：用真正的 disk file 存在性判斷，避開 aConfig.Exist 只檢查 module 關聯
                // 物理意義：aConfig.Exist == (p_Module != null) — 是「有沒有 module 對應」，
                //          不是「.json 檔在不在」。要避免 GetData 內 CreateData 對 missing 檔
                //          噴 !File.Exists 例外（每幀 OnGUI 呼叫會噴爆 console），必須真檢查
                // 數值影響：純讀取；File.Exists 是輕量 syscall
                if (!aConfig.Exist) return null;
                string aPath = aConfig.AssetPath;
                if (string.IsNullOrEmpty(aPath) || !File.Exists(aPath)) return null;

                // 雙重保險：即便檔在，GetData 內部還是可能因其他原因 throw（解析錯、type mismatch 等）
                return UCL_ConfigAsset.Util.GetData(iKey, true);
            }
            catch
            {
                // 任何意外（ModuleService 未就緒、asset 路徑解析失敗、parse 錯）→ 都當作不存在
                return null;
            }
        }

        // ===========================================================
        // Has / Delete
        // ===========================================================

        public static bool HasKey(string iKey)
        {
            if (string.IsNullOrEmpty(iKey)) return false;
            if (s_PendingValues.ContainsKey(iKey)) return true;
            return TryGetAsset(iKey) != null;
        }

        /// <summary>刪除 key 對應的 asset 檔（同時從 pending map 移除）。</summary>
        public static void DeleteKey(string iKey)
        {
            if (string.IsNullOrEmpty(iKey)) return;
            s_PendingValues.Remove(iKey);
            try
            {
                if (!UCL_ModuleService.Initialized) return;
                var aConfig = UCL_ModuleService.Ins.GetAssetConfig(typeof(UCL_ConfigAsset), iKey);
                if (aConfig == null || !aConfig.Exist) return;
                UCL_ConfigAsset.Util.Delete(iKey);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[UCL_Config] DeleteKey('{iKey}') failed: {e.Message}");
            }
        }

        // ===========================================================
        // Save — 把 dirty keys 一次寫到 disk
        // ===========================================================

        /// <summary>把所有 pending key 各自寫成獨立 ConfigAsset .json 檔。</summary>
        public static void Save()
        {
            if (s_PendingValues.Count == 0) return;
            if (!UCL_ModuleService.Initialized)
            {
                Debug.LogWarning($"[UCL_Config] Save skipped — ModuleService not initialized ({s_PendingValues.Count} pending key(s) deferred)");
                return;
            }

            // 區塊職責：把每筆 pending (key, value) flush 到對應 asset 檔
            // 物理意義：對每個 key 用 new UCL_ConfigAsset(key) 建 in-memory asset，set m_Value，
            //          呼叫 .Save()。Save 內部會 CreateAssetConfig + SaveAsset → 寫成 .json
            //          失敗的 key 留在 pending map，下次 Save 會再試
            var aKeysToFlush = new List<string>(s_PendingValues.Keys);
            foreach (var aKey in aKeysToFlush)
            {
                try
                {
                    var aAsset = new UCL_ConfigAsset(aKey);
                    aAsset.m_Value = s_PendingValues[aKey] ?? string.Empty;
                    aAsset.Save();
                    s_PendingValues.Remove(aKey);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[UCL_Config] Save key '{aKey}' failed: {e.Message}");
                    // 不從 pending 移除，下次 Save 會再嘗試
                }
            }
        }

        // ===========================================================
        // Get* / Set* — 字串為基礎，型別轉換用 Try*
        // ===========================================================

        // ---- string ----
        public static string GetString(string iKey, string iDefault = "")
        {
            if (string.IsNullOrEmpty(iKey)) return iDefault;
            // 優先查 pending（Set 後尚未 Save 的值）→ 否則查 disk asset → 都沒有回 default
            if (s_PendingValues.TryGetValue(iKey, out var aPending)) return aPending;
            var aAsset = TryGetAsset(iKey);
            return aAsset != null ? aAsset.m_Value : iDefault;
        }

        public static void SetString(string iKey, string iValue)
        {
            if (string.IsNullOrEmpty(iKey)) return;
            // 直接寫進 pending map — 不必 lazy create asset object（Save 時才 new + 落地）
            s_PendingValues[iKey] = iValue ?? string.Empty;
        }

        // ---- int ----
        public static int GetInt(string iKey, int iDefault = 0)
        {
            string aRaw = GetString(iKey, null);
            if (string.IsNullOrEmpty(aRaw)) return iDefault;
            return int.TryParse(aRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : iDefault;
        }

        public static void SetInt(string iKey, int iValue)
            => SetString(iKey, iValue.ToString(CultureInfo.InvariantCulture));

        // ---- float ----
        public static float GetFloat(string iKey, float iDefault = 0f)
        {
            string aRaw = GetString(iKey, null);
            if (string.IsNullOrEmpty(aRaw)) return iDefault;
            return float.TryParse(aRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : iDefault;
        }

        public static void SetFloat(string iKey, float iValue)
            => SetString(iKey, iValue.ToString(CultureInfo.InvariantCulture));

        // ---- bool ----
        public static bool GetBool(string iKey, bool iDefault = false)
        {
            string aRaw = GetString(iKey, null);
            if (string.IsNullOrEmpty(aRaw)) return iDefault;
            // 支援「True / False / true / false / 1 / 0」
            if (bool.TryParse(aRaw, out var v)) return v;
            if (aRaw == "1") return true;
            if (aRaw == "0") return false;
            return iDefault;
        }

        public static void SetBool(string iKey, bool iValue)
            => SetString(iKey, iValue ? "True" : "False");
    }
}
