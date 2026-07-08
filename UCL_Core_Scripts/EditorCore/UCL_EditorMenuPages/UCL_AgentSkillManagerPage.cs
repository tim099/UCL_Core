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
        // Claude → .claude/skills/、Antigravity → .agents/skills/，全域 .ucl_installed marker 都放在該目錄根
        static string TargetMarkerRelDir(AgentTarget t) => t switch
        {
            AgentTarget.Claude => Path.Combine(".claude", "skills"),
            AgentTarget.Antigravity => Path.Combine(".agents", "skills"),
            _ => Path.Combine(".claude", "skills"),
        };

        // Per-target 狀態：合併單一 dict 比平行欄位更易擴充新 target
        readonly Dictionary<AgentTarget, InstallStatus> m_StatusByTarget = new Dictionary<AgentTarget, InstallStatus>();
        readonly Dictionary<AgentTarget, string> m_InstalledCommitByTarget = new Dictionary<AgentTarget, string>();
        readonly Dictionary<AgentTarget, string> m_InstalledHashByTarget = new Dictionary<AgentTarget, string>();
        readonly Dictionary<AgentTarget, string> m_CurrentHashByTarget = new Dictionary<AgentTarget, string>();
        readonly HashSet<AgentTarget> m_InstallingSet = new HashSet<AgentTarget>();

        // 區塊職責：記錄各 target 上次安裝被跳過的檔案數（install_skills.py 的 local-edit 保護）
        // 物理意義：exit=2 + stdout 的 "skipped=N" 代表有 N 檔因本地改動沒被覆蓋 — 內容實際未更新；
        //          舊版只進 Console，使用者看頁面以為裝完了（「外觀 OK ≠ 真的 OK」）→ 必須上頁面顯示
        // 數值影響：>0 時 DrawTargetRow 顯示黃字警告；每次 RunInstall 重新解析覆寫
        readonly Dictionary<AgentTarget, int> m_LastSkipCountByTarget = new Dictionary<AgentTarget, int>();

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
        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentSkill.Btn.Doc"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                Application.OpenURL(UCL_URL.ResolveURL("ucl_core:Skills~/README.md"));
            }
        }
        // 區塊職責：對 selected skill names 算 aggregate SHA1，演算法必須跟
        //          install_skills.py 的 compute_source_hash() 一致：
        //            for skill in sorted(names):
        //              files = rglob("*")
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

                    List<string> files = Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories).ToList();

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

        /// <summary>同步跑 install_skills.py --target X。Block UI 但通常 &lt;500ms。
        /// <para>force=true 加 --force-overwrite — 覆蓋本地改動（對應頁面「強制同步」）。</para></summary>
        void RunInstall(AgentTarget target, bool force = false)
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
                    p.StartInfo.Arguments = $"\"{scriptPath}\" --target {TargetCliName(target)}"
                        + (force ? " --force-overwrite" : "");
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

                    // 區塊職責：從 stdout 摘要列 "Done. copied=X skipped=N ..." 解析跳過數
                    // 物理意義：skipped>0 = 有檔案因 local-edit 保護沒被覆蓋（內容未更新），
                    //          必須顯示在頁面上而非只進 Console — 否則使用者以為一鍵安裝後就是最新
                    // 數值影響：寫 m_LastSkipCountByTarget[target]，DrawTargetRow 據此畫黃字警告
                    var skipMatch = System.Text.RegularExpressions.Regex.Match(stdout ?? "", @"skipped=(\d+)");
                    m_LastSkipCountByTarget[target] = skipMatch.Success ? int.Parse(skipMatch.Groups[1].Value) : 0;

                    if (p.ExitCode == 0)
                    {
                        Debug.Log($"[AgentSkillManager:{tag}] Skill 安裝完成");
                    }
                    else if (p.ExitCode == 2)
                    {
                        // 物理意義：Python 定義 exit=2 為「部分檔案因偵測到 local edit 而跳過（安全機制）」
                        //          這屬於預期中的保護行為，不應報紅色 P0 錯。
                        Debug.LogWarning($"[AgentSkillManager:{tag}] Skill 安裝部分完成 (有檔案因本地修改而跳過，Exit=2)");
                    }
                    else
                    {
                        Debug.LogError($"[AgentSkillManager:{tag}] install_skills.py exit={p.ExitCode}");
                    }
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
        // 物理意義：使用者按「全部 target 一鍵裝」時觸發；同步 sequential，第二個 target 在第一個結束後才開始。
        //          force=true 對應「強制同步全部」— 覆蓋本地改動（清掉被 local-edit 保護卡住的 stale 檔）
        // 數值影響：m_InstallingSet 在每 target 結束時即釋放；UI Disabled 期間就是兩支 process 串連跑的時間
        void RunInstallAll(bool force = false)
        {
            foreach (var t in AllTargets) RunInstall(t, force);
        }

        // 區塊職責：逐 skill 安裝/解除安裝 — spawn install_skills.py --include <skill> [--uninstall] [--force-overwrite]
        // 物理意義：對接 install_skills.py 的 per-skill 化(merge marker / partial uninstall / drift 警告)。
        //          uninstall 預設不帶 force → 若該 skill 被本地改過, Python 端會警告跳過(破壞看得見);
        //          force=true 才強制(覆蓋本地改動 / 強制移除)。
        // 數值影響：Claude target only(MVP)；跑完 m_StatusDirty=true 刷新狀態列
        void RunInstallSkill(string skill, bool uninstall, bool force = false)
        {
            if (string.IsNullOrEmpty(skill)) return;
            try
            {
                string scriptPath = Path.Combine(m_UCLCorePath, "Tools~", "install_skills.py");
                if (!File.Exists(scriptPath))
                {
                    Debug.LogError($"[AgentSkillManager] install_skills.py 不存在：{scriptPath}");
                    return;
                }
                string args = $"\"{scriptPath}\" --target claude --include {skill}";
                if (uninstall) args += " --uninstall";
                if (force) args += " --force-overwrite";
                using (var p = new Process())
                {
                    p.StartInfo.FileName = "python";
                    p.StartInfo.Arguments = args;
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardOutput = true;
                    p.StartInfo.RedirectStandardError = true;
                    p.StartInfo.CreateNoWindow = true;
                    p.Start();
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);
                    string verb = uninstall ? "解除安裝" : "安裝";
                    if (!string.IsNullOrEmpty(stdout)) Debug.Log($"[AgentSkillManager:{skill}] {verb} stdout:\n{stdout}");
                    if (!string.IsNullOrEmpty(stderr)) Debug.LogWarning($"[AgentSkillManager:{skill}] {verb} stderr:\n{stderr}");
                    if (p.ExitCode == 0) Debug.Log($"[AgentSkillManager:{skill}] {verb}完成");
                    else Debug.LogWarning($"[AgentSkillManager:{skill}] {verb} exit={p.ExitCode}（uninstall 被本地改動擋下時為正常保護；需強制請用 force）");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentSkillManager:{skill}] 操作失敗：{ex.Message}（python 不在 PATH？）");
            }
            finally
            {
                m_StatusDirty = true;
            }
        }

        // 區塊職責：對單一 skill 目錄算 content hash（同 ComputeSourceHashFor 演算法, 但任意 dir + 排除 .ucl_source）
        // 物理意義：per-skill drift 偵測 = hash(Skills~/<skill> 源) vs hash(.claude/skills/<skill> 已裝)；
        //          排除 .ucl_source（已裝端才有, 源端沒有）才能公平比對。
        // 數值影響：回 hex string；dir 不存在回 ""
        static string HashSkillDirContent(string dir)
        {
            if (!Directory.Exists(dir)) return "";
            var entries = new List<(string rel, string abs)>();
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/');
                if (rel == ".ucl_source") continue;   // 已裝端標記檔, 源端沒有 → 排除
                entries.Add((rel, f));
            }
            entries.Sort((a, b) => string.CompareOrdinal(a.rel, b.rel));
            using (var sha = SHA1.Create())
            {
                byte[] sep = new byte[] { 0 };
                foreach (var e in entries)
                {
                    byte[] relBytes = Encoding.UTF8.GetBytes(e.rel);
                    sha.TransformBlock(relBytes, 0, relBytes.Length, null, 0);
                    sha.TransformBlock(sep, 0, 1, null, 0);
                    byte[] hexBytes = Encoding.ASCII.GetBytes(ComputeFileSha1Hex(e.abs));
                    sha.TransformBlock(hexBytes, 0, hexBytes.Length, null, 0);
                    sha.TransformBlock(sep, 0, 1, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return ToHex(sha.Hash);
            }
        }

        // ===========================================================
        // 區塊：UCL_SkillConfigAsset 同步（per-skill 開關狀態持久層）
        // 物理意義：Tim 拍板 — Page 對單一 skill 按裝/移除時, 要同步資料到對應的 UCL_SkillConfigAsset。
        //          真實 source of truth 是 runtime <UnityAssets>/.BuiltinModules/.../Core/UCL_Assets/
        //          UCL_SkillConfigAsset/<skill>.json(install_skills.py 也讀這裡), 不是 Templates~
        //          (Templates~ 只是初始模板, 安裝後複製進 .BuiltinModules, 使用者編輯都同步 .BuiltinModules)。
        // 設計取捨：直寫該 JSON(而非走 UCL_Asset.Save()) — Save() 落點依 CurEditModule, 若使用者正在
        //          編輯別的 module 會存錯地方; skill config 概念上恆屬 Core, 故直接定位 Core 路徑最穩。
        // ===========================================================
        static readonly string[] SkillConfigRel =
            { "ModulesRoot", "Modules", "Core", "UCL_Assets", "UCL_SkillConfigAsset" };

        /// <summary>從 m_UCLCorePath 往上走找 .BuiltinModules，回 Core 的 UCL_SkillConfigAsset 目錄；
        /// BuiltinModules 不存在(全新專案) → 回 Templates~ 模板預設目錄(僅供讀預設, 寫入時會 mkdir runtime)。</summary>
        string ResolveSkillConfigDir()
        {
            if (string.IsNullOrEmpty(m_UCLCorePath)) return "";
            string cur = m_UCLCorePath;
            for (int i = 0; i < 10; i++)
            {
                string builtin = Path.Combine(cur, ".BuiltinModules");
                if (Directory.Exists(builtin))
                    return Path.Combine(new[] { builtin }.Concat(SkillConfigRel).ToArray());
                string parent = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(parent) || parent == cur) break;
                cur = parent;
            }
            // fallback：Templates~ 模板預設（全新專案還沒 materialize BuiltinModules）
            return Path.Combine(new[] { m_UCLCorePath, "Templates~", "Assets", ".BuiltinModules" }
                .Concat(SkillConfigRel).ToArray());
        }

        /// <summary>讀 UCL_SkillConfigAsset 目錄，回傳 Enabled=false 的 skill 名集合。</summary>
        HashSet<string> LoadDisabledSkills()
        {
            var disabled = new HashSet<string>();
            string dir = ResolveSkillConfigDir();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return disabled;
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    string json = File.ReadAllText(f);
                    // 欄位 "Enabled"(UCL_Asset strip m_); 預設 true → 只有顯式 false 才算停用
                    if (ExtractJsonBoolField(json, "Enabled", true) == false)
                        disabled.Add(Path.GetFileNameWithoutExtension(f));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AgentSkillManager] 讀 skill config {f} 失敗：{ex.Message}");
                }
            }
            return disabled;
        }

        /// <summary>同步單一 skill 的 UCL_SkillConfigAsset 到 runtime .BuiltinModules。
        /// <para>enabled=false → 一律寫 {Enabled:false, Note}(停用是相對預設的偏離, 必落檔)。</para>
        /// <para>enabled=true → 僅在已有 config 檔時翻成 Enabled=true(消除既有停用記錄);
        ///   無檔則 no-op — 預設即啟用, 不需為每個裝的 skill 製造冗餘檔(asset 缺檔=啟用, 本來就同步)。</para>
        /// 保留既有 Note；寫入失敗只記 warning 不中斷安裝流程。</summary>
        void SyncSkillConfig(string skill, bool enabled)
        {
            try
            {
                string dir = ResolveSkillConfigDir();
                if (string.IsNullOrEmpty(dir)) return;
                // 若 fallback 落在 Templates~（理論上不該寫 Templates~）→ 跳過, 維持 Templates~ 純模板
                if (dir.Replace('\\', '/').Contains("/Templates~/"))
                {
                    Debug.LogWarning("[AgentSkillManager] 找不到 runtime .BuiltinModules，跳過 skill config 同步（請先安裝 Core module）");
                    return;
                }
                string path = Path.Combine(dir, skill + ".json");
                bool exists = File.Exists(path);
                // enable 且無既有記錄 → 預設即啟用, 不製造冗餘檔
                if (enabled && !exists) return;

                Directory.CreateDirectory(dir);
                string note = exists ? ExtractJsonStringField(File.ReadAllText(path), "Note") : "";
                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append($"\t\"Enabled\":{(enabled ? "true" : "false")},\n");
                sb.Append($"\t\"Note\":{EscapeJsonString(note)}\n");
                sb.Append("}\n");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                Debug.Log($"[AgentSkillManager:{skill}] UCL_SkillConfigAsset 同步 Enabled={enabled} → {path}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentSkillManager:{skill}] skill config 同步失敗：{ex.Message}");
            }
        }

        // 區塊職責：lightweight JSON bool 欄位抽取（"<key>": true/false）
        // 數值影響：找不到 / 非 bool → 回 iDefault
        static bool ExtractJsonBoolField(string json, string key, bool iDefault)
        {
            string token = "\"" + key + "\"";
            int idx = json.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return iDefault;
            int colon = json.IndexOf(':', idx + token.Length);
            if (colon < 0) return iDefault;
            string tail = json.Substring(colon + 1).TrimStart();
            if (tail.StartsWith("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (tail.StartsWith("false", StringComparison.OrdinalIgnoreCase)) return false;
            return iDefault;
        }

        // 區塊職責：最小 JSON 字串轉義（含外層雙引號）
        static string EscapeJsonString(string s)
        {
            if (s == null) s = "";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('\"');
            return sb.ToString();
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
                        // 區塊職責：「強制同步全部」— 帶 --force-overwrite 重跑所有 target
                        // 物理意義：一鍵安裝因 local-edit 保護跳過檔案時的顯式覆蓋出口；
                        //          使用者明知會蓋掉本地改動才按（橘色按鈕示警，與跳過警告同色系）
                        // 數值影響：對被跳過的檔案強制寫入 source 內容並刷新 .ucl_source 記錄
                        if (GUILayout.Button(UCL_CodeLocalize.Get("AgentSkill.Btn.ForceSyncAll"),
                            UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.7f, 0.3f)),
                            GUILayout.Width(260), GUILayout.Height(32)))
                        {
                            RunInstallAll(force: true);
                        }
                    }
                    if (GUILayout.Button(UCL_CodeLocalize.Get("AgentSkill.Btn.Refresh"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_StatusDirty = true;
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

                // 區塊職責：上次安裝有檔案被 local-edit 保護跳過 → 黃字警告上頁面
                // 物理意義：跳過 = 該檔內容實際沒更新，但上方 Synced 判定只比 source 側 hash 看不出來；
                //          不顯示的話使用者會以為一鍵安裝後就是最新（本 bug 的原始回報場景）
                // 數值影響：純顯示；數值來自 m_LastSkipCountByTarget（RunInstall 時解析 stdout）
                if (m_LastSkipCountByTarget.TryGetValue(t, out int skipCount) && skipCount > 0)
                {
                    var warnStyle = new GUIStyle(WrapLabelStyle);
                    warnStyle.normal.textColor = new Color(1f, 0.7f, 0.2f);
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("AgentSkill.SkippedWarn"), skipCount), warnStyle);
                }

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

                string skillsRoot = Path.Combine(m_UCLCorePath, "Skills~");
                if (!Directory.Exists(skillsRoot))
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("AgentSkill.Matrix.NoSource"), WrapLabelStyle);
                    return;
                }
                // 逐 skill 列（Claude target MVP）：狀態 + 裝/移除按鈕。
                // 狀態 per-skill：未裝 / 已同步 / ⚠已改動(drift) / 🚫停用(UCL_SkillConfigAsset Enabled=false)。
                // 停用語意：disabled 但實體還在 → 「🚫停用·待移除」(下次同步會解除安裝);
                //          disabled 且已移除 → 「🚫停用」。按鈕同步 UCL_SkillConfigAsset(裝→Enabled=true / 移除→false)。
                string installRoot = Path.Combine(m_HostProjectRoot, TargetMarkerRelDir(AgentTarget.Claude));
                var disabledSet = LoadDisabledSkills();
                foreach (var dir in Directory.GetDirectories(skillsRoot).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith("_") || name.EndsWith("~")) continue;
                    string instDir = Path.Combine(installRoot, name);
                    bool installed = File.Exists(Path.Combine(instDir, ".ucl_source")) || Directory.Exists(instDir);
                    bool disabled = disabledSet.Contains(name);
                    string statusTxt; Color statusCol;
                    if (disabled)
                    {
                        // 停用優先顯示（不論是否實體還在）
                        if (installed) { statusTxt = UCL_CodeLocalize.Get("AgentSkill.Matrix.Status.DisabledPendingRemove"); statusCol = new Color(1f, 0.5f, 0.4f); }
                        else { statusTxt = UCL_CodeLocalize.Get("AgentSkill.Matrix.Status.Disabled"); statusCol = new Color(0.7f, 0.55f, 0.55f); }
                    }
                    else if (!installed) { statusTxt = UCL_CodeLocalize.Get("AgentSkill.Matrix.Status.NotInstalled"); statusCol = new Color(0.6f, 0.6f, 0.6f); }
                    else
                    {
                        bool drift = HashSkillDirContent(dir) != HashSkillDirContent(instDir);
                        if (drift) { statusTxt = UCL_CodeLocalize.Get("AgentSkill.Matrix.Status.Drift"); statusCol = new Color(1f, 0.7f, 0.3f); }
                        else { statusTxt = UCL_CodeLocalize.Get("AgentSkill.Matrix.Status.Synced"); statusCol = new Color(0.4f, 0.85f, 0.5f); }
                    }
                    using (new GUILayout.HorizontalScope())
                    {
                        var st = new GUIStyle(UCL_GUIStyle.LabelStyle); st.normal.textColor = statusCol;
                        GUILayout.Label(statusTxt, st, GUILayout.Width(UCL_GUIStyle.GetScaledSize(110)));
                        GUILayout.Label(name, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(220)));
                        // 區塊職責：「📄 預覽」— 參考 UCL_DocSearchPage 的文件預覽按鈕, 在 Editor 內嵌渲染源 SKILL.md。
                        // 物理意義：預覽永遠看 Skills~/<name>/SKILL.md(source of truth, 必存在), 不論裝/未裝;
                        //          走 UCL_MarkdownViewerPage(Push 一頁, 按 Back 返回), 不離開 Unity 視窗。
                        // 數值影響：純讀檔渲染, 不改任何安裝狀態。
                        if (GUILayout.Button(UCL_CodeLocalize.Get("Preview"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))))
                        {
                            string skillMdAbs = Path.Combine(dir, "SKILL.md").Replace('\\', '/');
                            string skillMdRel = Path.GetRelativePath(m_UCLCorePath, skillMdAbs).Replace('\\', '/');
                            if (File.Exists(skillMdAbs))
                                UCL_MarkdownViewerPage.Create(skillMdRel, skillMdAbs);
                            else
                                Debug.LogWarning($"[AgentSkillManager:{name}] 找不到 SKILL.md：{skillMdAbs}");
                        }
                        if (disabled)
                        {
                            // 啟用：同步 Enabled=true → 安裝
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentSkill.Matrix.Btn.Enable"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60))))
                            {
                                SyncSkillConfig(name, enabled: true);
                                RunInstallSkill(name, uninstall: false);
                            }
                            // disabled 但實體還在 → 提供立即解除安裝（不改 config，維持停用）
                            if (installed && GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Remove"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60))))
                                RunInstallSkill(name, uninstall: true);
                        }
                        else if (!installed)
                        {
                            // 裝：同步 Enabled=true(消除可能殘留的 disabled 記錄) → 安裝
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentSkill.Btn.Install"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60))))
                            {
                                SyncSkillConfig(name, enabled: true);
                                RunInstallSkill(name, uninstall: false);
                            }
                        }
                        else
                        {
                            // 移除：同步 Enabled=false(停用) → 解除安裝
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Remove"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60))))
                            {
                                SyncSkillConfig(name, enabled: false);
                                RunInstallSkill(name, uninstall: true);
                            }
                            // drift 時提供強制重裝（覆蓋本地改動）
                            if (statusTxt == UCL_CodeLocalize.Get("AgentSkill.Matrix.Status.Drift") && GUILayout.Button(UCL_CodeLocalize.Get("AgentSkill.Btn.Reinstall"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60))))
                                RunInstallSkill(name, uninstall: false, force: true);
                        }
                        GUILayout.FlexibleSpace();
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
