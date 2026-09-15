// 區塊職責：Missing Reference 排查與修復頁 —— 掃出「欄位指著一個已經不存在的東西」，並可就地清空。
// 物理意義：Unity 的序列化把物件引用存成 fileID/instanceID。目標被刪掉之後，
//          那個欄位**不會變成乾淨的 null** —— 它變成「instanceID 還在、物件已經沒了」的半死狀態。
//          ⭐ 而這正是本頁存在的理由：**乾淨的 null 與斷掉的引用在 Inspector 上都畫成 None**，
//            兩者在畫面上同形，而只有後者會在別人遍歷它時炸。
// 數值影響：
//          · 掃描：**純讀**（開場景物件走 SerializedObject 讀取，不 Apply、不標 dirty）。
//          · 修復：**會改檔**。兩種修法不同性質，所以分成兩顆按鈕、各自確認：
//            ① 清空欄位（`objectReferenceInstanceIDValue = 0`）—— 可逆（重新指定即可）
//            ② 移除缺腳本的 Component —— **不可逆**（那個 Component 上的資料一併消失）
//          · 每次修復都會 MarkSceneDirty／SetDirty，但**不自動存檔** —— 存不存由人按 Ctrl+S 決定。
//
// 🩸 為什麼「缺腳本」與「斷引用」要分開報（2026-09-15 Tim 回報的那條 NRE 推來的）：
//   Tim 撞到的是 `Odin.InspectorConfig.UpdateOdinEditors()` → `PropertyEditor.ClearEditorsAndRebuild()` 的 NRE。
//   那條 stack 炸在 Unity 內部，**它不是欄位斷掉造成的** —— 最常見的成因是
//   「被 Inspector 追蹤中的物件上有一個 m_Script 為 null 的 Component」。
//   ⇒ 所以把兩者混成一個「missing」數字會讓人拿錯的那一格去對錯的症狀。
//   ⛔ 本頁不宣稱能修那條 NRE：它只負責把**可觀測的**兩類斷點列出來並清掉。
//
// 設計取捨：
//   · 掃描範圍是**顯式勾選**，預設只勾開啟中的場景 —— 全庫 prefab 掃描在大專案上是數十秒等待，
//     預設打開會讓人以為本頁很慢。
//   · 「掃了 0 個目標」與「掃了 N 個目標、0 個問題」**在輸出上不同形**（前者是設定錯，後者是好消息）。
//   · 逐筆列出完整路徑 ＋ 欄位 propertyPath —— ⛔ 不只給計數：
//     一個只給數字的掃描工具，修完之後沒有人能說出他修掉的是什麼。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// Missing Reference 排查／修復頁 —— 掃描場景／Prefab／ScriptableObject 上
    /// 「指向已刪除物件」的欄位與「缺腳本」的 Component，並可就地清空／移除。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_MissingReferencePage.md")]
    public class UCL_MissingReferencePage : UCL_CommonEditorPage
    {
        public override string WindowName => UCL_CodeLocalize.Get("MissingRef.Title");
        public override bool ShowInPageMenu => false;   // 由 ToolBox 進入

        public static UCL_MissingReferencePage Create() => UCL_EditorPage.Create<UCL_MissingReferencePage>();

        #region 資料

        /// <summary>一筆問題的種類 —— 兩類的**修法不同、可逆性也不同**，所以不合併。</summary>
        public enum Kind
        {
            /// <summary>欄位指向一個已被刪除的物件（instanceID 還在、物件沒了）。清空即可。</summary>
            BrokenReference,
            /// <summary>Component 的腳本不見了（m_Script == null）。只能整個移除。</summary>
            MissingScript,
        }

        // 區塊職責：一筆掃描結果。
        // 物理意義：`Target` 是**持有那個欄位的物件**（Component／ScriptableObject），
        //          `PropertyPath` 是欄位在它裡面的序列化路徑 —— 兩者合起來才定位得到一格。
        // 數值影響：純資料。⚠ `Target` 可能在掃描之後被別的操作銷毀 ⇒ 修復前一律重驗（見 FixOne）。
        class Hit
        {
            public Kind Kind;
            public UnityEngine.Object Target;      // Component / ScriptableObject（MissingScript 時為 null）
            public GameObject Owner;               // 所屬 GameObject（資產類為 null）
            public string Path;                    // 場景階層路徑 或 資產路徑
            public string ComponentType;
            public string PropertyPath;            // BrokenReference 才有
            public string PropertyDisplay;
            public bool Fixed;
        }

        readonly List<Hit> m_Hits = new List<Hit>();
        // ⛔ 刻意沒有 UCL_ObjectDictionary：三個掃描開關走 `CheckBox(bool, label)`，狀態由本類自持。
        //    留一個沒人讀的 dic 會讓下一個人以為「折疊／勾選狀態有被持久化」，而它其實沒有。

        bool m_ScanScenes = true;
        bool m_ScanPrefabs = false;
        bool m_ScanScriptableObjects = false;

        bool m_Scanned = false;          // ⚠ 與「掃到 0 筆」不同意義，見 DrawResult
        int m_ScannedObjectCount = 0;    // 這次掃了幾個序列化物件（分母）
        int m_ScannedRootCount = 0;      // 這次掃了幾個場景物件／資產（目標數）
        string m_Result = "";
        Vector2 m_Scroll;

        const int MAX_LIST = 300;        // 只畫前 N 筆，其餘走「匯出全文」

        #endregion

        protected override void ContentOnGUI()
        {
            DrawIntro();
            DrawScanPanel();
            DrawResult();
        }

        // ===========================================================
        // 區塊：說明 —— ⛔ 不只寫按鈕名
        // 物理意義：使用者來這一頁通常是因為看到一條 NRE，而他需要先知道「本頁治不治得了它」。
        // 數值影響：純顯示。
        // ===========================================================
        void DrawIntro()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("MissingRef.Intro"), WrapLabelStyle);
                GUILayout.Space(2);
                GUILayout.Label(UCL_CodeLocalize.Get("MissingRef.NotThis"), WrapLabelStyle);
            }
        }

        void DrawScanPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label($"<b>{UCL_CodeLocalize.Get("MissingRef.Scope")}</b>", WrapLabelStyle);

                // ⭐ 用 `UCL_GUILayout.CheckBox(bool, label)`（Tim 2026-09-15 指定）——
                //   它畫 ✔/空 的勾選盒，語意是「這個範圍要不要掃」；
                //   `Toggle` 畫的是 ▼/► 折疊三角，那是「要不要展開」，**兩者語意不同**。
                // ⚠ 刻意用「呼叫端自持 bool」這支多載而不是吃 UCL_ObjectDictionary 的：
                //   本頁三個開關各有不同**預設值**（場景預設開、全庫掃描預設關），
                //   dic 版本要另外種一次初值，而那份初值會跟欄位宣告各說各話。
                m_ScanScenes = UCL_GUILayout.CheckBox(
                    m_ScanScenes, UCL_CodeLocalize.Get("MissingRef.Scope.Scenes"));
                m_ScanPrefabs = UCL_GUILayout.CheckBox(
                    m_ScanPrefabs, UCL_CodeLocalize.Get("MissingRef.Scope.Prefabs"));
                m_ScanScriptableObjects = UCL_GUILayout.CheckBox(
                    m_ScanScriptableObjects, UCL_CodeLocalize.Get("MissingRef.Scope.ScriptableObjects"));

                GUILayout.Space(4);
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(UCL_CodeLocalize.Get("MissingRef.Scan"),
                        UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        Scan();
                    }
                    // ⚠ 兩顆修復鈕分開，而且**只有掃過之後才出現** ——
                    //   沒掃就給修復鈕，等於讓人對一份不存在的清單按「全部修好」。
                    if (m_Scanned && CountUnfixed(Kind.BrokenReference) > 0)
                    {
                        if (GUILayout.Button(
                            string.Format(UCL_CodeLocalize.Get("MissingRef.FixAllRefsFmt"),
                                CountUnfixed(Kind.BrokenReference)),
                            UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            if (EditorUtility.DisplayDialog(
                                UCL_CodeLocalize.Get("MissingRef.Title"),
                                string.Format(UCL_CodeLocalize.Get("MissingRef.ConfirmClearFmt"),
                                    CountUnfixed(Kind.BrokenReference)),
                                "OK", "Cancel"))
                            {
                                FixAll(Kind.BrokenReference);
                            }
                        }
                    }
                    GUILayout.FlexibleSpace();
                }

                if (!string.IsNullOrEmpty(m_Result))
                {
                    GUILayout.Label(m_Result, WrapLabelStyle);
                }
            }
        }

        // ===========================================================
        // 區塊：掃描 —— 純讀，不改任何東西。
        // 物理意義：三種來源最後都收斂成同一件事「拿到一個 UnityEngine.Object，走它的 SerializedObject」。
        // 數值影響：⚠ 不 Apply、不 SetDirty。掃描完場景仍應該是乾淨的（除非本來就髒）。
        // ⚠ Prefab／SO 掃描會 LoadAssetAtPath 大量資產 ⇒ 記憶體與時間都不便宜，所以預設不勾。
        // ===========================================================
        void Scan()
        {
            m_Hits.Clear();
            m_ScannedObjectCount = 0;
            m_ScannedRootCount = 0;
            m_Scanned = true;

            try
            {
                if (m_ScanScenes) ScanOpenScenes();
                if (m_ScanPrefabs) ScanAssets("t:Prefab", true);
                if (m_ScanScriptableObjects) ScanAssets("t:ScriptableObject", false);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                m_Result = $"❌ {ex.Message}";
                return;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            BuildResultLine();
        }

        // ⚠ 結論句刻意分三種，因為它們要人做的事完全不同：
        //   ① 一個目標都沒掃到 ⇒ 是**範圍沒勾對**，不是「很乾淨」
        //   ② 掃到目標、零問題 ⇒ 真的乾淨
        //   ③ 有問題 ⇒ 給計數並往下看清單
        void BuildResultLine()
        {
            int aBroken = CountUnfixed(Kind.BrokenReference);
            int aMissing = CountUnfixed(Kind.MissingScript);

            if (m_ScannedRootCount == 0)
            {
                m_Result = UCL_CodeLocalize.Get("MissingRef.NoTarget");
                return;
            }
            if (aBroken == 0 && aMissing == 0)
            {
                m_Result = string.Format(UCL_CodeLocalize.Get("MissingRef.CleanFmt"),
                    m_ScannedRootCount, m_ScannedObjectCount);
                return;
            }
            m_Result = string.Format(UCL_CodeLocalize.Get("MissingRef.FoundFmt"),
                aBroken, aMissing, m_ScannedRootCount, m_ScannedObjectCount);
        }

        void ScanOpenScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; ++i)
            {
                var aScene = SceneManager.GetSceneAt(i);
                if (!aScene.isLoaded) continue;
                foreach (var aRoot in aScene.GetRootGameObjects())
                {
                    ScanGameObjectRecursive(aRoot, aScene.name);
                }
            }
        }

        void ScanGameObjectRecursive(GameObject iGo, string iSceneName)
        {
            if (iGo == null) return;
            ++m_ScannedRootCount;
            string aPath = $"{iSceneName}/{GetHierarchyPath(iGo)}";
            ScanGameObject(iGo, aPath);

            var aTrans = iGo.transform;
            for (int i = 0; i < aTrans.childCount; ++i)
            {
                ScanGameObjectRecursive(aTrans.GetChild(i).gameObject, iSceneName);
            }
        }

        // 區塊職責：掃一個 GameObject 上的所有 Component。
        // 物理意義：⭐ `GetComponents` 回傳的陣列裡，**缺腳本的那一格是 null** ——
        //          那是 Unity 唯一告訴你「這裡本來有個 Component」的方式。
        // 數值影響：null 那格記成 MissingScript 並**跳過欄位掃描**（沒有 SerializedObject 可建）。
        void ScanGameObject(GameObject iGo, string iPath)
        {
            var aComponents = iGo.GetComponents<Component>();
            for (int i = 0; i < aComponents.Length; ++i)
            {
                var aComp = aComponents[i];
                if (aComp == null)
                {
                    // ⚠ 這一格就是 Inspector 重建最容易炸的那種。索引要留 —— 移除時要指得回去。
                    m_Hits.Add(new Hit
                    {
                        Kind = Kind.MissingScript,
                        Target = null,
                        Owner = iGo,
                        Path = iPath,
                        ComponentType = $"(index {i})",
                    });
                    continue;
                }
                ScanSerialized(aComp, iGo, iPath, aComp.GetType().Name);
            }
        }

        void ScanAssets(string iFilter, bool iIsPrefab)
        {
            var aGuids = AssetDatabase.FindAssets(iFilter);
            for (int i = 0; i < aGuids.Length; ++i)
            {
                string aPath = AssetDatabase.GUIDToAssetPath(aGuids[i]);
                if (string.IsNullOrEmpty(aPath)) continue;
                if (EditorUtility.DisplayCancelableProgressBar(
                    UCL_CodeLocalize.Get("MissingRef.Title"), aPath, (float)i / Mathf.Max(1, aGuids.Length)))
                {
                    break;   // 取消 ⇒ 帶著**已掃到的部分**離開；分母也停在這裡，不假裝掃完了
                }
                ++m_ScannedRootCount;

                if (iIsPrefab)
                {
                    var aGo = AssetDatabase.LoadAssetAtPath<GameObject>(aPath);
                    if (aGo == null) continue;
                    ScanPrefabRecursive(aGo, aPath);
                }
                else
                {
                    // ⭐ `LoadAllAssetsAtPath` 而不是 `LoadAssetAtPath<T>`：後者**只拿主資產**，
                    //    看不到子資產（sub-asset）。
                    // 🩸 2026-09-15 實測血證：`Assets/Settings/DefaultVolumeProfile.asset` 裡有 5 個
                    //    `m_Script: {fileID: 0}` 的 VolumeComponent 子資產
                    //    （`Unity.RenderPipelines.Core.Editor.Tests` 的測試類別，正常專案不編那個組件）。
                    //    ⇒ 用 `LoadAssetAtPath<ScriptableObject>` 掃的話，這一份會被報成「乾淨」——
                    //      而它正是本頁最該抓到的那一種。
                    // ⚠ 陣列裡**腳本解析不到的那幾格是 null** —— 那就是子資產層的「缺腳本」。
                    var aAll = AssetDatabase.LoadAllAssetsAtPath(aPath);
                    if (aAll == null) continue;
                    for (int k = 0; k < aAll.Length; ++k)
                    {
                        var aObj = aAll[k];
                        if (aObj == null)
                        {
                            m_Hits.Add(new Hit
                            {
                                Kind = Kind.MissingScript,
                                Target = null,
                                Owner = null,
                                Path = aPath,
                                ComponentType = $"(sub-asset index {k})",
                            });
                            continue;
                        }
                        ScanSerialized(aObj, null, aPath, aObj.GetType().Name);
                    }
                }
            }
        }

        void ScanPrefabRecursive(GameObject iGo, string iAssetPath)
        {
            if (iGo == null) return;
            ScanGameObject(iGo, $"{iAssetPath} :: {GetHierarchyPath(iGo)}");
            var aTrans = iGo.transform;
            for (int i = 0; i < aTrans.childCount; ++i)
            {
                ScanPrefabRecursive(aTrans.GetChild(i).gameObject, iAssetPath);
            }
        }

        // ===========================================================
        // 區塊職責：走一個物件的所有序列化欄位，挑出「斷掉的物件引用」。
        // 物理意義：⭐ **判準只有一條**：
        //          `objectReferenceValue == null` ＋ `objectReferenceInstanceIDValue != 0`。
        //          乾淨的 null 兩者都是 0／null；斷掉的引用則是「id 還在、物件已經沒了」。
        //          ⛔ 只看 `objectReferenceValue == null` 會把**每一個沒填的欄位**都報成錯，
        //            那種清單長到沒有人會看，而它看起來跟真的掃描一模一樣。
        // 數值影響：純讀。`NextVisible(true)` 會走進子物件與陣列元素。
        // ===========================================================
        void ScanSerialized(UnityEngine.Object iObj, GameObject iOwner, string iPath, string iTypeName)
        {
            if (iObj == null) return;
            ++m_ScannedObjectCount;

            using (var aSo = new SerializedObject(iObj))
            {
                var aIt = aSo.GetIterator();
                while (aIt.NextVisible(true))
                {
                    if (aIt.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (aIt.objectReferenceValue != null) continue;
                    if (aIt.objectReferenceInstanceIDValue == 0) continue;   // 乾淨的空欄位，不是問題

                    m_Hits.Add(new Hit
                    {
                        Kind = Kind.BrokenReference,
                        Target = iObj,
                        Owner = iOwner,
                        Path = iPath,
                        ComponentType = iTypeName,
                        PropertyPath = aIt.propertyPath,
                        PropertyDisplay = aIt.displayName,
                    });
                }
            }
        }

        #region 修復

        // ===========================================================
        // 區塊職責：清空一格斷掉的引用。
        // 物理意義：`objectReferenceInstanceIDValue = 0` 才是真的清空 ——
        //          `objectReferenceValue = null` 在某些情況下不會把殘存的 id 抹掉。
        // 數值影響：**會改檔**。改完標 dirty，⛔ 但不自動存檔（存檔是人的決定，不是工具的）。
        // ⚠ 修復前一律重驗那一格**現在仍然是壞的** —— 掃描與按下按鈕之間可能隔了很久，
        //   而「照著一份過期清單去改檔」是這類工具最貴的失效方式。
        // ===========================================================
        bool FixOne(Hit iHit, out string oWhy)
        {
            oWhy = "";
            if (iHit.Kind != Kind.BrokenReference) { oWhy = "not a broken reference"; return false; }
            if (iHit.Target == null) { oWhy = "target gone"; return false; }

            using (var aSo = new SerializedObject(iHit.Target))
            {
                var aProp = aSo.FindProperty(iHit.PropertyPath);
                if (aProp == null) { oWhy = "property gone"; return false; }
                if (aProp.propertyType != SerializedPropertyType.ObjectReference)
                {
                    oWhy = "property type changed";
                    return false;
                }
                // 重驗：現在還是「壞的」嗎
                if (aProp.objectReferenceValue != null) { oWhy = "already resolved"; return false; }
                if (aProp.objectReferenceInstanceIDValue == 0) { oWhy = "already empty"; return false; }

                // ⭐ 陣列元素要**移除**，不是清成 null。
                // 物理意義：`List<T>` 裡清成 null 會留下一個洞，而洞照樣會被 foreach 走到 ——
                //          遍歷它的那段程式碼（例如 VolumeProfile 逐個畫 component）**還是會炸**，
                //          只是炸在 null 而不是炸在半死的引用上。
                // ⇒ 症狀從「壞的引用」變成「null 洞」，而修的人會以為自己修好了。
                if (TryGetArrayOwner(aSo, iHit.PropertyPath, out var aArray, out int aIndex))
                {
                    int aBefore = aArray.arraySize;
                    aArray.DeleteArrayElementAtIndex(aIndex);
                    // ⚠ 物件引用陣列的既有怪癖：第一次 Delete 只是把該格設成 null，
                    //   要再刪一次才真的縮短陣列。⇒ 用**長度有沒有變**判定，不靠記憶。
                    if (aArray.arraySize == aBefore) aArray.DeleteArrayElementAtIndex(aIndex);
                    aSo.ApplyModifiedPropertiesWithoutUndo();
                }
                else
                {
                    aProp.objectReferenceInstanceIDValue = 0;
                    aSo.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            MarkDirty(iHit);
            iHit.Fixed = true;
            return true;
        }

        // 區塊職責：移除一個缺腳本的 Component。
        // 物理意義：⛔ **不可逆** —— 那個 Component 上原本序列化的資料會一起消失，
        //          而腳本如果只是暫時編不過（改名／assembly 沒編好），移除就等於把資料丟了。
        // 數值影響：走 Unity 官方的 `GameObjectUtility.RemoveMonoBehavioursWithMissingScript`，
        //          它一次移除**該 GameObject 上全部**缺腳本的 Component ⇒ 回傳數量可能 > 1。
        int FixMissingScripts(GameObject iGo)
        {
            if (iGo == null) return 0;
            int aCount = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(iGo);
            if (aCount > 0)
            {
                EditorUtility.SetDirty(iGo);
                if (!EditorUtility.IsPersistent(iGo))
                {
                    EditorSceneManager.MarkSceneDirty(iGo.scene);
                }
            }
            return aCount;
        }

        void MarkDirty(Hit iHit)
        {
            if (iHit.Target != null) EditorUtility.SetDirty(iHit.Target);
            if (iHit.Owner != null)
            {
                EditorUtility.SetDirty(iHit.Owner);
                if (!EditorUtility.IsPersistent(iHit.Owner))
                {
                    EditorSceneManager.MarkSceneDirty(iHit.Owner.scene);
                }
            }
        }

        // ⚠ 批次修復要印出**逐筆結果**，包含失敗的那幾筆與理由 ——
        //   只印「已修復 N 筆」的話，失敗的那幾筆會安靜地留在磁碟上，而使用者以為全清了。
        void FixAll(Kind iKind)
        {
            int aOk = 0;
            var aFail = new List<string>();
            foreach (var aHit in m_Hits)
            {
                if (aHit.Kind != iKind || aHit.Fixed) continue;
                if (FixOne(aHit, out string aWhy)) ++aOk;
                else aFail.Add($"{aHit.Path} :: {aHit.ComponentType}.{aHit.PropertyPath} ({aWhy})");
            }

            var aSb = new StringBuilder();
            aSb.Append(string.Format(UCL_CodeLocalize.Get("MissingRef.FixedFmt"), aOk));
            if (aFail.Count > 0)
            {
                aSb.Append(string.Format(UCL_CodeLocalize.Get("MissingRef.FixFailFmt"), aFail.Count));
                foreach (var aLine in aFail) Debug.LogWarning($"[MissingRef] 未修復：{aLine}");
            }
            m_Result = aSb.ToString();
            Debug.Log($"[MissingRef] {m_Result}");
        }

        #endregion

        // ===========================================================
        // 區塊職責：結果清單。
        // 物理意義：逐筆給「在哪個物件、哪個 Component、哪個欄位」——
        //          修完之後要說得出修掉的是什麼，那需要這三格。
        // 數值影響：純顯示 ＋ 單筆修復／選取按鈕。
        // ===========================================================
        void DrawResult()
        {
            if (!m_Scanned) return;
            if (m_Hits.Count == 0) return;

            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label($"<b>{string.Format(UCL_CodeLocalize.Get("MissingRef.ListFmt"), m_Hits.Count)}</b>",
                    WrapLabelStyle);

                using (var aScope = new GUILayout.ScrollViewScope(m_Scroll, GUILayout.MaxHeight(420)))
                {
                    m_Scroll = aScope.scrollPosition;
                    int aDrawn = 0;
                    foreach (var aHit in m_Hits)
                    {
                        if (aDrawn >= MAX_LIST)
                        {
                            GUILayout.Label(string.Format(UCL_CodeLocalize.Get("MissingRef.TruncatedFmt"),
                                MAX_LIST, m_Hits.Count), WrapLabelStyle);
                            break;
                        }
                        ++aDrawn;
                        DrawHit(aHit);
                    }
                }
            }
        }

        void DrawHit(Hit iHit)
        {
            using (new GUILayout.HorizontalScope())
            {
                string aIcon = iHit.Fixed ? "✅"
                    : iHit.Kind == Kind.MissingScript ? "🔴" : "⚠";
                string aWhat = iHit.Kind == Kind.MissingScript
                    ? $"{UCL_CodeLocalize.Get("MissingRef.Kind.MissingScript")} {iHit.ComponentType}"
                    : $"{iHit.ComponentType}.{iHit.PropertyPath}";

                GUILayout.Label($"{aIcon} {iHit.Path}　<b>{aWhat}</b>", WrapLabelStyle);
                GUILayout.FlexibleSpace();

                if (iHit.Owner != null &&
                    GUILayout.Button(UCL_CodeLocalize.Get("MissingRef.Select"),
                        UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    Selection.activeGameObject = iHit.Owner;
                    EditorGUIUtility.PingObject(iHit.Owner);
                }

                if (iHit.Fixed) return;

                if (iHit.Kind == Kind.BrokenReference)
                {
                    if (GUILayout.Button(UCL_CodeLocalize.Get("MissingRef.Clear"),
                        UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        if (FixOne(iHit, out string aWhy))
                        {
                            m_Result = string.Format(UCL_CodeLocalize.Get("MissingRef.FixedFmt"), 1);
                        }
                        else
                        {
                            m_Result = $"⚠ {aWhy}";
                        }
                    }
                }
                else
                {
                    // ⚠ 不可逆 ⇒ 每一顆都要跳確認，⛔ 沒有「全部移除」那顆按鈕。
                    if (GUILayout.Button(UCL_CodeLocalize.Get("MissingRef.RemoveComponent"),
                        UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        if (EditorUtility.DisplayDialog(
                            UCL_CodeLocalize.Get("MissingRef.Title"),
                            string.Format(UCL_CodeLocalize.Get("MissingRef.ConfirmRemoveFmt"), iHit.Path),
                            "OK", "Cancel"))
                        {
                            int aRemoved = FixMissingScripts(iHit.Owner);
                            iHit.Fixed = aRemoved > 0;
                            m_Result = string.Format(UCL_CodeLocalize.Get("MissingRef.RemovedFmt"), aRemoved);
                            // 同一個 GameObject 上的其他缺腳本項一起被移掉了 ⇒ 一併標記，
                            // 否則清單上會留下幾筆「按了沒反應」的殘影。
                            if (aRemoved > 0)
                            {
                                foreach (var aOther in m_Hits)
                                {
                                    if (aOther.Kind == Kind.MissingScript && aOther.Owner == iHit.Owner)
                                        aOther.Fixed = true;
                                }
                            }
                        }
                    }
                }
            }
        }

        #region 小工具

        // 區塊職責：判斷一個 propertyPath 是不是「某個陣列的第 N 格」，是的話交出陣列本身與索引。
        // 物理意義：Unity 的序列化路徑長成 `components.Array.data[3]` —— 尾巴那段是固定形狀。
        // 數值影響：純解析，不改任何東西。解析不出來就回 false，呼叫端退回「清成 null」那條。
        // ⛔ 不用 regex：這個形狀固定且只在尾巴，`LastIndexOf` 比 regex 好讀也好除錯。
        static bool TryGetArrayOwner(SerializedObject iSo, string iPath,
            out SerializedProperty oArray, out int oIndex)
        {
            oArray = null;
            oIndex = -1;
            const string MARK = ".Array.data[";
            int aAt = iPath.LastIndexOf(MARK, StringComparison.Ordinal);
            if (aAt < 0) return false;
            if (!iPath.EndsWith("]", StringComparison.Ordinal)) return false;

            int aNumStart = aAt + MARK.Length;
            string aNum = iPath.Substring(aNumStart, iPath.Length - 1 - aNumStart);
            if (!int.TryParse(aNum, out oIndex)) return false;

            oArray = iSo.FindProperty(iPath.Substring(0, aAt));
            if (oArray == null || !oArray.isArray) { oArray = null; return false; }
            if (oIndex < 0 || oIndex >= oArray.arraySize) { oArray = null; return false; }
            return true;
        }

        int CountUnfixed(Kind iKind)
        {
            int aCount = 0;
            foreach (var aHit in m_Hits)
            {
                if (aHit.Kind == iKind && !aHit.Fixed) ++aCount;
            }
            return aCount;
        }

        static string GetHierarchyPath(GameObject iGo)
        {
            var aSb = new StringBuilder(iGo.name);
            var aParent = iGo.transform.parent;
            while (aParent != null)
            {
                aSb.Insert(0, aParent.name + "/");
                aParent = aParent.parent;
            }
            return aSb.ToString();
        }

        static GUIStyle s_WrapLabel;
        static GUIStyle WrapLabelStyle
        {
            get
            {
                if (s_WrapLabel == null)
                {
                    s_WrapLabel = new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true };
                }
                return s_WrapLabel;
            }
        }

        #endregion
    }
}
#endif
