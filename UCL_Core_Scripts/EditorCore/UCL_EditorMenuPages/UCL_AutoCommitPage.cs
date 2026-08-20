// 區塊職責：自動 Commit 頁 — 把「機器自動生成的檔」分群、一鍵各自成 commit
// 物理意義：兩種掃描對象，同一套「分群→勾選→每群一筆 commit」機制：
//          ① **AgentCommands 本層**：Treasury 帳本、酒館訊息、inbox cursor、bartender state
//             這類檔案整天在長，人工 commit 的成本是「分類」不是「打字」。
//          ② **Persona 信件庫**（`letters/<persona>/`，各自是一個巢狀 submodule）：
//             收尾 commit 之後才落地的**系統信**（`mailbox/`）與**別人投遞的畫像**
//             （`portraits/`）—— 落地時該 persona 已經下線，沒有人會 commit 它們，
//             於是它們一路躺到下次醒來才被順手掃進某一筆不相干的 commit 裡。
//          分類規則寫死成程式碼，按鈕觸發、訊息自動生成。**不是背景全自動**
//          （Tim 2026-08-07 拍板：按鈕觸發、訊息自動化），按下去之前分群結果與檔案清單全攤在畫面上。
// 數值影響：commit 只寫該 repo 自己的 history（不 push、不動父層 pointer）。
//          掃描唯讀。ephemeral 檔（log / wait 旗標 / 臨時渲染）永遠不進候選。
//
// 設計決策（2026-08-07）：
//   · **走純 git commit，不走 git_commit.py** —— 那支工具的 trailer / 酒館公告 / 領薪
//     是給「有作者的工作產出」用的；本頁提交的是機器生成的狀態殘渣，掛誰的名字領誰的薪
//     都是假帳。agent 自己的工作 commit 照舊走 ucl-commit skill，兩條路不混。
//   · 分群規則**已抽到 `UCL_AutoCommitRules`**（2026-08-20，Tim 要求 /ucl-commit 也能用自動 commit
//     ⇒ 出現第二個消費端 `Cmd_AutoCommit`）。本頁只保留「掃描 / 勾選 / 執行 git」那半。
//   · 分群規則寫在程式碼（GroupDefs）不開放 UI 編輯 —— 規則是專案慣例的一部分
//     （[chat] 獨立 commit 是 CLAUDE.md 等級的硬規則），能在 UI 亂改的規則等於沒有規則。
//   · 巢狀 submodule 的 pointer 變更獨立一群、**預設不勾** —— 那些 pointer 指向別人
//     （其他 persona 的信件庫）的未推 commit，bump 了別人 pull 會拿到拿不到的 hash。
//   · 未分類檔獨立一群、**預設不勾** —— 分類規則沒認出來的檔不該被「自動」二字順手帶走。
//   · stage 分批餵（每批 CHUNK 個路徑）—— Windows 命令列長度上限 32k，訊息檔一天數百顆。
//
// 設計決策（2026-08-19，persona 信件庫模式）：
//   · **只收「不是那個 persona 自己寫的」與「機械維護的」** —— 投遞件（mailbox/ portraits/）
//     與指標檔（`_latest.md` / `cmd/.gitignore`）。她自己寫的信、碎片、見叢、素描本
//     全部落到「未分類」群且**預設不勾** —— 有作者的產出要掛她的名字、走她的收尾 commit，
//     被別人的自動化順手帶走等於替她簽名。
//   · **在線的 persona 預設不勾**（判準是 lock 檔未過期，不是 registry 的 status 欄）——
//     她可能正在寫，而「動別人正在寫的東西」的後果不是衝突報錯，是靜默把工作清掉。
//     要勾得每次自己勾，**勾選不持久化、掃描一次用完即棄**（預設值是裝填好的槍）。
//   · **detached HEAD 的 repo 硬擋**（勾不動）—— 那裡 commit 出來的是游離 commit，
//     沒有分支指到它，下次 checkout 就只剩 reflog 找得到。與 ucl-commit skill 同一條規矩。
//   · 一樣**不 bump 父層 pointer** —— commit 完回 AgentCommands 模式重掃，
//     submodule pointer 那一群會出現（一次性勾選），bump 與否仍是人的決定。
// RequiresConstantRepaint：git 在背景跑，進度與報告要即時反映。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.EditorLib.AgentCommands;
using UCL.Core.Page;
using UCL.Core.StringExtensionMethods;   // CopyToClipboard —— 既有基建
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
// 分群規則已抽到 UCL_AutoCommitRules（單一真相源；Cmd_AutoCommit 共用）——
// 別名讓本頁既有的 `GroupDef` 寫法原樣可用，避免整頁改名生出無意義的 diff。
using GroupDef = UCL.Core.EditorLib.AgentCommands.UCL_AutoCommitRules.GroupDef;

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

        /// <summary>掃描對象。AgentCommands＝單一 repo；PersonaLetters＝letters/ 底下每個信件庫各一個 repo。</summary>
        public enum ScanMode
        {
            AgentCommands = 0,
            PersonaLetters = 1,
        }

        // 規則本體在 UCL_AutoCommitRules（本頁與 Cmd_AutoCommit 共用同一份）。
        // 🩸 為什麼不是各留一份：這種規則的錯配等級是「檔進錯 commit」，
        //   而兩份規則漂掉之後，兩邊各自看起來都正常。
        GroupDef[] CurrentGroupDefs
            => UCL_AutoCommitRules.Defs(m_Settings.Mode == ScanMode.PersonaLetters);

        // ephemeral 判定同樣共用（規則見 UCL_AutoCommitRules.IsEphemeral）。
        static bool IsEphemeral(string path) => UCL_AutoCommitRules.IsEphemeral(path);

        [Serializable]
        public class PageSettings
        {
            public string Root = "";
            public ScanMode Mode = ScanMode.AgentCommands;
            // 記「被關掉的群組」而不是「被打開的」—— 新增群組時舊設定不會把它靜默關掉
            public List<string> DisabledGroups = new List<string>();
        }

        PageSettings m_Settings = new PageSettings();

        // 區塊職責：一個 repo 的掃描結果
        // 物理意義：AgentCommands 模式永遠只有一筆；persona 模式一個信件庫一筆。
        //          Blocked 非空＝這個 repo 不可 commit（目前唯一來源是 detached HEAD）。
        class RepoScan
        {
            public string Root = "";      // 絕對路徑（git 工作目錄）
            public string Name = "";      // 顯示名（persona 名 / repo 目錄名）
            public string Branch = "";    // 追蹤中的分支名；空＝detached
            public bool Online;           // persona 在線（lock 未過期）—— 只有 persona 模式有意義
            public string Blocked = "";   // 非空＝擋下的理由
            public Dictionary<string, List<string>> Groups = new Dictionary<string, List<string>>();
            public int Total;             // 進候選的檔數（不含 ephemeral）
        }

        // 掃描結果：群組 key → 檔案清單（含 submodule pointer 與未分類兩個特殊群）
        const string KEY_SUBPTR = UCL_AutoCommitRules.KEY_SUBPTR;
        const string KEY_OTHER = UCL_AutoCommitRules.KEY_OTHER;
        List<RepoScan> m_Repos = new List<RepoScan>();
        int m_EphemeralSkipped = 0;
        bool m_Scanned = false;
        // 折疊狀態 —— 獨立 dict，⚠ 不與 PopupSearchCache 共用（資料重載 Clear 會吃掉折疊值）
        readonly Dictionary<string, bool> m_Fold = new Dictionary<string, bool>();

        string m_Report = "";
        bool m_Running = false;
        string m_RunningLabel = "";

        const double COPY_HINT_SECONDS = 3.0;
        string m_CopyHint = "";
        double m_CopyHintAt = 0;

        // 報告文字：換行開著 —— 內層捲動拿掉之後，不換行的長行會把**外層**撐出水平捲軸
        GUIStyle m_MonoStyle;
        GUIStyle MonoStyle => m_MonoStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            wordWrap = true,
            richText = false,
        };

        // 說明字與檔案清單：一律換行 —— 視窗變窄時寧可折行，也不要把整頁撐出水平捲軸
        GUIStyle m_DimLabelStyle;
        GUIStyle DimLabelStyle => m_DimLabelStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            wordWrap = true,
            normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
        };

        GUIStyle m_WarnLabelStyle;
        GUIStyle WarnLabelStyle => m_WarnLabelStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            wordWrap = true,
            normal = { textColor = new Color(1f, 0.72f, 0.35f) },
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

        // session 內的一次性勾選（見 GroupEnabled —— 特殊群不持久化）。
        // persona 模式下同一個特殊群在每個 repo 各自一格，所以 key 帶 repo。
        readonly HashSet<string> m_SessionOn = new HashSet<string>();

        static string SessionKey(RepoScan repo, string key) => repo.Root + " " + key;

        bool IsGroupOn(RepoScan repo, string key)
        {
            var def = Array.Find(CurrentGroupDefs, g => g.Key == key);
            if (def != null) return GroupEnabled(key, def.DefaultOn);
            return m_SessionOn.Contains(SessionKey(repo, key));
        }

        void SetGroupOn(RepoScan repo, string key, bool on)
        {
            var def = Array.Find(CurrentGroupDefs, g => g.Key == key);
            if (def != null)
            {
                if (on) m_Settings.DisabledGroups.Remove(key);
                else if (!m_Settings.DisabledGroups.Contains(key)) m_Settings.DisabledGroups.Add(key);
                SaveSettings();
            }
            else
            {
                if (on) m_SessionOn.Add(SessionKey(repo, key));
                else m_SessionOn.Remove(SessionKey(repo, key));
            }
        }

        // repo 層開關 —— 每次掃描重算預設（在線者預設關）。
        // ⚠ 刻意不持久化：「這一次我認了」不該延續到下一次掃描。
        readonly Dictionary<string, bool> m_RepoOn = new Dictionary<string, bool>();

        bool IsRepoOn(RepoScan repo)
        {
            if (!string.IsNullOrEmpty(repo.Blocked)) return false;
            return m_RepoOn.TryGetValue(repo.Root, out var on) && on;
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
            DrawRepos();
            DrawActions();
            DrawReport();
        }

        void DrawSettingsPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("掃描對象", UCL_GUIStyle.LabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    DrawModeButton(ScanMode.AgentCommands, "AgentCommands 本層");
                    DrawModeButton(ScanMode.PersonaLetters, "Persona 信件庫（letters/*）");
                    GUILayout.FlexibleSpace();
                }
                if (m_Settings.Mode == ScanMode.AgentCommands)
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
                else
                {
                    GUILayout.Label($"letters 根：{UCL_LettersPath.Root}", DimLabelStyle);
                    GUILayout.Label("收的是**別人寫進來的**（系統信 / 掛號信 / 他人投遞的畫像）與機械維護檔；"
                                    + "persona 自己寫的信、碎片、見叢一律落在「未分類」且預設不勾 —— "
                                    + "那些要掛她的名字，走她自己的收尾 commit。",
                        DimLabelStyle);
                    GUILayout.Label("⚠ 在線的 persona 預設不勾（她可能正在寫）；detached HEAD 的信件庫直接擋下。",
                        WarnLabelStyle);
                }
            }
        }

        void DrawModeButton(ScanMode mode, string label)
        {
            bool cur = m_Settings.Mode == mode;
            var style = cur
                ? UCL_GUIStyle.GetButtonStyle(new Color(0.45f, 0.8f, 1f))
                : UCL_GUIStyle.ButtonStyle;
            using (new EditorGUI.DisabledScope(m_Running))
            {
                if (GUILayout.Button(label, style, GUILayout.ExpandWidth(false)) && !cur)
                {
                    m_Settings.Mode = mode;
                    SaveSettings();
                    GUI.FocusControl(null);
                    Scan();
                }
            }
        }

        // ===========================================================
        // repo / 群組顯示
        // ===========================================================
        void DrawRepos()
        {
            if (!m_Scanned)
            {
                GUILayout.Label("（尚未掃描 —— 按上方「重新掃描」）", UCL_GUIStyle.LabelStyle);
                return;
            }
            int dirty = 0;
            foreach (var r in m_Repos) if (r.Total > 0) dirty++;
            if (dirty == 0)
            {
                GUILayout.Label(m_Settings.Mode == ScanMode.PersonaLetters
                        ? $"✓ {m_Repos.Count} 個信件庫全乾淨（ephemeral 除外）—— 沒有可 commit 的自動生成檔"
                        : "✓ 工作樹乾淨（ephemeral 除外）—— 沒有可 commit 的自動生成檔",
                    UCL_GUIStyle.LabelStyle);
                if (m_EphemeralSkipped > 0)
                {
                    GUILayout.Label($"　（另有 {m_EphemeralSkipped} 個 ephemeral 檔被排除，不進 commit）",
                        DimLabelStyle);
                }
                return;
            }

            // ⚠ 這裡**不再開 ScrollView** —— UCL_EditorPage 已經把 ContentOnGUI 包在捲動區裡，
            //   再包一層是雙捲軸（滑鼠滾輪落在哪一層取決於游標位置，等於捲不動）。
            foreach (var repo in m_Repos)
            {
                if (repo.Total == 0) continue;
                DrawRepo(repo);
            }

            if (m_EphemeralSkipped > 0)
            {
                GUILayout.Label($"🚫 ephemeral 已排除 {m_EphemeralSkipped} 個"
                                + "（log / wait 旗標 / _last_op / DebugLogs…）—— 永遠不進 commit",
                    DimLabelStyle);
            }
        }

        void DrawRepo(RepoScan repo)
        {
            bool single = m_Settings.Mode == ScanMode.AgentCommands;
            using (new GUILayout.VerticalScope("box"))
            {
                if (!single)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(repo.Blocked)))
                        {
                            bool on = IsRepoOn(repo);
                            bool next = UCL_GUILayout.CheckBox(on);
                            if (next != on) m_RepoOn[repo.Root] = next;
                        }
                        GUILayout.Label($"📁 {repo.Name}　({repo.Total} 檔)", UCL_GUIStyle.LabelStyle,
                            GUILayout.ExpandWidth(false));
                        GUILayout.Label(string.IsNullOrEmpty(repo.Branch) ? "detached" : repo.Branch,
                            DimLabelStyle, GUILayout.ExpandWidth(false));
                        if (repo.Online)
                        {
                            GUILayout.Label("🟢 在線中", WarnLabelStyle, GUILayout.ExpandWidth(false));
                        }
                        GUILayout.FlexibleSpace();
                    }
                    // 擋下的理由自成一列 —— 它是一句話，塞進標題列會把分支/在線標籤擠出畫面
                    if (!string.IsNullOrEmpty(repo.Blocked))
                    {
                        GUILayout.Label($"⛔ {repo.Blocked}", WarnLabelStyle);
                        return;   // 擋下的 repo 不列群組 —— 它的內容此刻不能被 commit
                    }
                }
                foreach (var def in CurrentGroupDefs)
                {
                    DrawGroup(repo, def.Key, def.Label, def.Message);
                }
                DrawGroup(repo, KEY_SUBPTR, "巢狀 submodule pointer（⚠ 指向別人的信件庫 —— 確認對方已 push 再勾）",
                    "chore(submodule): bump nested submodule pointers (auto)");
                DrawGroup(repo, KEY_OTHER, single
                        ? "未分類（分群規則沒認出來的檔 —— 逐一看過再勾）"
                        : "未分類／她自己寫的（收尾信・碎片・見叢・素描本 —— 逐一看過再勾）",
                    UnclassifiedMessage());
            }
        }

        string UnclassifiedMessage()
            => m_Settings.Mode == ScanMode.PersonaLetters
                ? "[misc] 同步未分類檔 (auto)"
                : "chore(misc): sync unclassified changes (auto)";

        void DrawGroup(RepoScan repo, string key, string label, string message)
        {
            if (!repo.Groups.TryGetValue(key, out var files) || files.Count == 0) return;
            string foldKey = SessionKey(repo, key);
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    bool on = IsGroupOn(repo, key);
                    bool next = UCL_GUILayout.CheckBox(on);
                    if (next != on) SetGroupOn(repo, key, next);
                    // 折疊鈕排在標題**之前** —— 標題長度各群差很多，擺在尾端會讓每一列的
                    // 按鈕位置隨文字長度跳動（滑鼠要重新瞄準才點得到）。
                    bool fold = m_Fold.TryGetValue(foldKey, out var f) && f;
                    bool nextFold = UCL_GUILayout.Toggle(fold);   // ▼/► —— 折疊語彙全專案統一
                    if (nextFold != fold) m_Fold[foldKey] = nextFold;
                    GUILayout.Label($"{label}　({files.Count} 檔)",
                        on ? UCL_GUIStyle.LabelStyle : DimLabelStyle);
                }
                GUILayout.Label($"　訊息: {message} [{files.Count} files]", DimLabelStyle);
                if (m_Fold.TryGetValue(foldKey, out var open) && open)
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
        class PendingCommit
        {
            public string RepoRoot;
            public string RepoName;
            public string Key;
            public string Message;
            public List<string> Files;
        }

        void DrawActions()
        {
            if (!m_Scanned) return;
            var pending = PendingCommits();
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

        List<PendingCommit> PendingCommits()
        {
            var o = new List<PendingCommit>();
            bool single = m_Settings.Mode == ScanMode.AgentCommands;
            foreach (var repo in m_Repos)
            {
                if (!single && !IsRepoOn(repo)) continue;
                if (!string.IsNullOrEmpty(repo.Blocked)) continue;
                foreach (var def in CurrentGroupDefs)
                {
                    Add(repo, def.Key, def.Message);
                }
                Add(repo, KEY_SUBPTR, "chore(submodule): bump nested submodule pointers (auto)");
                Add(repo, KEY_OTHER, UnclassifiedMessage());
            }
            return o;

            void Add(RepoScan repo, string key, string message)
            {
                if (!IsGroupOn(repo, key)) return;
                if (!repo.Groups.TryGetValue(key, out var files) || files.Count == 0) return;
                o.Add(new PendingCommit
                {
                    RepoRoot = repo.Root,
                    RepoName = repo.Name,
                    Key = key,
                    Message = message,
                    Files = files,
                });
            }
        }

        void ConfirmAndCommit(List<PendingCommit> pending)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(m_Settings.Mode == ScanMode.PersonaLetters
                ? $"Persona 信件庫（letters/）—— {CountRepos(pending)} 個 repo"
                : $"Repo: {m_Settings.Root}");
            sb.AppendLine($"將建立 {pending.Count} 筆 commit（每群一筆，具名 stage、不 git add -A）：");
            foreach (var p in pending)
            {
                sb.AppendLine($"　· [{p.RepoName}] {p.Message} [{p.Files.Count} files]");
            }
            sb.AppendLine("\n只 commit 各自本層 —— 不 push、不動父層 pointer。");
            UCL_OptionPage.Create("確認自動 Commit？", sb.ToString(),
                new ButtonData("Commit", () => RunCommits(pending),
                    UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.5f, 0.3f))),
                new ButtonData("取消"));
        }

        static int CountRepos(List<PendingCommit> pending)
        {
            var set = new HashSet<string>();
            foreach (var p in pending) set.Add(p.RepoRoot);
            return set.Count;
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
            // 同上：不開內層 ScrollView。報告長度隨 commit 筆數走，交給頁面本身的捲動。
            EditorGUILayout.TextArea(m_Report, MonoStyle);
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
        // 區塊職責：決定要掃哪些 repo → 逐個 git status → 依 GroupDefs 分群
        // 物理意義：-uall 展開 untracked 目錄成逐檔（訊息檔整天在新增，目錄縮寫會讓
        //          檔數統計與清單都失真）。submodule pointer 變更用 `submodule status`
        //          的路徑集合辨識，與檔案群分開。
        // ⚠ 目標清單與在線 persona **在主執行緒先算好再丟進背景** ——
        //   路徑解析與 lock 掃描都走 Unity 端 API，背景執行緒讀到的可能不是同一份世界。
        // quiet=true：不覆寫 m_Report（commit 完的自動重掃別蓋掉 commit 報告）
        void Scan(bool quiet = false)
        {
            if (m_Running)
            {
                Debug.LogWarning($"[AutoCommit] 已有操作進行中（{m_RunningLabel}）— 忽略掃描");
                return;
            }
            var targets = new List<RepoScan>();
            if (m_Settings.Mode == ScanMode.PersonaLetters)
            {
                string lettersRoot = UCL_LettersPath.Root;
                if (string.IsNullOrEmpty(lettersRoot) || !Directory.Exists(lettersRoot))
                {
                    m_Report = $"✗ letters 根目錄不存在: {lettersRoot}";
                    return;
                }
                var online = new HashSet<string>();
                foreach (var lockInfo in UCL_ActivePersonaLocks.ListOnline()) online.Add(lockInfo.Persona);
                foreach (string dir in Directory.GetDirectories(lettersRoot))
                {
                    string gitPath = Path.Combine(dir, ".git");
                    // submodule 的 .git 是**檔案**（gitdir: 指標），獨立 clone 才是目錄 —— 兩種都收
                    if (!File.Exists(gitPath) && !Directory.Exists(gitPath)) continue;
                    string name = Path.GetFileName(dir);
                    targets.Add(new RepoScan
                    {
                        Root = dir.Replace('\\', '/'),
                        Name = name,
                        Online = online.Contains(name),
                    });
                }
                targets.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                if (targets.Count == 0)
                {
                    m_Report = $"✗ {lettersRoot} 底下找不到任何 git repo";
                    return;
                }
            }
            else
            {
                string root = m_Settings.Root;
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    m_Report = $"✗ Repo 根目錄不存在: {root}";
                    return;
                }
                targets.Add(new RepoScan
                {
                    Root = root.Replace('\\', '/'),
                    Name = Path.GetFileName(root.TrimEnd('/', '\\')),
                });
            }

            m_Running = true;
            m_RunningLabel = "掃描";
            var defs = CurrentGroupDefs;
            System.Threading.Tasks.Task.Run(() =>
            {
                var log = new System.Text.StringBuilder();
                int ephemeralTotal = 0;
                try
                {
                    UCL_ProcessRegistryService.KillAllByTag(PROC_TAG);
                    foreach (var repo in targets)
                    {
                        ephemeralTotal += ScanOne(repo, defs, log);
                    }
                    int total = 0, dirty = 0;
                    foreach (var r in targets)
                    {
                        total += r.Total;
                        if (r.Total > 0) dirty++;
                    }
                    log.AppendLine($"✓ 掃描完成：{targets.Count} 個 repo（{dirty} 個有變更）、"
                                   + $"{total} 檔進候選、{ephemeralTotal} 檔 ephemeral 排除");
                }
                catch (Exception ex)
                {
                    log.AppendLine(ex.ToString());
                }
                int ephemeralSnap = ephemeralTotal;
                EditorApplication.delayCall += () =>
                {
                    m_Running = false;
                    m_RunningLabel = "";
                    m_Repos = targets;
                    m_EphemeralSkipped = ephemeralSnap;
                    m_Scanned = true;
                    // repo 勾選重算 —— 在線者預設關（上一輪的一次性授權不延續）
                    m_RepoOn.Clear();
                    foreach (var r in targets)
                    {
                        m_RepoOn[r.Root] = string.IsNullOrEmpty(r.Blocked) && !r.Online;
                    }
                    m_SessionOn.Clear();
                    if (!quiet) m_Report = log.ToString();
                };
            });
        }

        /// <summary>掃一個 repo（背景執行緒）；回傳被排除的 ephemeral 檔數。</summary>
        int ScanOne(RepoScan repo, GroupDef[] defs, System.Text.StringBuilder log)
        {
            int ephemeral = 0;
            // 分支：detached 就擋下 —— 那裡 commit 出來沒有分支指到，等於游離
            var (eb, ob, _) = Git(repo.Root, "symbolic-ref --short -q HEAD");
            repo.Branch = eb == 0 ? ob.Trim() : "";
            if (string.IsNullOrEmpty(repo.Branch))
            {
                repo.Blocked = "detached HEAD —— commit 會變游離，先 checkout 追蹤分支";
            }
            // submodule 路徑集合 —— pointer 變更要跟一般檔案分開治理
            var subPaths = new HashSet<string>();
            var (es, os, _) = Git(repo.Root, "submodule status");
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
            var (e, o, se2) = Git(repo.Root, "status --porcelain -uall");
            if (e != 0)
            {
                log.AppendLine($"✗ [{repo.Name}] git status 失敗 (exit {e})\n{se2}");
                return ephemeral;
            }
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
                else if (IsEphemeral(path)) { ephemeral++; continue; }
                else
                {
                    key = KEY_OTHER;
                    foreach (var def in defs)
                    {
                        if (def.Match(path)) { key = def.Key; break; }
                    }
                }
                if (!repo.Groups.TryGetValue(key, out var list))
                {
                    repo.Groups[key] = list = new List<string>();
                }
                list.Add(path);
                repo.Total++;
            }
            return ephemeral;
        }

        // ===========================================================
        // 批次 commit
        // ===========================================================
        // 區塊職責：每群一筆 commit —— 具名 stage（分批）→ commit → 記 SHA
        // 物理意義：stage 用 `git add -- <files>` 逐批餵，**絕不 git add -A**
        //          （別人正在寫的檔會被一起帶走，而那不會有錯誤訊息）。
        //          訊息用 -F 檔案餵 —— 訊息含統計行，走 argv 會踩引號 / 長度的坑
        //          （Bash 反引號雙殺的 C# 版本：判準不是「含不含特殊字元」，是長文一律走檔案）。
        void RunCommits(List<PendingCommit> pending)
        {
            if (m_Running)
            {
                Debug.LogWarning($"[AutoCommit] 已有操作進行中（{m_RunningLabel}）— 忽略");
                return;
            }
            m_Running = true;
            m_RunningLabel = "commit";
            m_Report = "⏳ commit 執行中…";
            // 模式先快照 —— 背景跑到一半使用者仍可切模式，邊跑邊讀會讓報告描述另一個世界
            bool aPersonaMode = m_Settings.Mode == ScanMode.PersonaLetters;
            System.Threading.Tasks.Task.Run(() =>
            {
                var log = new System.Text.StringBuilder();
                int ok = 0, fail = 0;
                try
                {
                    UCL_ProcessRegistryService.KillAllByTag(PROC_TAG);
                    foreach (var p in pending)
                    {
                        string root = p.RepoRoot;
                        // 逐批 stage
                        bool stageFailed = false;
                        for (int i = 0; i < p.Files.Count && !stageFailed; i += CHUNK)
                        {
                            var batch = p.Files.GetRange(i, Math.Min(CHUNK, p.Files.Count - i));
                            var quoted = batch.ConvertAll(f => "\"" + f + "\"");
                            var (ea, _, sa) = Git(root, "add -- " + string.Join(" ", quoted));
                            if (ea != 0)
                            {
                                log.AppendLine($"✗ [{p.RepoName}/{p.Key}] stage 失敗: {sa}");
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
                            log.AppendLine($"⏭ [{p.RepoName}/{p.Key}] 掃描後已無變更 —— 跳過");
                            continue;
                        }
                        // 訊息走暫存檔（-F），不走 argv
                        string msgFile = Path.Combine(Path.GetTempPath(), $"ucl_autocommit_{p.Key}.txt");
                        File.WriteAllText(msgFile,
                            $"{p.Message} [{p.Files.Count} files]\n", new System.Text.UTF8Encoding(false));
                        var (ec, _, sc) = Git(root, $"commit -F \"{msgFile}\"");
                        try { File.Delete(msgFile); } catch { /* 暫存檔清不掉不影響結果 */ }
                        if (ec != 0)
                        {
                            log.AppendLine($"✗ [{p.RepoName}/{p.Key}] commit 失敗: {sc}");
                            Git(root, "reset");
                            fail++;
                            continue;
                        }
                        var (eh, oh, _) = Git(root, "rev-parse --short HEAD");
                        log.AppendLine($"✓ [{p.RepoName}] {(eh == 0 ? oh.Trim() : "?")} — {p.Message} [{p.Files.Count} files]");
                        ok++;
                    }
                    log.AppendLine($"\n— 完成：✓{ok} ✗{fail} —— 未 push、父層 pointer 未動 —");
                    if (ok > 0 && aPersonaMode)
                    {
                        log.AppendLine("↳ 父層 AgentCommands 的 submodule pointer 還停在舊 hash："
                                       + "切回「AgentCommands 本層」重掃，pointer 那一群會出現（一次性勾選）。");
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
