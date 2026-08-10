// 區塊職責：Git Submodule 同步頁 — 批量對「本專案（或指定 repo）的所有 submodule」切預設 branch / pull / push
// 物理意義：多層 submodule 專案的日常痛點是「submodule update 之後全員 detached HEAD、
//          分支跑掉、誰 ahead 誰 behind 沒人一眼看得到」。本頁把這些收成一張狀態表 + 批次按鈕。
//          與 UCL_GitFlattenSyncPage 的分工：那頁「攤平檔案到另一個 repo、兩邊 git 都不碰」，
//          本頁「只碰 git（branch / pull / push）、不動任何工作目錄檔案內容」。
// 數值影響：掃描 / Fetch 唯讀（fetch 只寫 remote-tracking ref，不動工作目錄）。
//          切 branch / Pull 會移動各 submodule 的 HEAD；Push 會寫遠端 —— 一律走二次確認。
//
// 設計決策（2026-08-07 分析，酒館待砸磚）：
//   · **C# 直呼 git CLI，不用 LibGit2Sharp、不另寫 Python 端。**
//     - git.exe 必在（本專案本身就靠它活著）；認證走系統 credential manager，跟命令列同一套，
//       push 不必自己管憑證。
//     - LibGit2Sharp 要佈署 native dll（多平台 libgit2）、submodule 支援殘缺、
//       push 認證要自己寫 callback —— 等於把「git 行為」變成第二套實作，跟系統 git 漂移。
//     - 不走 Python：本頁是互動操作台（看狀態、按按鈕），agent 端已有自己的 git 流程
//       （git_commit.py / ucl-commit skill），沒有「同一套邏輯要在無 Editor 環境跑」的需求 ——
//       這正是與 FlattenSync 相反的取捨，那邊的事實來源必須是腳本，這邊不必。
//   · 預設 branch 三層解析：本頁逐項覆寫 > .gitmodules 的 branch 欄 > 本頁全域預設。
//     .gitmodules 是 git 原生的「這個 submodule 該追哪條 branch」欄位，已填的直接尊重。
//   · 切 branch 的安全線：**HEAD 不在目標 branch 歷史上 → 跳過並列出，不切**。
//     detached HEAD 上可能有未合併的 commit，切走 = 指標遺失（reflog 能救但沒人會去看）。
//     dirty（有未 commit 修改）同樣跳過 —— 本頁不做 stash，那是把別人的工作區當自己的。
//   · Push 順序**由深到淺**（巢狀最深的先推、root 最後）：parent 的 bump commit 引用 child SHA，
//     先推 parent 會讓別人 pull 到指向不存在 commit 的 gitlink。
//   · **多 remote push（2026-08-10 Tim 提，預設 off）**：同一份程式碼同時掛 GitHub 與 GitLab 時，
//     只推 origin 會讓另一邊靜默落後。開關開著時逐 repo 展開它自己的 remote 清單各推一次；
//     每個 repo 推完全部 remote 才換下一個 repo —— 深→淺的順序對**每一個** remote 各自成立，
//     所以 gitlink 不變量不因多 remote 而破。pull 不跟進（從哪合併是 merge 決策，不是同步）。
//   · git 預設讀 UCL_RepoPath.RepoRoot（目前專案的 git root），保留可改路徑（Tim 2026-08-07）。
//   · 設定存 EditorPrefs（JSON 字串）—— 跟 FlattenSync 同一套慣例，跨機器不通用可接受。
// RequiresConstantRepaint：批次操作在背景跑，逐項進度與完成報告要即時反映。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_GitSubmoduleSyncPage.md")]
    public class UCL_GitSubmoduleSyncPage : UCL_CommonEditorPage
    {
        public override string WindowName => "UCL_GitSubmoduleSync";
        public override bool ShowInPageMenu => true;

        const string PrefKey_Settings = "UCL_GitSubmoduleSync.Settings";

        // Process 註冊中心的 tag —— KillAllByTag / Register / Unregister 三處共用。
        // 硬規則：C# 開的每顆外部 Process 都要登記（見 Coding_Standards.md「外部 Process」）。
        const string PROC_TAG = "git_submodule_sync";

        // 單一 git 指令的逾時上限。pull / push 走網路，大 repo 首次抓取確實會久；
        // 命中這個上限代表卡住（credential prompt / 網路死），不是「檔案多」。
        const int GIT_TIMEOUT_MS = 5 * 60 * 1000;

        // 區塊職責：可保存的設定
        // 物理意義：轉 JSON 存 EditorPrefs。路徑是絕對路徑，跨機器不通用（慣例同 FlattenSync）。
        //          JsonUtility 不吃 Dictionary，逐項 branch 覆寫用 pair list。
        [Serializable]
        public class SyncSettings
        {
            public string Root = "";
            // 全域預設 branch —— 逐項覆寫與 .gitmodules 都沒給時的最後一層。
            // 空字串 = 沒有預設：解析不到目標 branch 的 submodule 會被跳過並列出，
            // 不會靜默拿「目前所在 branch」頂替（那等於沒有這個功能）。
            public string DefaultBranch = "";
            public List<string> Excluded = new List<string>();
            public List<BranchOverride> Overrides = new List<BranchOverride>();
            // root repo 本身要不要一起 pull / push（切 branch 不含 root —— 專案根切分支
            // 影響整個 Unity 工程，那個動作該是人自己下的，不進批次）。
            public bool IncludeRoot = false;
            // push 要推到「該 repo 設定的每一個 remote」還是只推 origin（預設 false = 只推 origin）。
            // 物理意義：同一份程式碼同時掛 GitHub 與 GitLab 時，只推 origin 會讓另一邊靜默落後 ——
            //          而落後的那一邊不會叫（沒人 pull 它就沒人知道），正是最難抓的壞法。
            // 為何預設 off：開著等於把「推去哪」從一個明確的名字擴張成「repo 現在恰好設了什麼」，
            //          而 remote 清單是每台機器各自的 local config。擴大寫入範圍要人顯式點頭。
            // ⚠ pull 不跟進多 remote —— 從哪個 remote 合併是 merge 決策，不是同步動作。
            public bool PushAllRemotes = false;
        }

        [Serializable]
        public class BranchOverride
        {
            public string Path = "";
            public string Branch = "";
        }

        SyncSettings m_Settings = new SyncSettings();

        // 區塊職責：掃出來的 submodule 狀態列
        // 物理意義：清單事實來源是 `git submodule status --recursive`（git 自己的答案），
        //          C# 不另寫一套 .gitmodules 遍歷來「探索」submodule —— 兩套遲早不一致。
        //          （.gitmodules 只拿來查 branch 欄，那是它的原生欄位，不是第二套探索。）
        public class SubEntry
        {
            public string Path = "";              // 相對 root，正斜線
            public string Sha = "";
            public string CurBranch = "";         // "(detached)" = detached HEAD
            public string GitmodulesBranch = "";  // .gitmodules 的 branch 欄（可空）
            public string HeuristicBranch = "";   // 掃描時算好的啟發式預設（規則見 TargetBranch）
            public List<string> Branches = new List<string>();  // 本地+origin 的 branch 名（下拉選項用）
            // 該 repo 設定的 remote 名清單。**只給畫面顯示與二次確認用** ——
            // 真正 push 時的清單在 RunOne 內即時重問（理由同 dirty / branch：
            // 掃描結果是照片，而「要推去哪些遠端」是決定不是報告）。
            public List<string> Remotes = new List<string>();
            public bool Dirty = false;            // 有未 commit 的追蹤檔修改
            public bool Uninitialized = false;    // status 前綴 '-'：內容不在本機
            public int Ahead = -1;                // 對 upstream；-1 = 無 upstream / 未知
            public int Behind = -1;
            public string FetchAge = "";          // 上次 fetch 距今多久（FETCH_HEAD mtime；空 = 沒 fetch 過）
            public string Note = "";              // 最近一次批次操作對它的結果
        }
        List<SubEntry> m_Subs = new List<SubEntry>();
        bool m_Scanned = false;   // 區分「還沒掃」與「掃過但真的沒有 submodule」

        // PopupSearchCache 的內部狀態容器（對齊 FlattenSync 的 m_PickerDic 慣例）。
        // key 逐列帶 path —— 各列下拉互不共用狀態。⚠ 不拿它存折疊狀態（資料重載會 Clear）。
        readonly UCL_ObjectDictionary m_PickerDic = new UCL_ObjectDictionary();

        string m_Report = "";
        bool m_Running = false;
        string m_RunningLabel = "";
        Vector2 m_ReportScroll = Vector2.zero;

        // 複製鈕的即時回饋 —— 純顯示狀態，不進 EditorPrefs（慣例同 FlattenSync）。
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

        // 警示字色 —— 只給「這一下會弄壞東西」的說明用（到處都紅 = 沒有紅）。
        GUIStyle m_WarnStyle;
        GUIStyle WarnStyle => m_WarnStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            wordWrap = true,
            normal = { textColor = new Color(1f, 0.55f, 0.35f) },
        };

        public static UCL_GitSubmoduleSyncPage Create() => UCL_EditorPage.Create<UCL_GitSubmoduleSyncPage>();

        public override void Init(UCL_GUIPageController p_Controller)
        {
            base.Init(p_Controller);
            LoadSettings();
            // 進頁面自動掃一次（不 fetch）—— EditorPrefs 讀回來的設定可能是好幾天前的，
            // 而 branch / ahead 狀態同一時間早就變了。不掃就是拿舊狀態配新 repo。
            Scan(fetch: false);
        }

        // ===========================================================
        // 設定持久化
        // ===========================================================
        void LoadSettings()
        {
            try
            {
                string json = EditorPrefs.GetString(PrefKey_Settings, "");
                if (!string.IsNullOrEmpty(json))
                {
                    var s = JsonUtility.FromJson<SyncSettings>(json);
                    if (s != null) m_Settings = s;
                }
            }
            catch (Exception e)
            {
                // 壞掉的設定不可靜默吞掉 —— 使用者會以為「沒存到」而重填，真相是 JSON 壞了。
                Debug.LogWarning($"[GitSubmoduleSync] 設定讀取失敗，改用預設值: {e.Message}");
            }
            m_Settings.Excluded ??= new List<string>();
            m_Settings.Overrides ??= new List<BranchOverride>();
            if (string.IsNullOrEmpty(m_Settings.Root)) m_Settings.Root = UCL_RepoPath.RepoRoot;
        }

        void SaveSettings()
        {
            try
            {
                EditorPrefs.SetString(PrefKey_Settings, JsonUtility.ToJson(m_Settings));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GitSubmoduleSync] 設定保存失敗: {e.Message}");
            }
        }

        string GetOverride(string path)
        {
            foreach (var o in m_Settings.Overrides)
            {
                if (o.Path == path) return o.Branch ?? "";
            }
            return "";
        }

        void SetOverride(string path, string branch)
        {
            for (int i = 0; i < m_Settings.Overrides.Count; i++)
            {
                if (m_Settings.Overrides[i].Path != path) continue;
                if (string.IsNullOrEmpty(branch)) m_Settings.Overrides.RemoveAt(i);
                else m_Settings.Overrides[i].Branch = branch;
                SaveSettings();
                return;
            }
            if (!string.IsNullOrEmpty(branch))
            {
                m_Settings.Overrides.Add(new BranchOverride { Path = path, Branch = branch });
                SaveSettings();
            }
        }

        // 區塊職責：目標 branch 四層解析
        // 物理意義：逐項覆寫 > .gitmodules branch 欄 > 全域預設 > 啟發式。空字串 = 解析不到（跳過）。
        //          啟發式（Tim 2026-08-07 拍板，於掃描時算好存 HeuristicBranch）：
        //          ① 資料夾名以 UCL_ 開頭（UCL_Core 與其他 UCL 系）→ Dev
        //          ② 全 repo 只有一條 branch → 就是它（沒有歧義可言）
        //          ③ 其餘 → master；沒有 master 才 main（GitHub/GitLab 2020 後新 repo 預設 main，
        //            舊 repo 是 master —— 目前沒有兩者並存的 repo，並存時 master 贏）
        string TargetBranch(SubEntry s)
        {
            string o = GetOverride(s.Path);
            if (!string.IsNullOrEmpty(o)) return o;
            return AutoTarget(s);
        }

        // 覆寫以外的三層（下拉的「(自動)」選項要顯示這個 —— 顯示含覆寫的 TargetBranch 的話，
        // 「選了自動會變成什麼」在選之前是看不到的）。
        string AutoTarget(SubEntry s)
        {
            if (!string.IsNullOrEmpty(s.GitmodulesBranch)) return s.GitmodulesBranch;
            if (!string.IsNullOrEmpty(m_Settings.DefaultBranch)) return m_Settings.DefaultBranch;
            return s.HeuristicBranch ?? "";
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            using (new EditorGUI.DisabledScope(m_Running))
            {
                if (GUILayout.Button("重新掃描", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    Scan(fetch: false);
                }
                // fetch 分開一顆 —— 掃描要快（進頁面自動跑），fetch 走網路且 submodule 多時要數十秒。
                // ahead/behind 的準確度依賴 fetch，所以按鈕文案把這件事講明。
                if (GUILayout.Button("Fetch 全部後掃描", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    Scan(fetch: true);
                }
            }
        }

        protected override void ContentOnGUI()
        {
            DrawSettingsPanel();
            DrawSubmodules();
            DrawActions();
            DrawReport();
        }

        // ===========================================================
        // 設定區
        // ===========================================================
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
                    if (GUILayout.Button("本專案", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_Settings.Root = UCL_RepoPath.RepoRoot;
                        SaveSettings();
                        GUI.FocusControl(null);
                    }
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("全域預設 branch", UCL_GUIStyle.LabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    string next = GUILayout.TextField(m_Settings.DefaultBranch ?? "", UCL_GUIStyle.TextFieldStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    if (next != m_Settings.DefaultBranch)
                    {
                        m_Settings.DefaultBranch = next;
                        SaveSettings();
                    }
                    GUILayout.Label("（逐項覆寫 > .gitmodules 的 branch 欄 > 這裡 > 啟發式："
                                    + "UCL_* → Dev、單一 branch → 它、其餘 master 沒有才 main）",
                        DimLabelStyle);
                    GUILayout.FlexibleSpace();
                }
                using (new GUILayout.HorizontalScope())
                {
                    bool inc = UCL_GUILayout.CheckBox(m_Settings.IncludeRoot);
                    GUILayout.Label(" root repo 本身也一起 Pull / Push（切 branch 永遠不含 root —— "
                                    + "專案根換分支該是人自己下的動作）", UCL_GUIStyle.LabelStyle);
                    if (inc != m_Settings.IncludeRoot) { m_Settings.IncludeRoot = inc; SaveSettings(); }
                }
                using (new GUILayout.HorizontalScope())
                {
                    bool all = UCL_GUILayout.CheckBox(m_Settings.PushAllRemotes);
                    GUILayout.Label(" Push 到該 repo 的**所有** remote（關 = 只推 origin）",
                        UCL_GUIStyle.LabelStyle);
                    if (all != m_Settings.PushAllRemotes) { m_Settings.PushAllRemotes = all; SaveSettings(); }
                }
                if (m_Settings.PushAllRemotes)
                {
                    GUILayout.Label("　⚠ 推去哪由各 repo 的 remote 設定決定（每台機器各自的 local config）。"
                                    + "一個 remote 失敗不影響其他 remote，但整列會記成失敗並逐個列出。"
                                    + "Pull 不跟進 —— 從哪合併是 merge 決策。", WarnStyle);
                }
            }
        }

        // ===========================================================
        // submodule 狀態表
        // ===========================================================
        void DrawSubmodules()
        {
            if (m_Subs.Count == 0)
            {
                GUILayout.Label(m_Scanned ? "（這個 repo 沒有 submodule）"
                        : "（尚未掃描 —— 按上方「重新掃描」）", UCL_GUIStyle.LabelStyle);
                return;
            }

            // staleness 逐列標（Sirius 2026-08-07 砸磚④）：剛 fetch 過的跟三天沒動的
            // 掛同一句總警語，等於沒有警語 —— 各列自己的 FETCH_HEAD 時間才誠實。
            GUILayout.Label($"Submodule（{m_Subs.Count} 個，排除 {m_Settings.Excluded.Count} 個）"
                            + " — ahead/behind 以各列上次 fetch 時間為準", UCL_GUIStyle.LabelStyle);
            using (new GUILayout.VerticalScope("box"))
            {
                foreach (var s in m_Subs)
                {
                    DrawSubRow(s);
                }
            }
        }

        void DrawSubRow(SubEntry s)
        {
            using (new GUILayout.HorizontalScope())
            {
                // 勾選 = 納入批次操作
                bool inc = !m_Settings.Excluded.Contains(s.Path);
                bool next = GUILayout.Toggle(inc, "", GUILayout.Width(UCL_GUIStyle.GetScaledSize(20)));
                if (next != inc)
                {
                    if (next) m_Settings.Excluded.Remove(s.Path);
                    else m_Settings.Excluded.Add(s.Path);
                    SaveSettings();
                }

                GUILayout.Label(s.Path, inc ? UCL_GUIStyle.LabelStyle : DimLabelStyle,
                    GUILayout.Width(UCL_GUIStyle.GetScaledSize(360)));

                // 目前 branch：detached 紅、不在目標上黃、對齊綠 —— 一眼看出誰跑掉了
                string target = TargetBranch(s);
                Color c = s.CurBranch == "(detached)" ? new Color(1f, 0.4f, 0.4f)
                    : (!string.IsNullOrEmpty(target) && s.CurBranch != target) ? new Color(1f, 0.85f, 0.4f)
                    : new Color(0.5f, 0.9f, 0.5f);
                GUILayout.Label(s.CurBranch, UCL_GUIStyle.GetLabelStyle(c), GUILayout.Width(UCL_GUIStyle.GetScaledSize(110)));

                // 逐項目標 branch —— 下拉選單（Tim 2026-08-07：用 PopupSearchCache 切換）。
                // 選項 = 掃描時收好的該 repo branch 清單（本地+origin）+ 開頭一格「(自動)」。
                // 選「(自動)」= 清掉覆寫，回到 .gitmodules / 全域預設 / 啟發式的解析結果 ——
                // 那格的文字直接把解析結果印出來，選之前就看得到選下去會變成什麼。
                string ov = GetOverride(s.Path);
                if (s.Branches.Count == 0)
                {
                    // 沒有 branch 清單（未 init / 掃描失敗）→ 退回手填。
                    // PopupSearchCache 選項為 0 會 LogError，這不是版面選擇，是不能呼叫它。
                    string ovNext = GUILayout.TextField(ov, UCL_GUIStyle.TextFieldStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(150)));
                    if (ovNext != ov) SetOverride(s.Path, ovNext);
                }
                else
                {
                    string autoTarget = AutoTarget(s);
                    var opts = new List<string>
                    {
                        $"(自動 → {(string.IsNullOrEmpty(autoTarget) ? "無目標" : autoTarget)})",
                    };
                    opts.AddRange(s.Branches);
                    // 手填過、但已不在清單裡的覆寫（branch 被刪 / 打錯字）也要佔一格 ——
                    // 憑空消失的話，使用者只會看到自己的設定不見了，不會知道它還在生效。
                    int cur = 0;
                    if (!string.IsNullOrEmpty(ov))
                    {
                        int idx = s.Branches.IndexOf(ov);
                        if (idx >= 0) cur = idx + 1;
                        else { opts.Add($"{ov}（清單外）"); cur = opts.Count - 1; }
                    }
                    using (new GUILayout.HorizontalScope(GUILayout.MinWidth(UCL_GUIStyle.GetScaledSize(150))))
                    {
                        int nextIndex = UCL_GUILayout.PopupSearchCache(cur, opts, m_PickerDic, $"SubBranchPicker_{s.Path}");
                        if (nextIndex != cur)
                        {
                            if (nextIndex == 0) SetOverride(s.Path, "");
                            else if (nextIndex <= s.Branches.Count) SetOverride(s.Path, s.Branches[nextIndex - 1]);
                            // 「清單外」那格只是顯示現況，選它不改任何值
                        }
                    }

                }

                if (s.Uninitialized)
                {
                    GUILayout.Label("⛔ 未 init", UCL_GUIStyle.GetLabelStyle(new Color(1f, 0.4f, 0.4f)),
                        GUILayout.ExpandWidth(false));
                }
                if (s.Dirty)
                {
                    GUILayout.Label("✎ dirty", UCL_GUIStyle.GetLabelStyle(new Color(1f, 0.85f, 0.4f)),
                        GUILayout.ExpandWidth(false));
                }
                if (s.Ahead > 0 || s.Behind > 0)
                {
                    GUILayout.Label($"↑{Mathf.Max(s.Ahead, 0)} ↓{Mathf.Max(s.Behind, 0)}",
                        UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                }
                // 多 remote 的列標出來（單一 remote 不標 —— 那是常態，標了等於雜訊）。
                // 開了「推所有 remote」卻一個 remote 都沒有 → 那列 push 會跳過，先在表上講。
                if (s.Remotes.Count > 1)
                {
                    GUILayout.Label($"⇈ {string.Join(" / ", s.Remotes)}",
                        m_Settings.PushAllRemotes ? UCL_GUIStyle.LabelStyle : DimLabelStyle,
                        GUILayout.ExpandWidth(false));
                }
                else if (m_Settings.PushAllRemotes && s.Remotes.Count == 0 && !s.Uninitialized)
                {
                    GUILayout.Label("⚠ 無 remote", UCL_GUIStyle.GetLabelStyle(new Color(1f, 0.85f, 0.4f)),
                        GUILayout.ExpandWidth(false));
                }
                if (!string.IsNullOrEmpty(s.FetchAge))
                {
                    GUILayout.Label(s.FetchAge, DimLabelStyle, GUILayout.ExpandWidth(false));
                }
                if (!string.IsNullOrEmpty(s.Note))
                {
                    GUILayout.Label(s.Note, DimLabelStyle);
                }
                GUILayout.FlexibleSpace();
            }
        }

        // ===========================================================
        // 動作
        // ===========================================================
        void DrawActions()
        {
            using (new GUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(m_Running || m_Subs.Count == 0))
                {
                    if (GUILayout.Button("切到預設 branch",
                            UCL_GUIStyle.GetButtonStyle(new Color(0.55f, 0.8f, 1f)), GUILayout.ExpandWidth(false)))
                    {
                        RunBatch("checkout", checkout: true, pull: false, push: false);
                    }
                    if (GUILayout.Button("Pull（ff-only）",
                            UCL_GUIStyle.GetButtonStyle(new Color(0.55f, 0.8f, 1f)), GUILayout.ExpandWidth(false)))
                    {
                        RunBatch("pull", checkout: false, pull: true, push: false);
                    }
                    // 寫遠端的動作走二次確認（慣例同 FlattenSync 的同步鈕）
                    if (GUILayout.Button("Push",
                            UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.6f, 0.35f)), GUILayout.ExpandWidth(false)))
                    {
                        ConfirmAndRun("push", checkout: false, pull: false, push: true);
                    }
                    if (GUILayout.Button("一鍵同步（切 → pull → push）",
                            UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.6f, 0.35f)), GUILayout.ExpandWidth(false)))
                    {
                        ConfirmAndRun("sync", checkout: true, pull: true, push: true);
                    }
                }
                if (m_Running)
                {
                    GUILayout.Label($"⏳ 執行中（{m_RunningLabel}）", UCL_GUIStyle.LabelStyle);
                }
                GUILayout.FlexibleSpace();
            }
            GUILayout.Label("　跳過不硬上：dirty / detached 上有未合併 commit / 解析不到目標 branch 的項目"
                            + "一律跳過並在報告列出 —— 本頁不 stash、不 force、不替人做決定。", DimLabelStyle);
        }

        void ConfirmAndRun(string label, bool checkout, bool pull, bool push)
        {
            int included = 0;
            foreach (var s in m_Subs)
            {
                if (!m_Settings.Excluded.Contains(s.Path)) included++;
            }
            // 「推去哪」要在按下去之前講清楚，而且講的是**具體的 remote 名字**不是「所有」——
            // 「所有」是設定的名字，人要確認的是它今天實際展開成什麼（清單取自掃描快照，
            // 真正執行時會再問一次即時值；兩者若在這幾秒內分岔，報告會逐個列出實際推了誰）。
            string pushWhere = "origin";
            if (push && m_Settings.PushAllRemotes)
            {
                var seen = new List<string>();
                foreach (var s in m_Subs)
                {
                    if (m_Settings.Excluded.Contains(s.Path) || s.Uninitialized) continue;
                    foreach (var r in s.Remotes)
                    {
                        if (!seen.Contains(r)) seen.Add(r);
                    }
                }
                pushWhere = seen.Count == 0 ? "（掃描時沒看到任何 remote）"
                    : $"所有 remote —— 掃描時看到的有: {string.Join(", ", seen)}";
            }
            string body =
                $"Repo: {m_Settings.Root}\n"
                + $"對象: {included} 個 submodule{(m_Settings.IncludeRoot ? " + root repo" : "")}\n"
                + $"動作: {(checkout ? "切到預設 branch → " : "")}{(pull ? "pull（ff-only）→ " : "")}"
                + $"{(push ? "push（由深到淺，root 最後）" : "")}\n"
                + (push ? $"推去: {pushWhere}\n" : "")
                + "\n"
                + "Push 會把各 repo 目標 branch 上的本地 commit 寫到遠端。\n"
                + "dirty / detached 有未合併 commit 的項目會被跳過並列出，不會被硬切或硬推。";
            UCL_OptionPage.Create($"確認 {label}？", body,
                new ButtonData("執行", () => RunBatch(label, checkout, pull, push),
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
                    // 讀回來才算數：systemCopyBuffer 被別的程式鎖住時會靜默失敗，
                    // 而「沒複製到」跟「複製成功」在畫面上長得一模一樣。
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
                       GUILayout.MinHeight(UCL_GUIStyle.GetScaledSize(220))))
            {
                m_ReportScroll = sv.scrollPosition;
                EditorGUILayout.TextArea(m_Report, MonoStyle);
            }
        }

        // ===========================================================
        // git 呼叫（背景執行緒）
        // ===========================================================
        // 區塊職責：跑一條 git 指令 —— 薄轉接到共用封裝 UCL_GitCli（雙 stream 非阻塞讀 /
        //          ProcessRegistry 登記 / 逾時 kill / GIT_TERMINAL_PROMPT 都在那邊）。
        //          只在背景 Task 內呼叫。
        (int exit, string stdout, string stderr) Git(string workDir, string args)
            => UCL_GitCli.Run(workDir, args, PROC_TAG, nameof(UCL_GitSubmoduleSyncPage), GIT_TIMEOUT_MS);

        // ===========================================================
        // 掃描
        // ===========================================================
        // 區塊職責：列 submodule + 各自的 branch / dirty / ahead-behind 狀態
        // 物理意義：清單來自 `git submodule status --recursive`；branch 欄來自各 owner 的
        //          .gitmodules（git 原生欄位）。fetch 開關分離 —— 掃描要快，fetch 要準，兩者是
        //          不同的問題（掃描每次進頁自動跑，fetch 走網路由人顯式按）。
        // quiet=true：不覆寫 m_Report（批次操作完成後的自動重掃 —— 蓋掉批次報告等於
        // 讓人讀不到剛剛發生什麼；狀態表更新就好，報告留著）。
        void Scan(bool fetch, bool quiet = false)
        {
            if (m_Running)
            {
                Debug.LogWarning($"[GitSubmoduleSync] 已有操作進行中（{m_RunningLabel}）— 忽略掃描");
                return;
            }
            string root = m_Settings.Root;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                m_Report = $"✗ Repo 根目錄不存在: {root}";
                return;
            }
            m_Running = true;
            m_RunningLabel = fetch ? "fetch + 掃描" : "掃描";
            // 批次開始前收掉同 tag 的舊 process（singleton 語意，防屍潮）——
            // 逐條 git 指令不各自 KillAllByTag，殺的粒度是「上一輪批次」不是「上一條指令」。
            System.Threading.Tasks.Task.Run(() =>
            {
                var log = new System.Text.StringBuilder();
                var list = new List<SubEntry>();
                try
                {
                    UCL_ProcessRegistryService.KillAllByTag(PROC_TAG);
                    var (exit, stdout, stderr) = Git(root, "submodule status --recursive");
                    if (exit != 0)
                    {
                        log.AppendLine($"✗ git submodule status 失敗 (exit {exit})\n{stderr}");
                    }
                    else
                    {
                        // 每行格式：`<flag><sha> <path> (<desc>)`，flag: ' '=正常 '-'=未init '+'=SHA漂移 'U'=衝突
                        foreach (var raw in stdout.Split('\n'))
                        {
                            string line = raw.TrimEnd();
                            if (line.Length < 42) continue;
                            char flag = line[0];
                            var rest = line.Substring(1).Split(' ');
                            if (rest.Length < 2) continue;
                            var s = new SubEntry
                            {
                                Sha = rest[0],
                                Path = rest[1],
                                Uninitialized = flag == '-',
                            };
                            list.Add(s);
                        }
                        // .gitmodules 的 branch 欄：root 與每個「自己還有 .gitmodules」的 submodule 都查。
                        // `config -f .gitmodules --get-regexp` 一次拿 path + branch，C# 端只做配對。
                        var owners = new List<string> { "" };
                        foreach (var s in list) owners.Add(s.Path);
                        foreach (var owner in owners)
                        {
                            string ownerAbs = string.IsNullOrEmpty(owner) ? root : Path.Combine(root, owner);
                            if (!File.Exists(Path.Combine(ownerAbs, ".gitmodules"))) continue;
                            var (e2, o2, _) = Git(ownerAbs,
                                "config -f .gitmodules --get-regexp \"submodule\\..*\\.(path|branch)\"");
                            if (e2 != 0) continue;
                            // 先收 path 行，再用 branch 行回填 —— 同名 submodule 的 path/branch 共享 key 前綴
                            var nameToPath = new Dictionary<string, string>();
                            var nameToBranch = new Dictionary<string, string>();
                            foreach (var l in o2.Split('\n'))
                            {
                                int sp = l.IndexOf(' ');
                                if (sp <= 0) continue;
                                string key = l.Substring(0, sp);
                                string val = l.Substring(sp + 1).Trim();
                                if (key.EndsWith(".path"))
                                    nameToPath[key.Substring(0, key.Length - 5)] = val;
                                else if (key.EndsWith(".branch"))
                                    nameToBranch[key.Substring(0, key.Length - 7)] = val;
                            }
                            foreach (var kv in nameToBranch)
                            {
                                if (!nameToPath.TryGetValue(kv.Key, out string relPath)) continue;
                                string full = string.IsNullOrEmpty(owner) ? relPath : $"{owner}/{relPath}";
                                var hit = list.Find(x => x.Path == full);
                                if (hit != null) hit.GitmodulesBranch = kv.Value;
                            }
                        }
                        // 逐個問 branch / dirty / ahead-behind（未 init 的沒有工作目錄可問，跳過）
                        foreach (var s in list)
                        {
                            if (s.Uninitialized) continue;
                            string abs = Path.Combine(root, s.Path);
                            if (fetch)
                            {
                                var (ef, _, sf) = Git(abs, "fetch --quiet");
                                if (ef != 0) log.AppendLine($"⚠ fetch 失敗 {s.Path}: {FirstLine(sf)}");
                            }
                            var (e3, o3, _) = Git(abs, "rev-parse --abbrev-ref HEAD");
                            s.CurBranch = e3 == 0 ? o3.Trim() : "?";
                            if (s.CurBranch == "HEAD") s.CurBranch = "(detached)";
                            // --untracked-files=no：untracked 不擋 checkout / pull，不算 dirty ——
                            // 把它算進來會讓每個有 Library/ 殘檔的 submodule 都紅，假警報訓練人忽略警報。
                            var (e4, o4, _) = Git(abs, "status --porcelain --untracked-files=no");
                            s.Dirty = e4 == 0 && o4.Trim().Length > 0;
                            // 啟發式預設 branch（規則見 TargetBranch 註解）——
                            // branch 清單本地 + origin 一起看：submodule update 完常常一條本地
                            // branch 都沒有（detached），只看本地會讓啟發式整批失效。
                            var (e6, o6, _) = Git(abs,
                                "for-each-ref --format=%(refname:short) refs/heads refs/remotes/origin");
                            if (e6 == 0)
                            {
                                var locals = new List<string>();
                                var all = new HashSet<string>();
                                foreach (var raw6 in o6.Split('\n'))
                                {
                                    string b = raw6.Trim();
                                    if (b.Length == 0) continue;
                                    if (b.StartsWith("origin/"))
                                    {
                                        string n = b.Substring("origin/".Length);
                                        if (n != "HEAD") all.Add(n);
                                    }
                                    else
                                    {
                                        locals.Add(b);
                                        all.Add(b);
                                    }
                                }
                                // branch 清單存給下拉選單用（本地+origin 合併去重）
                                s.Branches = new List<string>(all);
                                s.Branches.Sort(StringComparer.OrdinalIgnoreCase);
                                int slash = s.Path.LastIndexOf('/');
                                string dirName = slash < 0 ? s.Path : s.Path.Substring(slash + 1);
                                if (dirName.StartsWith("UCL_", StringComparison.Ordinal))
                                    s.HeuristicBranch = "Dev";
                                else if (locals.Count == 1)
                                    s.HeuristicBranch = locals[0];
                                else if (locals.Count == 0 && all.Count == 1)
                                    foreach (var only in all) s.HeuristicBranch = only;
                                else if (all.Contains("master"))
                                    s.HeuristicBranch = "master";
                                else if (all.Contains("main"))
                                    s.HeuristicBranch = "main";
                            }
                            // remote 名清單（顯示用）—— 多 remote 的 repo 在狀態表上要看得出來，
                            // 否則「push 到所有 remote」這個開關開下去，人不知道自己剛剛推去了幾個地方。
                            var (e8, o8, _) = Git(abs, "remote");
                            if (e8 == 0)
                            {
                                var remotes = new List<string>();
                                foreach (var raw8 in o8.Split('\n'))
                                {
                                    string r = raw8.Trim();
                                    if (r.Length > 0) remotes.Add(r);
                                }
                                s.Remotes = remotes;
                            }
                            // 上次 fetch 距今（FETCH_HEAD mtime）—— staleness 逐列標，
                            // 一句全域警語會把剛 fetch 的跟三天沒動的混為一談。
                            var (e7, o7, _) = Git(abs, "rev-parse --git-dir");
                            if (e7 == 0)
                            {
                                string gd = o7.Trim();
                                if (!Path.IsPathRooted(gd)) gd = Path.Combine(abs, gd);
                                string fh = Path.Combine(gd, "FETCH_HEAD");
                                if (File.Exists(fh))
                                {
                                    var age = DateTime.Now - File.GetLastWriteTime(fh);
                                    s.FetchAge = age.TotalHours < 1 ? $"fetch {(int)age.TotalMinutes}m前"
                                        : age.TotalDays < 1 ? $"fetch {(int)age.TotalHours}h前"
                                        : $"fetch {(int)age.TotalDays}d前";
                                }
                                else
                                {
                                    s.FetchAge = "未 fetch 過";
                                }
                            }
                            // ahead/behind 對 upstream；沒 upstream（沒 track / 沒 fetch 過）就維持 -1 顯示未知
                            var (e5, o5, _) = Git(abs, "rev-list --left-right --count @{upstream}...HEAD");
                            if (e5 == 0)
                            {
                                var parts = o5.Trim().Split('\t');
                                if (parts.Length == 2
                                    && int.TryParse(parts[0], out int behind)
                                    && int.TryParse(parts[1], out int ahead))
                                {
                                    s.Behind = behind;
                                    s.Ahead = ahead;
                                }
                            }
                        }
                        log.AppendLine($"✓ 掃描完成：{list.Count} 個 submodule"
                                       + (fetch ? "（已 fetch，ahead/behind 為即時值）"
                                           : "（未 fetch，ahead/behind 以上次 fetch 為準）"));
                    }
                }
                catch (Exception e)
                {
                    log.AppendLine(e.ToString());
                }
                EditorApplication.delayCall += () =>
                {
                    m_Running = false;
                    m_RunningLabel = "";
                    // 空清單也要落地 —— 「真的沒有 submodule」是有效答案；保留舊清單會顯示
                    // 上一個 root 的內容（看起來完全正常的錯）。
                    // Note（上次批次操作結果）按 Path 沿用 —— 掃描重建的是「狀態」，
                    // 不該順手把「剛剛對它做了什麼」的紀錄洗掉。
                    foreach (var n in list)
                    {
                        var old = m_Subs.Find(x => x.Path == n.Path);
                        if (old != null) n.Note = old.Note;
                    }
                    m_Subs = list;
                    m_Scanned = true;
                    if (!quiet) m_Report = log.ToString();
                };
            });
        }

        static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf('\n');
            return i < 0 ? s : s.Substring(0, i);
        }

        // ===========================================================
        // 批次操作
        // ===========================================================
        // 區塊職責：對納入的 submodule 依序跑 切branch / pull / push
        // 物理意義：**跳過不硬上** —— dirty、detached 上有未合併 commit、解析不到目標 branch，
        //          一律列進報告跳過。本頁不 stash / 不 force / 不替人收工作區（預設值是裝填好的槍，
        //          而「自動 stash」就是往槍裡再壓一發）。
        //          Push 由深到淺：parent 的 bump commit 引用 child 的 SHA，先推 parent 會讓
        //          別人 pull 到指向不存在 commit 的 gitlink（靜默壞 —— clone 的人才會發現）。
        // 數值影響：checkout / pull 移動各 submodule HEAD；push 寫遠端。全程不動 root 的 branch。
        void RunBatch(string label, bool checkout, bool pull, bool push)
        {
            if (m_Running)
            {
                Debug.LogWarning($"[GitSubmoduleSync] 已有操作進行中（{m_RunningLabel}）— 忽略 {label}");
                return;
            }
            string root = m_Settings.Root;
            // 快照要跑的清單（背景執行緒不讀 UI 正在改的集合）
            var targets = new List<SubEntry>();
            foreach (var s in m_Subs)
            {
                if (!m_Settings.Excluded.Contains(s.Path) && !s.Uninitialized) targets.Add(s);
            }
            // push 由深到淺（路徑深度降冪）；checkout / pull 順序無硬性要求，跟著同一排序不另寫一套
            targets.Sort((a, b) => Depth(b.Path).CompareTo(Depth(a.Path)));
            bool includeRoot = m_Settings.IncludeRoot;
            string rootBranchOverride = m_Settings.DefaultBranch;
            // 同 resolvedTargets 的理由：設定欄位在背景跑到一半時仍可被編輯，
            // 邊跑邊讀會讓同一輪批次前半推 origin、後半推全部。
            bool pushAllRemotes = m_Settings.PushAllRemotes;
            // 目標 branch 在主執行緒先解析成快照 —— 背景執行緒跑到一半時，設定欄位仍可被編輯，
            // 邊跑邊讀 m_Settings 會讓同一輪批次前後用到不同的目標。
            var resolvedTargets = new Dictionary<SubEntry, string>();
            foreach (var s in targets) resolvedTargets[s] = TargetBranch(s);

            m_Running = true;
            m_RunningLabel = label;
            m_Report = $"⏳ {label} 執行中…";
            System.Threading.Tasks.Task.Run(() =>
            {
                var log = new System.Text.StringBuilder();
                // Note 先收本地清單、回主執行緒才寫回 s.* —— 背景執行緒不寫 OnGUI 正在讀的物件
                // （讀的那半在 resolvedTargets 快照守住了，寫的這半也要守）。
                var notes = new List<(SubEntry sub, string note)>();
                int ok = 0, skip = 0, fail = 0;
                try
                {
                    UCL_ProcessRegistryService.KillAllByTag(PROC_TAG);
                    foreach (var s in targets)
                    {
                        string abs = Path.Combine(root, s.Path);
                        string target = resolvedTargets[s];
                        string note = RunOne(abs, s, target, checkout, pull, push, pushAllRemotes, log);
                        notes.Add((s, note));
                        if (note.StartsWith("✓")) ok++;
                        else if (note.StartsWith("⏭")) skip++;
                        else fail++;
                    }
                    // root repo：只 pull / push，永遠不切 branch（見設定區說明）
                    if (includeRoot && (pull || push))
                    {
                        var rootEntry = new SubEntry { Path = "(root)", CurBranch = "" };
                        var (eb, ob, _) = Git(root, "rev-parse --abbrev-ref HEAD");
                        rootEntry.CurBranch = eb == 0 ? ob.Trim() : "?";
                        string rb = string.IsNullOrEmpty(rootBranchOverride)
                            ? rootEntry.CurBranch : rootBranchOverride;
                        // root 不切 branch，所以目標一律當成「目前所在」處理 —— 只有在
                        // 全域預設 branch 有填且 root 不在那條上時列 skip，提醒人自己去切。
                        if (rb != rootEntry.CurBranch || rootEntry.CurBranch == "HEAD")
                        {
                            log.AppendLine($"⏭ (root) 目前在 {rootEntry.CurBranch}，預設是 {rb} —— "
                                           + "root 不自動切，請自行處理");
                            skip++;
                        }
                        else
                        {
                            string note = RunOne(root, rootEntry, rb,
                                checkoutStep: false, pullStep: pull, pushStep: push,
                                pushAllRemotes: pushAllRemotes, log: log);
                            if (note.StartsWith("✓")) ok++;
                            else if (note.StartsWith("⏭")) skip++;
                            else fail++;
                        }
                    }
                    log.AppendLine($"\n— {label} 完成：✓{ok} ⏭{skip} ✗{fail} —");
                }
                catch (Exception e)
                {
                    log.AppendLine(e.ToString());
                }
                EditorApplication.delayCall += () =>
                {
                    m_Running = false;
                    m_RunningLabel = "";
                    m_Report = log.ToString();
                    foreach (var (sub, note) in notes) sub.Note = note;
                    // 操作完自動重掃（quiet：別蓋掉上面的批次報告）——
                    // 報告說「切好了」不算數，狀態表讀回來的才算
                    Scan(fetch: false, quiet: true);
                };
            });
        }

        static int Depth(string path) => path.Split('/').Length;

        // 區塊職責：對單一 repo 跑 切branch / pull / push，回傳一句人讀得懂的結果
        // 物理意義：三步共用一個入口 —— 「一鍵同步」與單獨按鈕走同一條路徑，不各寫一份
        //          （各寫一份的話，改一邊忘一邊的那天不會有人發現）。
        //          **安全線一律用即時值，不讀 Scan 快照**（Sirius 2026-08-07 砸磚②）：
        //          s.Dirty / s.CurBranch 是上一次掃描的照片，而這裡是 Unity Editor ——
        //          兩次點擊之間會 import asset、寫 .meta、存 scene。照片乾淨、現在髒了的話，
        //          「dirty 一律跳過」的承諾就靜默失效，報告還照印 ✓。
        //          兩條本地 git 指令的成本比起 pull/push 走網路是零頭。
        string RunOne(string abs, SubEntry s, string target,
            bool checkoutStep, bool pullStep, bool pushStep, bool pushAllRemotes,
            System.Text.StringBuilder log)
        {
            if (string.IsNullOrEmpty(target))
            {
                log.AppendLine($"⏭ {s.Path} 解析不到目標 branch（覆寫 / .gitmodules / 全域預設 / 啟發式全空）");
                return "⏭ 無目標 branch";
            }
            var (ebr, obr, _) = Git(abs, "rev-parse --abbrev-ref HEAD");
            if (ebr != 0)
            {
                log.AppendLine($"✗ {s.Path} 讀不到目前 branch —— 不動它");
                return "✗ 狀態不明";
            }
            string cur = obr.Trim() == "HEAD" ? "(detached)" : obr.Trim();
            if (checkoutStep && cur != target)
            {
                var (edt, odt, _) = Git(abs, "status --porcelain --untracked-files=no");
                if (edt != 0 || odt.Trim().Length > 0)
                {
                    log.AppendLine($"⏭ {s.Path} dirty（有未 commit 修改{(edt != 0 ? "，或 status 失敗" : "")}）"
                                   + "—— 不切 branch，請先自行處理");
                    return "⏭ dirty";
                }
                // 切之前先 fetch（Sirius 砸磚③）：下面兩道檢查都拿 origin/<target> 當尺，
                // 沒 fetch 過的尺是舊的 —— 拿過期的尺做的是「決定」（切 / 不切），不是報告。
                // 只有真的要切的 repo 才 fetch（不是全員），失敗（離線）就用本地既有 ref 繼續並記一筆。
                var (eft, _, sft) = Git(abs, "fetch --quiet");
                if (eft != 0)
                {
                    log.AppendLine($"⚠ {s.Path} fetch 失敗（{FirstLine(sft)}）—— 以下判斷用本地既有 ref");
                }
                // 安全線：HEAD 必須已在目標 branch 歷史上才切 —— detached 上的未合併 commit
                // 切走就脫錨（reflog 能救，但沒人會去看 reflog）。
                // 目標 ref：本地有拿本地、沒有拿 origin/<target>（等下 checkout 也照這個順序）。
                bool hasLocal = Git(abs, $"rev-parse --verify --quiet refs/heads/{target}").exit == 0;
                bool hasRemote = Git(abs, $"rev-parse --verify --quiet refs/remotes/origin/{target}").exit == 0;
                if (!hasLocal && !hasRemote)
                {
                    log.AppendLine($"⏭ {s.Path} 找不到 branch「{target}」（本地與剛 fetch 完的 origin 都沒有）");
                    return "⏭ branch 不存在";
                }
                string checkRef = hasLocal ? target : $"origin/{target}";
                if (Git(abs, $"merge-base --is-ancestor HEAD {checkRef}").exit != 0)
                {
                    log.AppendLine($"⏭ {s.Path} 目前 HEAD 不在「{checkRef}」歷史上（可能有未合併 commit）"
                                   + "—— 不切，請先合併後再來");
                    return "⏭ HEAD 未合併";
                }
                var (ec, _, sc) = Git(abs, hasLocal
                    ? $"checkout {target}"
                    : $"checkout -b {target} --track origin/{target}");
                if (ec != 0)
                {
                    log.AppendLine($"✗ {s.Path} checkout {target} 失敗: {FirstLine(sc)}");
                    return "✗ checkout 失敗";
                }
                cur = target;
                log.AppendLine($"✓ {s.Path} 已切到 {target}");
            }
            if (pullStep)
            {
                if (cur != target)
                {
                    log.AppendLine($"⏭ {s.Path} 不在目標 branch（{cur} ≠ {target}）—— 不 pull");
                    return "⏭ 不在目標 branch";
                }
                // ff-only：本頁不替人做 merge / rebase 的決定 —— 分岔了就 fail loud 列出來
                var (ep, op, sp) = Git(abs, $"pull --ff-only origin {target}");
                if (ep != 0)
                {
                    log.AppendLine($"✗ {s.Path} pull 失敗（可能分岔，需人工 merge/rebase）: {FirstLine(sp)}");
                    return "✗ pull 失敗";
                }
                log.AppendLine($"✓ {s.Path} pull: {FirstLine(op)}");
            }
            if (pushStep)
            {
                // 用 cur（本函式開頭剛問到的即時值）不是 s.CurBranch（掃描快照）——
                // 快照在「一鍵同步」下必定是舊的：checkout 剛把 HEAD 移到 target，而快照還停在
                // 移動前的 (detached)，於是每一個「剛被切好的」repo 都會在這裡被判成不在目標 branch
                // 而靜默跳過 push。一鍵同步推不動東西的原因就是這一行（2026-08-10 修）。
                if (cur != target)
                {
                    log.AppendLine($"⏭ {s.Path} 不在目標 branch（{cur} ≠ {target}）—— 不 push");
                    return "⏭ 不在目標 branch";
                }
                // 要推去哪：即時問 git，不讀 s.Remotes 快照（同 dirty / branch 的理由 ——
                // 快照是照片，而這裡在下決定。掃描到現在之間有人加了 remote 的話，
                // 照片會讓「推所有 remote」漏掉那一個，而漏掉不會叫）。
                var remotes = new List<string>();
                if (pushAllRemotes)
                {
                    var (er, orr, sr) = Git(abs, "remote");
                    if (er != 0)
                    {
                        log.AppendLine($"✗ {s.Path} 讀不到 remote 清單 —— 不 push: {FirstLine(sr)}");
                        return "✗ remote 讀取失敗";
                    }
                    foreach (var rawr in orr.Split('\n'))
                    {
                        string r = rawr.Trim();
                        if (r.Length > 0) remotes.Add(r);
                    }
                    if (remotes.Count == 0)
                    {
                        // 「沒有 remote」不是成功也不是失敗，是沒地方推 —— 照本頁慣例列出來跳過，
                        // 不靜默當成 ✓（那會讓報告說推完了，而其實一個位元組都沒出去）。
                        log.AppendLine($"⏭ {s.Path} 沒有設定任何 remote —— 不 push");
                        return "⏭ 無 remote";
                    }
                }
                else
                {
                    remotes.Add("origin");
                }
                // 一個 remote 失敗不中斷其他 remote —— GitHub 推成功、GitLab 認證掛掉是兩件獨立的事，
                // 因為後者放棄前者就等於白跑。但整列記成失敗（部分成功不是成功）。
                int pushOk = 0;
                var pushFailed = new List<string>();
                foreach (var remote in remotes)
                {
                    var (eu, ou, su) = Git(abs, $"push {remote} {target}");
                    if (eu != 0)
                    {
                        log.AppendLine($"✗ {s.Path} push {remote} 失敗: {FirstLine(su)}");
                        pushFailed.Add(remote);
                        continue;
                    }
                    pushOk++;
                    // push 成功的訊息在 stderr（git 的慣例），只看 stdout 會以為它沒說話
                    log.AppendLine($"✓ {s.Path} push {remote}: {FirstLine(string.IsNullOrEmpty(su) ? ou : su)}");
                }
                if (pushFailed.Count > 0)
                {
                    return pushOk > 0
                        ? $"✗ push {pushOk}/{remotes.Count}（失敗: {string.Join(",", pushFailed)}）"
                        : "✗ push 失敗";
                }
                if (remotes.Count > 1) return $"✓ push ×{remotes.Count}";
            }
            return "✓";
        }
    }
}
#endif
