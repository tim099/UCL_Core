
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/06 2026
// 文件關聯：對應的計畫文件
// 繁體中文: Docs~/zh-Hant/Plan/SerializeReference_Symmetry_Plan.md
//
// 區塊職責：本檔提供「型別反射 metadata 的單一事實來源（SSOT）」，由 GUI 編輯路徑
//          （UCL_GUILayoutDrawObject）與 JSON 序列化路徑（UCL_JsonLib SaveFieldsToJson /
//          LoadFieldFromJson）共同使用。原本 GUI 內嵌的 FieldInfoCache / TypeFieldInfoCache
//          屬於 GUI 私有；JSON 兩條路徑各自走一次 GetAllFieldsUnityVer + GetCustomAttribute。
//          兩邊各算各的，導致「[SerializeReference] 偵測」「[UCL_HideInJson] 偵測」等判斷
//          散落多處、容易漂移。本檔把 metadata 抽到一處，**穩定性 + 可讀性優先**，效能不是
//          主要考量（cache 本來就只算一次，省下的反射呼叫是順帶收益）。
// 物理意義：對任意 Type，依 SaveMode（Normal / Unity）走訪欄位，per-field 把 GUI 與 JSON
//          所需的所有 attribute / 旗標一次抓齊存成 UCL_FieldEntry；type-level metadata
//          （[EnableUCLEditor] / methods）一併存在 UCL_TypeReflectCache。GUI cache 與 JSON
//          反射改讀本快取，**caller 自己用旗標過濾**（不在 cache 階段預過濾）。
// 數值影響：純讀取 / 純判斷；不修改任何狀態。本檔執行後 GUI / JSON 兩條路徑取得的 metadata
//          應與原本各自計算的結果一致（驗收門檻為 round-trip byte-identical）。
//
// 階段：Step 1.5 — 純新增檔；GUI 與 JSON 的 caller 改寫在後續 commits。
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
// 注意：刻意**不**引入 UCL.Core.LocalizeLib —— 本檔在 JSON 載入路徑會被觸發，
// 若構造期呼叫 UCL_LocalizeManager 會與 UCL_LocalizeAsset 自身的反序列化形成循環
// （JSON load → cache build → LocalizeManager → load UCL_LocalizeAsset → JSON load → ...）
// Header 在地化交給 GUI adapter 做 — 只 GUI 路徑會觸發，不會引爆 JSON 載入時的循環。

namespace UCL.Core
{
    /// <summary>
    /// 單一欄位的反射 metadata 集合。
    /// <para>
    /// 替代原本 <c>UCL_GUILayoutDrawObject.FieldInfoCache</c>（GUI 私有）+ JSON 端散落的 inline
    /// <c>GetCustomAttribute&lt;X&gt;()</c>。所有 GUI / JSON 兩邊**任一方**會用到的 attribute /
    /// 旗標都在這裡抓齊一次。
    /// </para>
    /// <para>
    /// 命名約定：以 <c>m_HideOnGUI</c> / <c>m_HideInJson</c> 區分兩條路徑各自的隱藏旗標；
    /// 不命名為單純 <c>m_Hidden</c> 避免兩邊誤共用。
    /// </para>
    /// </summary>
    public class UCL_FieldEntry
    {
        // ===== 基本資訊 =====

        /// <summary>原始的 FieldInfo（仍要保留供 SetValue / GetValue 使用）。</summary>
        public FieldInfo m_FieldInfo;

        // 注意：刻意**不**在這裡 cache「全部 attribute」陣列。
        // <c>GetCustomAttributes(true)</c> 會構造所有 attribute 實例 — 某些 attribute（如
        // <c>UCL_FolderExplorerAttribute</c>）的 ctor 會呼叫 <c>UCL_ModuleService</c> 等
        // runtime service。JSON 載入路徑很早就會建構此 cache（在 ModuleService 完成 init 前），
        // 觸發那些 attribute ctor 會 NRE。需要全 attribute 列表時，由 GUI adapter 在自己的
        // 構造期（render 時，service 已 ready）呼叫 <c>m_FieldInfo.GetCustomAttributes()</c>。
        // 本 cache 只存 JSON / GUI 兩邊都會直接讀的「特定 attribute 旗標」（見下方）— 這些
        // 旗標走 <c>GetCustomAttribute&lt;T&gt;()</c> 只構造 T 一個型別，不會誤觸發其他 ctor。

