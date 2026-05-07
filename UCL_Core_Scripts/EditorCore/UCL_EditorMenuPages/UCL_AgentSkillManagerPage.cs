// 區塊職責：Agent Skill 管理頁 — 第一次開 UCL_WelcomePage 時自動 push 到頂、
//            提供 onboarding 強制曝光，後續可從 Welcome 卡片或選單再開。
// 物理意義：UCL_Core 的 Skills~/ 是跨專案 Skill 的 source-of-truth；不同 agent
//            (Claude Code / Cursor / Antigravity / Gemini …) 的安裝路徑由
//            install_skills.py 的 --target 分支處理。本頁是 IMGUI 視覺化前端，
//            幫不會打 CLI 的開發者把「裝 Skill 給 AI 用」這件事一鍵化。
// 數值影響：點按鈕 spawn `python install_skills.py --target X`；安裝結果寫
//            <project-root>/.claude/skills/.ucl_installed (或對應 agent 的標記檔)；
//            「我知道了」勾選會寫 EditorPrefs，下次不再自動彈本頁。
#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// Agent Skill 安裝管理頁。
    ///
    /// 三種出現方式：
    /// <list type="number">
    ///   <item>第一次開 <see cref="UCL_WelcomePage"/> 時自動 push 到頂（強制曝光）</item>
    ///   <item>從 Welcome 頁的「Skill 管理」按鈕開啟</item>
    ///   <item>選單 <c>UCL → Agent Skill Manager</c></item>
    /// </list>
    ///
    /// 目前只支援 Claude Code（--target claude）。其他 agent (Cursor / Antigravity /
    /// Gemini) 的 target 由 install_skills.py 後續擴充；本頁預留 Per-Agent × Per-Skill
    /// 切換 matrix 的 UI 區塊（目前只有 placeholder）。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_AgentSkillManagerPage.md")]
    public class UCL_AgentSkillManagerPage : UCL_CommonEditorPage
    {
        public override string WindowName => "Agent Skill Manager";

        /// <summary>當前頁內容版本。EditorPrefs 紀錄的版本不同 → 視為「沒看過」會重新自動彈出。</summary>
        public const string CurrentAcknowledgeVersion = "1";

        // EditorPrefs 跨專案共享 → 用 ProjectFingerprint 加綴，避免 A 專案勾過 B 專案不彈
        static string s_PrefKey_Acknowledged;
        static string s_ProjectFingerprint;

        static string ProjectFingerprint =>
            s_ProjectFingerprint ??= Application.dataPath.GetHashCode().ToString("X");

        /// <summary>EditorPrefs key — 使用者已確認看過本頁的版本。空 = 第一次。</summary>
        public static string PrefKey_Acknowledged =>
            s_PrefKey_Acknowledged ??= $"UCL_Core.AgentSkill.AcknowledgedVersion@{ProjectFingerprint}";

        /// <summary>從外部建立並 push 一份本頁。</summary>
        public static UCL_AgentSkillManagerPage Create()
        {
            return UCL_EditorPage.Create<UCL_AgentSkillManagerPage>();
        }

        /// <summary>
        /// 「第一次開 Welcome → 自動把本頁 push 到頂」用的判斷 + 執行入口。
        /// 由 <see cref="UCL_WelcomePage"/> 的 ContentOnGUI 在首幀呼叫。
        /// </summary>
        /// <returns>true = 有彈；false = 已看過 / 已勾「不再自動彈」，沒彈</returns>
        public static bool MaybeAutoPopupOnWelcome(UCL_GUIPageController controller)
        {
            // 比對 EditorPrefs 內已確認的版本與當前版本；不同（含空字串）則彈
            string acked = EditorPrefs.GetString(PrefKey_Acknowledged, "");
            if (acked == CurrentAcknowledgeVersion) return false;

            UCL_EditorPage.Create<UCL_AgentSkillManagerPage>(controller);
            return true;
        }

        [MenuItem("UCL/Agent Skill Manager")]
        public static void OpenFromMenu()
        {
            // 沿用 Welcome 的入口模式 — 設 first-draw hook 讓 EditorMenu 開啟後 push 本頁
            UCL_EditorMenuPage.s_OnFirstDraw = (ctrl) =>
            {
                UCL_EditorPage.Create<UCL_AgentSkillManagerPage>(ctrl);
            };
            EditorApplication.ExecuteMenuItem("UCL/Menu");
        }

        // ===========================================================
        // 區塊：Skill 安裝狀態偵測（搬自 UCL_WelcomePage 的 DrawSkillsCard）
        // 物理意義：讀 .ucl_installed JSON 的 ucl_core_commit，跟 UCL_Core HEAD 比
        // ===========================================================

        enum InstallStatus
        {
            NoProjectRoot,
            NoUCLCore,
            NotInstalled,
            Synced,
            Stale,
            UnknownHead,
        }

        InstallStatus m_Status = InstallStatus.NotInstalled;
        string m_CurrentCommit = "";
        string m_InstalledCommit = "";
        string m_HostProjectRoot = "";
        string m_UCLCorePath = "";
        bool m_StatusDirty = true;
        bool m_Installing = false;
        Vector2 m_Scroll = Vector2.zero;

        // wordWrap 樣式快取（與 WelcomePage 同模式）
        GUIStyle m_WrapLabelStyle;
        GUIStyle WrapLabelStyle
        {
            get
            {
                if (m_WrapLabelStyle == null)
                {
                    m_WrapLabelStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true };
                }
                return m_WrapLabelStyle;
            }
        }

        /// <summary>找 host project root — 從 dataPath 往上走，偏好最外層含 .claude/ 的；
        /// 退一步取最外層 .git 目錄（跳過 submodule 的 .git file）。</summary>
        static string FindHostProjectRoot()
        {
            string current = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string outermostWithClaude = null;
            string outermostWithGit = null;
            for (int i = 0; i < 10; i++)
            {
                if (Directory.Exists(Path.Combine(current, ".claude"))) outermostWithClaude = current;
                else if (Directory.Exists(Path.Combine(current, ".git"))) outermostWithGit = current;
                string parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current) break;
                current = parent;
            }
            return outermostWithClaude ?? outermostWithGit;
        }

        static string TryGetGitHead(string repoPath)
        {
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo.FileName = "git";
                    p.StartInfo.Arguments = $"-C \"{repoPath}\" rev-parse HEAD";
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardOutput = true;
                    p.StartInfo.RedirectStandardError = true;
                    p.StartInfo.CreateNoWindow = true;
                    p.Start();
                    string stdout = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(3000);
                    if (p.ExitCode == 0 && !string.IsNullOrEmpty(stdout)) return stdout;
                }
            }
            catch { }
            return null;
        }

        static string ShortHash(string full)
        {
            if (string.IsNullOrEmpty(full)) return "(unknown)";
            return full.Length >= 7 ? full.Substring(0, 7) : full;
        }

        void RefreshStatus()
        {
            m_StatusDirty = false;
            m_CurrentCommit = "";
            m_InstalledCommit = "";

            string corePathRel = UCL_EditorPath.CorePath;
            if (string.IsNullOrEmpty(corePathRel))
            {
                m_Status = InstallStatus.NoUCLCore;
                return;
            }
            string projRootForCore = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            m_UCLCorePath = Path.GetFullPath(Path.Combine(projRootForCore, corePathRel));

            string hostRoot = FindHostProjectRoot();
            if (string.IsNullOrEmpty(hostRoot))
            {
                m_Status = InstallStatus.NoProjectRoot;
                return;
            }
            m_HostProjectRoot = hostRoot;

            m_CurrentCommit = TryGetGitHead(m_UCLCorePath) ?? "";

            string markerPath = Path.Combine(hostRoot, ".claude", "skills", ".ucl_installed");
            if (!File.Exists(markerPath))
            {
                m_Status = InstallStatus.NotInstalled;
                return;
            }

            try
            {
                string json = File.ReadAllText(markerPath);
                int idx = json.IndexOf("\"ucl_core_commit\"");
                if (idx >= 0)
                {
                    int colon = json.IndexOf(':', idx);
                    int q1 = json.IndexOf('"', colon + 1);
                    int q2 = json.IndexOf('"', q1 + 1);
                    if (q1 >= 0 && q2 > q1)
                    {
                        m_InstalledCommit = json.Substring(q1 + 1, q2 - q1 - 1);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentSkillManager] 讀 .ucl_installed 失敗：{ex.Message}");
            }

            if (string.IsNullOrEmpty(m_CurrentCommit) || m_CurrentCommit == "unknown")
                m_Status = InstallStatus.UnknownHead;
            else if (string.IsNullOrEmpty(m_InstalledCommit) || m_InstalledCommit == m_CurrentCommit)
                m_Status = InstallStatus.Synced;
            else
                m_Status = InstallStatus.Stale;
        }

        /// <summary>同步跑 install_skills.py。Block UI 但通常 &lt;500ms。</summary>
        void RunInstall()
        {
            if (m_Installing) return;
            m_Installing = true;
            try
            {
                string scriptPath = Path.Combine(m_UCLCorePath, "Tools~", "install_skills.py");
                if (!File.Exists(scriptPath))
                {
                    Debug.LogError($"[AgentSkillManager] install_skills.py 不存在：{scriptPath}");
                    return;
                }

                using (var p = new Process())
                {
                    p.StartInfo.FileName = "python";
                    p.StartInfo.Arguments = $"\"{scriptPath}\"";
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardOutput = true;
                    p.StartInfo.RedirectStandardError = true;
                    p.StartInfo.CreateNoWindow = true;
                    p.Start();
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);

                    if (!string.IsNullOrEmpty(stdout))
                        Debug.Log($"[AgentSkillManager] install_skills.py stdout:\n{stdout}");
                    if (!string.IsNullOrEmpty(stderr))
                        Debug.LogWarning($"[AgentSkillManager] install_skills.py stderr:\n{stderr}");

                    if (p.ExitCode == 0)
                        Debug.Log("[AgentSkillManager] Skill 安裝完成");
                    else
                        Debug.LogError($"[AgentSkillManager] install_skills.py exit={p.ExitCode}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentSkillManager] 安裝失敗：{ex.Message}\n（python 不在 PATH？）");
            }
            finally
            {
                m_Installing = false;
                m_StatusDirty = true;
            }
        }

        // ===========================================================
        // 區塊：UI
        // ===========================================================

        protected override void ContentOnGUI()
        {
            if (m_StatusDirty) RefreshStatus();

            m_Scroll = GUILayout.BeginScrollView(m_Scroll);

            DrawHeader();
            GUILayout.Space(8);
            DrawConcept();
            GUILayout.Space(8);
            DrawOneClickInstall();
            GUILayout.Space(8);
            DrawAgentMatrixPlaceholder();
            GUILayout.Space(8);
            DrawFooter();

            GUILayout.EndScrollView();
        }

        void DrawHeader()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                var titleStyle = new GUIStyle(UCL_GUIStyle.LabelStyle)
                {
                    fontSize = 20,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                };
                GUILayout.Label(UCL_CodeLocalize.Get("AgentSkill.Title"), titleStyle);

                var sub = new GUIStyle(UCL_GUIStyle.LabelStyle)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Italic,
                };
                GUILayout.Label(UCL_CodeLocalize.Get("AgentSkill.Subtitle"), sub);
            }
        }

        void DrawConcept()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("AgentSkill.ConceptTitle"), UCL_GUIStyle.LabelStyle);
                GUILayout.Label(UCL_CodeLocalize.Get("AgentSkill.ConceptBody"), WrapLabelStyle);
            }
        }

        void DrawOneClickInstall()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                var titleStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { fontStyle = FontStyle.Bold };
                GUILayout.Label(UCL_CodeLocalize.Get("AgentSkill.OneClick.Title"), titleStyle);
                GUILayout.Label(UCL_CodeLocalize.Get("AgentSkill.OneClick.Desc"), WrapLabelStyle);

                // 狀態行
                string statusLine;
                Color btnColor;
                string btnLabel;
                bool canInstall = true;

                switch (m_Status)
                {
                    case InstallStatus.NoProjectRoot:
                        statusLine = UCL_CodeLocalize.Get("AgentSkill.Status.NoProjectRoot");
                        btnColor = Color.gray; btnLabel = UCL_CodeLocalize.Get("AgentSkill.Btn.Install");
                        canInstall = false;
                        break;
                    case InstallStatus.NoUCLCore:
                        statusLine = UCL_CodeLocalize.Get("AgentSkill.Status.NoUCLCore");
                        btnColor = Color.gray; btnLabel = UCL_CodeLocalize.Get("AgentSkill.Btn.Install");
                        canInstall = false;
                        break;
                    case InstallStatus.NotInstalled:
                        statusLine = UCL_CodeLocalize.Get("AgentSkill.Status.NotInstalled");
                        btnColor = new Color(1f, 0.85f, 0.2f);
                        btnLabel = UCL_CodeLocalize.Get("AgentSkill.Btn.Install");
                        break;
                    case InstallStatus.Stale:
                        statusLine = string.Format(
                            UCL_CodeLocalize.Get("AgentSkill.Status.Stale"),
                            ShortHash(m_InstalledCommit), ShortHash(m_CurrentCommit));
                        btnColor = new Color(1f, 0.6f, 0.2f);
                        btnLabel = UCL_CodeLocalize.Get("AgentSkill.Btn.Sync");
                        break;
                    case InstallStatus.Synced:
                        statusLine = string.Format(
                            UCL_CodeLocalize.Get("AgentSkill.Status.Synced"),
                            ShortHash(m_CurrentCommit));
                        btnColor = new Color(0.6f, 0.9f, 0.6f);
                        btnLabel = UCL_CodeLocalize.Get("AgentSkill.Btn.Reinstall");
                        break;
                    case InstallStatus.UnknownHead:
                    default:
                        statusLine = UCL_CodeLocalize.Get("AgentSkill.Status.UnknownHead");
                        btnColor = Color.cyan;
                        btnLabel = UCL_CodeLocalize.Get("AgentSkill.Btn.Reinstall");
                        break;
                }

                GUILayout.Label(statusLine, WrapLabelStyle);
                if (!string.IsNullOrEmpty(m_HostProjectRoot))
                {
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("AgentSkill.HostRoot"), m_HostProjectRoot),
                        UCL_GUIStyle.LabelStyle);
                }

                using (new GUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!canInstall || m_Installing))
                    {
                        if (GUILayout.Button(btnLabel, UCL_GUIStyle.GetButtonStyle(btnColor), GUILayout.Width(180), GUILayout.Height(32)))
                        {
                            RunInstall();
                        }
                    }
                    if (GUILayout.Button(UCL_CodeLocalize.Get("AgentSkill.Btn.Refresh"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_StatusDirty = true;
                    }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(UCL_CodeLocalize.Get("AgentSkill.Btn.Doc"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        Application.OpenURL(UCL_URL.ResolveURL("ucl_core:Skills~/README.md"));
                    }
                }
            }
        }

        // 區塊職責：未來的 Per-Agent × Per-Skill 切換 matrix placeholder
        // 物理意義：使用者能勾選哪些 agent (Claude / Cursor / Antigravity / Gemini) 要裝、
        //          以及每個 agent 內哪些 skill 要裝（如不要 ucl-hook-setup 因為已配過）
        // 數值影響：(尚未實作) 改變 install_skills.py 呼叫的 --target / --include 參數
        // 設計取捨：先 placeholder，先驗一鍵安裝的價值再做客製化
        void DrawAgentMatrixPlaceholder()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                var titleStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { fontStyle = FontStyle.Bold };
                GUILayout.Label(UCL_CodeLocalize.Get("AgentSkill.Matrix.Title"), titleStyle);
                GUILayout.Label(UCL_CodeLocalize.Get("AgentSkill.Matrix.Body"), WrapLabelStyle);

                // 顯示目前 source 端有哪些 skill 可裝 — 從 Skills~/ 直接列目錄
                using (new EditorGUI.DisabledScope(true))
                {
                    string skillsRoot = Path.Combine(m_UCLCorePath, "Skills~");
                    if (Directory.Exists(skillsRoot))
                    {
                        foreach (var dir in Directory.GetDirectories(skillsRoot))
                        {
                            string name = Path.GetFileName(dir);
                            if (name.StartsWith("_") || name.EndsWith("~")) continue;
                            GUILayout.Toggle(true, $"  {name}", UCL_GUIStyle.LabelStyle);
                        }
                    }
                    else
                    {
                        GUILayout.Label(UCL_CodeLocalize.Get("AgentSkill.Matrix.NoSource"), WrapLabelStyle);
                    }
                }
            }
        }

        void DrawFooter()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool acked = EditorPrefs.GetString(PrefKey_Acknowledged, "") == CurrentAcknowledgeVersion;
                bool newAcked = GUILayout.Toggle(acked,
                    UCL_CodeLocalize.Get("AgentSkill.AckToggle"), WrapLabelStyle);
                if (newAcked != acked)
                {
                    if (newAcked)
                        EditorPrefs.SetString(PrefKey_Acknowledged, CurrentAcknowledgeVersion);
                    else
                        EditorPrefs.DeleteKey(PrefKey_Acknowledged);
                }

                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(UCL_CodeLocalize.Get("AgentSkill.Btn.ResetAck"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        EditorPrefs.DeleteKey(PrefKey_Acknowledged);
                        Debug.Log("[AgentSkillManager] 已重設確認狀態 — 下次開 Welcome 會再彈");
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("AgentSkill.AckVersionLabel"),
                        EditorPrefs.GetString(PrefKey_Acknowledged, UCL_CodeLocalize.Get("AgentSkill.NotSet"))),
                        UCL_GUIStyle.LabelStyle);
                }
            }
        }
    }
}
#endif
