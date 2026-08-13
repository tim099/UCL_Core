// 區塊職責：3D 雕刻觀測頁 — 後台一鍵渲染共用雕刻空間，看大家的作品（Tim 2026-08-13 派 task）。
// 物理意義：資料層在 sculpt.py（gura 的引擎：events 真相源＋增量快取＋等角渲染），本頁不重造渲染 ——
//          只做「展品清單直讀 + spawn 引擎出圖 + 顯示 PNG」三件事，結構對齊 UCL_LibraryManagePage
//          （讀 per-project JSON 顯示、操作 spawn UCL_Core 工具）。觀測免費（驗收管道零門檻），
//          本頁純唯讀不碰錢 —— 落子仍走 Cmd_Sculpture。
// 數值影響：讀 AgentCommands/Sculpture/exhibits.json（展品 preset）與 _last_view.png；
//          渲染 spawn python sculpt.py view（ProcessRegistry 登記，硬規則不裸 Process.Start）。
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
        string m_LastRenderLog = "";
        Texture2D m_ViewTex;
        DateTime m_ViewTexTime;
        bool m_Loaded;

        static string SculptureDir => Path.Combine(UCL_AgentCommandsPath.DataRoot, "Sculpture");
        static string ExhibitsJsonPath => Path.Combine(SculptureDir, "exhibits.json");
        static string LastViewPng => Path.Combine(SculptureDir, "_last_view.png");

        protected override void ContentOnGUI()
        {
            if (!m_Loaded) { ReloadExhibits(); m_Loaded = true; }

            // ── 展品導覽區：exhibits.json 直讀，一鍵套 preset 渲染 ──
            GUILayout.Label(UCL_CodeLocalize.Get("SculptureViewer.Exhibits"), UCL_GUIStyle.LabelStyle);
            using (new GUILayout.HorizontalScope())
            {
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
            }
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

            GUILayout.Space(8);

            // ── 手動觀測區：region / exclude-color / 打光 —— 直接對映引擎 view 旗標 ──
            GUILayout.Label(UCL_CodeLocalize.Get("SculptureViewer.Manual"), UCL_GUIStyle.LabelStyle);
            using (new GUILayout.VerticalScope("box"))
            {
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
                m_Shadow = GUILayout.Toggle(m_Shadow, " ☁ 陰影（cast shadow — 解正交圖深度歧義）", UCL_GUIStyle.LabelStyle);
                if (GUILayout.Button(UCL_CodeLocalize.Get("SculptureViewer.RenderManual"),
                        UCL_GUIStyle.GetButtonStyle(Color.green), GUILayout.Width(UCL_GUIStyle.GetScaledSize(200))))
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

                // 匯出模型檔（Tim 2026-08-13 追加）：只匯出觀測區域（region/exclude 同上方欄位）
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
            var (aExit, aSo, aSe) = UCL_ProcessCli.Run("python", aArgs,
                UCL_RepoPath.RepoRoot, PROC_TAG_PY, nameof(UCL_SculptureViewerPage), RENDER_TIMEOUT_MS);
            m_LastRenderLog = $"[{DateTime.Now:HH:mm:ss} export {iFormat}]\n" + (aExit == 0
                ? (aSo ?? "").Trim()
                : $"✗ 匯出失敗（exit={aExit}）\n{aSo}\n{aSe}");
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