        // ===== GUI metadata =====

        /// <summary>
        /// [Header] attribute 的**原始**文字（未 localize）。null 表示無 Header。
        /// <para>
        /// 注意：刻意**不**在 cache 構造期就呼叫 <c>UCL_LocalizeManager</c> — 會與
        /// <c>UCL_LocalizeAsset</c> 自身的 JSON 反序列化形成循環（cache build → Localize →
        /// load LocalizeAsset → JSON load → cache build）。在地化由 GUI adapter 自行處理
        /// （只 GUI 路徑會觸發 LocalizeManager，JSON 路徑不會碰）。
        /// </para>
        /// </summary>
        public string m_HeaderRaw;

        /// <summary>
        /// FieldType 自身有 <c>[AlwaysExpendOnGUI]</c> attribute → GUI 預設展開、不需要點擊摺疊。
        /// 注意：是 FieldType 上的 attribute（不是欄位），與原 GUI 邏輯一致。
        /// </summary>
        public bool m_AlwaysExpendOnGUI;

        /// <summary>
        /// GUI 應隱藏：欄位有 <c>[HideInInspector]</c> 或 <c>[UCL_HideOnGUI]</c>。
        /// 注意：當 cache 由 <c>GetAllFieldsUnityVer</c> 建出時，<c>[HideInInspector]</c> 已在
        /// 來源端被過濾，旗標通常為 false；當由 <c>GetAllFieldsUntil</c> 建出（Normal mode）時
        /// 才可能為 true。本旗標只描述「該欄位是否被宣告為 GUI hidden」，不代表必然存在於 entries。
        /// </summary>
        public bool m_HideOnGUI;

        // ===== JSON metadata =====

        /// <summary>JSON 應跳過：欄位有 <c>[UCL_HideInJson]</c>。</summary>
        public bool m_HideInJson;

        /// <summary>FieldType 是否為 delegate 子類（<c>MulticastDelegate</c>）— JSON 一律跳過。</summary>
        public bool m_IsMulticastDelegate;

        /// <summary>
        /// <c>[UCL_Conditional]</c> 物件本身。null 表示無此 attribute。
        /// 警告：<see cref="UCL.Core.PA.ConditionalAttribute.IsShow(object)"/> 是 instance-level 判斷
        /// （依 obj 當下狀態決定），**caller 必須 runtime 呼叫**，不可 cache 結果。
        /// </summary>
        public UCL.Core.PA.ConditionalAttribute m_Conditional;

        /// <summary>
        /// <c>[UCL_FormerlySerializedAs]</c> 物件本身。null 表示無此 attribute。
        /// JSON load 端遇 JSON 不含當前欄位名時，會 fallback 用此 attribute 內的舊名重試。
        /// </summary>
        public UCL.Core.ATTR.UCL_FormerlySerializedAsAttribute m_FormerlyAs;

        // ===== 共用 metadata =====

        /// <summary>
        /// 欄位有 <c>[SerializeReference]</c> attribute — GUI dropdown 會啟用，JSON 會包 ClassName。
        /// 透過 <see cref="UCL_PolymorphicHelper.IsPolymorphicField(FieldInfo)"/> 計算，與該 helper
        /// 維持一致；helper 是 SSOT，本旗標是其結果的 cache。
        /// </summary>
        public bool m_IsPolymorphicField;

        // ===== 冷門 attribute 的 lazy cache =====

