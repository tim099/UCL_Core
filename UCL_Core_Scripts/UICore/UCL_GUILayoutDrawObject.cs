using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UCL.Core.ATTR;
using UCL.Core.LocalizeLib;
using UnityEngine;

namespace UCL.Core.UI
{
    static public partial class UCL_GUILayout
    {
        public const string IsShowFieldKey = "IsShowField";
        public class DrawObjExSetting
        {
            public System.Action OnShowField;
        }
        #region DrawObject
        public enum EObjectType
        {
            None = 0,

            String,
            Bool,
            FieldOnGUI,
            Enum,
            Number,
            Tuple,
            IList,
            HashSet,
            IDictionary,
            Color,

            Vector2,
            Vector3,
            Vector2Int,
            Vector3Int,

            Component,
            StructOrClass,

            Unknown,
        }

        public class DrawObjectConfigs
        {
            public Func<string, string> m_FieldNameFunc;
            public System.Action<System.Action> m_OverrideDrawElement;
            public DrawObjectConfigs() {
                m_FieldNameFunc = UCL_StaticFunctions.LocalizeFieldName;
            }
            public DrawObjectConfigs(Func<string, string> iFieldNameFunc) {
                m_FieldNameFunc = iFieldNameFunc;
                if(m_FieldNameFunc == null)
                {
                    m_FieldNameFunc = UCL_StaticFunctions.LocalizeFieldName;
                }
            }
        }
        public class DrawObjectParams
        {
            /// <summary>
            /// dictionary to save display data
            /// </summary>
            public UCL_ObjectDictionary m_DataDic;
            /// <summary>
            /// the name show when hide detail
            /// </summary>
            public string m_DisplayName;
            public string m_FieldName;
            public FieldInfo m_FieldInfo;
            public bool m_IsAlwaysShowDetail;
            
            public System.Type m_FieldType;
            public DrawObjExSetting m_DrawObjExSetting;
            public DrawObjectConfigs m_DrawObjectConfigs;
            public bool m_SerializeReference = false;

            public DrawObjectParams() 
            {
                m_DataDic = new();
            }

            public DrawObjectParams(UCL_ObjectDictionary iDataDic, string iDisplayName, bool iIsAlwaysShowDetail = false, 
                System.Type iFieldType = null, DrawObjExSetting iDrawObjExSetting = null, DrawObjectConfigs iDrawObjectConfigs = null) {

                m_DataDic = iDataDic;
                m_DisplayName = iDisplayName;
                m_FieldType = iFieldType;
                m_IsAlwaysShowDetail = iIsAlwaysShowDetail;
                
                if(iDrawObjectConfigs == null)
                {
                    iDrawObjectConfigs = new DrawObjectConfigs();
                }
                m_DrawObjectConfigs = iDrawObjectConfigs;

                //if (iDrawObjExSetting == null)
                //{
                //    iDrawObjExSetting = new DrawObjExSetting();
                //}
                m_DrawObjExSetting = iDrawObjExSetting;
            }

            public DrawObjectParams CreateChild(UCL_ObjectDictionary iDataDic = null, string iDisplayName = null,
                FieldInfo iFieldInfo = null, bool iIsAlwaysShowDetail = false)
            {
                
                var aChild = new DrawObjectParams();
                aChild.m_DrawObjectConfigs = m_DrawObjectConfigs;//inherit config
                aChild.m_DataDic = iDataDic;
                aChild.m_DisplayName = iDisplayName;
                aChild.m_IsAlwaysShowDetail = iIsAlwaysShowDetail;
                aChild.m_FieldInfo = iFieldInfo;
                //if(iFieldInfo != null)
                //{
                //    aChild.m_FieldInfo = iFieldInfo;
                //}
                //else
                //{
                //    aChild.m_FieldInfo = m_FieldInfo;
                //}
                //aChild.m_FieldName = m_FieldName;

                return aChild;
            }
            public DrawObjectParams Copy()
            {
                var aChild = new DrawObjectParams();
                aChild.m_DrawObjectConfigs = m_DrawObjectConfigs;//inherit config
                aChild.m_DataDic = m_DataDic;
                aChild.m_DisplayName = m_DisplayName;
                aChild.m_IsAlwaysShowDetail = m_IsAlwaysShowDetail;
                aChild.m_FieldInfo = m_FieldInfo;
                aChild.m_FieldName = m_FieldName;

                return aChild;
            }
            public string FieldName
            {
                get
                {
                    if (!string.IsNullOrEmpty(m_FieldName))
                    {
                        return m_FieldName;
                    }
                    var name = m_DisplayName;
                    if (m_FieldInfo != null)
                    {
                        name = m_FieldInfo.Name;
                        if (m_DrawObjectConfigs.m_FieldNameFunc != null)
                        {
                            name = m_DrawObjectConfigs.m_FieldNameFunc(name);
                        }
                    }
                    return name;
                }
            }

