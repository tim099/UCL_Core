// 區塊職責：自動 Commit 頁 — 把 AgentCommands 裡「機器自動生成的檔」分群、一鍵各自成 commit
// 物理意義：Treasury 帳本、酒館訊息、inbox cursor、bartender state 這類檔案整天在長，
//          人工 commit 的成本是「分類」不是「打字」—— 本頁把分類規則寫死成程式碼，
//          按鈕觸發、訊息自動生成。**不是背景全自動**（Tim 2026-08-07 拍板：按鈕觸發、訊息自動化），
//          按下去之前分群結果與檔案清單全部攤在畫面上。
// 數值影響：commit 只寫本層 repo 的 history（不 push、不動父層 pointer）。
//          掃描唯讀。ephemeral 檔（log / wait 旗標 / 臨時渲染）永遠不進候選。
//
// 設計決策（2026-08-07）：
//   · **走純 git commit，不走 git_commit.py** —— 那支工具的 trailer / 酒館公告 / 領薪
//     是給「有作者的工作產出」用的；本頁提交的是機器生成的狀態殘渣，掛誰的名字領誰的薪
//     都是假帳。agent 自己的工作 commit 照舊走 ucl-commit skill，兩條路不混。
//   · 分群規則寫在程式碼（GroupDefs）不開放 UI 編輯 —— 規則是專案慣例的一部分
//     （[chat] 獨立 commit 是 CLAUDE.md 等級的硬規則），能在 UI 亂改的規則等於沒有規則。
//   · 巢狀 submodule 的 pointer 變更獨立一群、**預設不勾** —— 那些 pointer 指向別人
//     （其他 persona 的信件庫）的未推 commit，bump 了別人 pull 會拿到拿不到的 hash。
//   · 未分類檔獨立一群、**預設不勾** —— 分類規則沒認出來的檔不該被「自動」二字順手帶走。
//   · stage 分批餵（每批 CHUNK 個路徑）—— Windows 命令列長度上限 32k，訊息檔一天數百顆。
// RequiresConstantRepaint：git 在背景跑，進度與報告要即時反映。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.Page;
using UCL.Core.StringExtensionMethods;   // CopyToClipboard —— 既有基建
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.Page
{
    [UCL.Core.ATTR.RequiresConstantRepaint]
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_AutoCommitPage.md")]
    public class UCL_AutoCommitPage : UCL_CommonEditorPage
    {
        public override string WindowName => "UCL_AutoCommit";
        public override bool ShowInPageMenu => true;

        const string PrefKey_Settings = "UCL_AutoCommit.Settings";
        const string PROC_TAG = "auto_commit_git";
        const int GIT_TIMEOUT_MS = 2 * 60 * 1000;   // 全程本地操作，2 分鐘已是異常
        const int CHUNK = 40;                        // 每批 git add 的路徑數（防命令列超長）

        // 區塊職責：分群規則（順序即優先序，第一個命中的收走）
        // 物理意義：Match 吃「相對 repo root 的正斜線路徑」。規則刻意用前綴不用 regex ——
        //          這裡的錯配是「檔進錯 commit」等級，規則要一眼能驗證。
        class GroupDef
        {
            public string Key;
            public string Label;
            public Func<string, bool> Match;
            public string Message;       // commit 訊息主體（檔數統計由程式補在後面）
            public bool DefaultOn;
        }

        static readonly GroupDef[] GroupDefs =
        {
            new GroupDef
            {
                Key = "chat",
                Label = "酒館訊息（[chat] 獨立 commit — 硬規則）",
                Match = p => p.StartsWith("ChatTavern/rooms/"),
                Message = "[chat] sync tavern messages & inbox (auto)",
                DefaultOn = true,
            },
            new GroupDef
            {
                Key = "treasury",
                Label = "Treasury（帳本 / 帳戶）",
                Match = p => p.StartsWith("Treasury/"),
                Message = "chore(treasury): sync ledger & account state (auto)",
                DefaultOn = true,
            },
            new GroupDef
            {
                Key = "runtime",
                Label = "Agent runtime state（cursor / bartender / persona / canvas…）",
                Match = p => p.StartsWith("ChatTavern/") || p.StartsWith("AwakenInit/")
                             || p.StartsWith("Canvas/") || p.StartsWith("Inbox/"),
                Message = "chore(runtime): sync agent runtime state (auto)",
                DefaultOn = true,
            },
        };

        // ephemeral —— 永遠不進候選（分類矩陣：*.log / wait 旗標 / 臨時渲染 / DebugLogs，
        // 見 ucl-commit skill 的檔案分類）。pending.trigger / *.tmp 是 Cmd queue 的瞬時檔。
        static bool IsEphemeral(string path)
        {
            string name = path;
            int slash = path.LastIndexOf('/');
            if (slash >= 0) name = path.Substring(slash + 1);
            if (name.EndsWith(".log") || name.EndsWith(".tmp")) return true;
            if (name == "_last_op.md" || name == "_last_view.md"
                || name == "_active_waits.json" || name == "pending.trigger") return true;
            if (name.StartsWith("_wait_")) return true;
            if (path.StartsWith("DebugLogs/") || path.Contains("/DebugLogs/")) return true;
            return false;
        }

        [Serializable]
        public class PageSettings
        {
            public string Root = "";
            // 記「被關掉的群組」而不是「被打開的」—— 新增群組時舊設定不會把它靜默關掉
            public List<string> DisabledGroups = new List<string>();
        }

        PageSettings m_Settings = new PageSettings();

        // 掃描結果：群組 key → 檔案清單（含 submodule pointer 與未分類兩個特殊群）
        const string KEY_SUBPTR = "__subptr";
        const string KEY_OTHER = "__other";
        Dictionary<string, List<string>> m_Groups = new Dictionary<string, List<string>>();
        List<string> m_EphemeralSkipped = new List<string>();
        bool m_Scanned = false;
        // 折疊狀態 —— 獨立 dict，⚠ 不與 PopupSearchCache 共用（資料重載 Clear 會吃掉折疊值）
        readonly Dictionary<string, bool> m_Fold = new Dictionary<string, bool>();

        string m_Report = "";
        bool m_Running = false;
        string m_RunningLabel = "";
        Vector2 m_ReportScroll = Vector2.zero;

        const double COPY_HINT_SECONDS = 3.0;
        string m_CopyHint = "";
        double m_CopyHintAt = 0;

        GUIStyle m_MonoStyle;
        GUIStyle MonoStyle => m_MonoStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            wordWrap = false,
            richText = false,
        };

        GUIStyle m_DimLabelStyle;
        GUIStyle DimLabelStyle => m_DimLabelStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
        };

        public static UCL_AutoCommitPage Create() => UCL_EditorPage.Create<UCL_AutoCommitPage>();

        public override void Init(UCL_GUIPageController p_Controller)
        {
            base.Init(p_Controller);
            LoadSettings();
            Scan();
        }

        // ===========================================================
        // 設定持久化
        // ===========================================================
        void LoadSettings()
        {
            try
            {
                string json = UCL_ProjectEditorPrefs.GetString(PrefKey_Settings, "");
                if (!string.IsNullOrEmpty(json))
                {
                    var s = JsonUtility.FromJson<PageSettings>(json);
                    if (s != null) m_Settings = s;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AutoCommit] 設定讀取失敗，改用預設值: {e.Message}");
            }
            m_Settings.DisabledGroups ??= new List<string>();
            if (string.IsNullOrEmpty(m_Settings.Root))
            {
                m_Settings.Root = UCL_RepoPath.AgentCommandsDir;
            }
        }

        void SaveSettings()
        {
            try
            {
                UCL_ProjectEditorPrefs.SetString(PrefKey_Settings, JsonUtility.ToJson(m_Settings));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AutoCommit] 設定保存失敗: {e.Message}");
            }
        }

        bool GroupEnabled(string key, bool defaultOn)
        {
            if (m_Settings.DisabledGroups.Contains(key)) return false;
            // 特殊群（submodule pointer / 未分類）預設不勾，且**不持久化「打開」**——
            // 它們每次的內容都不同，上次的授權不該自動延續到這次（預設值是裝填好的槍）。
            return defaultOn;
        }

        // session 內的一次性勾選（見 GroupEnabled —— 特殊群不持久化）
        readonly HashSet<string> m_SessionOn = new HashSet<string>();

        bool IsGroupOn(string key)
        {
            var def = Array.Find(GroupDefs, g => g.Key == key);
            if (def != null) return GroupEnabled(key, def.DefaultOn);
            return m_SessionOn.Contains(key);
        }

        void SetGroupOn(string key, bool on)
        {
            var def = Array.Find(GroupDefs, g => g.Key == key);
            if (def != null)
            {
                if (on) m_Settings.DisabledGroups.Remove(key);
                else if (!m_Settings.DisabledGroups.Contains(key)) m_Settings.DisabledGroups.Add(key);
                SaveSettings();
            }
            else
            {
                if (on) m_SessionOn.Add(key);
                else m_SessionOn.Remove(key);
            }
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            using (new EditorGUI.DisabledScope(m_Running))
            {
                if (GUILayout.Button("重新掃描", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    Scan();
                }
            }
        }

        protected override void ContentOnGUI()
        {
            DrawSettingsPanel();
            DrawGroups();
            DrawActions();
            DrawReport();
        }

        void DrawSettingsPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Repo 根目錄", UCL_GUIStyle.LabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    string next = GUILayout.TextField(m_Settings.Root ?? "", UCL_GUIStyle.TextFieldStyle);
                    if (next != m_Settings.Root)
                    {
                        m_Settings.Root = next;
                        SaveSettings();
                    }
                    if (GUILayout.Button("…", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        string picked = EditorUtility.OpenFolderPanel("Repo 根目錄", m_Settings.Root ?? "", "");
                        if (!string.IsNullOrEmpty(picked))
                        {
                            m_Settings.Root = picked.Replace('/', Path.DirectorySeparatorChar);
                            SaveSettings();
                            GUI.FocusControl(null);
                        }
                    }
                    if (GUILayout.Button("AgentCommands", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_Settings.Root = UCL_RepoPath.AgentCommandsDir;
                        SaveSettings();
                        GUI.FocusControl(null);
                    }
                }
                GUILayout.Label("按鈕觸發、訊息自動生成 —— 本頁只 commit 本層（不 push、不 bump 父層）。"
                                + "工作產出的 commit 別走這裡（那要 trailer 與領薪，走 ucl-commit）。",
                    DimLabelStyle);
            }
        }

        // ===========================================================
        // 群組顯示
        // ===========================================================
        void DrawGroups()
        {
            if (!m_Scanned)
            {
                GUILayout.Label("（尚未掃描 —— 按上方「重新掃描」）", UCL_GUIStyle.LabelStyle);
                return;
            }
            int total = 0;
            foreach (var kv in m_Groups) total += kv.Value.Count;
            if (total == 0)
            {
                GUILayout.Label("✓ 工作樹乾淨（ephemeral 除外）—— 沒有可 commit 的自動生成檔",
                    UCL_GUIStyle.LabelStyle);
                if (m_EphemeralSkipped.Count > 0)
                {
                    GUILayout.Label($"　（另有 {m_EphemeralSkipped.Count} 個 ephemeral 檔被排除，不進 commit）",
                        DimLabelStyle);
                }
                return;
            }

            foreach (var def in GroupDefs)
            {
                DrawGroup(def.Key, def.Label, def.Message);
            }
            DrawGroup(KEY_SUBPTR, "巢狀 submodule pointer（⚠ 指向別人的信件庫 —— 確認對方已 push 再勾）",
                "chore(submodule): bump nested submodule pointers (auto)");
            DrawGroup(KEY_OTHER, "未分類（分群規則沒認出來的檔 —— 逐一看過再勾）",
                "chore(misc): sync unclassified changes (auto)");

            if (m_EphemeralSkipped.Count > 0)
            {
                GUILayout.Label($"🚫 ephemeral 已排除 {m_EphemeralSkipped.Count} 個"
                                + "（log / wait 旗標 / _last_op / DebugLogs…）—— 永遠不進 commit",
                    DimLabelStyle);
            }
        }

        void DrawGroup(string key, string label, string message)
        {
            if (!m_Groups.TryGetValue(key, out var files) || files.Count == 0) return;
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    bool on = IsGroupOn(key);
                    bool next = GUILayout.Toggle(on, "", GUILayout.Width(UCL_GUIStyle.GetScaledSize(20)));
                    if (next != on) SetGroupOn(key, next);
                    GUILayout.Label($"{label}　({files.Count} 檔)",
                        on ? UCL_GUIStyle.LabelStyle : DimLabelStyle);
                    GUILayout.FlexibleSpace();
                    bool fold = m_Fold.TryGetValue(key, out var f) && f;
                    if (GUILayout.Button(fold ? "收合" : "展開", UCL_GUIStyle.ButtonStyle,
                            GUILayout.ExpandWidth(false)))
                    {
                        m_Fold[key] = !fold;
                    }
                }
                GUILayout.Label($"　訊息: {message} [{files.Count} files]", DimLabelStyle);
                if (m_Fold.TryGetValue(key, out var open) && open)
                {
                    // 全列 —— 「自動」的前提是按之前看得到它要帶走什麼；
                    // 只列前 N 顆的清單對「未分類」那群等於沒列。
                    foreach (var f in files)
                    {
                        GUILayout.Label($"　　{f}", DimLabelStyle);
                    }
                }
            }
        }

        // ===========================================================
        // 動作
        // ===========================================================
        void DrawActions()
        {
            if (!m_Scanned) return;
            var pending = PendingGroups();
            using (new GUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(m_Running || pending.Count == 0))
                {
                    if (GUILayout.Button($"Commit 勾選群組（{pending.Count} 筆 commit）",
                            UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.6f, 0.35f)),
                            GUILayout.ExpandWidth(false)))
                    {
                        ConfirmAndCommit(pending);
                    }
                }
                if (m_Running)
                {
                    GUILayout.Label($"⏳ 執行中（{m_RunningLabel}）", UCL_GUIStyle.LabelStyle);
                }
                GUILayout.FlexibleSpace();
            }
        }

        List<(string key, string message, List<string> files)> PendingGroups()
        {
            var o = new List<(string, string, List<string>)>();
            foreach (var def in GroupDefs)
            {
                if (IsGroupOn(def.Key) && m_Groups.TryGetValue(def.Key, out var fs) && fs.Count > 0)
                {
                    o.Add((def.Key, def.Message, fs));
                }
            }
            if (IsGroupOn(KEY_SUBPTR) && m_Groups.TryGetValue(KEY_SUBPTR, out var sp) && sp.Count > 0)
            {
                o.Add((KEY_SUBPTR, "chore(submodule): bump nested submodule pointers (auto)", sp));
            }
            if (IsGroupOn(KEY_OTHER) && m_Groups.TryGetValue(KEY_OTHER, out var ot) && ot.Count > 0)
            {
                o.Add((KEY_OTHER, "chore(misc): sync unclassified changes (auto)", ot));
            }
            return o;
        }

        void ConfirmAndCommit(List<(string key, string message, List<string> files)> pending)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Repo: {m_Settings.Root}");
            sb.AppendLine($"將建立 {pending.Count} 筆 commit（每群一筆，具名 stage、不 git add -A）：");
            foreach (var (_, msg, files) in pending)
            {
                sb.AppendLine($"　· {msg} [{files.Count} files]");
            }
            sb.AppendLine("\n只 commit 本層 —— 不 push、不動父層 pointer。");
            UCL_OptionPage.Create("確認自動 Commit？", sb.ToString(),
                new ButtonData("Commit", () => RunCommits(pending),
                    UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.5f, 0.3f))),
                new ButtonData("取消"));
        }

        void DrawReport()
        {
            if (string.IsNullOrEmpty(m_Report)) return;
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("報告", UCL_GUIStyle.LabelStyle);
                if (GUILayout.Button("複製", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    m_Report.CopyToClipboard();
                    // 讀回來才算數 —— systemCopyBuffer 被鎖住時靜默失敗，成功與失敗長得一樣
                    m_CopyHint = GUIUtility.systemCopyBuffer == m_Report
                        ? $"✓ 已複製 {m_Report.Length} 字元"
                        : "✗ 複製失敗（剪貼簿被占用？）";
                    m_CopyHintAt = EditorApplication.timeSinceStartup;
                }
                if (!string.IsNullOrEmpty(m_CopyHint)
                    && EditorApplication.timeSinceStartup - m_CopyHintAt < COPY_HINT_SECONDS)
                {
                    GUILayout.Label(m_CopyHint, UCL_GUIStyle.LabelStyle);
                }
                GUILayout.FlexibleSpace();
            }
            using (var sv = new GUILayout.ScrollViewScope(m_ReportScroll,
                       GUILayout.MinHeight(UCL_GUIStyle.GetScaledSize(200))))
            {
                m_ReportScroll = sv.scrollPosition;
                EditorGUILayout.TextArea(m_Report, MonoStyle);
            }
        }

        // ===========================================================
        // git（背景執行緒）
        // ===========================================================
        // root 由呼叫端快照傳入 —— 背景執行緒跑到一半時 Root 欄位仍可被編輯，
        // 邊跑邊讀 m_Settings 會讓同一輪操作前後打到不同的 repo。
        static (int exit, string stdout, string stderr) Git(string root, string args)
            => UCL_GitCli.Run(root, args, PROC_TAG, nameof(UCL_AutoCommitPage), GIT_TIMEOUT_MS);

        // ===========================================================
        // 掃描
        // ===========================================================
        // 區塊職責：git status → 依 GroupDefs 分群
        // 物理意義：-uall 展開 untracked 目錄成逐檔（訊息檔整天在新增，目錄縮寫會讓
        //          檔數統計與清單都失真）。submodule pointer 變更用 `submodule status`
        //          的路徑集合辨識，與檔案群分開。
        // quiet=true：不覆寫 m_Report（commit 完的自動重掃別蓋掉 commit 報告）
        void Scan(bool quiet = false)
        {
            if (m_Running)
            {
                Debug.LogWarning($"[AutoCommit] 已有操作進行中（{m_RunningLabel}）— 忽略掃描");
                return;
            }
            string root = m_Settings.Root;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                m_Report = $"✗ Repo 根目錄不存在: {root}";
                return;
            }
            m_Running = true;
            m_RunningLabel = "掃描";
            string rootSnap = root;
            System.Threading.Tasks.Task.Run(() =>
            {
                var groups = new Dictionary<string, List<string>>();
                var ephemeral = new List<string>();
                var log = new System.Text.StringBuilder();
                try
                {
                    UCL_ProcessRegistryService.KillAllByTag(PROC_TAG);
                    // submodule 路徑集合 —— pointer 變更要跟一般檔案分開治理
                    var subPaths = new HashSet<string>();
                    var (es, os, _) = Git(rootSnap, "submodule status");
                    if (es == 0)
                    {
                        foreach (var raw in os.Split('\n'))
                        {
                            string line = raw.TrimEnd();
                            if (line.Length < 42) continue;
                            var parts = line.Substring(1).Split(' ');
                            if (parts.Length >= 2) subPaths.Add(parts[1]);
                        }
                    }
                    var (e, o, se2) = Git(rootSnap, "status --porcelain -uall");
                    if (e != 0)
                    {
                        log.AppendLine($"✗ git status 失敗 (exit {e})\n{se2}");
                    }
                    else
                    {
                        foreach (var raw in o.Split('\n'))
                        {
                            if (raw.Length < 4) continue;
                            string path = raw.Substring(3).Trim();
                            // rename 行是 "old -> new"，兩邊都要 stage（add 會同時記錄刪與增）
                            int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                            if (arrow >= 0) path = path.Substring(arrow + 4);
                            if (path.StartsWith("\"") && path.EndsWith("\"") && path.Length >= 2)
                            {
                                path = path.Substring(1, path.Length - 2);   // porcelain 對非 ASCII 檔名加引號
                            }
                            if (string.IsNullOrEmpty(path)) continue;

                            string key;
                            if (subPaths.Contains(path)) key = KEY_SUBPTR;
                            else if (IsEphemeral(path)) { ephemeral.Add(path); continue; }
                            else
                            {
                                key = KEY_OTHER;
                                foreach (var def in GroupDefs)
                                {
                                    if (def.Match(path)) { key = def.Key; break; }
                                }
                            }
                            if (!groups.TryGetValue(key, out var list))
                            {
                                groups[key] = list = new List<string>();
                            }
                            list.Add(path);
                        }
                        int total = 0;
                        foreach (var kv in groups) total += kv.Value.Count;
                        log.AppendLine($"✓ 掃描完成：{total} 檔進候選、{ephemeral.Count} 檔 ephemeral 排除");
                    }
                }
                catch (Exception ex)
                {
                    log.AppendLine(ex.ToString());
                }
                EditorApplication.delayCall += () =>
                {
                    m_Running = false;
                    m_RunningLabel = "";
                    m_Groups = groups;
                    m_EphemeralSkipped = ephemeral;
                    m_Scanned = true;
                    if (!quiet) m_Report = log.ToString();
                };
            });
        }

        // ===========================================================
        // 批次 commit
        // ===========================================================
        // 區塊職責：每群一筆 commit —— 具名 stage（分批）→ commit → 記 SHA
        // 物理意義：stage 用 `git add -- <files>` 逐批餵，**絕不 git add -A**
        //          （別人正在寫的檔會被一起帶走，而那不會有錯誤訊息）。
        //          訊息用 -F 檔案餵 —— 訊息含統計行，走 argv 會踩引號 / 長度的坑
        //          （Bash 反引號雙殺的 C# 版本：判準不是「含不含特殊字元」，是長文一律走檔案）。
        void RunCommits(List<(string key, string message, List<string> files)> pending)
        {
            if (m_Running)
            {
                Debug.LogWarning($"[AutoCommit] 已有操作進行中（{m_RunningLabel}）— 忽略");
                return;
            }
            string root = m_Settings.Root;
            m_Running = true;
            m_RunningLabel = "commit";
            m_Report = "⏳ commit 執行中…";
            System.Threading.Tasks.Task.Run(() =>
            {
                var log = new System.Text.StringBuilder();
                int ok = 0, fail = 0;
                try
                {
                    UCL_ProcessRegistryService.KillAllByTag(PROC_TAG);
                    foreach (var (key, message, files) in pending)
                    {
                        // 逐批 stage
                        bool stageFailed = false;
                        for (int i = 0; i < files.Count && !stageFailed; i += CHUNK)
                        {
                            var batch = files.GetRange(i, Math.Min(CHUNK, files.Count - i));
                            var quoted = batch.ConvertAll(f => "\"" + f + "\"");
                            var (ea, _, sa) = Git(root, "add -- " + string.Join(" ", quoted));
                            if (ea != 0)
                            {
                                log.AppendLine($"✗ [{key}] stage 失敗: {sa}");
                                stageFailed = true;
                            }
                        }
                        if (stageFailed)
                        {
                            // 這一群 stage 到一半 —— 退掉，別讓殘留的 staged 檔混進下一群的 commit
                            Git(root, "reset");
                            fail++;
                            continue;
                        }
                        // staged 是空的就跳過（例如檔案在掃描後被還原）—— commit 空樹會失敗且訊息難懂
                        var (ed, od, _) = Git(root, "diff --cached --name-only");
                        if (ed == 0 && string.IsNullOrEmpty(od.Trim()))
                        {
                            log.AppendLine($"⏭ [{key}] 掃描後已無變更 —— 跳過");
                            continue;
                        }
                        // 訊息走暫存檔（-F），不走 argv
                        string msgFile = Path.Combine(Path.GetTempPath(), $"ucl_autocommit_{key}.txt");
                        File.WriteAllText(msgFile,
                            $"{message} [{files.Count} files]\n", new System.Text.UTF8Encoding(false));
                        var (ec, _, sc) = Git(root, $"commit -F \"{msgFile}\"");
                        try { File.Delete(msgFile); } catch { /* 暫存檔清不掉不影響結果 */ }
                        if (ec != 0)
                        {
                            log.AppendLine($"✗ [{key}] commit 失敗: {sc}");
                            Git(root, "reset");
                            fail++;
                            continue;
                        }
                        var (eh, oh, _) = Git(root, "rev-parse --short HEAD");
                        log.AppendLine($"✓ [{key}] {(eh == 0 ? oh.Trim() : "?")} — {message} [{files.Count} files]");
                        ok++;
                    }
                    log.AppendLine($"\n— 完成：✓{ok} ✗{fail} —— 未 push、父層 pointer 未動 —");
                }
                catch (Exception ex)
                {
                    log.AppendLine(ex.ToString());
                }
                EditorApplication.delayCall += () =>
                {
                    m_Running = false;
                    m_RunningLabel = "";
                    m_Report = log.ToString();
                    // 特殊群的一次性授權用完即棄（同 ForceOnce 的語意：「這一次我認了」）
                    m_SessionOn.Clear();
                    Scan(quiet: true);
                };
            });
        }
    }
}
#endif