        // 區塊職責：對「不在上方常用旗標清單」內的 attribute 提供 lazy 取得 + 整 session cache 的 API
        // 物理意義：常用 attribute（SerializeReference / HideInJson / Conditional / FormerlyAs / HideOnGUI / AlwaysExpendOnGUI）
        //          已在 ctor 用 GetCustomAttribute<T>() 預抓 — 它們的 ctor 確認過不會觸發早期 NRE。
        //          冷門 attribute（如 [UCL_FolderExplorer] 之類 ctor 會打 service 的）若 ctor 內 eager 構造會撞
        //          load-time NRE；改成第一次呼叫 GetAttr<T>() 才構造，此時若是 GUI render 觸發，
        //          service 通常已 ready；若是 JSON 載入觸發，本來就不會走到這條 API（caller 是 GUI）。
        // 數值影響：dict 第一次填入後 O(1) 命中；攜帶 null（cached miss）也計入避免重複反射。
        private Dictionary<Type, Attribute> m_AttrCache;

        /// <summary>
        /// 取得指定型別的 attribute（lazy + cached）。
        /// <para>
        /// 第一次呼叫對應型別會 <c>GetCustomAttribute&lt;T&gt;()</c>（只構造 T 一個型別實例，不會
        /// 誤觸發其他 attribute ctor），結果（含 <c>null</c>）寫入 dict；後續同型別查詢 O(1)。
        /// </para>
        /// <para>
        /// 適用情境：呼叫端不確定 attribute 是否存在、需要避免「同欄位多次反射」、且該 attribute
        /// 不在 ctor 預抓清單內。**請勿**用此 API 取本檔已預抓的旗標（如 <c>[SerializeReference]</c>）—
        /// 直接讀對應旗標欄位即可。
        /// </para>
        /// <para>
        /// Thread-safety：UCL_Core 統一假設 main-thread 執行（見 UCL_AgentCommandRunner 等），
        /// 故 dict 不加鎖。
        /// </para>
        /// </summary>
        /// <typeparam name="T">attribute 型別</typeparam>
        /// <returns>attribute 實例或 null（不存在）</returns>
        public T GetAttr<T>() where T : Attribute
        {
            // 區塊職責：lazy 初始化 dict — 只有真的 GetAttr 才付出 dict 物件建構成本
            // 物理意義：絕大多數 entry 永遠不會被 GetAttr，省下 6000+ 個 dict 物件
            if (m_AttrCache == null)
            {
                m_AttrCache = new Dictionary<Type, Attribute>();
            }
            var aKey = typeof(T);
            if (m_AttrCache.TryGetValue(aKey, out var aCached))
            {
                // 命中（含 cached null）— 不再 reflect
                return aCached as T;
            }
            // 未命中 — 反射取得，存入 dict（無論 null 與否，都 cache 結果以避免下次重複反射）
            var aAttr = m_FieldInfo.GetCustomAttribute<T>();
            m_AttrCache[aKey] = aAttr;
            return aAttr;
        }

        // ===== 構造期 =====