            public string GetDisplayName(Type iType)
            {
                if (string.IsNullOrEmpty(m_DisplayName))
                {
                    return iType.Name;
                }
                return m_DisplayName;
            }
        }
        /// <summary>
        /// Draw a object inspector using GUILayout
        /// </summary>
        /// <param name="iObj">target object</param>
        /// <param name="iDataDic">dictionary to save display data</param>
        /// <param name="iDisplayName">the name show when hide detail</param>
        /// <param name="iIsAlwaysShowDetail">if set to true then will not show the detail toggle</param>
        /// <param name="iFieldNameFunc">param is the field name and return the display name</param>
        /// <returns></returns>
        public static object DrawObjectData(object iObj, UCL_ObjectDictionary iDataDic, string iDisplayName = "",
            bool iIsAlwaysShowDetail = false, Func<string, string> iFieldNameFunc = null, System.Type iFieldType = null
            , DrawObjExSetting iDrawObjExSetting = null)
        {
            return DrawObjectData(iObj, new DrawObjectParams(iDataDic, iDisplayName, iIsAlwaysShowDetail, iFieldType, iDrawObjExSetting, new DrawObjectConfigs(iFieldNameFunc)));
        }
        private static Dictionary<System.Type, EObjectType> s_DrawObjectDic = null;
        /// <summary>
        /// Draw a object inspector using GUILayout
        /// </summary>
        /// <param name="iTarget"></param>
        /// <param name="iDrawObjectParams"></param>
        /// <returns></returns>
        public static object DrawObjectData(object iTarget, DrawObjectParams iDrawObjectParams)
        {
            GUILayout.BeginVertical();
            bool aIsShowField = true;
            var aResult = iTarget;
            Type aType = null;
            if (iTarget != null)
            {
                aType = iTarget.GetType();
                //GUILayout.Label($"FieldName:{iDrawObjectParams.FieldName}", UCL_GUIStyle.LabelStyle);
                if (s_DrawObjectDic == null)
                {
                    s_DrawObjectDic = new();
                }
                if (!s_DrawObjectDic.ContainsKey(aType))
                {
                    if (iTarget is string)
                    {
                        s_DrawObjectDic[aType] = EObjectType.String;
                    }
                    else if (iTarget is bool)
                    {
                        s_DrawObjectDic[aType] = EObjectType.Bool;
                    }
                    else if (iTarget is UCLI_FieldOnGUI)
                    {
                        s_DrawObjectDic[aType] = EObjectType.FieldOnGUI;
                    }
                    else if (aType.IsEnum)
                    {
                        s_DrawObjectDic[aType] = EObjectType.Enum;
                    }
                    else if (iTarget.IsNumber())
                    {
                        s_DrawObjectDic[aType] = EObjectType.Number;
                    }
                    else if (aType.IsTuple())
                    {
                        s_DrawObjectDic[aType] = EObjectType.Tuple;
                    }
                    else if (iTarget is IList)
                    {
                        s_DrawObjectDic[aType] = EObjectType.IList;
                    }
                    else if (aType.IsHashSet())
                    {
                        s_DrawObjectDic[aType] = EObjectType.HashSet;
                    }
                    else if (iTarget is IDictionary)
                    {
                        s_DrawObjectDic[aType] = EObjectType.IDictionary;
                    }
                    else if (iTarget is Color)
                    {
                        s_DrawObjectDic[aType] = EObjectType.Color;
                    }
                    else if (iTarget is Component)
                    {
                        s_DrawObjectDic[aType] = EObjectType.Component;
                    }
                    else if (iTarget is Vector2)
                    {
                        s_DrawObjectDic[aType] = EObjectType.Vector2;
                    }
                    else if (iTarget is Vector3)
                    {
                        s_DrawObjectDic[aType] = EObjectType.Vector3;
                    }
                    else if (iTarget is Vector2Int)
                    {
                        s_DrawObjectDic[aType] = EObjectType.Vector2Int;
                    }
                    else if (iTarget is Vector3Int)
                    {
                        s_DrawObjectDic[aType] = EObjectType.Vector3Int;
                    }
                    else if (aType.IsStructOrClass())
                    {
                        s_DrawObjectDic[aType] = EObjectType.StructOrClass;
                    }
                    else
                    {
                        s_DrawObjectDic[aType] = EObjectType.Unknown;
                    }
                }
                //GUILayout.Label($"Type:{aType.FullName}", UCL_GUIStyle.LabelStyle);
                switch (s_DrawObjectDic[aType])
                {
                    case EObjectType.String:
                        {
                            //string displayName = iDrawObjectParams.GetDisplayName(aType);
                            //if (!string.IsNullOrEmpty(displayName))
                            //{
                            //    GUILayout.Label(displayName, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            //}
                            aResult = GUILayout.TextArea((string)iTarget, UCL_GUIStyle.TextAreaStyle);
                            break;
                        }
                    case EObjectType.Bool:
                        {
                            aResult = UCL_GUILayout.CheckBox((bool)iTarget);
                            break;
                        }
                    case EObjectType.FieldOnGUI:
                        {
                            aResult = (iTarget as UCLI_FieldOnGUI).OnGUI(iDrawObjectParams.GetDisplayName(aType), iDrawObjectParams.m_DataDic, iDrawObjectParams);
                            break;
                        }
                    case EObjectType.Enum:
                        {
                            GUILayout.BeginHorizontal();
                            GUILayout.Label(iDrawObjectParams.GetDisplayName(aType), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            aResult = PopupAuto((System.Enum)iTarget, iDrawObjectParams.m_DataDic);
                            GUILayout.EndHorizontal();
                            break;
                        }
                    case EObjectType.Number:
                        {
                            aResult = UCL_GUILayout.NumField(string.Empty, iTarget, iDrawObjectParams.m_DataDic);
                            break;
                        }
                    case EObjectType.Tuple:
                        {
                            aResult = iTarget;
                            aIsShowField = false;
                            var tupleElements = iTarget.GetTupleElements();
                            bool aIsValueChanged = false;
                            GUILayout.BeginVertical();
                            for (int i = 0; i < tupleElements.Count; i++)
                            {
                                var aTupleData = tupleElements[i];
                                var aResultData = DrawObjectData(aTupleData,
                                    iDrawObjectParams.CreateChild(iDrawObjectParams.m_DataDic.GetSubDic("_" + i.ToString()),
                                    aTupleData.UCL_GetShortName()));
                                //var aResultData = DrawObjectData(aTupleData, aDataDic.GetSubDic("_" + i.ToString()), aTupleData.UCL_GetShortName(), iFieldNameFunc: aFieldNameFunc);
                                if (aResultData != tupleElements[i])
                                {
                                    aIsValueChanged = true;
                                    tupleElements[i] = aResultData;
                                }
                            }
                            if (aIsValueChanged)
                            {
                                Type[] aTypeArray = aType.GetGenericArguments();

                                var aConstructer = aType.GetConstructor(aTypeArray);
                                if (aConstructer != null && aTypeArray.Length == tupleElements.Count)
                                {
                                    aResult = aConstructer.Invoke(tupleElements.ToArray());
                                }
                            }
                            GUILayout.EndVertical();
                            break;
                        }
                    case EObjectType.IList:
                        {
                            DrawList(iTarget as IList, iDrawObjectParams);
                            break;
                        }
                    case EObjectType.HashSet:
                        {
                            DrawHashSet(iTarget, iDrawObjectParams);
                            break;
                        }
                    case EObjectType.IDictionary:
                        {
                            DrawDictionary(iTarget as IDictionary, iDrawObjectParams);
                            break;
                        }
                    case EObjectType.Color:
                        {
                            var aOriginCol = (Color)iTarget;
                            using (new GUILayout.HorizontalScope())
                            {
                                bool aIsShow = Toggle(iDrawObjectParams.m_DataDic, "Toggle");
                                GUILayout.BeginVertical();
                                UCL_GUILayout.LabelAutoSize(string.Format("{0}{1}", iDrawObjectParams.GetDisplayName(aType), "■".RichTextColor(aOriginCol)));
                                if (aIsShow)
                                {
                                    aResult = SelectColor(aOriginCol);
                                }
                                GUILayout.EndVertical();
                            }
                            break;
                        }
                    case EObjectType.Vector2:
                        {
                            if (iTarget is Vector2 vector)
                            {
                                aResult = VectorField(iDrawObjectParams.GetDisplayName(aType), vector, iDrawObjectParams.m_DataDic);
                            }

                            break;
                        }
                    case EObjectType.Vector3:
                        {
                            if (iTarget is Vector3 vector)
                            {
                                aResult = VectorField(iDrawObjectParams.GetDisplayName(aType), vector, iDrawObjectParams.m_DataDic);
                            }
                            break;
                        }
                    case EObjectType.Vector2Int:
                        {
                            if (iTarget is Vector2Int vector)
                            {
                                aResult = VectorField(iDrawObjectParams.GetDisplayName(aType), vector, iDrawObjectParams.m_DataDic);
                            }
                            break;
                        }
                    case EObjectType.Vector3Int:
                        {
                            if (iTarget is Vector3Int vector)
                            {
                                aResult = VectorField(iDrawObjectParams.GetDisplayName(aType), vector, iDrawObjectParams.m_DataDic);
                            }
                            break;
                        }
                    case EObjectType.Component:
                        {
                            if (iTarget is Transform)
                            {
                                var aDataDic = iDrawObjectParams.m_DataDic;
                                var aTransform = iTarget as Transform;
                                using (new GUILayout.VerticalScope("box"))
                                {
                                    GUILayout.BeginHorizontal();
                                    aIsShowField = UCL_GUILayout.Toggle(aDataDic, IsShowFieldKey);
                                    GUILayout.Label(nameof(Transform), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                                    GUILayout.EndHorizontal();

                                    if (aIsShowField)
                                    {
                                        aTransform.position = VectorField("Position", aTransform.position, aDataDic.GetSubDic("Position"));
                                        aTransform.eulerAngles = VectorField("Rotation", aTransform.eulerAngles, aDataDic.GetSubDic("Rotation"));
                                        aTransform.localScale = VectorField("Scale", aTransform.localScale, aDataDic.GetSubDic("Scale"));
                                    }
                                    aResult = aTransform;
                                }
                            }
                            else
                            {
                                aResult = DrawField(iTarget, iDrawObjectParams);
                                //aResultObj = DrawField(aResultObj, aDataDic, aDisplayName, aIsAlwaysShowDetail, aFieldNameFunc, aFieldType);
                            }
                            break;
                        }
                    case EObjectType.StructOrClass:
                        {
                            if (aIsShowField)
                            {
                                aResult = DrawField(iTarget, iDrawObjectParams);
                                //aResultObj = DrawField(aResultObj, aDataDic, aDisplayName, aIsAlwaysShowDetail, aFieldNameFunc, aFieldType);
                                var aDrawObjExSetting = iDrawObjectParams.m_DrawObjExSetting;
                                if (aDrawObjExSetting != null)
                                {
                                    aDrawObjExSetting.OnShowField?.Invoke();
                                }
                            }
                            break;
                        }
                    case EObjectType.Unknown:
                        {
                            GUILayout.Label($"{iTarget}, Type:{aType.FullName}, not supported yet!", UCL_GUIStyle.LabelStyle);
                            break;
                        }
                }
                //aResult = s_DrawObjectDic[aType].Invoke(iTarget, iDrawObjectParams);
            }


            GUILayout.EndVertical();
            return aResult;
        }

        /// <summary>
        /// Draw all Field OnGUI
        /// </summary>
        /// <param name="iObj">target object</param>
        /// <param name="iDataDic"></param>
        /// <param name="iDisplayName"></param>
        /// <param name="iIsAlwaysShowDetail"></param>
        /// <param name="iFieldNameFunc"></param>
        /// <param name="iFieldType"></param>
        /// <returns></returns>
        public static object DrawField(object iObj, UCL_ObjectDictionary iDataDic, string iDisplayName = "",
            bool iIsAlwaysShowDetail = false, Func<string, string> iFieldNameFunc = null, System.Type iFieldType = null
            , DrawObjExSetting iDrawObjExSetting = null)
        {
            if (iObj == null) return null;
            var aParams = new DrawObjectParams(iDataDic, iDisplayName, iIsAlwaysShowDetail, iFieldType, iDrawObjExSetting, new DrawObjectConfigs(iFieldNameFunc));
            return DrawField(iObj, aParams);
        }

        // 區塊職責：FieldInfoCache / TypeFieldInfoCache 自 Step 1.5 起改為 adapter
        // 物理意義：原本 GUI 私有的 cache（內含全套反射呼叫）已抽到 UCL_TypeReflectCache（共用層）。
        //          這裡保留兩個 class 名稱與公開欄位 — DrawObject 主流程的所有呼叫點不變，
        //          僅 ctor 內部改成讀共用 cache 後 copy 出 GUI 所需子集。
        // 數值影響：行為等價（相同欄位順序、相同 Header / SerializeReference / Attrs）；
        //          原 GUI 構造期就丟掉的 [HideInInspector] / [UCL_HideOnGUI] 仍在這裡過濾。

        /// <summary>
        /// GUI 端對單一欄位的 metadata wrapper。Step 1.5 起改為 <see cref="UCL_FieldEntry"/> 的 adapter，
        /// 所有 attribute 抓取已收斂到共用層 — 本類別只負責挑出 GUI 用得到的欄位。
        /// </summary>
        public class FieldInfoCache
        {
            public FieldInfo m_FieldInfo;
            public bool m_AlwaysExpendOnGUI;
            public IEnumerable<Attribute> m_Attrs;
            public bool m_SerializeReference;
            public string m_Header;
            public FieldInfoCache() { }
            // 區塊職責：從共用 entry 抽出 GUI 用的子集
            // 物理意義：與舊版直接從 FieldInfo 反射的結果完全等價（共用層執行同樣邏輯）；
            //          [Header] 在地化在這裡做（共用 entry 不做，避免 JSON 載入路徑撞 LocalizeManager 循環）
            // 數值影響：m_Header 套 UCL_LocalizeManager；其餘欄位 1:1 映射
            public FieldInfoCache(UCL_FieldEntry iEntry)
            {
                m_FieldInfo = iEntry.m_FieldInfo;
                // [Header] localize — 只在 GUI 路徑構造 wrapper 時觸發 LocalizeManager
                m_Header = iEntry.m_HeaderRaw;
                if (!string.IsNullOrEmpty(m_Header) && UCL_LocalizeManager.ContainsKey(m_Header))
                {
                    m_Header = UCL_LocalizeManager.Get(m_Header);
                }
                m_SerializeReference = iEntry.m_IsPolymorphicField;
                m_AlwaysExpendOnGUI = iEntry.m_AlwaysExpendOnGUI;
                // 全 attribute 在這裡 lazy 取（GUI render 時 service 已 ready；不在共用 cache 算以避開
                // 早期載入 NRE，詳見 UCL_TypeReflectCache.UCL_FieldEntry 上方註解）
                m_Attrs = iEntry.m_FieldInfo.GetCustomAttributes();
            }
        }

        /// <summary>
        /// GUI 端對單一型別的 metadata wrapper。Step 1.5 起改為 <see cref="UCL_TypeReflectCache"/> 的
        /// adapter — 構造時讀共用 cache（Unity 模式），按 GUI 規則過濾出 <see cref="m_FieldInfos"/>。
        /// </summary>
        public class TypeFieldInfoCache
        {
            public bool m_EnableUCLEditor;
            public List<FieldInfoCache> m_FieldInfos = new List<FieldInfoCache>();
            public IList<MethodInfo> m_AllMethods;


            public TypeFieldInfoCache() { }
            // 區塊職責：從共用反射 cache 取得 type metadata 並過濾出 GUI 可顯示欄位
            // 物理意義：取代舊版的 GetAllFieldsUnityVer + per-field FieldInfoCache 反射。
            //          GUI 過濾規則：跳過 entry.m_HideOnGUI（[HideInInspector] || [UCL_HideOnGUI]）。
            // 數值影響：m_FieldInfos 順序與內容應與舊版完全等價（共用層走訪函式相同、過濾條件相同）
            public TypeFieldInfoCache(System.Type aType)
            {
                // GUI 一律走 Unity 模式（與原 GetAllFieldsUnityVer 路徑一致）
                var aShared = UCL_TypeReflectCache.Get(aType, JsonLib.JsonConvert.SaveMode.Unity);
                m_EnableUCLEditor = aShared.m_EnableUCLEditor;
                m_AllMethods = aShared.m_AllMethods;
                foreach (var aEntry in aShared.m_Entries)
                {
                    if (aEntry.m_HideOnGUI) continue; // 等價舊版 [HideInInspector] || [UCL_HideOnGUI] 過濾
                    m_FieldInfos.Add(new FieldInfoCache(aEntry));
                }
            }
        }
        /// <summary>
        /// [職責] 繪製 HelpURL 對應的幫助按鈕，並處理點擊開啟邏輯。
        /// [物理意義] 如果存在 HelpURLAttribute，則在介面上顯示一個 "?" 按鈕。點擊時自動判斷路徑類型並開啟。
        /// [Editor 行為] 目標是**存在的本地 .md** → 開內嵌的 <c>UCL_MarkdownViewerPage</c>
        ///   （對齊 UCL_DocSearchPage 的「📄 預覽」入口：不離開 Unity 視窗，且該頁 TopBar 另有
        ///   Reveal / OS Open / Copy raw，功能是 OS 開檔的超集）。其餘情形（http URL、非 md）
        ///   維持 <c>Application.OpenURL</c>。
        /// </summary>
        /// <param name="url">要開啟的 URL 字串或相對路徑。</param>
        public static void DrawHelpButton(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            // [數值影響] 按鈕大小會隨螢幕缩放比例調整。
            float size = UCL_GUIStyle.GetScaledSize(20);
            if (GUILayout.Button("?", UCL_GUIStyle.ButtonStyle, GUILayout.Width(size), GUILayout.Height(size)))
            {
#if UNITY_EDITOR
                if (TryOpenHelpInMarkdownViewer(url)) return;
#endif
                // [職責] 透過 UCL_URLLib 解析並開啟 URL。
                UCL_URL.OpenURL(url);
            }
        }

#if UNITY_EDITOR
        // ===========================================================
        // 區塊職責：把 HelpURL 導向內嵌 markdown 檢視頁（Tim 2026-08-17）。
        // 物理意義：HelpURL 十之八九指向 repo 內的 .md。用 OS 預設應用開它，等於把人踢出 Unity，
        //          而 UCL_MarkdownViewerPage 本身就內建 Reveal / OS Open / Copy raw ——
        //          **它是 OS 開檔的超集**，所以改走它不會少掉任何原本做得到的事。
        // 數值影響：只攔截「本地 .md 且檔案存在」；http URL 與非 md 一律讓原路徑處理，行為不變。
        //
        // ⚠ 順帶補掉一個靜默失敗（2026-08-17 分析 UCL_FreeTimeAdminPage 的 ? 按鈕沒反應時挖出來）：
        //   HelpURL 指向不存在的檔時，原本的鏈路是四層 fail-soft 疊起來 ——
        //   {lang} 找不到 → en fallback 也找不到 → 維持原路徑 → Application.OpenURL(不存在的路徑)
        //   → Windows shell 靜默無視，不丟例外、不寫 log。使用者只看到「按了沒反應」。
        //   每一層單獨看都合理，但**沒有任何一層負責說「我找不到」**。
        //   實測全 UCL_Core 80 筆 ucl_core: HelpURL 中有 22 筆指向不存在的文件，長期沒人發現，
        //   正是因為它不會叫。這裡補上那句話：找不到就 LogWarning 附解析後的實路徑。
        //   （只在點擊時喊，不做開頁掃描 —— 沒人要看的警告會變成背景音。）
        // ===========================================================
        static bool TryOpenHelpInMarkdownViewer(string iUrl)
        {
            try
            {
                string aResolved = UCL_URL.ResolveURL(iUrl);
                if (string.IsNullOrEmpty(aResolved)) return false;
                if (aResolved.Contains("://")) return false;                     // 雲端 URL → 交給瀏覽器
                if (!aResolved.EndsWith(".md", System.StringComparison.OrdinalIgnoreCase)) return false;

                if (!System.IO.File.Exists(aResolved))
                {
                    Debug.LogWarning($"[HelpURL] 找不到說明文件：`{iUrl}`\n  解析後：{aResolved}\n"
                        + "  —— 該 HelpURL 指向一個不存在的檔（多半是文件還沒寫，或改名後沒同步）。"
                        + "在此之前這個情況是**完全靜默**的：按鈕看起來壞了，但沒有任何地方會說話。");
                    return false;   // 仍讓原路徑跑 —— 這裡只負責讓它出聲，不改變既有行為
                }

                string aRel = ToRepoRelative(aResolved);
                UCL.Core.EditorLib.Page.UCL_MarkdownViewerPage.Create(aRel, aResolved);
                return true;
            }
            catch (System.Exception e)
            {
                // 開說明頁失敗不該擋住使用者 —— 退回 OS 開檔那條原路。
                Debug.LogWarning($"[HelpURL] 內嵌檢視頁開啟失敗，改走 OS 開檔：{e.Message}");
                return false;
            }
        }

        /// <summary>絕對路徑 → git-root 相對（純顯示用；推不出來就原樣回傳，不要為了好看而騙人）。</summary>
        static string ToRepoRelative(string iAbsolute)
        {
            try
            {
                string aRoot = UCL.Core.EditorLib.UCL_RepoPath.RepoRoot;
                if (string.IsNullOrEmpty(aRoot)) return iAbsolute;
                string aFull = System.IO.Path.GetFullPath(iAbsolute).Replace('\\', '/');
                string aRootFull = System.IO.Path.GetFullPath(aRoot).Replace('\\', '/').TrimEnd('/') + "/";
                return aFull.StartsWith(aRootFull, System.StringComparison.OrdinalIgnoreCase)
                    ? aFull.Substring(aRootFull.Length)
                    : aFull;
            }
            catch (System.Exception) { return iAbsolute; }
        }
#endif


        private static Dictionary<System.Type, TypeFieldInfoCache> s_TypeFieldInfoCacheDic = null;
        /// <summary>
        /// Draw all Field OnGUI
        /// </summary>
        /// <param name="iObj"></param>
        /// <param name="iParams"></param>
        /// <returns></returns>
        public static object DrawField(object iObj, DrawObjectParams iParams)
        {
            if (iObj == null)
            {
                if (iParams.m_SerializeReference && iParams.m_FieldType != null)
                {
                    // Step 2：用 SSOT helper 取得 concrete subtype 清單（與既有 GetAllITypes 行為等價）
                    var types = UCL_PolymorphicHelper.GetConcreteSubtypes(iParams.m_FieldType);
                    if (!types.IsNullOrEmpty())
                    {
                        iObj = types[0].CreateInstance();
                    }
                    else
                    {
                        return null;
                    }
                }
                return null;
            }
            //GUILayout.Label($"DrawField:{iParams.FieldName}", UCL_GUIStyle.LabelStyle);
            //if (GUILayout.Button("Log", UCL_GUIStyle.ButtonStyle))
            //{
            //    Debug.LogError($"DrawField FieldName:{iParams.FieldName}");
            //}
            var iFieldNameFunc = iParams.m_DrawObjectConfigs.m_FieldNameFunc;
            var iFieldType = iParams.m_FieldType;
            var iIsAlwaysShowDetail = iParams.m_IsAlwaysShowDetail;
            var iDataDic = iParams.m_DataDic;
            

            //if (iFieldNameFunc == null) iFieldNameFunc = UCL_StaticFunctions.LocalizeFieldName;
            bool aIsShowField = true;
            object aResultObj = iObj;
            Type aType = iObj.GetType();
            if (iFieldType == null) iFieldType = aType;


            GUILayout.BeginHorizontal();
            if (!iIsAlwaysShowDetail)
            {
                aIsShowField = UCL_GUILayout.Toggle(iDataDic, IsShowFieldKey);
            }
            //else
            //{
            //    aIsShowField = true;
            //}
            GUILayout.BeginVertical();
            if (!iIsAlwaysShowDetail)
            {
                var iDisplayName = iParams.GetDisplayName(aType);
                GUILayout.BeginHorizontal();
                if (iObj is UCLI_NameOnGUI aNameOnGUI)
                {
                    var url = aType.GetCustomAttribute<HelpURLAttribute>();
                    if(url != null) DrawHelpButton(url.URL);
                    aNameOnGUI.NameOnGUI(iDataDic, iDisplayName, iParams);
                }
                else
                {
                    
                    if (iObj is UCL.Core.UCLI_Icon aIcon)
                    {
                        var aTexture = aIcon.IconTexture;
                        if (aTexture != null)
                        {
                            float aSize = UCL_GUIStyle.GetScaledSize(24);
                            using (new GUILayout.VerticalScope(GUILayout.Width(aSize), GUILayout.Height(aSize)))
                            {
                                GUILayout.FlexibleSpace();
                                GUILayout.Box(aTexture, GUILayout.Width(aSize), GUILayout.Height(aSize));
                                GUILayout.FlexibleSpace();
                            }
                        }
                    }
                    var url = aType.GetCustomAttribute<HelpURLAttribute>();
                    if (url != null) DrawHelpButton(url.URL);

                    if (iObj is UCL.Core.UI.UCLI_IsEnable aEnable)
                    {
                        aEnable.IsEnable = UCL_GUILayout.CheckBox(aEnable.IsEnable);
                    }


                    GUILayout.Label(iDisplayName, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (iParams.m_SerializeReference && iParams.m_FieldType != null)
                    {
                        // Step 2：用 SSOT helper 取得 concrete subtype 清單（與既有 GetAllITypes 行為等價）
                        var types = UCL_PolymorphicHelper.GetConcreteSubtypes(iParams.m_FieldType);
                        //GUILayout.Label($"types:{types.ConcatToString(type => type.Name)}");
                        if (!types.IsNullOrEmpty())
                        {
                            var typeNames = UCLI_TypeListable.GetTypeNames(types);
                            int index = types.IndexOf(aType);
                            int newIndex = UCL_GUILayout.PopupAuto(index, typeNames, iDataDic, "SerializeReference");

                            if (newIndex != index)
                            {
                                try
                                {
                                    
                                    aType = types[newIndex];
                                    iObj = aResultObj = aType.CreateInstance();
                                    //Debug.LogError($"Set:{aType.Name}");
                                }
                                catch (Exception ex)
                                {
                                    Debug.LogException(ex);
                                }
                            }
                        }
                    }
                }

                if (iObj is UCLI_CopyPaste)
                {
                    if (DrawCopyPaste(ref iObj, iDataDic, iFieldType))
                    {
                        aIsShowField = false;
                        aResultObj = iObj;
                    }
                }
                //else
                //{
                //    GUILayout.Label("!UCLI_CopyPaste :" + aType.Name);
                //}
                GUILayout.EndHorizontal();
            }

            if (aIsShowField)
            {
                using (var aScope = new GUILayout.VerticalScope())
                {
                    if(s_TypeFieldInfoCacheDic == null)
                    {
                        s_TypeFieldInfoCacheDic = new Dictionary<Type, TypeFieldInfoCache>();
                    }
                    if (!s_TypeFieldInfoCacheDic.ContainsKey(aType))
                    {
                        var aFieldInfoCache = new TypeFieldInfoCache(aType);
                        s_TypeFieldInfoCacheDic[aType] = aFieldInfoCache;
                    }
                    TypeFieldInfoCache aInfoCache = s_TypeFieldInfoCacheDic[aType];

                    if (aInfoCache.m_EnableUCLEditor)
                    {
                        IList<MethodInfo> aAllMethods = aInfoCache.m_AllMethods;
                        if (aAllMethods.Count > 0)
                        {
                            GUILayout.BeginVertical();
                            for (int i = 0; i < aAllMethods.Count; i++)
                            {
                                var aMethod = aAllMethods[i];
                                var aMethodDic = iDataDic.GetSubDic("Method", i);
                                {
                                    var aAttrType = typeof(ATTR.UCL_Attribute);
                                    var aAttrs = aMethod.GetCustomAttributes(aAttrType, false);
                                    if (aAttrs.Length > 0)
                                    {
                                        for (int j = 0; j < aAttrs.Length; j++)
                                        {
                                            var aAttr = (ATTR.UCL_Attribute)aAttrs[j];
                                            try
                                            {
                                                aAttr.Draw(iObj, aMethod, aMethodDic.GetSubDic("Attr", j));
                                            }
                                            catch (Exception iE)
                                            {
                                                Debug.LogException(iE);
                                            }
                                        }
                                    }
                                }
                            }
                            GUILayout.EndVertical();
                        }
                    }


                    var aFieldInfos = aInfoCache.m_FieldInfos;
                    foreach (var aFieldInfoCache in aFieldInfos)
                    {
                        var aFieldInfo = aFieldInfoCache.m_FieldInfo;
                        var aData = aFieldInfo.GetValue(iObj);


                        var aHeader = aFieldInfoCache.m_Header;//aFieldInfo.GetCustomAttribute<HeaderAttribute>(true);
                        if (!string.IsNullOrEmpty(aHeader))
                        {
                            GUILayout.Box(aHeader, UI.UCL_GUIStyle.BoxStyle);
                        }

                        if (aData == null)
                        {
                            if (typeof(IList).IsAssignableFrom(aFieldInfo.FieldType) || typeof(IDictionary).IsAssignableFrom(aFieldInfo.FieldType))
                            {
                                aData = aFieldInfo.FieldType.CreateInstance();
                                aFieldInfo.SetValue(iObj, aData);
                            }
                        }

                        string aDisplayName = aFieldInfo.Name;
                        string aDataKey = "_" + aDisplayName;

                        aDisplayName = iFieldNameFunc(aDisplayName);
                        var aShortName = aData as UCL.Core.UCLI_ShortName;
                        if (aShortName != null)
                        {
                            var aName = aShortName.GetShortName();
                            if (!string.IsNullOrEmpty(aName)) aDisplayName += $"({aName})";
                        }
                        bool aIsAlwaysShowDetail = aFieldInfoCache.m_AlwaysExpendOnGUI;

                        bool aIsDrawed = false;
                        IEnumerable<Attribute> aAttrs = aFieldInfoCache.m_Attrs;
                        foreach (var aAttr in aAttrs)
                        {
                            if (aAttr is IShowInCondition && !((IShowInCondition)aAttr).IsShow(iObj))
                            {
                                aIsDrawed = true;
                                break;
                            }
                            else if (aAttr is IStrList aStrArr)
                            {
                                aIsDrawed = true;
                                GUILayout.BeginHorizontal();
                                GUILayout.Label(aDisplayName, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

                                aFieldInfo.SetValue(iObj, aStrArr.DrawOnGUILocalized(iObj, aData, iDataDic, aFieldInfo.Name));
                                GUILayout.EndHorizontal();
                            }
                            else if (aAttr is IValueDropdown valueDropdown)
                            {
                                aIsDrawed = true;
                                GUILayout.BeginHorizontal();
                                GUILayout.Label(aDisplayName, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                                var list = valueDropdown.GetStrList(iObj);
                                aFieldInfo.SetValue(iObj, list.DrawValueDropdown(iObj, aData, iDataDic, aFieldInfo.Name));
                                GUILayout.EndHorizontal();
                            }
                            else if (aAttr is ITexture2D)
                            {
                                var aTextureArr = aAttr as ITexture2D;
                                GUILayout.BeginHorizontal();
                                float size = UCL_GUIStyle.GetScaledSize(64);
                                GUILayout.Box(aTextureArr.GetTexture(iObj, aData), GUILayout.Width(size), GUILayout.Height(size));
                                GUILayout.EndHorizontal();
                            }
                            else if (aAttr is SpaceAttribute)
                            {
                                GUILayout.Space((aAttr as SpaceAttribute).height);
                            }
                            else if (aAttr is UCL.Core.PA.UCL_FolderExplorerAttribute)
                            {
                                if (aFieldInfo.FieldType == typeof(string))
                                {
                                    aIsDrawed = true;
                                    var aFolderExplorerAttribute = aAttr as UCL.Core.PA.UCL_FolderExplorerAttribute;
                                    if (aData == null) aData = "";
                                    string aPath = (string)aData;
                                    var aResult = aFolderExplorerAttribute.OnGUI(iDataDic.GetSubDic(aFieldInfo.Name), aPath, aDisplayName);
                                    aFieldInfo.SetValue(iObj, aResult);
                                }
                            }
                            else if (aAttr is ATTR.AlwaysExpendOnGUI)
                            {
                                aIsAlwaysShowDetail = true;
                            }
                            else if (aAttr is PA.UCL_IntSliderAttribute)
                            {
                                if (aData is int)
                                {
                                    aIsDrawed = true;
                                    var aSlider = aAttr as PA.UCL_IntSliderAttribute;
                                    int aVal = (int)aData;
                                    int aResult = aSlider.OnGUI(aDisplayName, aVal, iDataDic.GetSubDic(aFieldInfo.Name));

                                    if (aResult != aVal)
                                    {
                                        aFieldInfo.SetValue(iObj, aResult);
                                    }
                                }
                            }
                            else if (aAttr is PA.UCL_SliderAttribute)
                            {
                                if (aData is float)
                                {
                                    aIsDrawed = true;
                                    var aSlider = aAttr as PA.UCL_SliderAttribute;
                                    float aVal = (float)aData;
                                    float aResult = aSlider.OnGUI(aDisplayName, aVal, iDataDic.GetSubDic(aFieldInfo.Name));

                                    if (aResult != aVal)
                                    {
                                        aFieldInfo.SetValue(iObj, aResult);
                                    }
                                }
                            }
                        }




                        if (aIsDrawed)
                        {
                            //aField.SetValue(iObj, aDropDownAttr.DrawOnGUI(iObj, aData, m_DataDic, "_" + aDisplayName));
                        }
                        else if (aFieldInfo.FieldType == typeof(bool))
                        {
                            //if (aData == null) aData = false; //bool is not nullable
                            aFieldInfo.SetValue(iObj, UCL.Core.UI.UCL_GUILayout.BoolField(aDisplayName, (bool)aData));
                        }
                        else if (aFieldInfo.FieldType == typeof(string))
                        {
                            if (aData == null) aData = "";
                            //UCL_GUIStyle.CurStyleData.TextAreaStyle
                            var aResult = UCL.Core.UI.UCL_GUILayout.TextArea(aDisplayName, (string)aData);
                            aFieldInfo.SetValue(iObj, aResult);
                        }
                        else if (aData == null)
                        {
                            //Debug.LogError("aData == null aField:" + aField.Name);
                        }
                        else if (aData is UCLI_FieldOnGUI)
                        {
                            UCLI_FieldOnGUI aVar = (UCLI_FieldOnGUI)aData;
                            var aDic = iDataDic.GetSubDic(aFieldInfo.Name);
                            var aParams = iParams.CreateChild(aDic, aDisplayName, aFieldInfo);
                            aFieldInfo.SetValue(iObj, aVar.OnGUI(aDisplayName, aDic, aParams));
                        }
                        else if (aData.IsNumber())
                        {
                            string aValKey = aDataKey + "_Val";
                            if (!iDataDic.ContainsKey(aDataKey))
                            {
                                iDataDic.SetData(aDataKey, aData.ToString());//Save NumberStr
                            }
                            if (!iDataDic.ContainsKey(aValKey))
                            {
                                iDataDic.SetData(aValKey, aData);//Save Val
                            }
                            string aNumStr = iDataDic.GetData(aDataKey, string.Empty);
                            var aResult = UCL_GUILayout.TextField(aDisplayName, aNumStr);
                            if (aResult != aNumStr)//Set value
                            {
                                iDataDic.SetData(aDataKey, aResult);
                                object aResVal;
                                if (UCL.Core.MathLib.Num.TryParse(aResult, aFieldInfo.FieldType, out aResVal))
                                {
                                    if (!aResVal.Equals(aData))
                                    {
                                        aFieldInfo.SetValue(iObj, aResVal);
                                        iDataDic.SetData(aValKey, aResVal);//Save Val
                                    }
                                }
                            }
                            else
                            {
                                if (!iDataDic.GetData(aValKey, aData).Equals(aData))//Check if value changed
                                {
                                    string aDataStr = aData.ToString();
                                    if (aDataStr != aNumStr)
                                    {
                                        iDataDic.SetData(aDataKey, aDataStr);
                                    }
                                    iDataDic.SetData(aValKey, aData);
                                }
                            }
                        }
                        else if (aData is IList or IDictionary)//aData is IList || aData is IDictionary
                        {

                            ICollection aList = aData as ICollection;//IList and IDictionary is ICollection
                            var aParams = iParams.CreateChild(iDataDic.GetSubDic(aFieldInfo.Name), $"{aDisplayName}({aList.Count})", aFieldInfo, aIsAlwaysShowDetail);
                            aParams.m_SerializeReference = aFieldInfoCache.m_SerializeReference;
                            var aResult = DrawObjectData(aData, aParams);
                            //var aResult = DrawObjectData(aData, iDataDic.GetSubDic(aFieldInfo.Name), $"{aDisplayName}({aList.Count})", aAlwaysExpendOnGUI, iFieldNameFunc);
                            aFieldInfo.SetValue(iObj, aResult);
                        }
                        else if (aData is Color)
                        {
                            var aCol = (Color)aData;
                            var aParams = iParams.CreateChild(iDataDic.GetSubDic(aFieldInfo.Name), aDisplayName, aFieldInfo, aIsAlwaysShowDetail);
                            var aNewCol = (Color)DrawObjectData(aData, aParams);
                            //var aNewCol = (Color)DrawObjectData(aData, iDataDic.GetSubDic(aFieldInfo.Name), aDisplayName, aIsAlwaysShowDetail, iFieldNameFunc);
                            if (aNewCol != aCol)
                            {
                                aFieldInfo.SetValue(iObj, aNewCol);
                            }
                        }
                        else if (aFieldInfo.FieldType.IsEnum)
                        {
                            var aParams = iParams.CreateChild(iDataDic.GetSubDic(aFieldInfo.Name), aDisplayName, aFieldInfo, aIsAlwaysShowDetail);
                            var aResult = DrawObjectData(aData, aParams);
                            //var aResult = DrawObjectData(aData, iDataDic.GetSubDic(aFieldInfo.Name), aDisplayName, aIsAlwaysShowDetail, iFieldNameFunc);
                            if (aResult != aData)
                            {
                                aFieldInfo.SetValue(iObj, aResult);
                            }
                        }
                        else if (aFieldInfoCache.m_SerializeReference || aFieldInfo.FieldType.IsStructOrClass())
                        {
                            var dataDic = iDataDic.GetSubDic(aFieldInfo.Name + "_FieldData");

                            //GUILayout.Label($"aFieldInfoCache.m_SerializeReference:{aFieldInfoCache.m_SerializeReference}");
                            var aParams = iParams.CreateChild(dataDic, aDisplayName, aFieldInfo, aIsAlwaysShowDetail);
                            aParams.m_SerializeReference = aFieldInfoCache.m_SerializeReference;
                            aParams.m_FieldType = aFieldInfo.FieldType;
                            var result = DrawObjectData(aData, aParams);
                            //DrawObjectData(aData, iDataDic.GetSubDic(aFieldInfo.Name + "_FieldData"), aDisplayName, aIsAlwaysShowDetail, iFieldNameFunc);
                            aFieldInfo.SetValue(iObj, result);
                        }
                    }
                    var iDrawObjExSetting = iParams.m_DrawObjExSetting;
                    if (iDrawObjExSetting != null)
                    {
                        iDrawObjExSetting.OnShowField?.Invoke();
                    }
                }
            }
                
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            return aResultObj;
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="iObj"></param>
        /// <param name="iDataDic"></param>
        /// <param name="iFieldType"></param>
        /// <returns>return true if paste data</returns>
        public static bool DrawCopyPaste(ref object iObj, UCL_ObjectDictionary iDataDic, System.Type iFieldType = null)
        {
            bool aIsPaste = false;
            Type aType = iObj.GetType();
            if (iFieldType == null) iFieldType = aType;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(UCL_LocalizeManager.Get("Copy"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                UCLI_CopyPaste.s_Copying = true;
                UCL.Core.CopyPaste.SetCopyData(iObj);
                UCLI_CopyPaste.s_Copying = false;
            }
            bool aCanCopy = UCL.Core.CopyPaste.HasCopyData(iFieldType);
            if (GUILayout.Button(UCL_LocalizeManager.Get("Paste"), UCL_GUIStyle.GetButtonStyle(aCanCopy ? Color.white : Color.red),
                GUILayout.ExpandWidth(false)))
            {
                if (aCanCopy)
                {
                    UCLI_CopyPaste.s_Copying = true;
                    if (iObj != null && aType == CopyPaste.s_CopyType)
                    {
                        UCL.Core.CopyPaste.LoadCopyData(iObj);
                    }
                    else
                    {
                        iObj = UCL.Core.CopyPaste.GetCopyData();
                    }
                    aIsPaste = true;
                    iDataDic.Clear();
                    UCLI_CopyPaste.s_Copying = false;
                }
            }
            return aIsPaste;
        }
        public static class Preview
        {
            const int MaxRecursive = 10;
            static private Dictionary<Type, System.Action<string, object, UCL_ObjectDictionary, int>> s_OnGUICacheDic = new();
            public static void OnGUI(string iFieldName, object target, UCL_ObjectDictionary iDataDic, int iSpace = 0)
            {
                try
                {
                    if (iSpace > MaxRecursive) return;
                    if (target == null)
                    {
                        GUILayout.Label($"{iFieldName}: Null", UCL_GUIStyle.LabelStyle);
                        return;
                    }
                    Type aType = target.GetType();
                    if(!s_OnGUICacheDic.ContainsKey(aType))
                    {
                        if (aType.IsPrimitive || !aType.IsStructOrClass()
                            || (target is Enum or Vector4 or Vector3 or Vector2 or Vector3Int or Vector2Int))
                        {
                            void PrimitiveOnGUI(string fieldName, object iObj, UCL_ObjectDictionary iDataDic, int iSpace = 0)
                            {
                                GUILayout.Label($"{fieldName}: {iObj}", UCL_GUIStyle.LabelStyle);
                            }
                            s_OnGUICacheDic[aType] = PrimitiveOnGUI;
                        }
                        else if (target is string)
                        {
                            void StringOnGUI(string fieldName, object iObj, UCL_ObjectDictionary iDataDic, int iSpace = 0)
                            {
                                GUILayout.Label($"{fieldName}: {iObj}", UCL_GUIStyle.LabelStyle);
                            }
                            s_OnGUICacheDic[aType] = StringOnGUI;
                        }
                        else if (target is UCLI_AssetEntry)
                        {
                            void StringOnGUI(string fieldName, object iObj, UCL_ObjectDictionary iDataDic, int iSpace = 0)
                            {
                                GUILayout.Label($"{fieldName}: {(iObj as UCLI_AssetEntry).ID}", UCL_GUIStyle.LabelStyle);
                            }
                            s_OnGUICacheDic[aType] = StringOnGUI;
                        }
                        else if(target is JsonLib.JsonData)
                        {
                            void JsonDataOnGUI(string fieldName, object iObj, UCL_ObjectDictionary iDataDic, int iSpace = 0)
                            {
                                JsonLib.JsonData aJson = iObj as JsonLib.JsonData;
                                using (var aScope = new GUILayout.HorizontalScope())
                                {
                                    GUILayout.Label($"{fieldName}(Json):{aJson.ToJsonBeautify()}", UCL_GUIStyle.LabelStyle);
                                }
                            }
                            s_OnGUICacheDic[aType] = JsonDataOnGUI;
                        }
                        else if (target is IEnumerable)
                        {
                            s_OnGUICacheDic[aType] = IEnumerableOnGUI;
                        }
                        else
                        {
                            s_OnGUICacheDic[aType] = AllFieldsOnGUI;
                        }
                    }

                    s_OnGUICacheDic[aType].Invoke(iFieldName, target, iDataDic, iSpace);
                }
                catch (Exception iE)
                {
                    Debug.LogException(iE);
                    GUILayout.Label($"Exception:{iE}", UCL_GUIStyle.LabelStyle);
                }
            }
            //static private Dictionary<Type, System.Action<object, UCL_ObjectDictionary, int>> s_FieldOnGUICacheDic = new();
            public static void AllFieldsOnGUI(string fieldName, object iObj, UCL_ObjectDictionary iDataDic, int iSpace = 0)
            {
                Type aType = iObj.GetType();
                FieldInfo[] aFields = aType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (aFields.Length > 0)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        bool aIsShowField = UCL_GUILayout.Toggle(iDataDic, IsShowFieldKey);
                        using (new GUILayout.VerticalScope())
                        {
                            using (new GUILayout.HorizontalScope())
                            {
                                GUILayout.Label(aType.Name, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            }
                            if (aIsShowField)
                            {
                                foreach (var aField in aFields)
                                {
                                    using (new GUILayout.VerticalScope())
                                    {
                                        var aVal = aField.GetValue(iObj);
                                        Type aFieldType = aField.FieldType;
                                        if(aVal is string aStr)
                                        {
                                            GUILayout.Label($"({aFieldType.Name}){aField.Name} : {aStr}", UCL_GUIStyle.LabelStyle);
                                        }
                                        else if (aVal.IsNumber())
                                        {
                                            GUILayout.Label($"({aFieldType.Name}){aField.Name} : {aVal}", UCL_GUIStyle.LabelStyle);
                                        }
                                        else
                                        {
                                            //using (new GUILayout.HorizontalScope())
                                            //{
                                            //    GUILayout.Label($"({aFieldType.GetTypeName()}){aField.Name} : ", UCL_GUIStyle.LabelStyle);
                                            //}
                                            using (new GUILayout.HorizontalScope())//"box"
                                            {
                                                OnGUI($"({aFieldType.GetTypeName()}){aField.Name}", aVal, iDataDic.GetSubDic($"Field_{aField.Name}"), iSpace + 1);
                                            }
                                        }

                                    }
                                }
                            }
                        }
                    }

                    return;
                }
                GUILayout.Label(iObj.ToString(), UCL_GUIStyle.LabelStyle);
            }

            public static void IEnumerableOnGUI(string fieldName, object iObj, UCL_ObjectDictionary iDataDic, int iSpace = 0)
            {
                IEnumerable aEnum = iObj as IEnumerable;
                if(aEnum == null)
                {
                    return;
                }
                GUILayout.BeginHorizontal();
                bool aIsShowField = UCL_GUILayout.Toggle(iDataDic, IsShowFieldKey);
                using (var aScope = new GUILayout.VerticalScope())
                {
                    GUILayout.Label(fieldName, UCL_GUIStyle.LabelStyle);
                    if (aIsShowField)
                    {
                        if (aEnum is IDictionary aDic)
                        {
                            foreach (var aKey in aDic.Keys)
                            {
                                string aHash = aKey.GetHashCode().ToString();

                                var aVal = aDic[aKey];
                                if(aVal is string or int or float or System.Enum)
                                {
                                    GUILayout.Label($"{aKey} : {aVal}", UCL_GUIStyle.LabelStyle);
                                }
                                else
                                {
                                    using (new GUILayout.HorizontalScope())
                                    {
                                        bool show = UCL_GUILayout.Toggle(iDataDic, $"Show_{aHash}");
                                        using (var aScope2 = new GUILayout.VerticalScope())
                                        {
                                            GUILayout.Label(aKey.ToString(), UCL_GUIStyle.LabelStyle);
                                            if (show)
                                            {


                                                //OnGUI("Key", aKey, iDataDic.GetSubDic($"{aHash}_Key"), iSpace + 1);
                                                //GUILayout.Label($":", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                                                OnGUI("Value", aVal, iDataDic.GetSubDic($"{aHash}_Val"), iSpace + 1);
                                                //GUILayout.Label($",", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            int aIndex = 0;
                            foreach (var aVal in aEnum)
                            {
                                using (var aScope2 = new GUILayout.HorizontalScope())
                                {
                                    OnGUI($"({++aIndex})", aVal, iDataDic.GetSubDic($"{aIndex}_Val"), iSpace + 1);
                                    //GUILayout.Label($",", UCL_GUIStyle.LabelStyle);
                                }
                            }
                        }
                    }

                }


                GUILayout.EndHorizontal();


            }
        }

        #endregion
    }
}
