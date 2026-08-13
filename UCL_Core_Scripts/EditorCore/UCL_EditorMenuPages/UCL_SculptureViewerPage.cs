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
            foreach (var aEx in m_Exhibits)
            {
                using (new GUILayout.HorizontalScope("box"))
                {
                    if (GUILayout.Button($"🏛 {aEx.Title}", UCL_GUIStyle.GetButtonStyle(Color.cyan),
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(240))))
                    {
                        Render($" --exhibit \"{aEx.Id}\"");   // preset 由引擎讀 exhibits.json，本頁不重組參數
                    }
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
                m_Ambient = DrawField("ambient (0.0~1.0)", m_Ambient);
                if (GUILayout.Button(UCL_CodeLocalize.Get("SculptureViewer.RenderManual"),
                        UCL_GUIStyle.GetButtonStyle(Color.green), GUILayout.Width(UCL_GUIStyle.GetScaledSize(200))))
                {
                    string aArgs = "";
                    if (!string.IsNullOrEmpty(m_Region)) aArgs += $" --region \"{m_Region}\"";
                    if (!string.IsNullOrEmpty(m_ExcludeColor)) aArgs += $" --exclude-color \"{m_ExcludeColor}\"";
                    if (!string.IsNullOrEmpty(m_LightDir)) aArgs += $" --light-dir \"{m_LightDir}\"";
                    if (!string.IsNullOrEmpty(m_Ambient)) aArgs += $" --ambient {m_Ambient}";
                    Render(aArgs);
                }
            }

            if (!string.IsNullOrEmpty(m_LastRenderLog))
            {
                GUILayout.Label(m_LastRenderLog, WrapLabelStyle);
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
            m_LastRenderLog = aExit == 0
                ? (aSo ?? "").Trim()
                : $"✗ 渲染失敗（exit={aExit}）\n{aSo}\n{aSe}";
            m_ViewTexTime = default;   // 強制下次重載 texture
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
                if (!File.Exists(ExhibitsJsonPath)) return;
                var aJd = JsonData.ParseJson(File.ReadAllText(ExhibitsJsonPath, System.Text.Encoding.UTF8));
                if (aJd == null || !aJd.IsObject) return;
                foreach (string aKey in aJd.Keys)
                {
                    var aE = aJd[aKey];
                    m_Exhibits.Add(new ExhibitEntry
                    {
                        Id = ReadStr(aE, "id", aKey),
                        Title = ReadStr(aE, "title", aKey),
                        Author = ReadStr(aE, "author", "?"),
                        Description = ReadStr(aE, "description", ""),
                        Region = ReadStr(aE, "region", ""),
                        ExcludeColor = ReadStr(aE, "exclude_color", ""),
                        CreatedAt = ReadStr(aE, "created_at", ""),
                    });
                }
                m_Exhibits.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            }
            catch (Exception e)
            {
                m_LastRenderLog = $"✗ exhibits.json 讀取失敗: {e.Message}";
            }
        }

        static string ResolveEngineScript()
        {
            string aCoreRel = UCL_EditorPath.CorePath;
            if (string.IsNullOrEmpty(aCoreRel)) return null;
            string aScript = Path.GetFullPath(Path.Combine(
                UCL_RepoPath.UnityProjectRoot, aCoreRel, "Tools~/AgentCommands/sculpt.py"));
            return File.Exists(aScript) ? aScript : null;
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