        public UCL_FieldEntry(FieldInfo iField)
        {
            // 區塊職責：把 FieldInfo 轉成完整 metadata snapshot
            // 物理意義：一次性抓齊 GUI / JSON 兩條路徑會用到的所有 attribute / 旗標
            // 數值影響：cache 構造期一次反射呼叫；後續查詢都是 plain field access
            m_FieldInfo = iField;
            // 不在這裡呼叫 GetCustomAttributes(true) — 會構造所有 attribute 實例，引爆早期載入時的 NRE
            // （UCL_FolderExplorerAttribute ctor → UCL_ModuleService.ModResourcesPath，service 還沒 init）

            // ----- GUI metadata -----
            // [Header] 文字 — **不**在這裡 localize（避免 JSON 載入路徑撞 LocalizeManager 循環）
            // 在地化由 GUI 的 FieldInfoCache adapter 在 copy 時做
            var aHeader = iField.GetCustomAttribute<HeaderAttribute>(true);
            if (aHeader != null)
            {
                m_HeaderRaw = aHeader.header;
            }

            // [AlwaysExpendOnGUI] — 注意是 FieldType 上的 attribute，不是欄位
            m_AlwaysExpendOnGUI = (iField.FieldType.GetCustomAttribute<ATTR.AlwaysExpendOnGUI>() != null);

            // [HideInInspector] || [UCL_HideOnGUI] — 兩者皆視為 GUI 隱藏
            m_HideOnGUI = (iField.GetCustomAttribute<HideInInspector>() != null
                           || iField.GetCustomAttribute<ATTR.UCL_HideOnGUIAttribute>(false) != null);

            // ----- JSON metadata -----
            // [UCL_HideInJson] — JSON save / load 跳過此欄位
            m_HideInJson = (iField.GetCustomAttribute<ATTR.UCL_HideInJsonAttribute>() != null);

            // delegate 子類 — JSON 不可序列化
            m_IsMulticastDelegate = iField.FieldType.IsSubclassOf(typeof(MulticastDelegate));

            // [UCL_Conditional] — 存物件本身，IsShow(obj) 留 runtime 呼叫（instance-level）
            m_Conditional = iField.GetCustomAttribute<UCL.Core.PA.ConditionalAttribute>();

            // [UCL_FormerlySerializedAs] — load 端 fallback 用
            m_FormerlyAs = iField.GetCustomAttribute<ATTR.UCL_FormerlySerializedAsAttribute>();

            // ----- 共用 metadata -----
            // [SerializeReference] — 透過 helper 計算（SSOT 在 helper 而非本檔）
            m_IsPolymorphicField = UCL_PolymorphicHelper.IsPolymorphicField(iField);
        }
    }

    /// <summary>
    /// 型別級反射 metadata 的快取。
    /// <para>
    /// 對任意 Type，依 <see cref="JsonLib.JsonConvert.SaveMode"/> 走訪欄位
    /// （<see cref="UCL_AssemblyExtension.GetAllFieldsUnityVer(Type, Type, int)"/> for Unity 模式 /
    /// <see cref="UCL_AssemblyExtension.GetAllFieldsUntil(Type, Type, BindingFlags)"/> for Normal 模式），
    /// 每個欄位轉成 <see cref="UCL_FieldEntry"/>，附上 type-level metadata（<c>[EnableUCLEditor]</c> /
    /// methods 列表）。
    /// </para>
    /// <para>
    /// **不預過濾**：<see cref="m_Entries"/> 含全部欄位。GUI 用 <c>!entry.m_HideOnGUI</c> 過濾、
    /// JSON 用 <c>!entry.m_HideInJson &amp;&amp; !entry.m_IsMulticastDelegate</c> 過濾，並 runtime 呼
    /// <c>entry.m_Conditional?.IsShow(obj)</c>。
    /// </para>
    /// <para>
    /// Cache key 是 <c>(Type, SaveMode)</c> tuple — 兩種 SaveMode 走訪函式不同，欄位集合不同，
    /// 必須分開存。
    /// </para>
    /// </summary>
    public class UCL_TypeReflectCache
    {
        // ===== 屬性 =====

        /// <summary>本快取對應的型別。</summary>
        public Type m_Type;

        /// <summary>本快取對應的 SaveMode（決定欄位走訪函式）。</summary>
        public JsonLib.JsonConvert.SaveMode m_SaveMode;

        /// <summary>
        /// 全部欄位的 metadata（依 SaveMode 對應的走訪函式回傳的順序）。
        /// **不預過濾** — GUI / JSON 各自用旗標過濾。
        /// </summary>
        public List<UCL_FieldEntry> m_Entries;

        /// <summary>Type 自身有 <c>[EnableUCLEditor]</c> attribute（GUI 用，JSON 不關心）。</summary>
        public bool m_EnableUCLEditor;

