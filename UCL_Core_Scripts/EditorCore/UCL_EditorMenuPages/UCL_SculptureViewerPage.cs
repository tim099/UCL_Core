// 區塊職責：3D 雕刻觀測頁 — 後台一鍵渲染共用雕刻空間，看大家的作品（Tim 2026-08-13 派 task）。
// 物理意義：資料層在 sculpt.py（gura 的引擎：events 真相源＋增量快取＋等角渲染），本頁不重造渲染 ——
//          只做「展品清單直讀 + spawn 引擎出圖 + 顯示 PNG」三件事，結構對齊 UCL_LibraryManagePage
//          （讀 per-project JSON 顯示、操作 spawn UCL_Core 工具）。觀測免費（驗收管道零門檻），
//          本頁純唯讀不碰錢 —— 落子仍走 Cmd_Sculpture。
// 數值影響：讀 AgentCommands/Sculpture/exhibits.json（展品 preset）與 _last_view.png；
//          渲染 spawn python sculpt.py view（ProcessRegistry 登記，硬規則不裸 Process.Start）。
// 版面（Tim 2026-08-14 要求，比照 UCL_ControlPanelPage）：三個 section 各自可折疊 ——
//          **關鍵操作一律畫在折疊外層 header**（Reload／全景／手動渲染／產生預覽／複製指令），
//          收合後仍可一鍵操作；折疊內只放低頻設定與大面積內容（欄位、滑桿、預覽圖）。
//          折疊狀態走專用 m_FoldDic，不與 PopupAuto 的 m_Dic 共用（見該欄位註解的血證）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 3D 雕刻觀測頁 — 展品一鍵導覽（讀 exhibits.json preset）或手動設定觀測參數渲染。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/Plan/Plan_Sculpture_3D.md")]
    public class UCL_SculptureViewerPage : UCL_CommonEditorPage
    {
        public static UCL_SculptureViewerPage Create()
        {
            var page = new UCL_SculptureViewerPage();
            UCL_GUIPageController.CurrentRenderIns.Push(page);
            return page;
        }

        const string PROC_TAG_PY = "sculpt_viewer_py";
        const int RENDER_TIMEOUT_MS = 60000;

        public override string WindowName => UCL_CodeLocalize.Get("SculptureViewer.Title");
        public override bool ShowInPageMenu => true;

        // 區塊職責：展品 entry — 鏡射 exhibits.json（gura 引擎 exhibit register 寫入的 preset）
        public class ExhibitEntry
        {
            public string Id = "";
            public string Title = "";
            public string Author = "";
            public string Description = "";
            public string Region = "";
            public string ExcludeColor = "";
            public string CreatedAt = "";
        }

        // 區塊職責：頁面 state — 展品快取＋手動觀測參數＋最後渲染結果
        List<ExhibitEntry> m_Exhibits = new List<ExhibitEntry>();
        string m_Region = "";
        string m_ExcludeColor = "";
        string m_LightDir = "-1,-1,-2";
        string m_Ambient = "0.35";
        float m_LightAzimuth = 225f;     // 方位角（度）— 預設對應 -1,-1 方向
        float m_LightElevation = 55f;    // 仰角（度）— 越大光越從頭頂打
        bool m_Shadow;                   // cast shadow 開關（Tim 2026-08-13 拍板可開關，預設關）
        string m_Zoom = "";              // 觀測距離倍率（空=引擎自動縮放收進畫布）
        int m_SelectedExhibit;           // 下拉選單當前展品 index（對齊 m_Exhibits 排序）
        readonly List<string> m_ExhibitOptions = new List<string>();   // 下拉顯示字串（「title (id)」）
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();   // PopupAuto 搜尋 state
        // 區塊職責：各 section 折疊狀態 — **刻意跟 m_Dic 分開**（比照 UCL_ControlPanelPage）
        // 物理意義：折疊是使用者 UI 偏好（該長存）；PopupAuto 搜尋快取是衍生資料（選項變了該失效）。
        // 血證（2026-07-29 Tim QA, UCL_ChatTavernAdminPage）：兩者共用一個 dictionary 時，
        //          資料重載路徑上的 dic.Clear() 會把折疊值一併清掉 → 下一幀退回 iDefaultValue，
        //          症狀是「按某個按鈕就自動展開、而且收不起來」，看起來像 key 撞名、實際是共用快取被清。
        //          本頁 ReloadExhibits 目前不 Clear m_Dic，但先分開 —— 免得日後有人加 Clear 又踩一次。
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();
        string m_LastRenderLog = "";
        Texture2D m_ViewTex;
        DateTime m_ViewTexTime;
        bool m_Loaded;
        // ── 2D→3D 貼圖預覽區（唯讀：只出預覽與現成指令，落子走 Cmd_Sculpture）──
        string m_StampRegion = "1000,1000,9,6";   // 來源區域 x,y,w,h（2D 畫布座標）
        string m_StampAt = "10,10,10";            // 圖左上角貼在 3D 的哪一點
        string m_StampFacing = "z+";              // 貼片法線
        string m_StampThickness = "1";            // 沿法線擠出層數
        string m_StampPersona = "";               // 誰付這筆帳（空＝指令貼出去要自己填）
        string m_StampCmdLine = "";               // 組好的 Cmd 指令（含 expect_pixels 閘門）
        Texture2D m_StampTex;
        DateTime m_StampTexTime;
        // ── 📐 切片輸出區（3D→2D，voxel 色原樣當像素色；免費唯讀）──
        string m_SliceRegion = "";       // x1..x2,y1..y2,z1..z2；法線軸跨度＝厚度
        string m_SliceAxis = "z+";       // 投影法線與近端方向（'+' ＝近端是較小那端）
        string m_SliceOut = "";          // 輸出 PNG（空＝引擎預設 Sculpture/_last_slice.png）
        Texture2D m_SliceTex;
        DateTime m_SliceTexTime;
        string m_SliceOutResolved = "";  // 引擎回報的實際落檔路徑（不自己推）
        // ── 匯出設定（Tim 2026-08-14 追加：可設路徑 + 一鍵開資料夾）──
        // 空＝沿用引擎預設 Sculpture/exports；**檔名一律由引擎產生**，本頁不組檔名（避免兩邊分岔）
        string m_ExportDir = "";

        static string SculptureDir => Path.Combine(UCL_AgentCommandsPath.DataRoot, "Sculpture");
        static string ExhibitsJsonPath => Path.Combine(SculptureDir, "exhibits.json");
        static string LastViewPng => Path.Combine(SculptureDir, "_last_view.png");
        static string LastSlicePng => Path.Combine(SculptureDir, "_last_slice.png");
        // 匯出預設資料夾 —— 與 sculpt.py cmd_export 的 fallback 同值（兩端對齊義務）
        static string DefaultExportDir => Path.Combine(SculptureDir, "exports");
        // 2D 共用畫布的 view 輸出（canvas.py 寫）—— 貼圖預覽讀透明變體，未繪製＝alpha 0
        static string CanvasLastViewTPng => Path.Combine(UCL_AgentCommandsPath.DataRoot, "Canvas", "_last_view_t.png");
        // ⚠ 不寫死 UCL_Core 掛載路徑（各專案不同會靜默壞）—— 由 CorePath 現算，只用於組給人複製的指令字串
        static string CoreToolsRel => $"{UCL_EditorPath.CorePath}/Tools~/AgentCommands";

        // 區塊職責：頁面骨架 — 三個可折疊 section（比照 UCL_ControlPanelPage，Tim 2026-08-14 要求）。
        // 物理意義：**關鍵操作一律畫在折疊外層 header**（Reload／全景／手動渲染／產生預覽／複製指令），
        //          收合後仍可一鍵操作；折疊內只放低頻設定與大面積內容（欄位、滑桿、預覽圖）。
        //          預設展開的只有展品導覽 —— 那是本頁的主要用途；另外兩區預設收起，
        //          免得一進頁面就被一整片欄位淹掉（本頁的內容量已經是三個 section 的規模了）。
        protected override void ContentOnGUI()
        {
            if (!m_Loaded) { ReloadExhibits(); m_Loaded = true; }

            DrawExhibitSection();
            GUILayout.Space(8);
            DrawManualSection();
            GUILayout.Space(8);
            DrawSliceSection();
            GUILayout.Space(8);
            DrawStampPreviewSection();

            if (!string.IsNullOrEmpty(m_LastRenderLog))
            {
                using (new GUILayout.HorizontalScope())
                {
                    // 錯誤/輸出訊息一鍵複製（Tim 2026-08-13 追加）—— 貼給 agent 排錯不用手抄
                    if (GUILayout.Button("📋 複製", UCL_GUIStyle.GetButtonStyle(Color.white),
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(70))))
                    {
                        GUIUtility.systemCopyBuffer = m_LastRenderLog;
                    }
                    GUILayout.Label(m_LastRenderLog, WrapLabelStyle);
                }
            }

            // ── 渲染結果：_last_view.png（檔案 mtime 變了才重載，避免每幀 IO）──
            DrawViewTexture();
        }

        // ── 展品導覽區：exhibits.json 直讀，一鍵套 preset 渲染（預設展開＝本頁主要用途）──
        void DrawExhibitSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "ExhibitFold", 21, iDefaultValue: true);
                    GUILayout.Label($"<b>🏛 {UCL_CodeLocalize.Get("SculptureViewer.Exhibits")}</b>",
                        UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button(UCL_CodeLocalize.Get("SculptureViewer.Reload"),
                            UCL_GUIStyle.GetButtonStyle(Color.white), GUILayout.Width(UCL_GUIStyle.GetScaledSize(120))))
                    {
                        ReloadExhibits();
                    }
                    if (GUILayout.Button(UCL_CodeLocalize.Get("SculptureViewer.RenderAll"),
                            UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.Width(UCL_GUIStyle.GetScaledSize(160))))
                    {
                        Render("");   // 無參數＝整體全景
                    }
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                if (m_Exhibits.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("SculptureViewer.NoExhibit"), UCL_GUIStyle.LabelStyle);
                }
                else
                {
                    // 下拉選單（Tim 2026-08-13 改版 — 展品多了逐列按鈕會佔滿頁面；PopupAuto 過門檻自帶搜尋）
                    using (new GUILayout.HorizontalScope("box"))
                    {
                        int aNewSel = UCL_GUILayout.PopupAuto(m_SelectedExhibit, m_ExhibitOptions, m_Dic, "ExhibitPicker",
                            10, GUILayout.Width(UCL_GUIStyle.GetScaledSize(300)));
                        if (aNewSel != m_SelectedExhibit && aNewSel >= 0 && aNewSel < m_Exhibits.Count)
                        {
                            m_SelectedExhibit = aNewSel;
                            // 選中展品 → 觀測參數帶入 preset（手動區與匯出跟著這個 region 走）
                            m_Region = m_Exhibits[aNewSel].Region;
                            m_ExcludeColor = m_Exhibits[aNewSel].ExcludeColor;
                        }
                        if (GUILayout.Button("🏛 " + UCL_CodeLocalize.Get("SculptureViewer.RenderExhibit"),
                                UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.Width(UCL_GUIStyle.GetScaledSize(160))))
                        {
                            // preset 由引擎讀展品檔，本頁不重組參數；陰影開關疊加在 preset 上
                            Render($" --exhibit=\"{m_Exhibits[m_SelectedExhibit].Id}\"{(m_Shadow ? " --shadow" : "")}");
                        }
                    }
                    if (m_SelectedExhibit >= 0 && m_SelectedExhibit < m_Exhibits.Count)
                    {
                        var aEx = m_Exhibits[m_SelectedExhibit];
                        GUILayout.Label($"{aEx.Id}｜by {aEx.Author}｜region {aEx.Region}\n{aEx.Description}", WrapLabelStyle);
                    }
                }
            }
        }

        // ── 手動觀測區：region / exclude-color / 打光 —— 直接對映引擎 view 旗標 ──
        // header 留「手動渲染」：收合狀態下沿用上次參數重跑是高頻操作，不該逼人先展開。
        void DrawManualSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "ManualViewFold", 21, iDefaultValue: false);
                    GUILayout.Label($"<b>🔭 {UCL_CodeLocalize.Get("SculptureViewer.Manual")}</b>",
                        UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button(UCL_CodeLocalize.Get("SculptureViewer.RenderManual"),
                            UCL_GUIStyle.GetButtonStyle(Color.green), GUILayout.Width(UCL_GUIStyle.GetScaledSize(160))))
                    {
                        RenderManual();
                    }
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                m_Region = DrawField("region (x1..x2,y1..y2,z1..z2)", m_Region);
                m_ExcludeColor = DrawField("exclude-color (c,c,..)", m_ExcludeColor);
                m_LightDir = DrawField("light-dir (x,y,z)", m_LightDir);

                // 打光角度滑桿（Tim 2026-08-13 追加）：方位角/仰角 → 自動組 light-dir 向量
                // （手填向量與滑桿雙軌 —— 滑桿動了才覆寫文字欄，直接打字的自由保留）
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"☀ 方位角 {m_LightAzimuth:F0}°", UCL_GUIStyle.LabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(130)));
                    float aNewAz = GUILayout.HorizontalSlider(m_LightAzimuth, 0f, 360f,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(220)));
                    GUILayout.Label($"仰角 {m_LightElevation:F0}°", UCL_GUIStyle.LabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    float aNewEl = GUILayout.HorizontalSlider(m_LightElevation, 5f, 85f,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                    if (!Mathf.Approximately(aNewAz, m_LightAzimuth) || !Mathf.Approximately(aNewEl, m_LightElevation))
                    {
                        m_LightAzimuth = aNewAz;
                        m_LightElevation = aNewEl;
                        m_LightDir = AnglesToLightDir(m_LightAzimuth, m_LightElevation);
                    }
                }

                m_Ambient = DrawField("ambient (0.0~1.0)", m_Ambient);
                m_Zoom = DrawField("zoom（觀測距離倍率；空=自動縮放收進畫布，1.0=原始 24px/voxel）", m_Zoom);
                using(new GUILayout.HorizontalScope()) 
                {
                    m_Shadow = UCL_GUILayout.CheckBox(m_Shadow);
                    GUILayout.Label(" ☁ 陰影（cast shadow — 解正交圖深度歧義）", UCL_GUIStyle.LabelStyle);
                }

                // 匯出模型檔（Tim 2026-08-13 追加）：只匯出觀測區域（region/exclude 同上方欄位）
                // 輸出資料夾可設（Tim 2026-08-14 追加）—— 空＝引擎預設；**檔名一律由引擎產生**
                using (new GUILayout.HorizontalScope())
                {
                    m_ExportDir = DrawField("匯出資料夾（空＝預設）", m_ExportDir);
                    if (GUILayout.Button("📂 開啟", UCL_GUIStyle.GetButtonStyle(Color.white),
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))))
                    {
                        RevealFolder(string.IsNullOrWhiteSpace(m_ExportDir) ? DefaultExportDir : m_ExportDir);
                    }
                }
                GUILayout.Label($"預設：{DefaultExportDir}", WrapLabelStyle);
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("📦 匯出 .obj", UCL_GUIStyle.GetButtonStyle(Color.yellow),
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(130))))
                    {
                        Export("obj");
                    }
                    if (GUILayout.Button("📦 匯出 .vox", UCL_GUIStyle.GetButtonStyle(Color.yellow),
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(130))))
                    {
                        Export("vox");
                    }
                    GUILayout.Label("匯出範圍＝上方 region／exclude-color（空 region＝全空間）", WrapLabelStyle);
                }
            }
        }

        // ===========================================================
        // 區塊職責：📐 切片輸出 — 把 region 內的 voxel **顏色原樣當像素色**輸出成 2D PNG。
        // 物理意義：這是 stamp 的逆運算，不是 view 的變體 —— 不打光、不等角投影、不混色。
        //          與 stamp 共用同一組軸映射，所以切出來的圖貼回同一個 at 會逐 voxel 還原。
        //          厚度＝region 在法線軸上的跨度（寫 `210..210` 即厚度 1），>1 時前覆蓋後。
        // 數值影響：免費唯讀（只 spawn sculpt.py slice）；引擎回報 non_transparent_pixels，
        //          那個數字可直接當 stampimg 的 --expect-pixels 閘門。
        // ===========================================================
        void DrawSliceSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "SliceFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>📐 切片輸出（3D→2D PNG）</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("📐 產生切片", UCL_GUIStyle.GetButtonStyle(Color.magenta),
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(130))))
                    {
                        RenderSlice();
                    }
                    if (GUILayout.Button("📂 開啟", UCL_GUIStyle.GetButtonStyle(Color.white),
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))))
                    {
                        RevealFolder(Path.GetDirectoryName(SliceOutPath));
                    }
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                GUILayout.Label("voxel 顏色原樣當像素色（不打光／不投影／不混色）；空的地方透明。"
                                + " 厚度＝region 在法線軸上的跨度，>1 時前覆蓋後。", WrapLabelStyle);
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("↩ 帶入上方 region", UCL_GUIStyle.GetButtonStyle(Color.white),
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(160))))
                    {
                        m_SliceRegion = m_Region;   // 手動觀測區／展品 preset 的 region 直接沿用
                    }
                    GUILayout.Label("（展品選單會把 preset region 填進手動觀測區）", WrapLabelStyle);
                }
                m_SliceRegion = DrawField("region (x1..x2,y1..y2,z1..z2)", m_SliceRegion);
                m_SliceAxis = DrawField("axis（法線與近端方向 x+ x- y+ y- z+ z-）", m_SliceAxis);
                m_SliceOut = DrawField("輸出 PNG（空＝預設 _last_slice.png）", m_SliceOut);

                DrawSliceTexture();
            }
        }

        /// <summary>切片輸出路徑 —— 空欄位時退回引擎預設（與 sculpt.py 同值）。</summary>
        string SliceOutPath => string.IsNullOrWhiteSpace(m_SliceOut) ? LastSlicePng : m_SliceOut;

        // 區塊職責：spawn 引擎切片。region 必填 —— 空的話引擎會拒絕，這裡先擋下並說人話。
        void RenderSlice()
        {
            m_SliceOutResolved = "";
            string aScript = ResolveEngineScript();
            if (aScript == null)
            {
                m_LastRenderLog = "✗ 解析不到 sculpt.py（CorePath 空或檔案不存在）";
                return;
            }
            if (string.IsNullOrWhiteSpace(m_SliceRegion))
            {
                m_LastRenderLog = "✗ 切片需要 region（x1..x2,y1..y2,z1..z2）—— 可按「帶入上方 region」";
                return;
            }
            // ⚠ 一律 `--opt=value`：axis 的值含 '-'（如 z-），空格分隔會被 argparse 當旗標吃掉
            string aArgs = $"\"{aScript}\" slice --region=\"{m_SliceRegion}\"";
            if (!string.IsNullOrWhiteSpace(m_SliceAxis)) aArgs += $" --axis=\"{m_SliceAxis}\"";
            if (!string.IsNullOrWhiteSpace(m_SliceOut)) aArgs += $" --out=\"{m_SliceOut}\"";
            var (aExit, aSo, aSe) = UCL_ProcessCli.Run("python", aArgs,
                UCL_RepoPath.RepoRoot, PROC_TAG_PY, nameof(UCL_SculptureViewerPage), RENDER_TIMEOUT_MS);
            m_LastRenderLog = $"[{DateTime.Now:HH:mm:ss} slice]\n" + (aExit == 0
                ? (aSo ?? "").Trim()
                : $"✗ 切片失敗（exit={aExit}）\n{aSo}\n{aSe}");
            if (aExit != 0) return;
            // 落檔路徑讀引擎回報的 output_path —— 不自己推（--out 與預設兩條路，推錯會顯示到別張圖）
            m_SliceOutResolved = ExtractJsonString(aSo, "output_path");
            m_SliceTexTime = default;
        }

        // 區塊職責：顯示切片 PNG —— 與 3D 觀測圖、2D 貼圖預覽各自一張，別互相蓋掉。
        void DrawSliceTexture()
        {
            string aPng = string.IsNullOrEmpty(m_SliceOutResolved) ? SliceOutPath : m_SliceOutResolved;
            if (!File.Exists(aPng)) return;
            var aMtime = File.GetLastWriteTimeUtc(aPng);
            if (m_SliceTex == null || aMtime != m_SliceTexTime)
            {
                try
                {
                    var aBytes = File.ReadAllBytes(aPng);
                    if (m_SliceTex == null) m_SliceTex = new Texture2D(2, 2);
                    m_SliceTex.LoadImage(aBytes);
                    m_SliceTex.filterMode = FilterMode.Point;   // 像素硬邊，別被雙線性糊掉
                    m_SliceTexTime = aMtime;
                }
                catch (Exception e)
                {
                    m_LastRenderLog = $"✗ 切片 PNG 載入失敗: {e.Message}";
                    return;
                }
            }
            GUILayout.Label($"📄 {aPng}（{m_SliceTexTime.ToLocalTime():HH:mm:ss}；透明＝該處無 voxel）",
                UCL_GUIStyle.LabelStyle);
            float aSize = UCL_GUIStyle.GetScaledSize(256);
            var aRect = GUILayoutUtility.GetRect(aSize, aSize, GUILayout.ExpandWidth(false));
            GUI.DrawTexture(aRect, m_SliceTex, ScaleMode.ScaleToFit);
        }

        // 區塊職責：在系統檔案總管開啟資料夾。
        // 物理意義：**一律走 UCL_ExplorerUtil** —— 它把開檔案總管這件事收成唯一入口，
        //          外部 Process 有登記進 ProcessRegistry（硬規則：C# 開的每顆 process 都要登記），
        //          路徑不存在也會留 log 而不是靜默失敗。自己 call EditorUtility.RevealInFinder
        //          會少掉那兩層（summit 2026-08-14 第一版就這麼寫，違規當日自糾）。
        // 數值影響：先建資料夾再開 —— 否則第一次按會定位到 parent 或沒反應，
        //          而那看起來像按鈕壞掉，其實只是那個資料夾還不存在。
        void RevealFolder(string iDir)
        {
            if (string.IsNullOrWhiteSpace(iDir))
            {
                m_LastRenderLog = "✗ 資料夾路徑是空的";
                return;
            }
            try
            {
                Directory.CreateDirectory(iDir);
                if (!UCL_ExplorerUtil.Open(iDir, nameof(UCL_SculptureViewerPage)))
                    m_LastRenderLog = $"✗ 開啟資料夾失敗（詳見 Console）：{iDir}";
            }
            catch (Exception e)
            {
                m_LastRenderLog = $"✗ 開啟資料夾失敗 {iDir}: {e.Message}";
            }
        }

        /// <summary>
        /// 從引擎 stdout 的 JSON 撈某個字串欄位（只取第一個匹配）。
        /// 引擎印的是 pretty JSON，值裡的 Windows 路徑帶跳脫反斜線 —— 取出後要還原，
        /// 否則 File.Exists 對 `D:\\Unity\\...` 一律 false（而那看起來像「檔案沒產生」）。
        /// </summary>
        static string ExtractJsonString(string iStdout, string iKey)
        {
            if (string.IsNullOrEmpty(iStdout)) return "";
            string aNeedle = $"\"{iKey}\"";
            int aIdx = iStdout.IndexOf(aNeedle, StringComparison.Ordinal);
            if (aIdx < 0) return "";
            int aColon = iStdout.IndexOf(':', aIdx + aNeedle.Length);
            if (aColon < 0) return "";
            int aOpen = iStdout.IndexOf('"', aColon);
            if (aOpen < 0) return "";
            var aSb = new System.Text.StringBuilder();
            for (int i = aOpen + 1; i < iStdout.Length; i++)
            {
                char c = iStdout[i];
                if (c == '\\' && i + 1 < iStdout.Length) { aSb.Append(iStdout[++i]); continue; }
                if (c == '"') break;
                aSb.Append(c);
            }
            return aSb.ToString();
        }

        // 區塊職責：把手動觀測欄位組成引擎旗標並渲染。
        // 物理意義：抽成方法是因為觸發點在**折疊 header**（收合時也要能按），
        //          而欄位畫在折疊內 —— 兩者不同層，共用同一份組裝邏輯才不會分岔。
        void RenderManual()
        {
            string aArgs = "";
            if (!string.IsNullOrEmpty(m_Region)) aArgs += $" --region=\"{m_Region}\"";
            if (!string.IsNullOrEmpty(m_ExcludeColor)) aArgs += $" --exclude-color=\"{m_ExcludeColor}\"";
            // ⚠ light-dir 的值以 '-' 開頭（如 -1,-1,-2）—— argparse 會把空格分隔的值當旗標吃掉，
            //   必須用 `--opt=value` 等號形式（Tim 2026-08-13 實測 exit=2 血證）
            if (!string.IsNullOrEmpty(m_LightDir)) aArgs += $" --light-dir=\"{m_LightDir}\"";
            if (!string.IsNullOrEmpty(m_Ambient)) aArgs += $" --ambient={m_Ambient}";
            if (!string.IsNullOrEmpty(m_Zoom)) aArgs += $" --zoom={m_Zoom}";
            if (m_Shadow) aArgs += " --shadow";
            Render(aArgs);
        }

        // 區塊職責：spawn 引擎渲染（同步等待 —— 秒級渲染，對齊 LibraryManagePage spawn library.py 模式）
        void Render(string iViewArgs)
        {
            string aScript = ResolveEngineScript();
            if (aScript == null)
            {
                m_LastRenderLog = "✗ 解析不到 sculpt.py（CorePath 空或檔案不存在）";
                return;
            }
            var (aExit, aSo, aSe) = UCL_ProcessCli.Run("python", $"\"{aScript}\" view{iViewArgs}",
                UCL_RepoPath.RepoRoot, PROC_TAG_PY, nameof(UCL_SculptureViewerPage), RENDER_TIMEOUT_MS);
            // 時間戳＋動作前綴：舊錯誤訊息跟新結果長得一樣會誤導（Tim 2026-08-13 回報「報錯沒清除」）
            m_LastRenderLog = $"[{DateTime.Now:HH:mm:ss} view]\n" + (aExit == 0
                ? (aSo ?? "").Trim()
                : $"✗ 渲染失敗（exit={aExit}）\n{aSo}\n{aSe}");
            m_ViewTexTime = default;   // 強制下次重載 texture
        }

        // 區塊職責：匯出觀測區域為 3D 模型檔（spawn 引擎 export — 面剔除/材質由引擎管，本頁只轉參數）
        void Export(string iFormat)
        {
            string aScript = ResolveEngineScript();
            if (aScript == null)
            {
                m_LastRenderLog = "✗ 解析不到 sculpt.py（CorePath 空或檔案不存在）";
                return;
            }
            string aArgs = $"\"{aScript}\" export --format={iFormat}";
            if (!string.IsNullOrEmpty(m_Region)) aArgs += $" --region=\"{m_Region}\"";
            if (!string.IsNullOrEmpty(m_ExcludeColor)) aArgs += $" --exclude-color=\"{m_ExcludeColor}\"";
            // 只傳資料夾，檔名讓引擎依慣例產生 —— 本頁不組檔名（兩邊各組一份遲早分岔）
            if (!string.IsNullOrWhiteSpace(m_ExportDir)) aArgs += $" --out-dir=\"{m_ExportDir}\"";
            var (aExit, aSo, aSe) = UCL_ProcessCli.Run("python", aArgs,
                UCL_RepoPath.RepoRoot, PROC_TAG_PY, nameof(UCL_SculptureViewerPage), RENDER_TIMEOUT_MS);
            m_LastRenderLog = $"[{DateTime.Now:HH:mm:ss} export {iFormat}]\n" + (aExit == 0
                ? (aSo ?? "").Trim()
                : $"✗ 匯出失敗（exit={aExit}）\n{aSo}\n{aSe}");
        }

        // ===========================================================
        // 區塊：2D→3D 貼圖預覽（Tim 2026-08-14 拍板流程「先出預覽再轉繪」的後台入口）
        // 物理意義：本頁**只做預覽那一半** —— 預覽是唯讀免費（spawn canvas.py view），
        //          真正落 voxel 走 Cmd_Sculpture（收銀台）。頁面直接落子＝繞過計費，不做。
        //          預覽輸出 _last_view_t.png（RGBA，未繪製＝alpha 0）與非透明像素數，
        //          那個數字原樣填進下方指令的 expect_pixels —— 人核准的圖與引擎吃的圖靠它對帳。
        // 數值影響：來源區域填「x,y,w,h」（與 canvas view --region 同語意）；
        //          預授權在 Cmd 端算 w×h×thickness，這裡只顯示不收費。
        // ===========================================================
        void DrawStampPreviewSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "StampPreviewFold", 21, iDefaultValue: false);
                    GUILayout.Label("<b>🖼 2D→3D 貼圖預覽</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    // 兩顆關鍵操作留在 header：收合狀態下「重出預覽 → 複製指令」是完整可用的一條路，
                    // 展開只是為了改參數。複製鈕僅在指令存在時出現 —— 沒有閘門的指令本頁不給。
                    if (GUILayout.Button("🖼 產生預覽", UCL_GUIStyle.GetButtonStyle(Color.cyan),
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(130))))
                    {
                        RenderStampPreview();
                    }
                    if (!string.IsNullOrEmpty(m_StampCmdLine)
                        && GUILayout.Button("📋 複製貼圖指令", UCL_GUIStyle.GetButtonStyle(Color.yellow),
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(160))))
                    {
                        GUIUtility.systemCopyBuffer = m_StampCmdLine;
                    }
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                GUILayout.Label("唯讀免費（只 spawn canvas.py view）；落子仍走 Cmd_Sculpture ——"
                                + " 本頁不碰錢，也不直接貼。", WrapLabelStyle);
                m_StampRegion = DrawField("來源區域 x,y,w,h（2D 畫布座標）", m_StampRegion);
                m_StampAt = DrawField("at（圖左上角貼在 3D 的 x,y,z）", m_StampAt);
                m_StampFacing = DrawField("facing（貼片法線 x+ x- y+ y- z+ z-）", m_StampFacing);
                m_StampThickness = DrawField("thickness（沿法線擠出層數）", m_StampThickness);
                m_StampPersona = DrawField("persona（誰付這筆帳）", m_StampPersona);

                if (!string.IsNullOrEmpty(m_StampCmdLine))
                    GUILayout.Label(m_StampCmdLine, WrapLabelStyle);

                DrawStampTexture();
            }
        }

        // 區塊職責：spawn canvas.py view 產生 RGBA 預覽並解析非透明像素數 → 組出現成 Cmd 指令。
        // 失敗處置：region 格式不合 / 引擎失敗 / 解析不到數字 → 訊息寫進 log 且**不組指令**
        //          （組不出可信 expect_pixels 就不給指令 —— 給一個沒有閘門的指令比不給更糟）。
        void RenderStampPreview()
        {
            m_StampCmdLine = "";
            string aScript = ResolveCanvasScript();
            if (aScript == null)
            {
                m_LastRenderLog = "✗ 解析不到 canvas.py（CorePath 空或檔案不存在）";
                return;
            }
            var aRegion = ParseXywh(m_StampRegion);
            if (!aRegion.HasValue)
            {
                m_LastRenderLog = $"✗ 來源區域需為 x,y,w,h 四個正整數（got '{m_StampRegion}'）";
                return;
            }
            var (aExit, aSo, aSe) = UCL_ProcessCli.Run("python",
                $"\"{aScript}\" view --region=\"{m_StampRegion}\"",
                UCL_RepoPath.RepoRoot, PROC_TAG_PY, nameof(UCL_SculptureViewerPage), RENDER_TIMEOUT_MS);
            m_LastRenderLog = $"[{DateTime.Now:HH:mm:ss} stamp preview]\n" + (aExit == 0
                ? (aSo ?? "").Trim()
                : $"✗ 預覽失敗（exit={aExit}）\n{aSo}\n{aSe}");
            if (aExit != 0) return;

            int aOpaque = ParseOpaqueCount(aSo);
            if (aOpaque < 0)
            {
                m_LastRenderLog += "\n✗ 解析不到 non_transparent_pixels —— 不組指令（沒有閘門的指令不給）";
                return;
            }
            var (x, y, w, h) = aRegion.Value;
            m_StampCmdLine =
                $"python {CoreToolsRel}/run_cmd.py run Sculpture --arg op=stamp2d --arg persona={m_StampPersona} " +
                $"--arg src_x1={x} --arg src_y1={y} --arg src_x2={x + w - 1} --arg src_y2={y + h - 1} " +
                $"--arg at={m_StampAt} --arg facing={m_StampFacing} --arg thickness={m_StampThickness} " +
                $"--arg expect_pixels={aOpaque}";
            m_StampTexTime = default;   // 強制重載預覽 texture
        }

        // 區塊職責：顯示 _last_view_t.png（2D 預覽）— 與 3D 觀測圖分開兩張，別互相蓋掉。
        void DrawStampTexture()
        {
            string aPng = CanvasLastViewTPng;
            if (!File.Exists(aPng)) return;
            var aMtime = File.GetLastWriteTimeUtc(aPng);
            if (m_StampTex == null || aMtime != m_StampTexTime)
            {
                try
                {
                    var aBytes = File.ReadAllBytes(aPng);
                    if (m_StampTex == null) m_StampTex = new Texture2D(2, 2);
                    m_StampTex.LoadImage(aBytes);
                    m_StampTex.filterMode = FilterMode.Point;   // 像素硬邊，別被雙線性糊掉
                    m_StampTexTime = aMtime;
                }
                catch (Exception e)
                {
                    m_LastRenderLog = $"✗ 預覽 PNG 載入失敗: {e.Message}";
                    return;
                }
            }
            GUILayout.Label($"📄 {aPng}（{m_StampTexTime.ToLocalTime():HH:mm:ss}；透明＝未繪製，不會變 voxel）",
                UCL_GUIStyle.LabelStyle);
            float aSize = UCL_GUIStyle.GetScaledSize(256);
            var aRect = GUILayoutUtility.GetRect(aSize, aSize, GUILayout.ExpandWidth(false));
            GUI.DrawTexture(aRect, m_StampTex, ScaleMode.ScaleToFit);
        }

        /// <summary>解析 "x,y,w,h" 四個正整數；不合格回 null（不猜預設值）。</summary>
        static (int x, int y, int w, int h)? ParseXywh(string iVal)
        {
            if (string.IsNullOrWhiteSpace(iVal)) return null;
            var aParts = iVal.Split(',');
            if (aParts.Length != 4) return null;
            var aNums = new int[4];
            for (int i = 0; i < 4; i++)
                if (!int.TryParse(aParts[i].Trim(), out aNums[i])) return null;
            if (aNums[2] <= 0 || aNums[3] <= 0) return null;
            return (aNums[0], aNums[1], aNums[2], aNums[3]);
        }

        /// <summary>從 canvas.py view 的 stdout 撈 non_transparent_pixels 的值；找不到回 -1（不回 0 —— 0 是合法的「全透明」，會被誤當成功）。</summary>
        static int ParseOpaqueCount(string iStdout)
        {
            if (string.IsNullOrEmpty(iStdout)) return -1;
            const string aKey = "non_transparent_pixels:";
            int aIdx = iStdout.IndexOf(aKey, StringComparison.Ordinal);
            if (aIdx < 0) return -1;
            string aRest = iStdout.Substring(aIdx + aKey.Length).TrimStart();
            int aEnd = 0;
            while (aEnd < aRest.Length && char.IsDigit(aRest[aEnd])) aEnd++;
            return aEnd > 0 && int.TryParse(aRest.Substring(0, aEnd), out int aVal) ? aVal : -1;
        }

        static string ResolveCanvasScript()
        {
            string aCoreRel = UCL_EditorPath.CorePath;
            if (string.IsNullOrEmpty(aCoreRel)) return null;
            string aScript = Path.GetFullPath(Path.Combine(
                UCL_RepoPath.UnityProjectRoot, aCoreRel, "Tools~/AgentCommands/canvas.py"));
            return File.Exists(aScript) ? aScript : null;
        }

        // 區塊職責：顯示 _last_view.png — mtime 快取（texture 只在檔案變動時重建）
        void DrawViewTexture()
        {
            if (!File.Exists(LastViewPng)) return;
            var aMtime = File.GetLastWriteTimeUtc(LastViewPng);
            if (m_ViewTex == null || aMtime != m_ViewTexTime)
            {
                try
                {
                    var aBytes = File.ReadAllBytes(LastViewPng);
                    if (m_ViewTex == null) m_ViewTex = new Texture2D(2, 2);
                    m_ViewTex.LoadImage(aBytes);
                    m_ViewTexTime = aMtime;
                }
                catch (Exception e)
                {
                    m_LastRenderLog = $"✗ PNG 載入失敗: {e.Message}";
                    return;
                }
            }
            GUILayout.Label($"📄 {LastViewPng}（{m_ViewTexTime.ToLocalTime():HH:mm:ss}）", UCL_GUIStyle.LabelStyle);
            float aSize = UCL_GUIStyle.GetScaledSize(512);
            var aRect = GUILayoutUtility.GetRect(aSize, aSize, GUILayout.ExpandWidth(false));
            GUI.DrawTexture(aRect, m_ViewTex, ScaleMode.ScaleToFit);
        }

        // 區塊職責：exhibits.json 直讀（引擎寫、本頁唯讀 —— 單寫者不破壞）
        void ReloadExhibits()
        {
            m_Exhibits.Clear();
            try
            {
                // 新版佈局優先：exhibits/<id>.json 一展一檔（per-entry，解單檔多寫者）；
                // 舊單檔 exhibits.json 作 fallback（migration 期間兩邊都讀，同 id 新版蓋舊版）
                var aById = new Dictionary<string, ExhibitEntry>();
                if (File.Exists(ExhibitsJsonPath))
                {
                    var aJd = JsonData.ParseJson(File.ReadAllText(ExhibitsJsonPath, System.Text.Encoding.UTF8));
                    if (aJd != null && aJd.IsObject)
                        foreach (string aKey in aJd.Keys) AddExhibit(aById, aJd[aKey], aKey);
                }
                string aDir = Path.Combine(SculptureDir, "exhibits");
                if (Directory.Exists(aDir))
                {
                    foreach (var aFile in Directory.GetFiles(aDir, "*.json"))
                    {
                        var aJd = JsonData.ParseJson(File.ReadAllText(aFile, System.Text.Encoding.UTF8));
                        if (aJd != null) AddExhibit(aById, aJd, Path.GetFileNameWithoutExtension(aFile));
                    }
                }
                m_Exhibits.AddRange(aById.Values);
                m_Exhibits.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                m_ExhibitOptions.Clear();
                foreach (var aEx in m_Exhibits) m_ExhibitOptions.Add($"{aEx.Title} ({aEx.Id})");
                if (m_SelectedExhibit >= m_Exhibits.Count) m_SelectedExhibit = 0;
            }
            catch (Exception e)
            {
                m_LastRenderLog = $"✗ 展品清單讀取失敗: {e.Message}";
            }
        }

        static void AddExhibit(Dictionary<string, ExhibitEntry> ioById, JsonData iE, string iFallbackId)
        {
            string aId = ReadStr(iE, "id", iFallbackId);
            ioById[aId] = new ExhibitEntry
            {
                Id = aId,
                Title = ReadStr(iE, "title", aId),
                Author = ReadStr(iE, "author", "?"),
                Description = ReadStr(iE, "description", ""),
                Region = ReadStr(iE, "region", ""),
                ExcludeColor = ReadStr(iE, "exclude_color", ""),
                CreatedAt = ReadStr(iE, "created_at", ""),
            };
        }

        static string ResolveEngineScript()
        {
            string aCoreRel = UCL_EditorPath.CorePath;
            if (string.IsNullOrEmpty(aCoreRel)) return null;
            string aScript = Path.GetFullPath(Path.Combine(
                UCL_RepoPath.UnityProjectRoot, aCoreRel, "Tools~/AgentCommands/sculpt.py"));
            return File.Exists(aScript) ? aScript : null;
        }

        /// <summary>方位角/仰角（度）→ 平行光方向向量字串（指向場景、z 向下為負）。</summary>
        static string AnglesToLightDir(float iAzimuthDeg, float iElevationDeg)
        {
            float aAz = iAzimuthDeg * Mathf.Deg2Rad;
            float aEl = iElevationDeg * Mathf.Deg2Rad;
            float aX = Mathf.Cos(aEl) * Mathf.Cos(aAz);
            float aY = Mathf.Cos(aEl) * Mathf.Sin(aAz);
            float aZ = -Mathf.Sin(aEl);
            return $"{aX:F2},{aY:F2},{aZ:F2}";
        }

        static string ReadStr(JsonData iJd, string iKey, string iDefault = "")
            => iJd != null && iJd.Contains(iKey) ? iJd[iKey].ToString() : iDefault;

        string DrawField(string iLabel, string iValue)
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label(iLabel, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(260)));
                return GUILayout.TextField(iValue ?? "", UCL_GUIStyle.TextFieldStyle);
            }
        }

        GUIStyle m_WrapLabelStyle;
        GUIStyle WrapLabelStyle => m_WrapLabelStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            wordWrap = true,
        };
    }
}
#endif
