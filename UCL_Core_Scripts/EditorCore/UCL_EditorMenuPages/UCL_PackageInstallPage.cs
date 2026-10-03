// 區塊職責：套件安裝頁 —— 把「這個專案的工作流要用到、但不在 Unity 專案裡」的外部工具收成一頁：
//          看得到裝了沒、是哪一版、一鍵開官方安裝指令。
// 物理意義：目前兩項（Tim 2026-10-03 指派）：Unity 官方 CLI（`unity`，系統層）與 Unity Pipeline 套件
//          （`com.unity.pipeline`，專案層，CLI 連進 Editor 的前提）。每一項一個折疊區塊，
//          偵測與安裝邏輯住在各自的 installer 類別（本頁只畫 GUI、不碰 process 細節）。
// 數值影響：開頁時偵測一次（唯讀）；安裝／更新只在使用者按下按鈕並在確認彈窗按「執行」之後才發生，
//          而且是在獨立的 PowerShell 視窗裡跑（本頁不等它、不讀它的輸出）。
// 設計取捨：
//   · 入口掛在工具集（UCL_ToolBoxPage）的「環境安裝」組 ⇒ ShowInPageMenu=false，不重複出現在下拉。
//   · 之後要加別的工具（例如 Unity Pipeline package、ollama）就在這頁加一個 Draw<X>Panel，
//     ⛔ 不要把安裝功能散回各功能頁 —— 本地 LLM 頁的 ollama 安裝就是散在外面的那一個先例。
#if UNITY_EDITOR
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.PackageInstall;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>套件安裝 —— 外部工具（Unity CLI…）的偵測與安裝入口。</summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_PackageInstallPage.md")]
    public class UCL_PackageInstallPage : UCL_CommonEditorPage
    {
        public override string WindowName => UCL_CodeLocalize.Get("ToolBox.PackageInstall");

        // 由工具集進入，不重複出現在下拉頁選單
        public override bool ShowInPageMenu => false;

        public static UCL_PackageInstallPage Create() => UCL_EditorPage.Create<UCL_PackageInstallPage>();

        // 折疊狀態專用容器（不與任何 PopupSearchCache 共用 —— 見 Create_EditorPage_Workflow §5.1）
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();

        // ── Unity CLI 的畫面狀態 ──
        bool m_CliProbed;                  // 開頁後是否已發過第一次偵測（首幀 lazy，不在建構子碰 IO）
        bool m_CliProbing;                 // 偵測進行中 —— 期間按鈕停用，避免同時跑兩顆 `--version`
        UnityCliProbeResult m_CliResult;   // 最近一次偵測結果
        string m_CliActionLine = "";       // 最近一次按鈕動作的回報（開了哪個視窗／為什麼沒開）

        // ── Unity Pipeline 套件的畫面狀態 ──
        bool m_PipelineVersionRead;        // 首幀讀一次（Package Manager API；⛔ 不每幀讀）
        string m_PipelineVersion;          // 已解析到的版本；null＝沒裝
        bool m_PipelineBusy;               // 安裝／升級／連線測試進行中 —— 期間三顆鈕停用
        string m_PipelineBusyLabel = "";   // 進行中的是哪一件（畫面顯示用）
        string m_PipelineResultTitle = ""; // 最近一次動作的一行結論
        string m_PipelineResultBody = "";  // 最近一次動作的 CLI 原文輸出（不解讀）

        protected override void ContentOnGUI()
        {
            if (!m_CliProbed) StartCliProbe();
            if (!m_PipelineVersionRead) ReadPipelineVersion();
            DrawUnityCliPanel();
            GUILayout.Space(8);
            DrawPipelinePanel();
        }

        void ReadPipelineVersion()
        {
            m_PipelineVersionRead = true;
            m_PipelineVersion = UCL_UnityPipelineInstaller.InstalledVersion();
        }

        // 區塊職責：發一次背景偵測，完成後寫回畫面狀態
        // 物理意義：偵測會跑外部 process，⛔ 不能在 OnGUI 裡同步等（IMGUI 每秒重畫數十次，卡住＝Editor 凍結）。
        void StartCliProbe()
        {
            m_CliProbed = true;
            if (m_CliProbing) return;
            m_CliProbing = true;
            ProbeCli().Forget();
        }

        async UniTaskVoid ProbeCli()
        {
            try { m_CliResult = await UCL_UnityCliInstaller.ProbeAsync(); }
            finally { m_CliProbing = false; }
        }

        // ===========================================================
        // 區塊：Unity CLI
        // ===========================================================

        void DrawUnityCliPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "UnityCliFold", 21, iDefaultValue: true);
                    GUILayout.Label("<b>🧰 Unity CLI</b>　" + CliSummary(), RichLabelStyle, GUILayout.ExpandWidth(false));
                    using (new UnityEditor.EditorGUI.DisabledScope(m_CliProbing))
                    {
                        if (GUILayout.Button("🔄 重新偵測", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            m_CliProbed = false;   // 下一幀重發
                        }
                    }
                    if (GUILayout.Button("📖 官方文件", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        Application.OpenURL(UCL_UnityCliInstaller.DocUrl);
                    }
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                GUILayout.Label("Unity 官方命令列工具（`unity`）：在終端機控制正在跑的 Editor（`unity recompile`／`unity status`…）、" +
                                "安裝 Editor 與模組。⚠ 官方標為實驗性（beta）。要控制 Editor 另需專案安裝 `com.unity.pipeline` 套件（本頁還沒做）。",
                    WrapLabelStyle);
                GUILayout.Space(4);

                if (!UCL_UnityCliInstaller.IsSupportedPlatform)
                {
                    GUILayout.Label("⚠ 本頁的安裝與偵測目前只寫了 Windows。", WrapLabelStyle);
                    return;
                }

                DrawCliStatus();
                GUILayout.Space(4);
                DrawCliActions();
                if (!string.IsNullOrEmpty(m_CliActionLine))
                {
                    GUILayout.Label(m_CliActionLine, WrapLabelStyle);
                }
            }
        }

        // 標題列上的一句話現況（收合時也看得到裝了沒）
        string CliSummary()
        {
            if (m_CliProbing) return "<color=grey>偵測中…</color>";
            if (!m_CliResult.Found) return "<color=orange>未安裝</color>";
            return string.IsNullOrEmpty(m_CliResult.Version)
                ? "<color=orange>找到檔案但讀不到版本</color>"
                : $"<color=lime>{m_CliResult.Version}</color>";
        }

        // 區塊職責：偵測明細 —— 路徑、版本、為什麼讀不到、跳過了哪些 Editor 本體
        // 物理意義：「檔案在」與「跑得起來」分兩行講；被排除的 Editor 本體照實列出，
        //          否則使用者會看到「未安裝」卻不知道其實有一支同名的檔被刻意跳過。
        void DrawCliStatus()
        {
            if (m_CliProbing)
            {
                GUILayout.Label("偵測中…（找執行檔＋跑一次 `unity --version`）", WrapLabelStyle);
                return;
            }
            if (m_CliResult.Found)
            {
                GUILayout.Label($"路徑：{m_CliResult.ExePath}", WrapLabelStyle);
                GUILayout.Label(string.IsNullOrEmpty(m_CliResult.Version)
                    ? $"⚠ 版本讀不到：{m_CliResult.Error}"
                    : $"版本：{m_CliResult.Version}", WrapLabelStyle);
            }
            else
            {
                GUILayout.Label("沒有在 PATH（使用者／系統的最新值）與 WindowsApps 找到 `unity.exe`。", WrapLabelStyle);
            }
            if (m_CliResult.SkippedEditors != null)
            {
                foreach (string aPath in m_CliResult.SkippedEditors)
                {
                    GUILayout.Label($"（已跳過 Unity Editor 本體：{aPath}）", WrapLabelStyle);
                }
            }
        }

        // 區塊職責：安裝／更新按鈕 —— 每顆都先跳確認彈窗，把**實際會跑的指令**原文給人看
        void DrawCliActions()
        {
            using (new UnityEditor.EditorGUI.DisabledScope(m_CliProbing))
            using (new GUILayout.HorizontalScope())
            {
                using (new UnityEditor.EditorGUI.DisabledScope(!m_CliResult.WingetFound))
                {
                    if (GUILayout.Button("⬇ 安裝（winget，建議）", UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 0.85f, 0.5f)),
                            GUILayout.ExpandWidth(false)))
                    {
                        ConfirmLaunch("用 winget 安裝 Unity CLI？", UCL_UnityCliInstaller.WingetInstallCommand,
                            "· winget 會從 Microsoft 的套件來源下載 Unity 官方的 MSIX 套件\n" +
                            "· 第一次用 winget 會要求同意來源條款 —— 請在那個視窗裡自己決定（本頁不代按同意）",
                            UCL_UnityCliInstaller.LaunchWingetInstall);
                    }
                }
                if (GUILayout.Button("⬇ 安裝（官方腳本）", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    ConfirmLaunch("執行 Unity 官方安裝腳本？", UCL_UnityCliInstaller.ScriptInstallCommand,
                        "· 這是**下載並執行遠端腳本**：內容由 unity.com 當下提供\n" +
                        "· 官方文件標為推薦方式；裝完之後用 `unity self-update` 更新",
                        UCL_UnityCliInstaller.LaunchScriptInstall);
                }
                using (new UnityEditor.EditorGUI.DisabledScope(!m_CliResult.Found))
                {
                    if (GUILayout.Button("⤴ 更新（self-update）", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        string aExe = m_CliResult.ExePath;
                        ConfirmLaunch("更新 Unity CLI？", $"\"{aExe}\" self-update",
                            "· 會下載最新版並取代目前的執行檔",
                            () => UCL_UnityCliInstaller.LaunchSelfUpdate(aExe));
                    }
                }
                GUILayout.FlexibleSpace();
            }
            if (!m_CliResult.WingetFound && !m_CliProbing)
            {
                GUILayout.Label("（這台機器找不到 winget ⇒ winget 安裝鈕停用，可改用官方腳本）", WrapLabelStyle);
            }
        }

        // 區塊職責：確認彈窗 —— 指令原文＋要知道的事，按「執行」才開視窗
        // 物理意義：彈窗用既有的 UCL_OptionPage（本地 LLM 頁的 ollama 安裝也是這個形狀）。
        void ConfirmLaunch(string iTitle, string iCommand, string iNotes, System.Func<string> iLaunch)
        {
            UCL_OptionPage.Create(iTitle,
                iCommand + "\n\n" + iNotes + "\n" +
                "· 會開一個 PowerShell 視窗（可能跳 UAC）—— 進度看那個視窗，不是本頁\n" +
                "· 跑完回來按「🔄 重新偵測」；本頁讀的是 PATH 的最新值，不必重開 Unity\n" +
                "  （但已經開著的終端機要重開才看得到新的 PATH）",
                new ButtonData("執行", () => { m_CliActionLine = iLaunch(); },
                    UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 0.85f, 0.5f))),
                new ButtonData("取消"));
        }

        // ===========================================================
        // 區塊：Unity Pipeline 套件（com.unity.pipeline）
        // 物理意義：CLI 連進 Editor 的前提。安裝／升級**改的是本專案的 Packages/manifest.json**（進版控），
        //          所以確認彈窗會把這件事講在最前面。
        // ===========================================================

        void DrawPipelinePanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "PipelineFold", 21, iDefaultValue: true);
                    string aSummary = m_PipelineVersion == null
                        ? "<color=orange>未安裝</color>"
                        : $"<color=lime>{m_PipelineVersion}</color>";
                    GUILayout.Label("<b>🔌 Unity Pipeline 套件</b>　" + aSummary, RichLabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("🔄 重新讀取", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_PipelineVersionRead = false;   // 下一幀重讀
                    }
                    if (GUILayout.Button("📖 官方文件", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        Application.OpenURL(UCL_UnityPipelineInstaller.DocUrl);
                    }
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                GUILayout.Label($"`{UCL_UnityPipelineInstaller.PackageName}`：在 Editor 內開 7800 埠，讓 Unity CLI 連進正在跑的 Editor" +
                                "（`unity status`、`unity command recompile`…）。⚠ 官方標為實驗版；安裝／升級會改本專案的 " +
                                "Packages/manifest.json 與 packages-lock.json（進版控）。",
                    WrapLabelStyle);
                GUILayout.Space(4);

                bool aCliReady = m_CliResult.Found && !m_CliProbing;
                using (new UnityEditor.EditorGUI.DisabledScope(m_PipelineBusy || !aCliReady))
                using (new GUILayout.HorizontalScope())
                {
                    if (m_PipelineVersion == null)
                    {
                        if (GUILayout.Button("⬇ 安裝（unity pipeline install）", UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 0.85f, 0.5f)),
                                GUILayout.ExpandWidth(false)))
                        {
                            ConfirmPipeline("安裝 Unity Pipeline 套件？", "pipeline install", "安裝",
                                () => UCL_UnityPipelineInstaller.InstallAsync(m_CliResult.ExePath));
                        }
                    }
                    else
                    {
                        if (GUILayout.Button("⤴ 升級（unity pipeline upgrade）", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            ConfirmPipeline("升級 Unity Pipeline 套件？", "pipeline upgrade", "升級",
                                () => UCL_UnityPipelineInstaller.UpgradeAsync(m_CliResult.ExePath));
                        }
                    }
                    using (new UnityEditor.EditorGUI.DisabledScope(m_PipelineVersion == null))
                    {
                        // 連線測試不改任何檔 ⇒ 不跳確認
                        if (GUILayout.Button("🔌 測試連線（editor_status）", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            RunPipeline("測試連線", () => UCL_UnityPipelineInstaller.PingAsync(m_CliResult.ExePath)).Forget();
                        }
                    }
                    GUILayout.FlexibleSpace();
                }
                if (!aCliReady && !m_CliProbing)
                {
                    GUILayout.Label("（要先裝好上方的 Unity CLI —— 本區的動作都是透過 CLI 執行）", WrapLabelStyle);
                }
                if (m_PipelineBusy)
                {
                    GUILayout.Label($"⏳ {m_PipelineBusyLabel}中…（背景執行，不會卡住 Editor）", WrapLabelStyle);
                }
                if (!string.IsNullOrEmpty(m_PipelineResultTitle))
                {
                    GUILayout.Label(m_PipelineResultTitle, WrapLabelStyle);
                    GUILayout.Label(m_PipelineResultBody, WrapLabelStyle);
                }
            }
        }

        // 確認彈窗 —— 會改 manifest 的動作才走這裡
        void ConfirmPipeline(string iTitle, string iSubCommand, string iLabel,
            System.Func<UniTask<UnityPipelineRunResult>> iRun)
        {
            UCL_OptionPage.Create(iTitle,
                $"\"{m_CliResult.ExePath}\" {iSubCommand} --project-path \"{UCL_RepoPath.UnityProjectRoot}\" --non-interactive\n\n" +
                "· 會修改本專案的 Packages/manifest.json 與 packages-lock.json（進版控 —— 記得一起提交）\n" +
                "· 版本由 CLI 決定（官方標為實驗版）；完成後本頁會請 Package Manager 重新解析\n" +
                "· 在背景執行，結果顯示在本頁",
                new ButtonData("執行", () => RunPipeline(iLabel, iRun).Forget(),
                    UCL_GUIStyle.GetButtonStyle(new Color(0.5f, 0.85f, 0.5f))),
                new ButtonData("取消"));
        }

        // 區塊職責：跑一個 Pipeline 動作，寫回結論與原文輸出，最後重讀已安裝版本
        async UniTaskVoid RunPipeline(string iLabel, System.Func<UniTask<UnityPipelineRunResult>> iRun)
        {
            if (m_PipelineBusy) return;
            m_PipelineBusy = true;
            m_PipelineBusyLabel = iLabel;
            try
            {
                var aRes = await iRun();
                m_PipelineResultTitle = aRes.Ok
                    ? $"✅ {iLabel}完成（{aRes.Seconds:0.0} 秒）"
                    : $"❌ {iLabel}失敗：exit {aRes.ExitCode}（{aRes.Seconds:0.0} 秒）";
                m_PipelineResultBody = aRes.Output ?? "";
            }
            finally
            {
                m_PipelineBusy = false;
                m_PipelineVersionRead = false;   // 裝完／升完版本可能變了 —— 下一幀重讀
            }
        }

        GUIStyle m_WrapLabelStyle;
        GUIStyle WrapLabelStyle => m_WrapLabelStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            wordWrap = true,
        };

        // 標題列用 —— richText 讓 <b> / <color> 生效
        GUIStyle m_RichLabelStyle;
        GUIStyle RichLabelStyle => m_RichLabelStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            richText = true,
        };
    }
}
#endif