        /// <summary>
        /// Type 上所有 method（含 instance / public+nonpublic）。只在 <see cref="m_EnableUCLEditor"/>
        /// 為 true 時才會填，否則為 null。GUI 用，JSON 不關心。
        /// </summary>
        public IList<MethodInfo> m_AllMethods;

        // ===== 靜態快取 =====

        // 區塊職責：(Type, SaveMode) → cache 的全域字典
        // 物理意義：避免相同 (type, mode) 重複建構；Unity domain reload 時整個 static dict 自動清
        // 數值影響：cache 命中 → O(1)；未命中 → O(field count) 反射呼叫
        private static readonly Dictionary<(Type, JsonLib.JsonConvert.SaveMode), UCL_TypeReflectCache>
            s_Cache = new Dictionary<(Type, JsonLib.JsonConvert.SaveMode), UCL_TypeReflectCache>();

        /// <summary>
        /// 取得（或構造並快取）指定 (type, mode) 的反射 metadata。
        /// </summary>
        /// <param name="iType">目標型別</param>
        /// <param name="iSaveMode">JSON SaveMode；GUI 路徑統一傳 <see cref="JsonLib.JsonConvert.SaveMode.Unity"/></param>
        /// <returns>共享 cache 物件 — caller 不應修改其欄位</returns>
        public static UCL_TypeReflectCache Get(Type iType, JsonLib.JsonConvert.SaveMode iSaveMode)
        {
            // 區塊職責：cache 命中 / 未命中分支
            // 物理意義：未命中時呼叫私有 ctor，建構期間做完所有反射 + attribute 查詢
            // 數值影響：每個 (type, mode) 最多構造一次（直到 domain reload）
            var aKey = (iType, iSaveMode);
            if (!s_Cache.TryGetValue(aKey, out var aCache))
            {
                aCache = new UCL_TypeReflectCache(iType, iSaveMode);
                s_Cache[aKey] = aCache;
            }
            return aCache;
        }

        // ===== 構造期（private — 一律走 Get(...) 取得，避免重複構造）=====

        private UCL_TypeReflectCache(Type iType, JsonLib.JsonConvert.SaveMode iSaveMode)
        {
            m_Type = iType;
            m_SaveMode = iSaveMode;

            // 區塊職責：依 SaveMode 選擇欄位走訪函式
            // 物理意義：Unity 模式跳過 [HideInInspector] 與無 [SerializeField] 的 NonPublic（在來源端過濾）；
            //          Normal 模式取所有 instance public + nonpublic
            // 數值影響：m_Entries 的欄位集合大小與順序由此決定
            List<FieldInfo> aFields;
            switch (iSaveMode)
            {
                case JsonLib.JsonConvert.SaveMode.Unity:
                {
                    aFields = iType.GetAllFieldsUnityVer(typeof(object));
                    break;
                }
                case JsonLib.JsonConvert.SaveMode.Normal:
                default:
                {
                    aFields = iType.GetAllFieldsUntil(typeof(object),
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    break;
                }
            }

            // 區塊職責：把每個 FieldInfo 轉成 UCL_FieldEntry（含完整 metadata）
            // 物理意義：保持來源走訪函式的順序（影響 JSON 欄位輸出順序與 GUI 顯示順序）
            // 數值影響：m_Entries.Count == aFields.Count（不在此階段過濾）
            m_Entries = new List<UCL_FieldEntry>(aFields.Count);
            foreach (var aField in aFields)
            {
                m_Entries.Add(new UCL_FieldEntry(aField));
            }

            // 區塊職責：Type-level metadata（GUI 專用，JSON 不關心）
            // 物理意義：[EnableUCLEditor] 為 true 時才抓 method 清單供 GUI 自訂按鈕用
            // 數值影響：m_AllMethods 在 m_EnableUCLEditor=false 時為 null（行為等價原 GUI）
            m_EnableUCLEditor = (iType.GetCustomAttribute<ATTR.EnableUCLEditor>(true) != null);
            if (m_EnableUCLEditor)
            {
                m_AllMethods = iType.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
        }
    }
}
