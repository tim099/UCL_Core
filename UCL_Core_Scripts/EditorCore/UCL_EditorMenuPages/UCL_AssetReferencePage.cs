
// 區塊職責：UCL_Asset 雙向引用查詢 Page（Runtime 可用的 IMGUI 檢視頁）。
// 物理意義：給定一個目標 asset (Type + ID)，呈現兩個方向的引用關係：
//   ① 被引用 (Referenced By)：哪些其他 asset 引用了這個 asset，並標出引用發生的欄位路徑。
//   ② 引用到 (References To)：這個 asset 引用了哪些其他 asset，並標出欄位路徑。
//   每筆可點按鈕直接跳轉到該 asset 的編輯頁。
// 數值影響：純讀取查詢，不修改任何 asset。掃描邏輯全走 UCL_AssetReferenceUtil（Runtime-safe）。
//
// 設計理由 (Tim 2026-05-27 派 task)：
//   從 UCL_CommonEditPage 的 top-bar 入口按鈕開啟。功能需在 build 出來的遊戲內也能運作，
//   故不依賴 Editor-only 的 Cmd / AssetDatabase，改用 UCL_ModuleService + 反射（見 UCL_AssetReferenceUtil）。
//
// 開啟方式（從 UCL_CommonEditPage）：
//   var p = new UCL_AssetReferencePage();
//   p.SetTarget(assetType, assetID, assetInstance);  // assetInstance 選填（正向查詢可吃當前未存的編輯內容）
//   UCL_GUIPageController.CurrentRenderIns.Push(p);   // Push 內部會呼叫 Init → 自動掃描

