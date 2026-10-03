// 區塊職責：套件安裝頁 —— 把「這個專案的工作流要用到、但不在 Unity 專案裡」的外部工具收成一頁：
//          看得到裝了沒、是哪一版、一鍵開官方安裝指令。
// 物理意義：第一項是 Unity 官方 CLI（`unity`，Tim 2026-10-03 指派）。每一項一個折疊區塊，
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

        protected override void ContentOnGUI()
        {
            if (!m_CliProbed) StartCliProbe();
            DrawUnityCliPanel();
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
