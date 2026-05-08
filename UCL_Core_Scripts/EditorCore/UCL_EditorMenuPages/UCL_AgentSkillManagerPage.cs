// 區塊職責：Agent Skill 管理頁 — 第一次開 UCL_WelcomePage 時自動 push 到頂、
//            提供 onboarding 強制曝光，後續可從 Welcome 卡片或選單再開。
// 物理意義：UCL_Core 的 Skills~/ 是跨專案 Skill 的 source-of-truth；不同 agent
//            (Claude Code / Antigravity / 規劃中：Cursor / Gemini …) 的安裝路徑由
//            install_skills.py 的 --target 分支處理。本頁是 IMGUI 視覺化前端，
//            幫不會打 CLI 的開發者把「裝 Skill 給 AI 用」這件事一鍵化。
// 數值影響：每個 target 一顆按鈕 spawn `python install_skills.py --target X`；
//            安裝結果寫對應 dst 的 .ucl_installed（Claude → .claude/skills/，
//            Antigravity → .agents/rules/）；「我知道了」勾選會寫 EditorPrefs，
//            下次不再自動彈本頁。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
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
    /// 目前支援 Claude Code（--target claude）與 Antigravity（--target antigravity），
    /// 各自渲染一行狀態 + 安裝按鈕；另提供「Install All」一鍵跑所有 target。
    /// 其他 agent (Cursor / Gemini) 的 target 由 install_skills.py 後續擴充。
    /// 本頁預留 Per-Agent × Per-Skill 切換 matrix 的 UI 區塊（目前只有 placeholder）。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_AgentSkillManagerPage.md")]
    public class UCL_AgentSkillManagerPage : UCL_CommonEditorPage
    {
        public override string WindowName => "Agent Skill Manager";

        // opt-in 進 UCL_EditorMenuPage 的 Page 選擇器下拉
        // 物理意義：Skill 安裝是常用但非首屏動作，放下拉避免外側按鈕區擁擠；
        //          首次曝光由 MaybeAutoPopupOnWelcome 處理，事後使用者要再開可從 Page Picker 找
        public override bool ShowInPageMenu => true;

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

        // 區塊職責：支援的 install target 列舉
        // 物理意義：對應 install_skills.py 的 --target choices
        // 數值影響：每個 target 各自有 RunInstall 按鈕 + 狀態列；MarkerRelDir / CliName / DisplayName 三 helper 統一管理映射
        public enum AgentTarget
        {
            Claude = 0,
            Antigravity = 1,
        }
        static readonly AgentTarget[] AllTargets = { AgentTarget.Claude, AgentTarget.Antigravity };

        static string TargetCliName(AgentTarget t) => t switch
        {
            AgentTarget.Claude => "claude",
            AgentTarget.Antigravity => "antigravity",
            _ => "claude",
        };

        static string TargetDisplayName(AgentTarget t) => t switch
        {
            AgentTarget.Claude => "Claude Code",
            AgentTarget.Antigravity => "Antigravity",
            _ => t.ToString(),
        };

        // 不同 target 的安裝目錄（相對 host project root）
        // Claude → .claude/skills/、Antigravity → .agents/rules/，全域 .ucl_installed marker 都放在該目錄根
        static string TargetMarkerRelDir(AgentTarget t) => t switch
        {
            AgentTarget.Claude => Path.Combine(".claude", "skills"),
            AgentTarget.Antigravity => Path.Combine(".agents", "rules"),
            _ => Path.Combine(".claude", "skills"),
        };

        // Per-target 狀態：合併單一 dict 比平行欄位更易擴充新 target
        readonly Dictionary<AgentTarget, InstallStatus> m_StatusByTarget = new Dictionary<AgentTarget, InstallStatus>();
        readonly Dictionary<AgentTarget, string> m_InstalledCommitByTarget = new Dictionary<AgentTarget, string>();
        readonly HashSet<AgentTarget> m_InstallingSet = new HashSet<AgentTarget>();

        string m_CurrentCommit = "";
        string m_HostProjectRoot = "";
        string m_UCLCorePath = "";
        bool m_StatusDirty = true;
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
            m_StatusByTarget.Clear();
            m_InstalledCommitByTarget.Clear();

            string corePathRel = UCL_EditorPath.CorePath;
            if (string.IsNullOrEmpty(corePathRel))
            {
                foreach (var t in AllTargets) m_StatusByTarget[t] = InstallStatus.NoUCLCore;
                return;
            }
            string projRootForCore = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            m_UCLCorePath = Path.GetFullPath(Path.Combine(projRootForCore, corePathRel));

            string hostRoot = FindHostProjectRoot();
            if (string.IsNullOrEmpty(hostRoot))
            {
                foreach (var t in AllTargets) m_StatusByTarget[t] = InstallStatus.NoProjectRoot;
                return;
            }
            m_HostProjectRoot = hostRoot;

            m_CurrentCommit = TryGetGitHead(m_UCLCorePath) ?? "";

            foreach (var t in AllTargets) ComputeStatusFor(t, hostRoot);
        }

        // 區塊職責：對單一 target 計算 .ucl_installed marker 狀態
        // 物理意義：marker 路徑 = hostRoot/<target dir>/.ucl_installed；JSON 內 ucl_core_commit 跟 git HEAD 比對 → Synced / Stale
        // 數值影響：寫 m_StatusByTarget[t] 與 m_InstalledCommitByTarget[t]
        void ComputeStatusFor(AgentTarget t, string hostRoot)
        {
            string markerPath = Path.Combine(hostRoot, TargetMarkerRelDir(t), ".ucl_installed");
            if (!File.Exists(markerPath))
            {
                m_StatusByTarget[t] = InstallStatus.NotInstalled;
                m_InstalledCommitByTarget[t] = "";
                return;
            }

            string installed = "";
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
                        installed = json.Substring(q1 + 1, q2 - q1 - 1);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentSkillManager] 讀 {markerPath} 失敗：{ex.Message}");
            }
            m_InstalledCommitByTarget[t] = installed;

            if (string.IsNullOrEmpty(m_CurrentCommit) || m_CurrentCommit == "unknown")
                m_StatusByTarget[t] = InstallStatus.UnknownHead;
            else if (string.IsNullOrEmpty(installed) || installed == m_CurrentCommit)
                m_StatusByTarget[t] = InstallStatus.Synced;
            else
                m_StatusByTarget[t] = InstallStatus.Stale;
        }

        /// <summary>同步跑 install_skills.py --target X。Block UI 但通常 &lt;500ms。</summary>
        void RunInstall(AgentTarget target)
        {
            if (m_InstallingSet.Contains(target)) return;
            m_InstallingSet.Add(target);
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
                    p.StartInfo.Arguments = $"\"{scriptPath}\" --target {TargetCliName(target)}";
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardOutput = true;
                    p.StartInfo.RedirectStandardError = true;
                    p.StartInfo.CreateNoWindow = true;
                    p.Start();
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);

                    string tag = TargetDisplayName(target);
                    if (!string.IsNullOrEmpty(stdout))
                        Debug.Log($"[AgentSkillManager:{tag}] install_skills.py stdout:\n{stdout}");
                    if (!string.IsNullOrEmpty(stderr))
                        Debug.LogWarning($"[AgentSkillManager:{tag}] install_skills.py stderr:\n{stderr}");

                    if (p.ExitCode == 0)
                        Debug.Log($"[AgentSkillManager:{tag}] Skill 安裝完成");
                    else
                        Debug.LogError($"[AgentSkillManager:{tag}] install_skills.py exit={p.ExitCode}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentSkillManager:{TargetDisplayName(target)}] 安裝失敗：{ex.Message}\n（python 不在 PATH？）");
            }
            finally
            {
                m_InstallingSet.Remove(target);
                m_StatusDirty = true;
            }
        }

        // 區塊職責：依序跑所有 target 的安裝
        // 物理意義：使用者按「全部 target 一鍵裝」時觸發；同步 sequential，第二個 target 在第一個結束後才開始
        // 數值影響：m_InstallingSet 在每 target 結束時即釋放；UI Disabled 期間就是兩支 process 串連跑的時間
        void RunInstallAll()
        {
            foreach (var t in AllTargets) RunInstall(t);
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

                if (!string.IsNullOrEmpty(m_HostProjectRoot))
                {
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("AgentSkill.HostRoot"), m_HostProjectRoot),
                        UCL_GUIStyle.LabelStyle);
                }

                // 區塊職責：每個 target 一行（label + 狀態 + 按鈕）
                // 物理意義：Claude / Antigravity 各自獨立安裝；狀態互不影響
                // 數值影響：每行內 RunInstall(target) 只動該 target 的 dst 目錄
                foreach (var t in AllTargets)
                {
                    DrawTargetRow(t);
                }

                GUILayout.Space(4);
                using (new GUILayout.HorizontalScope())
                {
                    bool anyInstalling = m_InstallingSet.Count > 0;
                    bool anyBlocked = false;
                    foreach (var t in AllTargets)
                    {
                        if (m_StatusByTarget.TryGetValue(t, out var st) &&
                            (st == InstallStatus.NoProjectRoot || st == InstallStatus.NoUCLCore))
                            anyBlocked = true;
                    }

                    using (new EditorGUI.DisabledScope(anyInstalling || anyBlocked))
                    {
                        if (GUILayout.Button(UCL_CodeLocalize.Get("AgentSkill.Btn.InstallAll"),
                            UCL_GUIStyle.GetButtonStyle(new Color(0.4f, 0.8f, 1f)),
                            GUILayout.Width(220), GUILayout.Height(32)))
                        {
                            RunInstallAll();
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

        // 區塊職責：單一 target 的狀態 + 安裝按鈕橫列
        // 物理意義：把 status enum 翻成顏色 / label / 按鈕文字 + dst 路徑顯示，
        //          使用者一眼可看到「Claude 已同步、Antigravity 尚未安裝」之類的狀況
        // 數值影響：點擊按鈕呼叫 RunInstall(target)；該 target 的 m_InstallingSet bit 控制 DisabledScope
        void DrawTargetRow(AgentTarget t)
        {
            InstallStatus status = m_StatusByTarget.TryGetValue(t, out var s) ? s : InstallStatus.NotInstalled;
            string installedCommit = m_InstalledCommitByTarget.TryGetValue(t, out var c) ? c : "";

            string statusLine;
            Color btnColor;
            string btnLabel;
            bool canInstall = true;

            switch (status)
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
                        ShortHash(installedCommit), ShortHash(m_CurrentCommit));
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

            using (new GUILayout.VerticalScope("box"))
            {
                var headerStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { fontStyle = FontStyle.Bold };
                GUILayout.Label($"▸ {TargetDisplayName(t)}  ({TargetMarkerRelDir(t)}/)", headerStyle);
                GUILayout.Label(statusLine, WrapLabelStyle);

                using (new GUILayout.HorizontalScope())
                {
                    bool installingThis = m_InstallingSet.Contains(t);
                    using (new EditorGUI.DisabledScope(!canInstall || installingThis))
                    {
                        if (GUILayout.Button(btnLabel, UCL_GUIStyle.GetButtonStyle(btnColor), GUILayout.Width(180), GUILayout.Height(28)))
                        {
                            RunInstall(t);
                        }
                    }
                    GUILayout.FlexibleSpace();
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
