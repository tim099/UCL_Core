// 區塊職責：Git 攤平同步頁 — 把含 submodule 的 repo 攤平成純檔案，同步到另一個 repo 的工作目錄
// 物理意義：UI 只負責「選來源/目標、勾要不要同步哪個 submodule、確認、看報告」；
//          實際工作全由 <UCL_Core>/Tools~/git_flatten_sync.py 做（src 只讀 git 物件、
//          dst 只寫檔案，兩邊 git 都不碰）。頁面**不自己實作任何 git 操作** ——
//          同一套邏輯要能在沒有 Editor 的環境（CI / agent）跑，所以事實來源是那支腳本。
// 數值影響：Dry-run 完全唯讀。同步會寫 dst 工作目錄；不 commit、不動任何 index / ref。
//
// 設計決策（2026-08-05 Tim 拍板，gura / Sirius 砸磚後定案 — 細節與血證見腳本檔頭）：
//   · src / dst 為**任意兩個 repo**，不綁本專案
//   · per-submodule 開關；排除父 submodule 自動連帶排除巢狀
//   · drift（父記錄 gitlink SHA ≠ submodule 磁碟 HEAD）→ **腳本端 fail closed**，
//     UI 不提供「隨便挑一個」的預設，只提供顯式選 recorded / head
//   · 設定存 EditorPrefs（JSON 字串）—— Tim 明示可接受跨機器不通用
//   · 同步按鈕必須走 UCL_OptionPage 二次確認
// RequiresConstantRepaint：腳本在背景跑，完成回呼走 EditorApplication.delayCall；
//   常駐 repaint 讓「執行中」提示與完成後的報告即時反映，不必等滑鼠移動。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.StringExtensionMethods;   // CopyToClipboard —— 既有基建，不在本頁另寫一份剪貼簿寫入
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.Page
{
    [UCL.Core.ATTR.RequiresConstantRepaint]
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_GitFlattenSyncPage.md")]
    public class UCL_GitFlattenSyncPage : UCL_CommonEditorPage
    {
        public override string WindowName => "UCL_GitFlattenSync";
        public override bool ShowInPageMenu => true;

        const string PrefKey_Settings = "UCL_GitFlattenSync.Settings";

        // Process 註冊中心的 tag —— 穩定識別字，KillAllByTag / Register / Unregister 三處共用。
        // 硬規則：C# 開的每顆外部 Process 都要登記（見 Coding_Standards.md「外部 Process」）。
        const string PROC_TAG = "git_flatten_sync";

        // 區塊職責：可保存的設定（Tim 2026-08-05：頁面設定要能下次直接讀取）
        // 物理意義：轉 JSON 存 EditorPrefs。**路徑是絕對路徑，跨機器不通用** ——
        //          Tim 明示可接受（換機器重填即可），所以不做環境變數替換那層複雜度。
        [Serializable]
        public class SyncSettings
        {
            public string Src = "";
            public string Dst = "";
            public string Mode = "";                     // "" / "recorded" / "head"
            public List<string> Excluded = new List<string>();
            public bool Prune = false;
            // 讓 dst 檔案集合與 src 一致（對應腳本 --delete-extra）。
            // 與 Prune 是**兩件事**：Prune 只認得本工具寫過的遺留（候選＝manifest），
            // 這個認的是「dst 追蹤了但 src 沒有」的檔（候選＝dst 自己的 git 清單）。
            // 存檔是安全的 —— 它的候選集合永遠限縮在「dst 的 git 還原得回來」的範圍內，
            // 這點跟 ForceOnce 不同（那個一旦存起來會蓋掉救不回來的本地修改）。
            public bool DeleteExtra = false;
            public string ManifestOverride = "";
        }

        SyncSettings m_Settings = new SyncSettings();

        // 區塊職責：由 src 掃出來的 submodule 清單（勾選用）
        // 物理意義：走腳本的 dry-run --format json 拿，**不在 C# 端另寫一套 submodule 探索** ——
        //          兩套探索遲早會不一致，而不一致的那天沒人會發現（勾選畫面看起來永遠正常）。
        public class SubEntry
        {
            public string Path = "";
            public string Owner = "";
            public string Recorded = "";
            public string Head = "";
            public bool Drift = false;
            public bool Uninitialized = false;
        }
        List<SubEntry> m_Subs = new List<SubEntry>();
        // m_Scanned 區分「還沒掃」與「掃過但真的沒有 submodule」——
        // 兩者都是 m_Subs.Count == 0，但前者要提示使用者去掃、後者該整區隱藏。
        bool m_Scanned = false;
        int m_SelectedSubIdx = 0;
        // PopupSearchCache 的內部狀態容器（對齊 UCL_ControlPanelPage 的 m_PickerDic 慣例）
        readonly UCL_ObjectDictionary m_PickerDic = new UCL_ObjectDictionary();

        string m_Report = "";
        bool m_Running = false;
        string m_RunningLabel = "";
        Vector2 m_ReportScroll = Vector2.zero;

        // 複製鈕的即時回饋（見 DrawReport）。純顯示狀態，不進 EditorPrefs ——
        // 它描述的是「剛剛那一下按成功沒有」，跨 session 保存沒有意義。
        const double COPY_HINT_SECONDS = 3.0;   // 提示存活秒數
        string m_CopyHint = "";
        double m_CopyHintAt = 0;

        // 區塊職責：腳本回傳的 exit code，用來把「拒絕原因」翻成本頁的操作指引（見 ExitHintLines）。
        // 物理意義：腳本的拒絕訊息寫的是 CLI 旗標（`--force` / `--mode`），因為它也要在沒有 Editor 的
        //          環境跑，那邊那樣寫是對的。但**在本頁按按鈕的人手上沒有命令列** ——
        //          照字面讀只會得到一個做不到的指示。所以 exit code 留在 C# 這一側，
        //          由擁有 UI 的人翻成「該動本頁哪一個控制項」。
        // 邊界：翻譯只加不改 —— 原始報告全文照樣顯示在下方，這裡只在它上面補一段指路。
        const int EXIT_DRIFT = 4;       // 父記錄與磁碟 HEAD 不一致
        const int EXIT_CONFLICT = 5;    // dst 上有本地修改
        const int EXIT_NO_DST_GIT = 7;  // 要求 --delete-extra 但 dst 不是 git repo
        int m_LastExit = 0;

        // 區塊職責：dst 是不是 git repo —— 決定「同步刪除」那個選項出不出現。
        // 物理意義：Tim 2026-08-06「這個功能可以在兩端都有 git 時才提供」。
        //          src 必為 git（工具的前提），所以實際要判的只有 dst。
        // 數值影響：純顯示閘門。**真正的把關在腳本**（dst 非 repo → exit 7 fail closed）——
        //          UI 少畫一個勾只是別讓人白按，不是防線；防線只能有一道，而它在唯一事實來源那邊。
        // 設計取捨：快取而不是每幀 File.Exists —— 本頁有 RequiresConstantRepaint，
        //          每幀敲磁碟會在大專案上變成可觀測的卡頓。Dst 字串一變就重算。
        string m_DstGitCheckedFor;
        bool m_DstIsGitRepo;
        bool DstIsGitRepo()
        {
            string dst = m_Settings.Dst ?? "";
            if (m_DstGitCheckedFor == dst) return m_DstIsGitRepo;
            m_DstGitCheckedFor = dst;
            // `.git` 可能是目錄（一般 repo）也可能是檔案（submodule / worktree）—— 兩種都算。
            // 只判目錄的話，掛成 submodule 的 dst 會被誤判成「沒有 git」而少一個選項，
            // 而使用者只會看到選項不見了，不會知道為什麼。
            string g = string.IsNullOrEmpty(dst) ? null : Path.Combine(dst, ".git");
            m_DstIsGitRepo = g != null && (Directory.Exists(g) || File.Exists(g));
            return m_DstIsGitRepo;
        }

        // 區塊職責：一次性的 `--force`（放行 warn 等級防呆）。
        // 物理意義：**刻意不進 EditorPrefs、且每次執行後歸零。** 其餘設定存起來是方便，
        //          這一個存起來是災難：它的語意是「這一次我認了」，不是「以後都算了」。
        //          設定一次就永久生效的覆寫開關，就是預設值裝填好的槍 ——
        //          下一次同步會安靜地蓋掉 dst 上的本地修改，而畫面跟正常成功一模一樣。
        bool m_ForceOnce = false;
        GUIStyle m_MonoStyle;
        GUIStyle MonoStyle => m_MonoStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            wordWrap = false,
            richText = false,
        };

        public static UCL_GitFlattenSyncPage Create() => UCL_EditorPage.Create<UCL_GitFlattenSyncPage>();

        public override void Init(UCL_GUIPageController p_Controller)
        {
            base.Init(p_Controller);
            LoadSettings();
            // 進頁面自動掃一次 submodule 清單（Tim 2026-08-06）。
            // 理由不只是省一次點擊：設定是從 EditorPrefs 讀回來的，**可能是好幾天前的**，
            // 而 submodule 清單同一時間可能已經增刪。不掃就會拿舊勾選狀態配新 repo，
            // 而畫面看起來完全正常 —— 使用者不會知道自己在看一份過期的清單。
            RefreshSubmodules();
        }

        // 掃 submodule 清單 —— 進頁面與「重整」鈕共用同一條路徑（唯一入口）。
        // 兩處各寫一份參數的話，改一邊忘一邊會讓「重整」與「自動掃」得到不同結果。
        void RefreshSubmodules()
        {
            if (string.IsNullOrEmpty(m_Settings.Src)) return;
            RunScript(new List<string> { "--src", m_Settings.Src, "--list-submodules" }, "scan");
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
                // 壞掉的設定不可靜默吞掉：使用者會以為「設定沒存到」而重填，
                // 而真正的問題是那串 JSON 壞了。留 warning。
                Debug.LogWarning($"[GitFlattenSync] 設定讀取失敗，改用預設值: {e.Message}");
            }
            m_Settings.Excluded ??= new List<string>();
            if (string.IsNullOrEmpty(m_Settings.Src)) m_Settings.Src = UCL_RepoPath.RepoRoot;
        }

        void SaveSettings()
        {
            try
            {
                EditorPrefs.SetString(PrefKey_Settings, JsonUtility.ToJson(m_Settings));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GitFlattenSync] 設定保存失敗: {e.Message}");
            }
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button(UCL_CodeLocalize.Get("LoginStatus.Btn.Refresh"), UCL_GUIStyle.ButtonStyle,
                    GUILayout.ExpandWidth(false)))
            {
                // 只掃 submodule 清單 —— 不需要 dst、不受 drift / 未 init 的 fail closed 影響。
                // 清單是「這個 repo 有什麼」，同步條件是「能不能跑」，兩件事分開問。
                RefreshSubmodules();
            }
        }

        protected override void ContentOnGUI()
        {
            DrawPaths();
            DrawSubmodules();
            DrawOptions();
            DrawActions();
            DrawReport();
        }

        // ===========================================================
        // 路徑
        // ===========================================================
        void DrawPaths()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                DrawPathRow("來源 repo (src)", ref m_Settings.Src);
                DrawPathRow("目標 repo (dst)", ref m_Settings.Dst);
                // 防呆提示在 UI 就先講，不用等腳本擋 —— 但**擋的權責在腳本**（唯一事實來源）
                if (!string.IsNullOrEmpty(m_Settings.Dst) && Directory.Exists(m_Settings.Dst))
                {
                    if (File.Exists(Path.Combine(m_Settings.Dst, "Temp", "UnityLockfile")))
                    {
                        GUILayout.Label("🚫 目標有 Temp/UnityLockfile —— Unity 正開著那個專案，"
                                        + "同步會覆蓋正在編輯的本地內容。腳本端會拒絕執行。",
                            UCL_GUIStyle.GetLabelStyle(new Color(1f, 0.4f, 0.4f)));
                    }
                    else if (Directory.Exists(Path.Combine(m_Settings.Dst, "ProjectSettings"))
                             && Directory.Exists(Path.Combine(m_Settings.Dst, "Assets")))
                    {
                        GUILayout.Label("⚠ 目標是一個 Unity 專案 —— 同步會直接覆蓋它 Assets/ 等路徑下的檔案。"
                                        + "先按「試跑」看衝突清單。",
                            UCL_GUIStyle.GetLabelStyle(new Color(1f, 0.85f, 0.4f)));
                    }
                }
            }
        }

        void DrawPathRow(string label, ref string value)
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label(label, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                string next = GUILayout.TextField(value ?? "", UCL_GUIStyle.TextFieldStyle);
                if (next != value)
                {
                    value = next;
                    SaveSettings();
                }
                if (GUILayout.Button("…", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    string picked = EditorUtility.OpenFolderPanel(label, value ?? "", "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        value = picked.Replace('/', Path.DirectorySeparatorChar);
                        SaveSettings();
                        GUI.FocusControl(null);
                    }
                }
            }
        }

        // ===========================================================
        // submodule 勾選
        // ===========================================================
        // 區塊職責：submodule 選單 + 逐項同步開關
        // 物理意義：清單由腳本 `--list-submodules` 提供（**含被排除的**）。
        //          ⚠ 我第一版吃 dry-run 的 `inputs`，而那只含**納入**的 submodule ——
        //            於是取消勾選之後那一列就消失、使用者無法還原。清單與勾選狀態是兩件事，
        //            清單必須是「全部」，勾選才是「要不要」。
        // 邊界：src 沒有 submodule 時**整區隱藏** ——
        //      `UCL_GUILayout.PopupSearchCache` 在選項為 0 時會 LogError，
        //      所以「若無則隱藏」不只是版面問題，是不能呼叫它。
        void DrawSubmodules()
        {
            if (m_Subs.Count == 0)
            {
                // 有 src 但還沒掃 → 給一行提示；掃過確定是 0 → 什麼都不畫（真的沒有 submodule）
                if (!m_Scanned)
                {
                    GUILayout.Label("(尚未掃描 submodule — 按上方 Refresh)", UCL_GUIStyle.LabelStyle);
                }
                return;
            }

            GUILayout.Label($"Submodule 同步開關（{m_Subs.Count} 個，"
                            + $"排除 {CountEffectivelyExcluded()} 個）", UCL_GUIStyle.LabelStyle);
            using (new GUILayout.VerticalScope("box"))
            {
                // ── 下拉選單：submodule 多的時候用來快速定位（Tim 2026-08-05 指定 PopupSearchCache）──
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("選擇 submodule", UCL_GUIStyle.LabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    var labels = new List<string>();
                    foreach (var s in m_Subs)
                    {
                        labels.Add(BlockedByParent(s.Path) ? $"{s.Path}  (父被排除 → 屏蔽)"
                            : m_Settings.Excluded.Contains(s.Path) ? $"{s.Path}  (排除)"
                            : s.Path);
                    }
                    m_SelectedSubIdx = UCL_GUILayout.PopupSearchCache(
                        Mathf.Clamp(m_SelectedSubIdx, 0, m_Subs.Count - 1),
                        labels, m_PickerDic, "FlattenSubPicker");
                    var sel = m_Subs[Mathf.Clamp(m_SelectedSubIdx, 0, m_Subs.Count - 1)];
                    GUILayout.FlexibleSpace();
                    DrawToggleFor(sel, "同步這個");
                }

                // ── 全清單：狀態一覽 + 逐項開關 ──
                foreach (var s in m_Subs)
                {
                    bool blocked = BlockedByParent(s.Path);
                    using (new GUILayout.HorizontalScope())
                    {
                        DrawToggleFor(s, "");
                        GUILayout.Label(s.Path, blocked ? DimLabelStyle : UCL_GUIStyle.LabelStyle,
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(420)));
                        GUILayout.Label(Short(s.Recorded), UCL_GUIStyle.LabelStyle,
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                        GUILayout.Label(Short(s.Head), UCL_GUIStyle.LabelStyle,
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                        if (blocked)
                        {
                            GUILayout.Label("⛔ 父被排除 → 無論本身設定都被屏蔽", DimLabelStyle);
                        }
                        else if (s.Uninitialized)
                        {
                            GUILayout.Label("⛔ 未 init（內容不在本機，腳本會拒絕執行）",
                                UCL_GUIStyle.GetLabelStyle(new Color(1f, 0.4f, 0.4f)));
                        }
                        else if (s.Drift)
                        {
                            GUILayout.Label("⚠ 父記錄≠磁碟 HEAD",
                                UCL_GUIStyle.GetLabelStyle(new Color(1f, 0.85f, 0.4f)));
                        }
                    }
                }
                GUILayout.Label("  ↳ 取消勾選父 submodule 時，其下巢狀**無論自己勾不勾都被屏蔽**；"
                                + "但它們自己的設定會保留，父恢復同步後就回到原本的選擇。",
                    UCL_GUIStyle.LabelStyle);
            }
        }

        // 區塊職責：畫某個 submodule 的同步開關
        // 物理意義：被父屏蔽時 toggle **禁用但不改值** —— 值改掉的話，父恢復同步後
        //          使用者原本的選擇就永久遺失了（而那個遺失是靜默的）。
        void DrawToggleFor(SubEntry s, string label)
        {
            bool blocked = BlockedByParent(s.Path);
            bool inc = !m_Settings.Excluded.Contains(s.Path);
            using (new EditorGUI.DisabledScope(blocked))
            {
                bool shown = inc && !blocked;   // 顯示為「不同步」，但底下存的值不動
                bool next = GUILayout.Toggle(shown, label,
                    string.IsNullOrEmpty(label) ? GUILayout.Width(UCL_GUIStyle.GetScaledSize(20))
                        : GUILayout.ExpandWidth(false));
                if (!blocked && next != inc)
                {
                    if (next) m_Settings.Excluded.Remove(s.Path);
                    else if (!m_Settings.Excluded.Contains(s.Path)) m_Settings.Excluded.Add(s.Path);
                    SaveSettings();
                }
            }
        }

        // 區塊職責：這個路徑有沒有任何**祖先** submodule 被排除
        // 物理意義：Tim 2026-08-05 明示的規則 —— nested 的 root 被屏蔽時，child 無論設定都被屏蔽。
        //          判準用路徑前綴（`a/b` 是 `a` 的後代），與腳本端 cascade_exclude 同一套語意。
        //          比對前綴而不是只看直接 owner —— 三層巢狀時中間那層若被排除，最深那層也要屏蔽。
        bool BlockedByParent(string path)
        {
            foreach (var ex in m_Settings.Excluded)
            {
                if (path.Length > ex.Length && path.StartsWith(ex + "/")) return true;
            }
            return false;
        }

        int CountEffectivelyExcluded()
        {
            int n = 0;
            foreach (var s in m_Subs)
            {
                if (m_Settings.Excluded.Contains(s.Path) || BlockedByParent(s.Path)) n++;
            }
            return n;
        }

        GUIStyle m_DimLabelStyle;
        GUIStyle DimLabelStyle => m_DimLabelStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
        };

        // 警示字色 —— 只給「這一下會弄壞東西」的說明用，不拿來當一般強調。
        // 到處都紅就等於沒有紅（假警報訓練人忽略警報）。
        GUIStyle m_WarnStyle;
        GUIStyle WarnStyle => m_WarnStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            wordWrap = true,
            normal = { textColor = new Color(1f, 0.55f, 0.35f) },
        };

        static string Short(string sha) => string.IsNullOrEmpty(sha) ? "—"
            : (sha.Length > 8 ? sha.Substring(0, 8) : sha);

        // ===========================================================
        // 選項
        // ===========================================================
        void DrawOptions()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("攤平基準", UCL_GUIStyle.LabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    // 刻意沒有「自動」選項：drift 時腳本 fail closed，選擇必須是人做的顯式手勢。
                    // 「父記錄」靜默少東西、「磁碟 HEAD」靜默多一份無法回溯的東西，兩種都外觀成功。
                    string[] opts = { "(未指定 — 有 drift 時腳本會拒絕)", "recorded 父記錄的 gitlink SHA",
                                      "head submodule 磁碟 HEAD" };
                    int cur = m_Settings.Mode == "recorded" ? 1 : m_Settings.Mode == "head" ? 2 : 0;
                    int next = GUILayout.SelectionGrid(cur, opts, 1, UCL_GUIStyle.ButtonStyle);
                    if (next != cur)
                    {
                        m_Settings.Mode = next == 1 ? "recorded" : next == 2 ? "head" : "";
                        SaveSettings();
                    }
                }
                GUILayout.BeginHorizontal();
                bool prune = UCL_GUILayout.CheckBox(m_Settings.Prune);
                GUILayout.Label(" 清除 stale（刪掉「上次同步寫過、這次來源已沒有」的檔；首次同步不刪）", UCL_GUIStyle.LabelStyle);
                GUILayout.EndHorizontal();
                if (prune != m_Settings.Prune) { m_Settings.Prune = prune; SaveSettings(); }

                // 覆寫開關 —— 對應腳本的 `--force`。在此之前面板根本沒有這個控制項，
                // 於是腳本印「確認要蓋才加 --force」時，按按鈕的人是走進死路的：
                // 提示要求的動作在他的介面上不存在（名字比事實大的鄰居 —— 指示比能力大）。
                // 它與其他選項不同：**不存檔、執行後歸零**（見 m_ForceOnce 註解）。
                // 同步刪除 —— **只在 dst 也是 git repo 時才出現**（Tim 2026-08-06「兩端都有 git 才提供」）。
                // 它靠 dst 的追蹤清單決定刪誰，也靠 dst 的 git 才還原得回來；
                // 沒有 git 就兩者皆無，那時提供這個勾等於提供一個沒有還原點的刪除鍵。
                if (DstIsGitRepo())
                {
                    GUILayout.BeginHorizontal();
                    bool del = UCL_GUILayout.CheckBox(m_Settings.DeleteExtra);
                    GUILayout.Label(" 同步刪除（--delete-extra）— 刪掉「dst 有追蹤、但 src 已經沒有」的檔，讓兩邊檔案集合一致",
                        UCL_GUIStyle.LabelStyle);
                    GUILayout.EndHorizontal();
                    if (del != m_Settings.DeleteExtra) { m_Settings.DeleteExtra = del; SaveSettings(); }
                    if (m_Settings.DeleteExtra)
                    {
                        GUILayout.Label("　　候選只取 dst **有追蹤**的檔（untracked / Library 等一律不碰）；"
                                        + "排除的 submodule 底下不動。刪錯可用 dst 自己的 git 還原。",
                            DimLabelStyle);
                    }
                }
                else if (!string.IsNullOrEmpty(m_Settings.Dst))
                {
                    // 不靜默隱藏 —— 選項憑空消失時，使用者只會以為工具沒有這個功能。
                    GUILayout.Label("　（dst 不是 git repo → 不提供「同步刪除」：沒有追蹤清單可判斷刪誰，也沒有還原點）",
                        DimLabelStyle);
                }

                // 覆寫開關 —— 對應腳本的 `--force`。在此之前面板根本沒有這個控制項，
                // 於是腳本印「確認要蓋才加 --force」時，按按鈕的人是走進死路的：
                // 提示要求的動作在他的介面上不存在（名字比事實大的鄰居 —— 指示比能力大）。
                // 它與其他選項不同：**不存檔、執行後歸零**（見 m_ForceOnce 註解）。
                GUILayout.BeginHorizontal();
                // ⚠ 這裡的 current value 必須是 m_ForceOnce 自己。
                // 2026-08-06 一度誤傳 m_Settings.Prune：勾「清除 stale」會讓覆寫授權
                // **一起靜默打開**，而且覆寫鈕自己永遠勾不動（每幀被 Prune 覆寫回去）。
                // 一個「勾 A 會打開 B」的 UI 不會報錯，只會在某次同步把本地修改蓋掉。
                m_ForceOnce = UCL_GUILayout.CheckBox(m_ForceOnce);
                GUILayout.Label(" ⚠ 覆寫 dst 上被本地改過的檔（--force）— 僅本次有效，執行後自動關閉", UCL_GUIStyle.LabelStyle);
                GUILayout.EndHorizontal();

                if (m_ForceOnce)
                {
                    GUILayout.Label("　　本地修改會被蓋掉且救不回來（dst 的 git 不會被動，所以沒有還原點）。",
                        WarnStyle);
                }
            }
        }

        // 區塊職責：把腳本的 exit code 翻成「在本頁該動哪一個控制項」。
        // 物理意義：見 m_LastExit 註解 —— 腳本講 CLI，本頁講按鈕，各自講自己擁有的那一層。
        // 數值影響：純顯示，不改任何設定；使用者仍須自己動手（不代按）。
        List<string> ExitHintLines()
        {
            var o = new List<string>();
            if (m_LastExit == EXIT_DRIFT)
            {
                o.Add("🧭 本頁怎麼做：上方「攤平基準」目前是「(未指定)」，改選 "
                      + "「recorded 父記錄的 gitlink SHA」或「head submodule 磁碟 HEAD」。");
                o.Add("　 兩者都會外觀成功：recorded 會少掉尚未 bump 的內容，"
                      + "head 會多一份無法回溯的內容。也可以先把來源的 parent bump 做完，drift 就消失。");
            }
            else if (m_LastExit == EXIT_CONFLICT)
            {
                o.Add("🧭 本頁怎麼做：勾選上方「⚠ 覆寫 dst 上被本地改過的檔（--force）」再按一次「同步到目標」。");
                o.Add("　 那個勾**只對這一次有效**，執行後會自動關閉 —— 不會變成以後的預設。");
            }
            else if (m_LastExit == EXIT_NO_DST_GIT)
            {
                o.Add("🧭 本頁怎麼做：目標 repo 不是 git repo，所以「同步刪除」無法執行 —— "
                      + "取消勾選它，或把目標換成一個 git repo。");
                o.Add("　 這不是保守：刪除候選要靠 dst 的追蹤清單決定，還原也要靠它的 git。"
                      + "兩者都沒有時，刪下去就真的沒了。");
            }
            return o;
        }

        // ===========================================================
        // 動作
        // ===========================================================
        void DrawActions()
        {
            using (new GUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(m_Running))
                {
                    if (GUILayout.Button("試跑（唯讀，不寫任何檔）",
                            UCL_GUIStyle.GetButtonStyle(new Color(0.55f, 0.8f, 1f)),
                            GUILayout.ExpandWidth(false)))
                    {
                        RunScript(BuildArgs(dryRun: true, jsonFormat: false), "dry-run");
                    }
                    // 同步一律走 UCL_OptionPage 二次確認（Tim 2026-08-05 明示）
                    if (GUILayout.Button("同步到目標",
                            UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.6f, 0.35f)),
                            GUILayout.ExpandWidth(false)))
                    {
                        ConfirmAndSync();
                    }
                }
                if (m_Running)
                {
                    GUILayout.Label($"⏳ 執行中（{m_RunningLabel}）— 大量檔案時需要數分鐘",
                        UCL_GUIStyle.LabelStyle);
                }
            }
        }

        // 區塊職責：同步前的二次確認彈窗
        // 物理意義：Create 只推彈窗、不做事；真正執行在 callback（下一幀）——
        //          跟 UCL_ChatTavernAdminPage 取消註冊同一個慣例。
        void ConfirmAndSync()
        {
            int excluded = m_Settings.Excluded?.Count ?? 0;
            string body =
                $"來源: {m_Settings.Src}\n目標: {m_Settings.Dst}\n"
                + $"基準: {(string.IsNullOrEmpty(m_Settings.Mode) ? "未指定" : m_Settings.Mode)}\n"
                + $"排除 submodule: {excluded} 個\n"
                + $"清除 stale: {(m_Settings.Prune ? "是" : "否")}\n"
                + $"同步刪除: {(m_Settings.DeleteExtra ? "是（dst 追蹤但 src 沒有的檔會被刪）" : "否")}\n\n"
                + "這會**直接覆寫目標工作目錄裡的檔案**（不 commit、不動目標的 git）。\n"
                // 兩種情況要講的是**不同的一句話**。共用一句「會被擋下」在 force 打開時是假的，
                // 而二次確認彈窗說謊比沒有彈窗更糟：它讓人以為自己被保護著。
                + (m_ForceOnce
                    ? "⚠ 已勾「覆寫本地修改（--force）」：目標上被改過的檔**會直接被蓋掉**，"
                      + "而 dst 的 git 不會被動，所以沒有還原點。\n"
                    : "目標上被本地改過的檔會先被擋下並列出，不會被無聲蓋掉。\n")
                + "還沒試跑過的話，建議先按「試跑」看清單。";
            UCL_OptionPage.Create("確認同步到目標？", body,
                new ButtonData("同步", () => RunScript(BuildArgs(dryRun: false, jsonFormat: false), "sync"),
                    UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.5f, 0.3f))),
                new ButtonData("取消"));
        }

        void DrawReport()
        {
            if (string.IsNullOrEmpty(m_Report)) return;
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("報告", UCL_GUIStyle.LabelStyle);
                // 複製鈕（Tim 2026-08-06）：下方 TextArea 本來就能選取，但**「能選」跟「選得到」是兩回事** ——
                // 報告關在固定高度的 ScrollView 裡，錯誤動輒數十行、dry-run 更是上千行，
                // 要貼給人看得先滾到底再拖選。一鍵拿全文才是這個區塊實際被使用的方式。
                // 複製的是**整份報告**（stderr + stdout + exit code）而不是只取 stderr：
                // 貼錯誤訊息時，exit code 與它前面那幾行 stdout 往往才是判斷的依據，
                // 只給 stderr 等於替讀的人先做了一次「哪些不重要」的判斷。
                if (GUILayout.Button("複製", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    m_Report.CopyToClipboard();
                    // 讀回來才算數：systemCopyBuffer 在剪貼簿被別的程式鎖住時會**靜默失敗**，
                    // 而「沒複製到」與「複製成功」在畫面上長得一模一樣（按鈕都彈回來）。
                    // 一個不會叫的失敗最難抓 —— 所以這裡回讀比對，並把字元數印出來當可對帳的證據。
                    m_CopyHint = GUIUtility.systemCopyBuffer == m_Report
                        ? $"✓ 已複製 {m_Report.Length} 字元"
                        : "✗ 複製失敗（剪貼簿被占用？）";
                    m_CopyHintAt = EditorApplication.timeSinceStartup;
                }
                // 提示只活 COPY_HINT_SECONDS 秒 —— 常駐會讓人分不清這次按的還是上次按的。
                // 本頁有 RequiresConstantRepaint，時間到自然消失，不必另外排 repaint。
                if (!string.IsNullOrEmpty(m_CopyHint)
                    && EditorApplication.timeSinceStartup - m_CopyHintAt < COPY_HINT_SECONDS)
                {
                    GUILayout.Label(m_CopyHint, UCL_GUIStyle.LabelStyle);
                }
                GUILayout.FlexibleSpace();
            }
            // 操作指引排在報告**上方** —— 拒絕訊息在數千行報告的最後一行，
            // 而滾到底才看得到的指引，跟沒有指引差別不大。
            var hints = ExitHintLines();
            if (hints.Count > 0)
            {
                using (new GUILayout.VerticalScope("box"))
                {
                    foreach (var h in hints) GUILayout.Label(h, WarnStyle);
                }
            }
            using (var sv = new GUILayout.ScrollViewScope(m_ReportScroll, GUILayout.MinHeight(
                       UCL_GUIStyle.GetScaledSize(260))))
            {
                m_ReportScroll = sv.scrollPosition;
                // 唯讀 TextArea：報告要能被選取複製（貼進 commit 訊息 / 酒館），不是只能看
                EditorGUILayout.TextArea(m_Report, MonoStyle);
            }
        }

        // ===========================================================
        // 腳本呼叫
        // ===========================================================
        string ScriptPath()
        {
            string core = UCL_EditorPath.CorePath;
            if (string.IsNullOrEmpty(core)) return null;
            return Path.GetFullPath(Path.Combine(UCL_RepoPath.UnityProjectRoot, core,
                "Tools~", "git_flatten_sync.py"));
        }

        List<string> BuildArgs(bool dryRun, bool jsonFormat)
        {
            var a = new List<string>
            {
                "--src", m_Settings.Src ?? "",
                "--dst", m_Settings.Dst ?? "",
            };
            if (!string.IsNullOrEmpty(m_Settings.Mode)) { a.Add("--mode"); a.Add(m_Settings.Mode); }
            if (m_Settings.Excluded != null && m_Settings.Excluded.Count > 0)
            {
                a.Add("--exclude");
                a.Add(string.Join(",", m_Settings.Excluded));
            }
            if (!string.IsNullOrEmpty(m_Settings.ManifestOverride))
            {
                a.Add("--manifest"); a.Add(m_Settings.ManifestOverride);
            }
            if (m_Settings.Prune) a.Add("--prune");
            // --delete-extra 在 dry-run 也要帶：它是報告的一部分（要先看清單才敢按同步），
            // 而 dry-run 完全唯讀，帶了不會刪任何東西。
            // 這點跟 --force 相反 —— 那個在 dry-run 帶了會**消音衝突清單**，所以只在 apply 帶。
            if (m_Settings.DeleteExtra) a.Add("--delete-extra");
            // --force 只在**真的要寫**時才帶：dry-run 是唯讀的，帶了不會改變它讀到什麼，
            // 只會讓試跑報告不再列出衝突 —— 那等於把唯一一次看清單的機會消音。
            if (!dryRun && m_ForceOnce) a.Add("--force");
            if (!dryRun) a.Add("--apply");
            if (jsonFormat) { a.Add("--format"); a.Add("json"); }
            return a;
        }

        // 區塊職責：背景跑腳本，完成後回主線程更新 UI
        // 物理意義：同步大量檔案要數分鐘 —— 在主線程跑會凍住整個 Editor（含 AgentCommand watcher）。
        //          stdout / stderr 必須**同時非阻塞讀取**：只讀一個 stream 時，child 寫另一個
        //          把 buffer 填滿 → child 卡在 write、caller 卡在讀 → 永久 deadlock。
        //          （這條在本專案踩過不只一次，包含這支工具的 Python 端。）
        // 數值影響：只讀腳本輸出；不碰 Unity 資產。回主線程只更新 m_Report / m_Subs。
        void RunScript(List<string> args, string label)
        {
            string script = ScriptPath();
            if (string.IsNullOrEmpty(script) || !File.Exists(script))
            {
                m_Report = $"✗ 找不到 git_flatten_sync.py（解析結果: {script}）";
                return;
            }
            if (m_Running)
            {
                Debug.LogWarning($"[GitFlattenSync] 已有操作進行中（{m_RunningLabel}）— 忽略 {label}");
                return;
            }
            m_Running = true;
            m_RunningLabel = label;
            m_Report = $"⏳ {label} 執行中…";
            // 掃清單模式才解析 json —— 判準用 `--list-submodules` 這個旗標本身，
            // 不用「args 裡有 json 字樣」（那會被 --format json 的一般 dry-run 誤命中）。
            bool wantJson = args.Contains("--list-submodules");

            var argList = new List<string>(args);
            string argLine = "\"" + script + "\" " + string.Join(" ",
                argList.ConvertAll(x => x.StartsWith("--") ? x : "\"" + x + "\""));

            System.Threading.Tasks.Task.Run(() =>
            {
                var so = new System.Text.StringBuilder();
                var se = new System.Text.StringBuilder();
                int exit = -1;
                int pid = -1;
                try
                {
                    using (var p = new Process())
                    {
                        p.StartInfo.FileName = "python";
                        p.StartInfo.Arguments = argLine;
                        p.StartInfo.UseShellExecute = false;
                        p.StartInfo.RedirectStandardOutput = true;
                        p.StartInfo.RedirectStandardError = true;
                        p.StartInfo.CreateNoWindow = true;
                        p.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
                        p.StartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
                        p.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                        p.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
                        p.ErrorDataReceived += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };
                        // 區塊職責：spawn 前先收掉同 tag 的舊 process（singleton 語意）
                        // 物理意義：全量同步可能跑數分鐘，而 domain reload / recompile 會清掉 C# 的
                        //          Process 物件 —— **但 OS 層的 python 不會跟著死**。
                        //          沒有這道 guard，每次重編再按一次就多一顆孤兒，累積成屍潮
                        //          （重複開 process 直到電腦卡死，Tim 遇過）。
                        //          KillAllByTag 的身分是從磁碟記錄讀回來的，所以跨 domain reload 仍有效。
                        UCL_ProcessRegistryService.KillAllByTag(PROC_TAG);
                        p.Start();
                        // spawn 後立刻登記 —— 身分 = PID + name + start time（只憑 PID 會誤殺被回收的 PID）
                        UCL_ProcessRegistryService.Register(p, PROC_TAG,
                            $"git_flatten_sync.py（{label}）", nameof(UCL_GitFlattenSyncPage));
                        pid = p.Id;
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        // 30 分鐘上限：三萬檔的全量寫入 + 逐檔驗證確實會跑很久（效能不是本工具的優先）。
                        // 命中這個上限代表真的異常，不是「檔案多」。
                        if (!p.WaitForExit(30 * 60 * 1000))
                        {
                            se.AppendLine("[GitFlattenSync] 30 分鐘未結束 — 已放棄等待（行程可能仍在跑）");
                        }
                        else
                        {
                            exit = p.ExitCode;
                        }
                    }
                }
                catch (Exception e)
                {
                    se.AppendLine(e.ToString());
                }
                finally
                {
                    // 反登記放 finally —— 例外路徑也要清，否則記錄檔留著一個已死的 PID，
                    // 下次 KillAllByTag 會對它做身分驗證（會判 Dead 而跳過，不誤殺），
                    // 但留著的殘檔會讓 UCL_ProcessAdminPage 顯示不存在的 process。
                    if (pid > 0) UCL_ProcessRegistryService.Unregister(pid, PROC_TAG);
                }
                string stdout = so.ToString();
                string stderr = se.ToString();
                EditorApplication.delayCall += () =>
                {
                    m_Running = false;
                    m_RunningLabel = "";
                    m_LastExit = exit;              // 留著把拒絕原因翻成本頁操作指引（見 ExitHintLines）
                    // 覆寫授權用完即棄：它的語意是「這一次我認了」。
                    // 放在這裡而不是送出參數時歸零 —— 送出時就關掉的話，執行中畫面會顯示未勾選，
                    // 而那時它其實正在生效，畫面會說一個當下不成立的話。
                    m_ForceOnce = false;
                    m_Report = (string.IsNullOrEmpty(stderr) ? "" : $"— stderr —\n{stderr}\n")
                               + stdout
                               + $"\n— exit code: {exit} —";
                    if (wantJson) ParseScanJson(stdout);
                };
            });
        }

        // 區塊職責：吃腳本 --format json 的輸出，更新 submodule 勾選清單
        // 物理意義：清單的事實來源是腳本（C# 端不另寫一套 submodule 探索 —— 兩套遲早不一致，
        //          而不一致的那天沒人會發現：勾選畫面看起來永遠正常）。
        // 邊界：drift 導致腳本 exit 4 時沒有 json（它印的是拒絕報告）——
        //      那時保留上一次的清單並在報告區顯示原因，不要清空成「沒有 submodule」。
        void ParseScanJson(string stdout)
        {
            try
            {
                int jsonStart = stdout.IndexOf('{');
                if (jsonStart < 0) return;
                var jd = UCL.Core.JsonLib.JsonData.ParseJson(stdout.Substring(jsonStart));
                if (jd == null || !jd.IsObject) return;
                var arr = jd.Get("submodules");
                if (arr == null || !arr.IsArray) return;
                var list = new List<SubEntry>();
                for (int i = 0; i < arr.Count; i++)
                {
                    var e = arr[i];
                    if (e == null || !e.IsObject) continue;
                    list.Add(new SubEntry
                    {
                        Path = e.GetString("path", ""),
                        Owner = e.GetString("owner", ""),
                        Recorded = e.GetString("recorded_sha", ""),
                        Head = e.GetString("head_sha", ""),
                        Drift = e.GetBool("drift", false),
                        Uninitialized = e.GetBool("uninitialized", false),
                    });
                }
                // **空清單也要落地** —— 「這個 repo 真的沒有 submodule」是有效答案，
                // 而 `if (count > 0)` 會讓它保留上一個 src 的清單，於是換了 src 之後
                // 畫面還顯示舊 repo 的 submodule（看起來完全正常的錯）。
                m_Subs = list;
                m_Scanned = true;
                m_SelectedSubIdx = 0;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GitFlattenSync] scan json 解析失敗: {e.Message}");
            }
        }
    }
}
#endif
