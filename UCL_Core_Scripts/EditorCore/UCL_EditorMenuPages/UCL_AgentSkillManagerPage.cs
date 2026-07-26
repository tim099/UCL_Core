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

        /// <summary>「永不自動彈」opt-out 旗標的版本值。勾選 footer toggle 寫入本值 → 之後即使
        /// skill 有更新也不自動彈（Tim 2026-07-14 拍板：彈窗判定改走 hash 快照，本旗標降級為逃生門）。</summary>
        public const string CurrentAcknowledgeVersion = "1";

        // EditorPrefs 跨專案共享 → 用 ProjectFingerprint 加綴，避免 A 專案勾過 B 專案不彈。
        // 指紋必須用穩定值（dataPath hash）— 任何隨開發活動自然變動的值（如 git commit）都不配
        // 當快照 key，否則永遠判定有變動（summit install marker churn 血證）。
        static string s_PrefKey_Acknowledged;
        static string s_ProjectFingerprint;

        static string ProjectFingerprint =>
            s_ProjectFingerprint ??= Application.dataPath.GetHashCode().ToString("X");

        /// <summary>EditorPrefs key — 使用者勾過「永不自動彈」的 opt-out 旗標。空 = 未勾。</summary>
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

        // ===========================================================
        // 區塊：Skill source hash 快照（自動彈窗判定用，Tim 2026-07-14 拍板）
        // 物理意義：彈窗條件從「使用者沒勾確認」改為「Skills~ 源內容自上次彈窗後有變動」。
        //          對每個 skill 目錄算 content hash 存成單一 EditorPrefs 快照；開選單首幀
        //          重算比對，有 diff（新增/變更/移除）才彈，彈窗當下即覆寫快照（送達即簽收，
        //          同組變動不因 domain reload 反覆彈 — 舊 static bool guard 活不過編譯的
        //          失憶問題被快照連根拔掉）。初次（無快照）維持無條件彈的首曝語意。
        //          只偵測 source 演進；安裝副本被改的 local drift 仍由 Matrix ⚠ 負責。
        // ===========================================================

        /// <summary>EditorPrefs key — 所有 skill 的 hash 快照，單一 key 存 "name=hash;..."（依名稱排序）。</summary>
        public static string PrefKey_SkillHashes =>
            $"UCL_Core.AgentSkill.SkillHashes@{ProjectFingerprint}";

        /// <summary>EditorPrefs key — 上次自動彈窗的變動清單（秒關彈窗的人可事後在頁內查看）。</summary>
        public static string PrefKey_LastSkillChanges =>
            $"UCL_Core.AgentSkill.LastChanges@{ProjectFingerprint}";

        // 區塊職責：static 解析 Skills~ 源根目錄（MaybeAutoPopupOnWelcome 無 instance 可用）。
        // 物理意義：與 RefreshStatus 同一條解析鏈（UnityProjectRoot + CorePath）；解析失敗回 null。
        static string TryResolveSkillsRoot()
        {
            string corePathRel = UCL_EditorPath.CorePath;
            if (string.IsNullOrEmpty(corePathRel)) return null;
            string root = Path.Combine(Path.GetFullPath(Path.Combine(UCL_RepoPath.UnityProjectRoot, corePathRel)), "Skills~");
            return Directory.Exists(root) ? root : null;
        }

        // 區塊職責：列舉合法 skill 目錄（與 BuildSkillRowCache 同規則，抽 helper 防兩處漂移）。
        static IEnumerable<string> EnumerateSkillDirs(string skillsRoot)
        {
            foreach (var dir in Directory.GetDirectories(skillsRoot))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith("_") || name.EndsWith("~") || name.StartsWith(".")) continue;
                yield return dir;
            }
        }

        // 區塊職責：算單一 skill 目錄的 content hash。
        // 物理意義：檔案列舉後以 Ordinal 顯式排序 — Directory.GetFiles 順序跨檔案系統不保證，
        //          不排序 = 同內容不同機器不同 hash、快照永遠 miss（summit 血證）。逐檔餵
        //          「相對路徑 + '\0' + 正規化內文」：路徑入 hash 讓改名/搬移測得到（apex-one）；
        //          內文走 ReadAllText（吃掉 BOM）+ \r\n 與孤立 \r 都摺成 \n，防 autocrlf /
        //          平台切換造成 hash 假變動。隱藏檔（. 開頭）不入 hash。
        // 數值影響：回傳 MD5 hex 前 12 chars（非密碼學用途，12 chars 對 31 個 skill 碰撞機率可忽略）。
        static string ComputeSkillHash(string skillDir)
        {
            using var md5 = MD5.Create();
            var files = Directory.GetFiles(skillDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(skillDir, f).Replace('\\', '/'))
                .Where(rel => !Path.GetFileName(rel).StartsWith("."))
                .OrderBy(rel => rel, StringComparer.Ordinal)
                .ToList();
            var sb = new StringBuilder();
            foreach (var rel in files)
            {
                string content = File.ReadAllText(Path.Combine(skillDir, rel))
                    .Replace("\r\n", "\n").Replace('\r', '\n');
                sb.Append(rel).Append('\0').Append(content).Append('\0');
            }
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            var hex = new StringBuilder(12);
            for (int i = 0; i < 6; i++) hex.Append(hash[i].ToString("x2"));
            return hex.ToString();
        }

        // 區塊職責：算全部 skill 的快照字串 "name=hash;..."（依名稱 Ordinal 排序，格式穩定可直接字串比對）。
        // 數值影響：Skills~ 解析失敗回 null（呼叫端決定 fallback）。
        static string ComputeSkillHashSnapshot()
        {
            string skillsRoot = TryResolveSkillsRoot();
            if (skillsRoot == null) return null;
            return string.Join(";",
                EnumerateSkillDirs(skillsRoot)
                    .Select(dir => $"{Path.GetFileName(dir)}={ComputeSkillHash(dir)}")
                    .OrderBy(s => s, StringComparer.Ordinal));
        }

        // 區塊職責：解析快照字串成 name→hash map（容錯：空字串 → 空 map）。
        static Dictionary<string, string> ParseSnapshot(string snapshot)
        {
            var map = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(snapshot)) return map;
            foreach (var entry in snapshot.Split(';'))
            {
                int eq = entry.IndexOf('=');
                if (eq > 0) map[entry.Substring(0, eq)] = entry.Substring(eq + 1);
            }
            return map;
        }

        // 區塊職責：diff 兩份快照 → 人類可讀變動清單（+新增 / ~變更 / -移除 三類都列，summit 提醒）。
        static List<string> DiffSnapshots(string cachedSnapshot, string currentSnapshot)
        {
            var cached = ParseSnapshot(cachedSnapshot);
            var current = ParseSnapshot(currentSnapshot);
            var changes = new List<string>();
            foreach (var kv in current)
            {
                if (!cached.TryGetValue(kv.Key, out var oldHash)) changes.Add($"+{kv.Key}");
                else if (oldHash != kv.Value) changes.Add($"~{kv.Key}");
            }
            foreach (var name in cached.Keys)
            {
                if (!current.ContainsKey(name)) changes.Add($"-{name}");
            }
            changes.Sort(StringComparer.Ordinal);
            return changes;
        }

        /// <summary>
        /// 「開 Welcome / EditorMenu → 自動把本頁 push 到頂」用的判斷 + 執行入口。
        /// 由 <see cref="UCL_WelcomePage"/> 的 ContentOnGUI 在首幀呼叫。
        /// 判定：勾過「永不自動彈」→ 不彈；初次（無 hash 快照）→ 無條件彈；
        /// 之後只有 Skills~ 源 hash 快照有 diff 才彈，彈窗當下即覆寫快照。
        /// </summary>
        /// <returns>true = 有排程彈；false = 已 opt-out / 無變動 / 本 session 已彈過，沒彈</returns>
        public static bool MaybeAutoPopupOnWelcome(UCL_GUIPageController controller)
        {
            // opt-out 逃生門：勾過「永不自動彈」→ 即使 skill 有更新也不彈（頁內 Matrix 仍看得到狀態）
            string acked = EditorPrefs.GetString(PrefKey_Acknowledged, "");
            if (acked == CurrentAcknowledgeVersion) return false;
            if (s_AutoPoppedThisSession) return false;

            string cached = EditorPrefs.GetString(PrefKey_SkillHashes, "");
            string current = ComputeSkillHashSnapshot();
            bool firstTime = string.IsNullOrEmpty(cached);
            if (firstTime)
            {
                // 初次：無條件彈（首曝語意）。Skills~ 解析失敗時快照不寫，下次成功解析再簽收。
                if (current != null) EditorPrefs.SetString(PrefKey_SkillHashes, current);
            }
            else
            {
                // 解析失敗算不出快照 → 無從判定變動，不彈（避免 NoUCLCore 場景每次騷擾）
                if (current == null) return false;
                var changes = DiffSnapshots(cached, current);
                if (changes.Count == 0) return false;
                // 彈窗即簽收：覆寫快照 + 留存變動清單供秒關的人事後在頁內查看（summit 緩衝方案）
                EditorPrefs.SetString(PrefKey_SkillHashes, current);
                EditorPrefs.SetString(PrefKey_LastSkillChanges,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm} | {string.Join(", ", changes)}");
            }
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
        // 區塊職責：直接比對源資料夾與目標資料夾下所有檔案的內文，判斷是否完全相同以確認是否需要同步。
        // 物理意義：透過比對源 Skills~ 目錄與安裝後 .agents/skills 目錄下的檔案內容，判定 Skill 是否有被修改（drift/stale）。
        //          在 Windows 與 Unix 混用的環境下，新行字元可能會有 \r\n 與 \n 的差異，此處將進行正規化處理，防止因為格式而誤判為 stale。
        // 數值影響：若有任何檔案缺失或內文不符，回傳 false（判定為 drift 狀態，顯示需要 Sync）；若全部一致則回傳 true。
        bool SkillContentMatches(string srcDir, string instDir, AgentTarget target)
        {
            // 遍歷來源 Skill 目錄下的所有檔案，包括所有子資料夾的檔案
            foreach (var srcFile in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                // 計算當前來源檔案相對於來源目錄的相對路徑，以對應到目標目錄中的檔案
                string rel = Path.GetRelativePath(srcDir, srcFile);
                // 組合出目標安裝目錄中的對應檔案絕對路徑
                string instFile = Path.Combine(instDir, rel);
                // 如果目標檔案在安裝目錄中根本不存在，代表該 Skill 狀態不完整，回傳 false 表示內容不匹配
                if (!File.Exists(instFile)) return false;
                
                // Antigravity 的 SKILL.md：兩邊都剝掉 trigger: 行再比，只比作者內容。
                //   - install 衍生注入的 trigger: 只在已裝端 → 剝已裝端。
                //   - 作者顯式宣告的 trigger: 源端與已裝端都有(原樣複製) → 兩邊都剝才不誤判 drift。
                // trigger 值由 install 端自動衍生/作者宣告，Editor 端一律不重現、不依賴其值。
                bool stripTrigger = (target == AgentTarget.Antigravity && Path.GetFileName(srcFile) == "SKILL.md");
                string expected = stripTrigger ? StripInjectedTriggerLine(File.ReadAllText(srcFile)) : File.ReadAllText(srcFile);
                string actual = stripTrigger ? StripInjectedTriggerLine(File.ReadAllText(instFile)) : File.ReadAllText(instFile);

                // 比對預期內文與實際內文（\r\n 視同 \n），若不相符則判定內容已改變，回傳 false
                if (!ContentEqualsNewlineInsensitive(expected, actual)) return false;
            }
            // 若所有檔案皆存在且內容比對完全一致，則回傳 true，表示檔案完全同步
            return true;
        }

        // 區塊職責：換行不敏感的內文等價比對 — \r\n 視同 \n，取代舊的 Replace("\r\n","\n") 正規化副本。
        // 物理意義：先走 ordinal == fast path（無 drift 且換行同款的常見情況命中向量化比對，零配置）；
        //          不相等才 fallback 雙指針逐字元掃描，遇 \r\n 摺疊成 \n 再比。孤立 \r（無後隨 \n）維持
        //          嚴格比對，語意與舊 Replace 版完全一致。
        // 數值影響：省去最多 2 份全檔字串副本與重複掃描；首個實質差異即返回 false。判定結果與舊版相同。
        static bool ContentEqualsNewlineInsensitive(string a, string b)
        {
            // fast path：完全相同（含換行）直接命中，走 .NET 向量化 ordinal 比對
            if (a == b) return true;
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                char ca = a[i], cb = b[j];
                // \r 後隨 \n 時摺疊成 \n（跳過 \r），等價於舊版的 \r\n → \n 正規化
                if (ca == '\r' && i + 1 < a.Length && a[i + 1] == '\n') { i++; ca = '\n'; }
                if (cb == '\r' && j + 1 < b.Length && b[j + 1] == '\n') { j++; cb = '\n'; }
                if (ca != cb) return false;
                i++; j++;
            }
            // 兩邊必須同時耗盡才算相等，避免一邊是另一邊的前綴時誤判
            return i == a.Length && j == b.Length;
        }

        // 區塊職責：剝除 install_skills.py 注入的 trigger: frontmatter 行（Antigravity drift 比對用）。
        // 物理意義：Tim 2026-07-26 拍板 C 方案後，trigger 值由 install 端從 SKILL.md 描述「觸發詞」行
        //          / 顯式 on_intent 欄「自動衍生」（Claude 式自動發現），不再有 per-skill 硬編碼 map。
        //          → Editor 端不必、也不該重現該衍生值（否則又要跟 install_skills.py 手動同步，正是痛點）。
        //          改成比對時把「install 注入的 trigger: 行」從已裝內文剝掉，只比作者原始內容 →
        //          drift 判定不依賴 trigger 值，install 端怎麼改觸發詞衍生規則，這裡都不用動。
        // 數值影響：只影響 Antigravity target 的 SKILL.md 比對；剝掉 frontmatter 內以 trigger: 起首的行。
        static string StripInjectedTriggerLine(string content)
        {
            if (!content.StartsWith("---")) return content;
            string[] parts = content.Split(new[] { "---" }, 3, StringSplitOptions.None);
            if (parts.Length < 3) return content;
            var kept = new System.Collections.Generic.List<string>();
            bool swallowBlank = false;   // 舊版注入在 trigger: 行後多留一空行 → 一併吞掉, 讓既有已裝檔不重裝也對得上
            foreach (var ln in parts[1].Split('\n'))
            {
                if (ln.TrimStart().StartsWith("trigger:")) { swallowBlank = true; continue; } // 剝掉 install 注入的 trigger: 行
                if (swallowBlank && ln.Trim().Length == 0) { swallowBlank = false; continue; } // 吞掉緊接的殘留空行(舊注入 artifact)
                swallowBlank = false;
                kept.Add(ln);
            }
            return "---" + string.Join("\n", kept) + "---" + parts[2];
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
                // 上次自動彈窗的變動清單 — 彈窗即簽收的緩衝：秒關彈窗的人事後在這裡看得到是哪些 skill 變了
                string lastChanges = EditorPrefs.GetString(PrefKey_LastSkillChanges, "");
                if (!string.IsNullOrEmpty(lastChanges))
                {
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("AgentSkill.LastChangesLabel"), lastChanges), WrapLabelStyle);
                }
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
