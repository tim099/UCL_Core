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
        // 區塊：Skill 安裝狀態偵測（direct content compare, Tim 2026-07-14）
        // 物理意義：逐檔直接比對「Skills~/<skill> 源檔案內文」vs「已裝目錄內文」是否相同，
        //          不算 hash、不讀 marker 的 ucl_core_commit / source_hash（那會隨 commit churn
        //          且看不出已裝端實際內容）。antigravity 端 SKILL.md 因 install 會注入 trigger
        //          frontmatter，比對前對源套同樣轉換。結果在 RefreshStatus 一次算完並快取，
        //          只有安裝 / skill 操作(m_StatusDirty=true)才重算 — 不每幀重掃磁碟。
        // ===========================================================

        enum InstallStatus
        {
            NoProjectRoot,
            NoUCLCore,
            NotInstalled,
            Synced,
            Stale,
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
        readonly HashSet<AgentTarget> m_InstallingSet = new HashSet<AgentTarget>();

        // 區塊職責：per-skill 狀態快取（給 Matrix 用；Claude target）。
        // 物理意義：RefreshStatus 一次算完 installed / disabled / drift 三態並快取，Matrix 繪製只讀不算，
        //          避免舊版每幀對每個 skill 掃磁碟比對(perf) + 對齊 Tim「開頁一次判斷+快取」要求。
        struct SkillRowState { public bool installed; public bool disabled; public bool drift; }
        readonly Dictionary<string, SkillRowState> m_SkillRowCache = new Dictionary<string, SkillRowState>();

        // 區塊職責：記錄各 target 上次安裝被跳過的檔案數（install_skills.py 的 local-edit 保護）
        // 物理意義：exit=2 + stdout 的 "skipped=N" 代表有 N 檔因本地改動沒被覆蓋 — 內容實際未更新；
        //          舊版只進 Console，使用者看頁面以為裝完了（「外觀 OK ≠ 真的 OK」）→ 必須上頁面顯示
        // 數值影響：>0 時 DrawTargetRow 顯示黃字警告；每次 RunInstall 重新解析覆寫
        readonly Dictionary<AgentTarget, int> m_LastSkipCountByTarget = new Dictionary<AgentTarget, int>();

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

        void RefreshStatus()
        {
            m_StatusDirty = false;
            m_StatusByTarget.Clear();
            m_SkillRowCache.Clear();

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

            foreach (var t in AllTargets) ComputeStatusFor(t, hostRoot);
            BuildSkillRowCache(hostRoot);
        }

        // 區塊職責：對單一 target 計算安裝狀態（direct content compare, 不讀 marker hash/commit）。
        // 物理意義：.ucl_installed 存在且有裝過 skill(目錄含 .ucl_source) = 已安裝；再逐一比對每個
        //          已裝 skill「Skills~ 源內文 vs 已裝目錄內文」是否相同 — 任一不同(或源已刪) → Stale；
        //          全同 → Synced。完全不看 marker 的 source_hash / ucl_core_commit。
        // 數值影響：寫 m_StatusByTarget[t]
        void ComputeStatusFor(AgentTarget t, string hostRoot)
        {
            string installRoot = Path.Combine(hostRoot, TargetMarkerRelDir(t));
            string markerPath = Path.Combine(installRoot, ".ucl_installed");
            if (!File.Exists(markerPath) || !Directory.Exists(installRoot))
            {
                m_StatusByTarget[t] = InstallStatus.NotInstalled;
                return;
            }

            string skillsRoot = Path.Combine(m_UCLCorePath, "Skills~");
            bool anyInstalled = false;
            bool anyStale = false;
            try
            {
                foreach (var instDir in Directory.GetDirectories(installRoot))
                {
                    // 只看「本工具裝的」skill 目錄（含 .ucl_source 標記）
                    if (!File.Exists(Path.Combine(instDir, ".ucl_source"))) continue;
                    anyInstalled = true;
                    string name = Path.GetFileName(instDir);
                    string srcDir = Path.Combine(skillsRoot, name);
                    // 源已刪除但已裝殘留，或內文不同 → stale（需重裝 / 清理）
                    if (!Directory.Exists(srcDir) || !SkillContentMatches(srcDir, instDir, t))
                        anyStale = true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentSkillManager] 比對 {installRoot} 內文失敗：{ex.Message}");
            }

            if (!anyInstalled) m_StatusByTarget[t] = InstallStatus.NotInstalled;
            else m_StatusByTarget[t] = anyStale ? InstallStatus.Stale : InstallStatus.Synced;
        }

        // 區塊職責：lightweight JSON string 欄位抽取（SyncSkillConfig 讀既有 Note 用）
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
        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentSkill.Btn.Doc"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                Application.OpenURL(UCL_URL.ResolveURL("ucl_core:Skills~/README.md"));
            }
        }
        // 區塊職責：direct content compare — 源 skill 目錄 vs 已裝目錄，逐檔比對「內文是否相同」。
        // 物理意義：不算 hash（Tim 2026-07-14 拍板「甚至不用 hash，直接比對內文」）。claude 是原樣複製
        //          → 內文應完全相同；antigravity 的 SKILL.md 安裝時會注入 trigger frontmatter，故比對前
        //          對源 SKILL.md 套同樣轉換（鏡像 install_skills.py）。
        // 數值影響：任一源檔在已裝端缺失 / 內文不同 → 回 false（視為 drift/stale）。以文字（讀為 UTF-8）
        //          比對避開 BOM/編碼差異；.md 在本 repo 為 -text（無 CRLF 轉換）故換行穩定。忽略已裝端多出的
        //          .ucl_source 標記（源端沒有，不納入比對）。
        bool SkillContentMatches(string srcDir, string instDir, AgentTarget target)
        {
            foreach (var srcFile in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(srcDir, srcFile);
                string instFile = Path.Combine(instDir, rel);
                if (!File.Exists(instFile)) return false;
                string expected = (target == AgentTarget.Antigravity && Path.GetFileName(srcFile) == "SKILL.md")
                    ? TransformAntigravityFrontmatter(File.ReadAllText(srcFile), Path.GetFileName(srcDir))
                    : File.ReadAllText(srcFile);
                if (File.ReadAllText(instFile) != expected) return false;
            }
            return true;
        }

        // 區塊職責：鏡像 install_skills.py 的 antigravity SKILL.md frontmatter 轉換（比對用）。
        // 物理意義：install 端會在 SKILL.md frontmatter 注入一行 trigger:；比對已裝內文時源端必須套同一
        //          轉換才不會誤判 drift。**此對照表與轉換規則須與 install_skills.py 的
        //          get_antigravity_trigger_frontmatter() / transform_antigravity_frontmatter() 保持同步。**
        static string AntigravityTrigger(string skill)
        {
            switch (skill)
            {
                case "ucl-chat-tavern":
                    return "{ on_intent: [\"進入酒館\", \"聊天酒館\", \"進酒館\", \"去酒館\", \"enter tavern\", \"自言自語\", \"跟自己討論\", \"solo think\", \"腦力激盪\", \"solo brainstorm\", \"自我辯論\"] }";
                case "ucl-commit":
                    return "{ on_intent: [\"commit\", \"提交\", \"git commit\"] }";
                case "ucl-compile-error":
                    return "{ on_files: [\"*.cs\"], on_intent: [\"編譯錯\", \"compile error\", \"CS0103\", \"CS0117\", \"CS1503\", \"CS0246\", \"asmdef\", \"assembly\"] }";
                case "ucl-create-cmd":
                    return "{ on_intent: [\"新增 AgentCommand\", \"新增指令\", \"Create Cmd\", \"Create Command\"] }";
                case "ucl-hook-setup":
                    return "{ on_intent: [\"Hook Setup\", \"Hook 設置\", \"設置 Hook\", \"install skills\"] }";
                case "ucl-watch-video":
                    return "{ on_intent: [\"watch video\", \"看影片\", \"觀看影片\", \"YouTube\", \"影片心得\", \"影片轉錄\"] }";
                default:
                    return "\"always_on\"";
            }
        }

        static string TransformAntigravityFrontmatter(string content, string skill)
        {
            string trig = AntigravityTrigger(skill);
            if (content.StartsWith("---"))
            {
                // Python: content.split("---", 2) → 最多 3 段
                string[] parts = content.Split(new[] { "---" }, 3, StringSplitOptions.None);
                if (parts.Length >= 3)
                {
                    string frontmatter = parts[1];
                    if (!frontmatter.Contains("trigger:"))
                    {
                        frontmatter = $"trigger: {trig}\n{frontmatter}";
                        return $"---\n{frontmatter}---{parts[2]}";
                    }
                    // 已有 trigger → fall through 到底部 wrap（與 Python 行為一致）
                }
            }
            return $"---\ntrigger: {trig}\n---\n\n{content}";
        }

        // 區塊職責：一次算完 Matrix 用的 per-skill 三態（installed/disabled/drift）並快取。
        // 物理意義：由 RefreshStatus 呼叫；Matrix 繪製只讀 m_SkillRowCache，不每幀掃磁碟（對齊 Tim
        //          「開頁一次判斷+快取，只有安裝/skill 操作才刷新」）。drift 用 SkillContentMatches（Claude target）。
        void BuildSkillRowCache(string hostRoot)
        {
            m_SkillRowCache.Clear();
            string skillsRoot = Path.Combine(m_UCLCorePath, "Skills~");
            if (!Directory.Exists(skillsRoot)) return;
            string installRoot = Path.Combine(hostRoot, TargetMarkerRelDir(AgentTarget.Claude));
            var disabledSet = LoadDisabledSkills();
            foreach (var dir in Directory.GetDirectories(skillsRoot))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith("_") || name.EndsWith("~")) continue;
                string instDir = Path.Combine(installRoot, name);
                bool installed = File.Exists(Path.Combine(instDir, ".ucl_source")) || Directory.Exists(instDir);
                bool disabled = disabledSet.Contains(name);
                bool drift = installed && !disabled && !SkillContentMatches(dir, instDir, AgentTarget.Claude);
                m_SkillRowCache[name] = new SkillRowState { installed = installed, disabled = disabled, drift = drift };
            }
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
                    statusLine = UCL_CodeLocalize.Get("AgentSkill.Status.Stale");
                    btnColor = new Color(1f, 0.6f, 0.2f);
                    btnLabel = UCL_CodeLocalize.Get("AgentSkill.Btn.Sync");
                    break;
                case InstallStatus.Synced:
                    statusLine = UCL_CodeLocalize.Get("AgentSkill.Status.Synced");
                    btnColor = new Color(0.6f, 0.9f, 0.6f);
                    btnLabel = UCL_CodeLocalize.Get("AgentSkill.Btn.Reinstall");
                    break;
                default:
                    statusLine = UCL_CodeLocalize.Get("AgentSkill.Status.NotInstalled");
                    btnColor = new Color(1f, 0.85f, 0.2f);
                    btnLabel = UCL_CodeLocalize.Get("AgentSkill.Btn.Install");
                    break;
            }

            using (new GUILayout.VerticalScope("box"))
            {
                var headerStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { fontStyle = FontStyle.Bold };
                GUILayout.Label($"▸ {TargetDisplayName(t)}  ({TargetMarkerRelDir(t)}/)", headerStyle);
                GUILayout.Label(statusLine, WrapLabelStyle);

                // 區塊職責：上次安裝有檔案被 local-edit 保護跳過 → 黃字警告上頁面
                // 物理意義：跳過 = 該檔內容沒更新（維持本地改動）。現在上方狀態已改成 direct content compare，
                //          會把這種情況正確標成 Stale；本行是「這次安裝當下」的即時提示（skipped=N），互補。
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
                        if (GUILayout.Button(btnLabel, UCL_GUIStyle.GetButtonStyle(btnColor), GUILayout.ExpandWidth(false)))
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
                foreach (var dir in Directory.GetDirectories(skillsRoot).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith("_") || name.EndsWith("~")) continue;
                    // 讀 RefreshStatus 建好的 per-skill 快取（不每幀掃磁碟；安裝/skill 操作後 m_StatusDirty 才刷新）
                    if (!m_SkillRowCache.TryGetValue(name, out var row)) continue;
                    bool installed = row.installed;
                    bool disabled = row.disabled;
                    string statusTxt; Color statusCol;
                    if (disabled)
                    {
                        // 停用優先顯示（不論是否實體還在）
                        if (installed) { statusTxt = UCL_CodeLocalize.Get("AgentSkill.Matrix.Status.DisabledPendingRemove"); statusCol = new Color(1f, 0.5f, 0.4f); }
                        else { statusTxt = UCL_CodeLocalize.Get("AgentSkill.Matrix.Status.Disabled"); statusCol = new Color(0.7f, 0.55f, 0.55f); }
                    }
                    else if (!installed) { statusTxt = UCL_CodeLocalize.Get("AgentSkill.Matrix.Status.NotInstalled"); statusCol = new Color(0.6f, 0.6f, 0.6f); }
                    else if (row.drift) { statusTxt = UCL_CodeLocalize.Get("AgentSkill.Matrix.Status.Drift"); statusCol = new Color(1f, 0.7f, 0.3f); }
                    else { statusTxt = UCL_CodeLocalize.Get("AgentSkill.Matrix.Status.Synced"); statusCol = new Color(0.4f, 0.85f, 0.5f); }
                    using (new GUILayout.HorizontalScope())
                    {
                        var st = new GUIStyle(UCL_GUIStyle.LabelStyle); st.normal.textColor = statusCol;
                        GUILayout.Label(statusTxt, st, GUILayout.Width(UCL_GUIStyle.GetScaledSize(110)));
                        GUILayout.Label(name, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(220)));
                        // 區塊職責：「📄 預覽」— 參考 UCL_DocSearchPage 的文件預覽按鈕, 在 Editor 內嵌渲染源 SKILL.md。
                        // 物理意義：預覽永遠看 Skills~/<name>/SKILL.md(source of truth, 必存在), 不論裝/未裝;
                        //          走 UCL_MarkdownViewerPage(Push 一頁, 按 Back 返回), 不離開 Unity 視窗。
                        // 數值影響：純讀檔渲染, 不改任何安裝狀態。
                        float btnWidth = UCL_GUIStyle.GetScaledSize(100);
                        if (GUILayout.Button(UCL_CodeLocalize.Get("Preview"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(btnWidth)))
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
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentSkill.Matrix.Btn.Enable"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(btnWidth)))
                            {
                                SyncSkillConfig(name, enabled: true);
                                RunInstallSkill(name, uninstall: false);
                            }
                            // disabled 但實體還在 → 提供立即解除安裝（不改 config，維持停用）
                            if (installed && GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Remove"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(btnWidth)))
                                RunInstallSkill(name, uninstall: true);
                        }
                        else if (!installed)
                        {
                            // 裝：同步 Enabled=true(消除可能殘留的 disabled 記錄) → 安裝
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentSkill.Btn.Install"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(btnWidth)))
                            {
                                SyncSkillConfig(name, enabled: true);
                                RunInstallSkill(name, uninstall: false);
                            }
                        }
                        else
                        {
                            // 移除：同步 Enabled=false(停用) → 解除安裝
                            if (GUILayout.Button(UCL_CodeLocalize.Get("AgentCmd.Remove"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(btnWidth)))
                            {
                                SyncSkillConfig(name, enabled: false);
                                RunInstallSkill(name, uninstall: true);
                            }
                            // drift 時提供強制重裝（覆蓋本地改動）
                            if (row.drift && GUILayout.Button(UCL_CodeLocalize.Get("AgentSkill.Btn.Reinstall"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(btnWidth)))
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
