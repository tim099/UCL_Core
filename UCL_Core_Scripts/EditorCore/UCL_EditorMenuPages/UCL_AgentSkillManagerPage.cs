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
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

        // 區塊職責：同一 EditorWindow lifetime 內只允許自動彈一次的 session-once guard。
        // 物理意義：避免 ContentOnGUI 內 push 失敗（例如使用者 Pop 回 Welcome 時 instance flag 已重設）
        //          造成多次 push；與 EditorPrefs 版本旗標互補（後者跨 session 穩定，本旗標只活於當前 process）。
        // 數值影響：true 後 MaybeAutoPopupOnWelcome 永遠 return false，直到 domain reload 或重啟 Editor。
        static bool s_AutoPoppedThisSession = false;

        /// <summary>
        /// 「第一次開 Welcome → 自動把本頁 push 到頂」用的判斷 + 執行入口。
        /// 由 <see cref="UCL_WelcomePage"/> 的 ContentOnGUI 在首幀呼叫。
        /// </summary>
        /// <returns>true = 有排程彈；false = 已看過 / 已勾「不再自動彈」/ 本 session 已彈過，沒彈</returns>
        public static bool MaybeAutoPopupOnWelcome(UCL_GUIPageController controller)
        {
            // 比對 EditorPrefs 內已確認的版本與當前版本；不同（含空字串）則彈
            string acked = EditorPrefs.GetString(PrefKey_Acknowledged, "");
            if (acked == CurrentAcknowledgeVersion) return false;
            if (s_AutoPoppedThisSession) return false;
            s_AutoPoppedThisSession = true;

            // 區塊職責：把實際 Push 推遲到下一個 idle tick，離開當前 OnGUI stack 才動 controller。
            // 物理意義：在 ContentOnGUI 內直接 Push 會在同一幀的 Layout / Repaint 兩次 event 中
            //          看到不同 TopPage（layout 跑 Welcome、repaint 跑新 page），
            //          IMGUI layout cache 對不上 → ExitGUIException: Mismatched LayoutGroup.repaint。
            //          delayCall 會在 OnGUI 結束後才執行，下個 GUI tick controller 已穩定到新 page 上。
            // 數值影響：使用者主觀感受是「彈窗在按下 UCL/Menu 後一個 frame 內彈出」，差異無感。
            EditorApplication.delayCall += () =>
            {
                if (controller == null) return;
                // 二次確認 — 萬一在排程到執行間使用者已經自己手動開了
                if (controller.Pages != null)
                {
                    foreach (var p in controller.Pages)
                    {
                        if (p is UCL_AgentSkillManagerPage) return;
                    }
                }
                UCL_EditorPage.Create<UCL_AgentSkillManagerPage>(controller);
            };
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
        // 物理意義：對 Skills~/<installed_skills> 算 aggregate SHA1（演算法跟 install_skills.py
        //          compute_source_hash 一致），與 .ucl_installed.source_hash 比對。
        //          舊版 marker 沒有 source_hash 欄位 → 視為 LegacyNoHash 提示重裝刷新。
        //          仍保留 ucl_core_commit 顯示給使用者參考，但不參與 stale 判定。
        // ===========================================================

        enum InstallStatus
        {
            NoProjectRoot,
            NoUCLCore,
            NotInstalled,
            Synced,
            Stale,
            UnknownHead,
            LegacyNoHash,
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
        readonly Dictionary<AgentTarget, string> m_InstalledHashByTarget = new Dictionary<AgentTarget, string>();
        readonly Dictionary<AgentTarget, string> m_CurrentHashByTarget = new Dictionary<AgentTarget, string>();
        readonly HashSet<AgentTarget> m_InstallingSet = new HashSet<AgentTarget>();

        string m_CurrentCommit = "";
        string m_HostProjectRoot = "";
        string m_UCLCorePath = "";
        bool m_StatusDirty = true;

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
            string current = UCL_RepoPath.UnityProjectRoot;
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
            m_InstalledHashByTarget.Clear();
            m_CurrentHashByTarget.Clear();

            string corePathRel = UCL_EditorPath.CorePath;
            if (string.IsNullOrEmpty(corePathRel))
            {
                foreach (var t in AllTargets) m_StatusByTarget[t] = InstallStatus.NoUCLCore;
                return;
            }
            string projRootForCore = UCL_RepoPath.UnityProjectRoot;
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
        // 物理意義：讀 marker JSON 取出 source_hash + installed_skills；對 Skills~/<installed_skills>
        //          重新算 aggregate SHA1，相同 → Synced；不同 → Stale；舊版無 source_hash 欄位 → LegacyNoHash。
        // 數值影響：寫 m_StatusByTarget[t]、m_InstalledCommitByTarget[t]、m_InstalledHashByTarget[t]、m_CurrentHashByTarget[t]
        void ComputeStatusFor(AgentTarget t, string hostRoot)
        {
            string markerPath = Path.Combine(hostRoot, TargetMarkerRelDir(t), ".ucl_installed");
            if (!File.Exists(markerPath))
            {
                m_StatusByTarget[t] = InstallStatus.NotInstalled;
                m_InstalledCommitByTarget[t] = "";
                m_InstalledHashByTarget[t] = "";
                m_CurrentHashByTarget[t] = "";
                return;
            }

            string installedCommit = "";
            string installedHash = "";
            List<string> installedSkills = new List<string>();
            try
            {
                string json = File.ReadAllText(markerPath);
                installedCommit = ExtractJsonStringField(json, "ucl_core_commit");
                installedHash = ExtractJsonStringField(json, "source_hash");
                installedSkills = ExtractJsonStringArrayField(json, "installed_skills");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentSkillManager] 讀 {markerPath} 失敗：{ex.Message}");
            }
            m_InstalledCommitByTarget[t] = installedCommit;
            m_InstalledHashByTarget[t] = installedHash;

            // 算當前 source 端 hash — 用 marker 內 installed_skills 為基準
            // （這樣使用者「只裝某子集」不會因為新 skill 出現就誤判 stale）
            string currentHash = "";
            try
            {
                if (installedSkills != null && installedSkills.Count > 0)
                    currentHash = ComputeSourceHashFor(installedSkills, t);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentSkillManager] 算 source hash 失敗：{ex.Message}");
            }
            m_CurrentHashByTarget[t] = currentHash;

            if (string.IsNullOrEmpty(installedHash))
            {
                // legacy marker — 沒寫 source_hash，提示重跑 install 刷新
                m_StatusByTarget[t] = InstallStatus.LegacyNoHash;
            }
            else if (string.IsNullOrEmpty(currentHash))
            {
                m_StatusByTarget[t] = InstallStatus.UnknownHead;
            }
            else if (currentHash == installedHash)
            {
                m_StatusByTarget[t] = InstallStatus.Synced;
            }
            else
            {
                m_StatusByTarget[t] = InstallStatus.Stale;
            }
        }

        // 區塊職責：lightweight JSON string 欄位抽取（避免引入 JsonUtility 對 Dictionary 的限制）
        // 物理意義：找 "<key>": "<value>" 的 value，跳過轉義處理（marker 不含特殊字元）
        // 數值影響：找不到 → 回空字串
        static string ExtractJsonStringField(string json, string key)
        {
            string token = "\"" + key + "\"";
            int idx = json.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return "";
            int colon = json.IndexOf(':', idx + token.Length);
            if (colon < 0) return "";
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return "";
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 <= q1) return "";
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        // 區塊職責：抽取 JSON 內 string array 欄位（如 installed_skills）
        // 物理意義：找 "<key>": [ "a", "b", ... ]，逐個讀字串
        // 數值影響：找不到 → 空 list
        static List<string> ExtractJsonStringArrayField(string json, string key)
        {
            var list = new List<string>();
            string token = "\"" + key + "\"";
            int idx = json.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return list;
            int bracket = json.IndexOf('[', idx + token.Length);
            int endBracket = json.IndexOf(']', bracket + 1);
            if (bracket < 0 || endBracket < 0) return list;
            int cursor = bracket + 1;
            while (cursor < endBracket)
            {
                int q1 = json.IndexOf('"', cursor);
                if (q1 < 0 || q1 > endBracket) break;
                int q2 = json.IndexOf('"', q1 + 1);
                if (q2 < 0 || q2 > endBracket) break;
                list.Add(json.Substring(q1 + 1, q2 - q1 - 1));
                cursor = q2 + 1;
            }
            return list;
        }

        // 區塊職責：對 selected skill names 算 aggregate SHA1，演算法必須跟
        //          install_skills.py 的 compute_source_hash() 一致：
        //            for skill in sorted(names):
        //              files = SKILL.md only (antigravity) | rglob("*") (claude)
        //              for (rel-posix-path, file) in sorted by rel:
        //                hasher.update(rel + \0 + sha1(file)hex + \0)
        // 物理意義：跟 Python 同字節序列 → 同最終 hash → Editor 與 CLI 端可一致比對
        // 數值影響：m_UCLCorePath 必須有效；missing skill dir → 跳過該 skill
        string ComputeSourceHashFor(List<string> skillNames, AgentTarget target)
        {
            if (string.IsNullOrEmpty(m_UCLCorePath)) return "";
            string skillsRoot = Path.Combine(m_UCLCorePath, "Skills~");
            if (!Directory.Exists(skillsRoot)) return "";

            // 排序時用 ordinal compare（與 Python sorted() 預設行為一致）
            var orderedNames = skillNames.OrderBy(s => s, StringComparer.Ordinal).ToList();

            using (var sha = SHA1.Create())
            {
                byte[] sep = new byte[] { 0 };
                foreach (var name in orderedNames)
                {
                    string srcDir = Path.Combine(skillsRoot, name);
                    if (!Directory.Exists(srcDir)) continue;

                    List<string> files;
                    if (target == AgentTarget.Antigravity)
                    {
                        string skillMd = Path.Combine(srcDir, "SKILL.md");
                        files = File.Exists(skillMd) ? new List<string> { skillMd } : new List<string>();
                    }
                    else
                    {
                        files = Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories).ToList();
                    }

                    // 以 posix-style rel-to-skillsRoot path 排序，跟 Python relative_to(SKILLS_SRC).as_posix() 一致
                    var entries = files
                        .Select(f =>
                        {
                            string rel = MakePosixRelative(skillsRoot, f);
                            return new { rel, abs = f };
                        })
                        .OrderBy(e => e.rel, StringComparer.Ordinal)
                        .ToList();

                    foreach (var e in entries)
                    {
                        byte[] relBytes = Encoding.UTF8.GetBytes(e.rel);
                        sha.TransformBlock(relBytes, 0, relBytes.Length, null, 0);
                        sha.TransformBlock(sep, 0, 1, null, 0);
                        string fileHex = ComputeFileSha1Hex(e.abs);
                        byte[] hexBytes = Encoding.ASCII.GetBytes(fileHex);
                        sha.TransformBlock(hexBytes, 0, hexBytes.Length, null, 0);
                        sha.TransformBlock(sep, 0, 1, null, 0);
                    }
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return ToHex(sha.Hash);
            }
        }

        static string MakePosixRelative(string root, string fullPath)
        {
            string rel = Path.GetRelativePath(root, fullPath);
            return rel.Replace('\\', '/');
        }

        static string ComputeFileSha1Hex(string path)
        {
            using (var sha = SHA1.Create())
            using (var fs = File.OpenRead(path))
            {
                return ToHex(sha.ComputeHash(fs));
            }
        }

        static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
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

            // 注意：不要在這裡再開 BeginScrollView — UCL_EditorPage.OnGUI 已經包了 ScrollViewScope。
            // 巢狀 ScrollView 在 Unity 2021 IMGUI 會拋 InvalidCastException（Unity 6 才會被內部靜默 recover）。
            DrawHeader();
            GUILayout.Space(8);
            DrawConcept();
            GUILayout.Space(8);
            DrawOneClickInstall();
            GUILayout.Space(8);
            DrawAgentMatrixPlaceholder();
            GUILayout.Space(8);
            DrawFooter();
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
                case InstallStatus.LegacyNoHash:
                    statusLine = UCL_CodeLocalize.Get("AgentSkill.Status.LegacyNoHash");
                    btnColor = new Color(1f, 0.6f, 0.2f);
                    btnLabel = UCL_CodeLocalize.Get("AgentSkill.Btn.Sync");
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