using System;
using System.Collections.Generic;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// UCL_Asset 雙向引用查詢頁。給定目標 asset，列出「誰引用我」與「我引用誰」並可跳轉。
    /// </summary>
    public class UCL_AssetReferencePage : UCL_CommonEditorPage
    {
        public override string WindowName =>
            string.Format(UCL_CodeLocalize.Get("AssetRef.TitleFmt"),
                m_TargetType != null ? m_TargetType.Name : "?", m_TargetID ?? "?");

        // 子頁面 — 從 CommonEditPage 帶目標開啟，不掛進頂層 Page 選單
        public override bool ShowInPageMenu => false;

        // 區塊職責：查詢目標（Push 前由 SetTarget 設定）
        // 物理意義：m_TargetType + m_TargetID 唯一定位被查詢的 asset；
        //          m_TargetInstance 為當前編輯中的實例（正向查詢吃它 → 反映未存的修改），可為 null。
        Type m_TargetType;
        string m_TargetID;
        object m_TargetInstance;

        // 區塊職責：掃描結果 state
        // 物理意義：m_Reverse = 被引用清單（誰引用我）；m_Forward = 引用到清單（我引用誰）；
        //          m_Scanned = 是否已跑過掃描（避免每幀重掃）。
        List<UCL_AssetReferenceUtil.RefHit> m_Reverse = new List<UCL_AssetReferenceUtil.RefHit>();
        List<UCL_AssetReferenceUtil.RefHit> m_Forward = new List<UCL_AssetReferenceUtil.RefHit>();
        bool m_Scanned = false;

        // 區塊職責：Push 前由呼叫端設定查詢目標
        // 物理意義：Init 由 Push 觸發 → 故 target 必須在 Push 前先設定；instance 選填。
        public void SetTarget(Type iAssetType, string iAssetID, object iAssetInstance = null)
        {
            m_TargetType = iAssetType;
            m_TargetID = iAssetID;
            m_TargetInstance = iAssetInstance;
        }

        public override void Init(UCL_GUIPageController iController)
        {
            base.Init(iController);
            Rescan();
        }

        // 區塊職責：跑雙向掃描 → 填入 m_Reverse / m_Forward
        // 物理意義：正向用當前實例（若有，含未存修改）否則載入；反向掃所有 asset（可能較慢，故只在進頁 / 按鈕時跑）。
        void Rescan()
        {
            m_Reverse.Clear();
            m_Forward.Clear();
            m_Scanned = false;

            if (m_TargetType == null || string.IsNullOrEmpty(m_TargetID))
            {
                Debug.LogWarning("[AssetRefPage] target 未設定，略過掃描。");
                return;
            }

            // 正向：優先用當前編輯實例（反映未存的修改）；沒帶就從磁碟載入
            object aSource = m_TargetInstance ?? UCL_AssetReferenceUtil.LoadAsset(m_TargetType, m_TargetID);
            m_Forward = UCL_AssetReferenceUtil.FindForwardReferences(aSource);

            // 反向：掃所有 UCL_Asset
            m_Reverse = UCL_AssetReferenceUtil.FindReverseReferences(m_TargetType, m_TargetID);

            m_Scanned = true;
            Debug.Log($"[AssetRefPage] {m_TargetType.Name}/{m_TargetID} — 被引用 {m_Reverse.Count} 筆、引用到 {m_Forward.Count} 筆。");
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            // 返回上一頁
            if (GUILayout.Button(UCL_CodeLocalize.Get("AssetRef.Btn.Back"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                if (p_Controller != null) p_Controller.Pop();
            }
            // 重新掃描
            if (GUILayout.Button(UCL_CodeLocalize.Get("AssetRef.Btn.Rescan"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                Rescan();
            }
        }

        protected override void ContentOnGUI()
        {
            if (m_TargetType == null || string.IsNullOrEmpty(m_TargetID))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("AssetRef.NoTarget"), UCL_GUIStyle.LabelStyle);
                return;
            }

            // 區塊：目標摘要
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("AssetRef.TargetFmt"), m_TargetType.Name, m_TargetID), UCL_GUIStyle.LabelStyle);
                GUILayout.FlexibleSpace();
                if (!m_Scanned)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("AssetRef.NotScanned"), UCL_GUIStyle.LabelStyle);
                }
            }

            // 區塊：被引用 (Referenced By) — 誰引用了我
            DrawSection(
                string.Format(UCL_CodeLocalize.Get("AssetRef.ReverseHeaderFmt"), m_Reverse.Count),
                m_Reverse,
                "Reverse");

            GUILayout.Space(UCL_GUIStyle.GetScaledSize(8));

            // 區塊：引用到 (References To) — 我引用了誰
            DrawSection(
                string.Format(UCL_CodeLocalize.Get("AssetRef.ForwardHeaderFmt"), m_Forward.Count),
                m_Forward,
                "Forward");
        }

        // 區塊職責：畫一個引用清單區塊
        // 物理意義：每筆一列 — [開啟] 按鈕 + 「型別 / ID」+ 欄位路徑 +（模組 / 不存在標記）。
        // 數值影響：點「開啟」會載入該 asset 並 push 其編輯頁；不修改資料。
        void DrawSection(string iHeader, List<UCL_AssetReferenceUtil.RefHit> iHits, string iTag)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label(iHeader, UCL_GUIStyle.GetLabelStyle(Color.cyan));

                if (iHits == null || iHits.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("AssetRef.Empty"), UCL_GUIStyle.LabelStyle);
                    return;
                }

                for (int i = 0; i < iHits.Count; i++)
                {
                    var aHit = iHits[i];
                    using (new GUILayout.HorizontalScope())
                    {
                        // 開啟按鈕 — 跳轉到該 asset 的編輯頁
                        GUI.enabled = aHit.Exists && aHit.AssetType != null && !string.IsNullOrEmpty(aHit.AssetID);
                        if (GUILayout.Button(UCL_CodeLocalize.Get("AssetRef.Btn.Open"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            OpenAssetEditPage(aHit.AssetType, aHit.AssetID);
                        }
                        GUI.enabled = true;

                        // 型別 / ID
                        string aExistMark = aHit.Exists ? "" : "  ❌";
                        GUILayout.Label($"{aHit.AssetTypeName} / {aHit.AssetID}{aExistMark}", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(280)));

                        // 欄位路徑（引用發生處）
                        GUILayout.Label(aHit.FieldPath ?? "", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(300)));

                        // 模組
                        if (!string.IsNullOrEmpty(aHit.ModuleID))
                        {
                            GUILayout.Label($"[{aHit.ModuleID}]", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                        }
                        GUILayout.FlexibleSpace();
                    }
                }
            }
        }

        // 區塊職責：載入指定 asset 並開啟其編輯頁
        // 物理意義：用 Runtime-safe 的 LoadAsset 取得實例 → UCL_CommonEditPage.Create（會 clone 一份編輯）。
        void OpenAssetEditPage(Type iType, string iID)
        {
            try
            {
                object aAsset = UCL_AssetReferenceUtil.LoadAsset(iType, iID);
                if (aAsset is UCLI_CommonEditable aEditable)
                {
                    UCL_CommonEditPage.Create(aEditable);
                }
                else
                {
                    Debug.LogWarning($"[AssetRefPage] 無法開啟 {iType?.Name}/{iID}：載入結果非 UCLI_CommonEditable。");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[AssetRefPage] 開啟編輯頁失敗 {iType?.Name}/{iID}: {e.Message}");
            }
        }
    }
}
