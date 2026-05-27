
// 區塊職責：提供「UCL_Asset 雙向引用查詢」的純 Runtime 工具（SSOT）。
// 物理意義：
//   - 正向 (Forward)：給一份 asset 實例，走訪其欄位樹，找出它引用到了哪些其他 asset（UCLI_AssetEntry）。
//   - 反向 (Reverse)：給目標 (assetType, id)，掃描所有 UCL_Asset 子型別的全部實例，找出誰引用了它。
//   兩者皆額外記錄「引用發生在哪個欄位路徑（dotted field path，如 m_Effects[2].m_StatusEntry）」。
// 數值影響：純讀取，不修改任何 asset，也不寫檔。
//
// 設計理由 (Tim 2026-05-27 派 task)：
//   原本 Editor 端有 Cmd_FindAssetUsages / Cmd_ResolveAssetReferences，但兩者整支包在 #if UNITY_EDITOR，
//   build 出來的遊戲內不存在。本 UCL_Core 內建 IMGUI 編輯器可在正式遊戲中使用，故引用查詢功能必須能在
//   Runtime 運作 —— 因此本工具只使用 Runtime-safe 的介面：
//     UCL_ModuleService（asset 列舉 / 路徑查詢）、UCL_Util<T>（載入 asset 實例）、純反射。
//   不依賴 UnityEditor / AssetDatabase / UCL_RepoPath 等 Editor-only 設施。
//   邏輯參考既有 Cmd 的反射欄位走訪，但兩者各自獨立（Cmd 輸出檔案給 agent、本工具供 Runtime Page 用）。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// UCL_Asset 雙向引用查詢工具（Runtime-safe）。
    /// <para>正向：<see cref="FindForwardReferences"/> — 給一份 asset，列出它引用了哪些 asset。</para>
    /// <para>反向：<see cref="FindReverseReferences"/> — 給目標 asset，掃描所有 asset 列出誰引用了它。</para>
    /// 兩者皆附帶引用發生的 dotted field path。
    /// </summary>
    public static class UCL_AssetReferenceUtil
    {
        /// <summary>單一引用點紀錄。</summary>
        public class RefHit
        {
            /// <summary>
            /// 此引用「另一端」的 asset 型別。
            /// 正向查詢：被引用的 asset 型別（entry.AssetType）。
            /// 反向查詢：發出引用的 using-side asset 型別（searchType）。
            /// </summary>
            public Type AssetType;
            /// <summary>AssetType.Name 快取（型別解析失敗時可能為空）。</summary>
            public string AssetTypeName;
            /// <summary>另一端 asset 的 ID。</summary>
            public string AssetID;
            /// <summary>引用發生的欄位路徑（dotted field path，如 <c>$.m_Effects[2].m_StatusEntry</c>）。</summary>
            public string FieldPath;
            /// <summary>另一端 asset 所屬模組 ID（查不到為 null）。</summary>
            public string ModuleID;
            /// <summary>另一端 asset 的 JSON 檔路徑（查不到為 null；Runtime 為絕對路徑）。</summary>
            public string JsonPath;
            /// <summary>另一端 asset 是否實際存在（有對應模組 / 檔案）。</summary>
            public bool Exists;
        }

        // ===========================================================
        // Public API
        // ===========================================================

        /// <summary>
        /// 正向查詢：走訪 <paramref name="iAsset"/> 的欄位樹，列出它引用到的所有其他 asset。
        /// </summary>
        /// <param name="iAsset">來源 asset 實例（通常為當前正在編輯的資料，含未存的修改）。</param>
        /// <param name="iMaxFieldDepth">反射遞迴深度上限（防 cycle）。</param>
        /// <returns>每筆代表一個被引用的 asset 與其欄位路徑；依 (型別, ID, 路徑) 排序、去重。</returns>
        public static List<RefHit> FindForwardReferences(object iAsset, int iMaxFieldDepth = 16)
        {
            var aHits = new List<RefHit>();
            if (iAsset == null) return aHits;

            var aVisited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var aPathStack = new List<string>();

            try
            {
                WalkEntries(iAsset, "$", aPathStack, aVisited, iMaxFieldDepth, (entry, fieldPath) =>
                {
                    Type aRefType = TryGetEntryAssetType(entry);
                    string aRefID = TryGetEntryID(entry);
                    if (aRefType == null || string.IsNullOrEmpty(aRefID)) return;

                    var aHit = new RefHit
                    {
                        AssetType = aRefType,
                        AssetTypeName = aRefType.Name,
                        AssetID = aRefID,
                        FieldPath = fieldPath,
                    };
                    FillAssetLocation(aHit, aRefType, aRefID);
                    aHits.Add(aHit);
                });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AssetRefUtil] FindForwardReferences ex: {e.GetType().Name}: {e.Message}");
            }

            return Dedup(aHits);
        }

        /// <summary>
        /// 反向查詢：掃描所有（或指定）UCL_Asset 子型別的全部實例，列出誰引用了 (<paramref name="iTargetType"/>, <paramref name="iTargetID"/>)。
        /// </summary>
        /// <param name="iTargetType">被查詢的目標 asset 型別。</param>
        /// <param name="iTargetID">被查詢的目標 asset ID（大小寫不敏感比對）。</param>
        /// <param name="iMaxFieldDepth">反射遞迴深度上限（防 cycle）。</param>
        /// <param name="iSearchTypes">限定掃描的型別清單；null = 掃所有 UCL_Asset 子型別。</param>
        /// <returns>每筆代表一個 using-side asset 與引用發生的欄位路徑；依 (型別, ID, 路徑) 排序、去重。</returns>
        public static List<RefHit> FindReverseReferences(Type iTargetType, string iTargetID,
            int iMaxFieldDepth = 16, List<Type> iSearchTypes = null)
        {
            var aHits = new List<RefHit>();
            if (iTargetType == null || string.IsNullOrEmpty(iTargetID)) return aHits;

            List<Type> aSearchTypes = iSearchTypes ?? UCLI_Asset.GetAllAssetTypes();
            if (aSearchTypes == null) return aHits;

            foreach (var aSearchType in aSearchTypes)
            {
                if (aSearchType == null) continue;

                List<string> aIDs = GetAllIDs(aSearchType);
                if (aIDs == null) continue;

                foreach (var aID in aIDs)
                {
                    if (string.IsNullOrEmpty(aID)) continue;

                    object aAsset = LoadAsset(aSearchType, aID);
                    if (aAsset == null) continue;

                    var aVisited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                    var aPathStack = new List<string>();

                    // 區塊職責：單份 asset 反射防護 —— 個別壞蛋（malformed / 自訂 getter NRE）不中斷整批掃描。
                    try
                    {
                        WalkEntries(aAsset, "$", aPathStack, aVisited, iMaxFieldDepth, (entry, fieldPath) =>
                        {
                            Type aEntryType = TryGetEntryAssetType(entry);
                            string aEntryID = TryGetEntryID(entry);
                            if (aEntryType == null || string.IsNullOrEmpty(aEntryID)) return;

                            // 比對目標：型別精確相等（對齊 Cmd_FindAssetUsages 語意）+ ID 大小寫不敏感
                            if (aEntryType == iTargetType
                                && string.Equals(aEntryID, iTargetID, StringComparison.OrdinalIgnoreCase))
                            {
                                var aHit = new RefHit
                                {
                                    AssetType = aSearchType,
                                    AssetTypeName = aSearchType.Name,
                                    AssetID = aID,
                                    FieldPath = fieldPath,
                                };
                                FillAssetLocation(aHit, aSearchType, aID);
                                aHits.Add(aHit);
                            }
                        });
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[AssetRefUtil] Skipped {aSearchType.Name}/{aID} reflection error: {e.GetType().Name}: {e.Message}");
                    }
                }
            }

            return Dedup(aHits);
        }

        // ===========================================================
        // Asset enumeration / loading helpers (Runtime-safe)
        // ===========================================================

        /// <summary>反射呼叫 <c>UCL_Util&lt;T&gt;.Util.GetAllIDs(false)</c> 取得某型別所有 asset ID。</summary>
        public static List<string> GetAllIDs(Type iAssetType)
        {
            try
            {
                object aUtil = GetUtil(iAssetType);
                if (aUtil == null) return null;

                var aGetAllIDs = aUtil.GetType().GetMethod("GetAllIDs", new Type[] { typeof(bool) });
                if (aGetAllIDs == null) return null;

                var aRaw = aGetAllIDs.Invoke(aUtil, new object[] { false }) as IEnumerable;
                if (aRaw == null) return null;

                var aList = new List<string>();
                foreach (var o in aRaw)
                {
                    if (o is string s) aList.Add(s);
                }
                return aList;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AssetRefUtil] GetAllIDs({iAssetType?.Name}) ex: {e.Message}");
                return null;
            }
        }

        /// <summary>反射呼叫 <c>UCL_Util&lt;T&gt;.Util.GetAsset(id, true)</c> 載入 asset 實例。</summary>
        public static object LoadAsset(Type iAssetType, string iID)
        {
            try
            {
                object aUtil = GetUtil(iAssetType);
                if (aUtil == null) return null;

                var aGetAsset = aUtil.GetType().GetMethod("GetAsset", new Type[] { typeof(string), typeof(bool) });
                if (aGetAsset == null) return null;

                return aGetAsset.Invoke(aUtil, new object[] { iID, true });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AssetRefUtil] LoadAsset({iAssetType?.Name},{iID}) ex: {e.Message}");
                return null;
            }
        }

        // 區塊職責：取得 UCL_Util<T>.Util 單例（T = iAssetType）
        // 物理意義：UCL_Util<T> 是 AssetCore 的泛型工具入口，提供 GetAllIDs / GetAsset 等 Runtime 方法。
        private static object GetUtil(Type iAssetType)
        {
            if (iAssetType == null) return null;
            var aUtilGenericType = typeof(UCL_Util<>).MakeGenericType(iAssetType);
            var aUtilProp = aUtilGenericType.GetProperty("Util", BindingFlags.Public | BindingFlags.Static);
            return aUtilProp?.GetValue(null);
        }

        // 區塊職責：補上引用另一端 asset 的所屬模組 / JSON 路徑 / 存在性
        // 物理意義：透過 UCL_ModuleService（Runtime-safe）查 AssetConfig；查不到則欄位保持預設值。
        // 數值影響：只讀，不影響 asset 內容。
        private static void FillAssetLocation(RefHit iHit, Type iType, string iID)
        {
            try
            {
                var aConfig = UCL_ModuleService.Ins.GetAssetConfig(iType, iID);
                if (aConfig != null)
                {
                    iHit.Exists = aConfig.Exist;
                    iHit.ModuleID = aConfig.p_Module != null ? aConfig.p_Module.ID : null;
                    // AssetPath 在 p_Module == null 時會 LogError，故僅在 Exist 時取用
                    if (aConfig.Exist)
                    {
                        iHit.JsonPath = aConfig.AssetPath != null ? aConfig.AssetPath.Replace('\\', '/') : null;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AssetRefUtil] FillAssetLocation({iType?.Name},{iID}) ex: {e.Message}");
            }
        }

        // ===========================================================
        // Reflection field-tree walk core（正/反向共用）
        // ===========================================================

        /// <summary>
        /// 反射遞迴走訪 <paramref name="iObj"/> 的欄位樹，對遇到的每個「非空 UCLI_AssetEntry」呼叫 <paramref name="iOnEntry"/>。
        /// <para>與 Cmd 端一致：走 instance fields（含 private，對齊 UCL JSON serializer）、跳過 primitive/string/UnityObject、
        /// 維護 reference cycle guard、用 pathStack 組 dotted field path。</para>
        /// </summary>
        private static void WalkEntries(
            object iObj,
            string iRootName,
            List<string> iPathStack,
            HashSet<object> iVisited,
            int iDepthRemaining,
            Action<UCLI_AssetEntry, string> iOnEntry)
        {
            if (iObj == null) return;
            if (iDepthRemaining <= 0) return;
            if (iObj is string) return;
            Type t = iObj.GetType();
            if (t.IsPrimitive || t.IsEnum) return;
            if (!t.IsClass && !t.IsValueType) return;
            if (iObj is UnityEngine.Object) return;

            // Reference cycle guard（僅 class 實例需要；struct 為值複製）
            if (t.IsClass)
            {
                if (iVisited.Contains(iObj)) return;
                iVisited.Add(iObj);
            }

            // Case 1: AssetEntry → 回報，不再往下鑽（entry 內部的 ID/Type 即引用本身）
            if (iObj is UCLI_AssetEntry aEntry)
            {
                try
                {
                    if (!aEntry.IsEmpty)
                    {
                        iOnEntry(aEntry, JoinPath(iRootName, iPathStack));
                    }
                }
                catch
                {
                    // 個別 entry 屬性壞掉就略過，不影響其他欄位掃描
                }
                return;
            }

            // Case 2: Collection → 以索引擴增 path，逐項展開
            if (iObj is IEnumerable aEnumerable)
            {
                IEnumerator aIt = null;
                try { aIt = aEnumerable.GetEnumerator(); }
                catch { return; }
                if (aIt == null) return;

                int aIdx = 0;
                while (true)
                {
                    object aItem;
                    try
                    {
                        if (!aIt.MoveNext()) break;
                        aItem = aIt.Current;
                    }
                    catch
                    {
                        break; // 迭代失敗 → 結束此集合展開
                    }

                    iPathStack.Add($"[{aIdx}]");
                    WalkEntries(aItem, iRootName, iPathStack, iVisited, iDepthRemaining - 1, iOnEntry);
                    iPathStack.RemoveAt(iPathStack.Count - 1);
                    aIdx++;
                }
                return;
            }

            // Case 3: 一般 class / struct — 走 instance fields（含 private，與 UCL JSON serializer 一致）
            var aFields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var f in aFields)
            {
                if (f.IsNotSerialized) continue;
                if (f.IsDefined(typeof(NonSerializedAttribute), false)) continue;

                Type aFt = f.FieldType;
                if (aFt.IsPrimitive || aFt.IsEnum || aFt == typeof(string)) continue;

                object aVal;
                try { aVal = f.GetValue(iObj); }
                catch { continue; }
                if (aVal == null) continue;

                iPathStack.Add("." + f.Name);
                WalkEntries(aVal, iRootName, iPathStack, iVisited, iDepthRemaining - 1, iOnEntry);
                iPathStack.RemoveAt(iPathStack.Count - 1);
            }
        }

        // ===========================================================
        // Tiny helpers
        // ===========================================================

        // 安全讀 entry.AssetType（自訂 getter 可能 NRE）
        private static Type TryGetEntryAssetType(UCLI_AssetEntry iEntry)
        {
            try { return iEntry?.AssetType; }
            catch { return null; }
        }

        // 安全讀 entry.ID
        private static string TryGetEntryID(UCLI_AssetEntry iEntry)
        {
            try { return iEntry?.ID; }
            catch { return null; }
        }

        // path 組裝：rootName + 各 segment（field 已含 "." 前綴；index 是 "[i]"）
        private static string JoinPath(string iRoot, List<string> iStack)
        {
            if (iStack == null || iStack.Count == 0) return iRoot;
            var aSb = new StringBuilder(iRoot);
            foreach (var aSeg in iStack) aSb.Append(aSeg);
            return aSb.ToString();
        }

        // 去重 + 穩定排序（同一引用點不重複；依 型別→ID→欄位路徑）
        private static List<RefHit> Dedup(List<RefHit> iHits)
        {
            return iHits
                .GroupBy(h => $"{h.AssetTypeName}|{h.AssetID}|{h.FieldPath}", StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(h => h.AssetTypeName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.AssetID, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.FieldPath, StringComparer.Ordinal)
                .ToList();
        }

        // .NET Standard 2.0 在 Unity 沒有 ReferenceEqualityComparer，自前實作
        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
