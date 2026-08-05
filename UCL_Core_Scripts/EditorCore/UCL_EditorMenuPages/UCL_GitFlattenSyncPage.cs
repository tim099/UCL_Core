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
            public string ManifestOverride = "";
        }

        SyncSettings m_Settings = new SyncSettings();

        // 區塊職責：由 src 掃出來的 submodule 清單（勾選用）
        // 物理意義：走腳本的 dry-run --format json 拿，**不在 C# 端另寫一套 submodule 探索** ——
        //          兩套探索遲早會不一致，而不一致的那天沒人會發現（勾選畫面看起來永遠正常）。
        public class SubEntry
        {
            public string Path = "";
            public string Recorded = "";
            public string Head = "";
            public bool Drift = false;
        }
        List<SubEntry> m_Subs = new List<SubEntry>();

        string m_Report = "";
        bool m_Running = false;
        string m_RunningLabel = "";
        Vector2 m_ReportScroll = Vector2.zero;
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
                RunScript(BuildArgs(dryRun: true, jsonFormat: true), "scan");
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
                string next = GUILayout.TextField(value ?? "", UCL_GUIStyle.LabelStyle);
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
        void DrawSubmodules()
        {
            GUILayout.Label($"Submodule 同步開關（{m_Subs.Count} 個）", UCL_GUIStyle.LabelStyle);
            using (new GUILayout.VerticalScope("box"))
            {
                if (m_Subs.Count == 0)
                {
                    GUILayout.Label("(尚未掃描 — 按上方 Refresh 或「試跑」)", UCL_GUIStyle.LabelStyle);
                    return;
                }
                foreach (var s in m_Subs)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        bool inc = !m_Settings.Excluded.Contains(s.Path);
                        bool next = GUILayout.Toggle(inc, "", GUILayout.Width(UCL_GUIStyle.GetScaledSize(20)));
                        if (next != inc)
                        {
                            if (next) m_Settings.Excluded.Remove(s.Path);
                            else if (!m_Settings.Excluded.Contains(s.Path)) m_Settings.Excluded.Add(s.Path);
                            SaveSettings();
                        }
                        GUILayout.Label(s.Path, UCL_GUIStyle.LabelStyle,
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(420)));
                        GUILayout.Label(Short(s.Recorded), UCL_GUIStyle.LabelStyle,
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                        GUILayout.Label(Short(s.Head), UCL_GUIStyle.LabelStyle,
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                        if (s.Drift)
                        {
                            GUILayout.Label("⚠ 父記錄≠磁碟 HEAD",
                                UCL_GUIStyle.GetLabelStyle(new Color(1f, 0.85f, 0.4f)));
                        }
                    }
                }
                // 勾掉父 submodule 時，腳本會自動連帶排除巢狀 —— 這裡明說，免得使用者以為漏了
                GUILayout.Label("  ↳ 取消勾選父 submodule 時，其底下的巢狀 submodule 會自動一併排除。",
                    UCL_GUIStyle.LabelStyle);
            }
        }

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
                bool prune = GUILayout.Toggle(m_Settings.Prune,
                    " 清除 stale（刪掉「上次同步寫過、這次來源已沒有」的檔；首次同步不刪）");
                if (prune != m_Settings.Prune) { m_Settings.Prune = prune; SaveSettings(); }
            }
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
                + $"清除 stale: {(m_Settings.Prune ? "是" : "否")}\n\n"
                + "這會**直接覆寫目標工作目錄裡的檔案**（不 commit、不動目標的 git）。\n"
                + "目標上被本地改過的檔會先被擋下並列出，不會被無聲蓋掉。\n"
                + "還沒試跑過的話，建議先按「試跑」看清單。";
            UCL_OptionPage.Create("確認同步到目標？", body,
                new ButtonData("同步", () => RunScript(BuildArgs(dryRun: false, jsonFormat: false), "sync"),
                    UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.5f, 0.3f))),
                new ButtonData("取消"));
        }

        void DrawReport()
        {
            if (string.IsNullOrEmpty(m_Report)) return;
            GUILayout.Label("報告", UCL_GUIStyle.LabelStyle);
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
            bool wantJson = args.Contains("json");

            var argList = new List<string>(args);
            string argLine = "\"" + script + "\" " + string.Join(" ",
                argList.ConvertAll(x => x.StartsWith("--") ? x : "\"" + x + "\""));

            System.Threading.Tasks.Task.Run(() =>
            {
                var so = new System.Text.StringBuilder();
                var se = new System.Text.StringBuilder();
                int exit = -1;
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
                        p.Start();
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
                string stdout = so.ToString();
                string stderr = se.ToString();
                EditorApplication.delayCall += () =>
                {
                    m_Running = false;
                    m_RunningLabel = "";
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
                int i = stdout.IndexOf('{');
                if (i < 0) return;
                var jd = UCL.Core.JsonLib.JsonData.ParseJson(stdout.Substring(i));
                if (jd == null || !jd.IsObject) return;
                if (!jd.Dic.TryGetValue("inputs", out var inputs) || inputs == null || !inputs.IsObject) return;
                var list = new List<SubEntry>();
                foreach (var kv in inputs.Dic)
                {
                    list.Add(new SubEntry { Path = kv.Key, Recorded = kv.Value?.GetString() ?? "" });
                }
                if (list.Count > 0) m_Subs = list;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GitFlattenSync] scan json 解析失敗: {e.Message}");
            }
        }
    }
}
#endif
