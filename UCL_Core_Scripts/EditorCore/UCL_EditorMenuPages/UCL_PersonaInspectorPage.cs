// 2026-05-18 (gura T19 BuildPlayerCheck fix): 整檔包 #if UNITY_EDITOR — 用 UCL_MarkdownViewerPage
// (該 type 自己包 #if UNITY_EDITOR), Player Build 找不到 → CS0103. 本 page IMGUI editor-only.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UCL.Core.EditorLib.AgentCommands;   // UCL_ActivePersonaLocks（在線判定唯一掃描實作）
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.Page
{
    // 區塊職責：Persona Inspector / Letters Debug Page — 列 persona registry, 顯示 metadata,
    //          讀 baton/letters/<persona>/ 列該 persona 的 letter chain, 點 letter 顯示 body。
    // 物理意義：letter 是 persona-level 自我書信; 路徑單層 <persona>/, 每 persona 一條 chain +
    //          _latest.md pointer。本 page 把 persona metadata ↔ 其 letter chain 視覺化以利 debug。
    // 數值影響：純 read-only — 不寫 file, 不改 registry。只開 explorer / 顯示 body。
    //
    // 設計沿革 (Tim 2026-05-14 → 2026-06-15):
    //   舊版為解「crest-001 letter 散落多 actor folder (claude-da-xiaojie / Zeta-da-xiaojie),
    //   awakening.py 只看 canonical actor 拿不到散落信」而生, 帶 canonical/misrouted/orphan 三套機制。
    //   2026-06-15 Tim 拍板 letter 結構壓平為單層 letters/<persona>/ (砍 agent 層, persona 名全域唯一),
    //   散落 / misroute / orphan 問題從根消除 — 本版全數移除那套機制, 回歸單純 persona↔chain 檢視。
    // 2026-08-17：舊值指向的 Plan 整個 UCL_Core 都不存在（死連結）。改指本頁的操作說明 ——
    // 就算那份 Plan 存在也不該指它：Plan 是設計沿革，而按「說明」的人要的是「這頁怎麼用」。
    // 另補 {lang}：舊值寫死 zh-Hant，其他語系拿不到回退。
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_PersonaInspectorPage.md")]
    public class UCL_PersonaInspectorPage : UCL_CommonEditorPage
    {
        public override string WindowName => UCL_CodeLocalize.Get("PersonaInspector.Title");
        public override bool ShowInPageMenu => true;

        // fire-and-forget 的 Process 註冊 tag（硬規則：每顆外部 Process 都要登記）。
        // ⚠ 本頁的 OpenInExplorer 是 UCL_ExplorerUtil 的**重複複本之一**（見該檔檔頭）。
        //   併過去之後這個 const 與上面那段就該一起刪 —— 併不併待 Tim 拍板，
        //   這裡先只補登記，不順手重構（兩件事混在一起改，出問題分不出是哪個造成的）。
        const string PROC_TAG_EXPLORER = "explorer_open";
        public static UCL_PersonaInspectorPage Create() => UCL_EditorPage.Create<UCL_PersonaInspectorPage>();

        // 區塊職責：Persona registry 完整 entry
        // 物理意義：對齊 AwakenInit/personas/<name>.json 全部欄位 (含 fork lineage / session keys / vector history 簡記)
        public class PersonaInfo
        {
            public string Name = "";
            public string Agent = "";
            public string Model = "";
            public string Status = "";   // registry 快取欄 — 在線與否請看 HasLock，不要看這欄
            // 該 persona 目前是否持有 session lock。**在線判定的唯一真相源**
            // (Tim 2026-07-31)：status 欄是登出流程寫的快取，沒走完就停在 "online"。
            public bool HasLock = false;
            public int WakeCount = 0;
            public string LayerRole = "";
            public string LastActive = "";
            public string CreatedAt = "";
            public string ForkedFrom = "";
            public string ForkedAt = "";
            public List<string> ForkLineage = new List<string>();
            public List<string> LastSessionKeys = new List<string>();
            public int VectorHistoryCount = 0;
            public string LastVectorHash = "";
            public double LastDeltaMag = 0;
            public string LastVectorTrigger = "";
        }

        // 區塊職責：單封 letter 紀錄
        // 物理意義：letter 檔在 baton/letters/<persona>/<ts>.md, frontmatter 含 actor/written_by_persona/written_at/trigger
        // 數值影響：FrontmatterActor 僅作 provenance 顯示 (哪個 agent/bank 寫的), 不再參與 misroute 判定
        public class LetterEntry
        {
            public string FilePath = "";           // 絕對路徑
            public string FileName = "";           // 顯示用
            public string FrontmatterActor = "";   // letter 自報的 actor (provenance)
            public string FrontmatterPersona = ""; // letter 自報的 written_by_persona
            public string WrittenAt = "";
            public string Trigger = "";
            public long FileSize = 0;
        }

        // ---- 快取 ----
        List<PersonaInfo> m_Personas = new List<PersonaInfo>();
        List<LetterEntry> m_SelectedLetters = new List<LetterEntry>();

        PersonaInfo m_Selected = null;
        Vector2 m_LettersScroll = Vector2.zero;
        // PopupSearchCache 用 — labels 跟 personas 同步, picker dic 存 search state
        List<string> m_PersonaLabels = new List<string>();
        readonly UCL_ObjectDictionary m_PickerDic = new UCL_ObjectDictionary();

        // ---- 路徑 ----
        string m_AgentCommandsDir = "";
        string m_LettersDir = "";

        public override void Init(UCL_GUIPageController p_Controller)
        {
            base.Init(p_Controller);
            // 區塊：路徑解析 — 用 UCL_RepoPath.AgentCommandsDir 撈 cross-project 共用 awakening state
            m_AgentCommandsDir = UCL_RepoPath.AgentCommandsDir;
            // ⚠ 同 UCL_LoginStatusPage：persona 目錄改走單一解析點（可 override 的 DataRoot），
            //   原本走 UCL_RepoPath.AgentCommandsDir。設了 override 的機器上本頁與 Cmd 讀不同目錄，
            //   而兩邊都不會報錯。預設模式下兩者逐字相同。
            m_LettersDir = UCL_LettersPath.Root;   // 同 UCL_LoginStatusPage：走唯一解析點（BUG-2）
            LoadData();
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button(UCL_CodeLocalize.Get("PersonaInspector.Btn.Refresh"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                LoadData();
            }
            if (GUILayout.Button(UCL_CodeLocalize.Get("PersonaInspector.Btn.OpenLettersDir"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                OpenInExplorer(m_LettersDir);
            }
        }

        // 區塊職責：載入 persona registry
        // 數值影響：刷新 m_Personas / m_PersonaLabels；不再讀 agent_banks (canonical actor 概念已隨單層化移除)
        void LoadData()
        {
            m_Personas.Clear();

            // 區塊：先取 session lock 名單 — 在線判定一律以 lock 為準，registry 的 status 只是快取
            // 走 UCL_ActivePersonaLocks 唯一掃描實作（有 lock ＝ 在線；過期機制已於 2026-08-19 移除）
            var lockedPersonas = UCL_ActivePersonaLocks.LockedNames();

            // 區塊：scan personas —— 走 UCL_PersonaProfile 唯一讀取入口（Phase 0 接縫）
            {
                foreach (var name in UCL_PersonaProfile.PoolNamesSorted())
                {
                    var jd = UCL_PersonaProfile.GetRaw(name);
                    if (jd == null) continue;   // 壞檔接縫已警告
                    {
                        var info = new PersonaInfo
                        {
                            Name = name,
                            Agent = jd.GetString("agent", ""),
                            Model = jd.GetString("model", ""),
                            Status = jd.GetString("status", ""),
                            HasLock = lockedPersonas.Contains(name),
                            WakeCount = jd.GetInt("wake_count", 0),
                            LayerRole = jd.GetString("layer_role", ""),
                            LastActive = jd.GetString("last_active", ""),
                            CreatedAt = jd.GetString("created_at", ""),
                            ForkedFrom = jd.GetString("forked_from", ""),
                            ForkedAt = jd.GetString("forked_at", ""),
                        };
                        // fork_lineage
                        if (jd.Contains("fork_lineage"))
                        {
                            var arr = jd["fork_lineage"];
                            if (arr != null && arr.IsArray)
                            {
                                for (int i = 0; i < arr.Count; i++) info.ForkLineage.Add(arr[i].GetString());
                            }
                        }
                        // last_session_keys
                        if (jd.Contains("last_session_keys"))
                        {
                            var arr = jd["last_session_keys"];
                            if (arr != null && arr.IsArray)
                            {
                                for (int i = 0; i < arr.Count; i++) info.LastSessionKeys.Add(arr[i].GetString());
                            }
                        }
                        // vector_history — 取 count + last entry
                        if (jd.Contains("vector_history"))
                        {
                            var arr = jd["vector_history"];
                            if (arr != null && arr.IsArray)
                            {
                                info.VectorHistoryCount = arr.Count;
                                if (info.VectorHistoryCount > 0)
                                {
                                    var last = arr[info.VectorHistoryCount - 1];
                                    if (last != null && last.IsObject)
                                    {
                                        info.LastVectorHash = last.GetString("hash", "");
                                        info.LastDeltaMag = last.GetFloat("delta_mag", 0f);
                                        info.LastVectorTrigger = last.GetString("trigger", "");
                                    }
                                }
                            }
                        }
                        m_Personas.Add(info);
                    }
                }
                m_Personas.Sort((a, b) => b.WakeCount.CompareTo(a.WakeCount));
            }

            // 區塊：rebuild PopupSearchCache labels — 含 agent + wake# 方便搜尋
            m_PersonaLabels.Clear();
            foreach (var p in m_Personas)
            {
                // 在線圖示依 lock 判定，不看 registry 的 status 快取（Tim 2026-07-31）
                string icon = p.HasLock ? " 🟢" : "";
                m_PersonaLabels.Add($"{p.Name} [{p.Agent}] w#{p.WakeCount}{icon}");
            }

            // 自動 reselect 之前選的 persona (LoadData 後)
            if (m_Selected != null)
            {
                var still = m_Personas.Find(p => p.Name == m_Selected.Name);
                m_Selected = still;
                if (still != null) RescanLettersForSelected();
            }
        }

        // 區塊職責：對 m_Selected 讀 baton/letters/<persona>/ 列出該 persona 全部 letter
        // 物理意義：單層結構 — 一個 persona 的信全在自己同名資料夾下, 不再跨 actor 散落
        void RescanLettersForSelected()
        {
            m_SelectedLetters.Clear();
            if (m_Selected == null) return;
            string personaDir = Path.Combine(m_LettersDir, m_Selected.Name);
            if (!Directory.Exists(personaDir)) return;

            foreach (var file in Directory.GetFiles(personaDir, "*.md"))
            {
                var entry = new LetterEntry
                {
                    FilePath = file,
                    FileName = Path.GetFileName(file),
                };
                try
                {
                    var info = new FileInfo(file);
                    entry.FileSize = info.Length;
                }
                catch { }
                ParseLetterFrontmatter(file, entry);
                m_SelectedLetters.Add(entry);
            }
            // 排序：_latest.md 永遠置頂 (per Tim 2026-05-14 拍板) → 其餘按 WrittenAt desc
            m_SelectedLetters.Sort((a, b) =>
            {
                bool aLatest = a.FileName == "_latest.md";
                bool bLatest = b.FileName == "_latest.md";
                if (aLatest != bLatest) return aLatest ? -1 : 1;
                return string.Compare(b.WrittenAt, a.WrittenAt, StringComparison.Ordinal);
            });
        }

        // 區塊職責：簡易 YAML frontmatter parser
        // 物理意義：letter md 開頭 --- ... --- 區段, 抓 actor / written_by_persona / written_at / trigger
        // 數值影響：找不到 frontmatter 就留空, 不噴錯
        static void ParseLetterFrontmatter(string path, LetterEntry entry)
        {
            try
            {
                using (var reader = new StreamReader(path))
                {
                    string first = reader.ReadLine();
                    if (first == null || first.Trim() != "---") return;
                    for (int i = 0; i < 32; i++)   // cap 32 行
                    {
                        string line = reader.ReadLine();
                        if (line == null) break;
                        if (line.Trim() == "---") break;
                        int colon = line.IndexOf(':');
                        if (colon < 0) continue;
                        string key = line.Substring(0, colon).Trim();
                        string val = line.Substring(colon + 1).Trim();
                        switch (key)
                        {
                            case "actor": entry.FrontmatterActor = val; break;
                            case "written_by_persona": entry.FrontmatterPersona = val; break;
                            case "written_at": entry.WrittenAt = val; break;
                            case "trigger": entry.Trigger = val; break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PersonaInspector] parse frontmatter {path} failed: {e.Message}");
            }
        }

        // ==================== GUI ====================
        // 區塊職責：全寬縱向佈局 — Persona 池 header → 全寬 picker → metadata → letters chain
        // 物理意義：原左右分欄 letters 空間被擠, Tim 2026-05-14 拍板改縱向, 信件能跑滿頁寬
        protected override void ContentOnGUI()
        {
            DrawPersonaPicker();
            GUILayout.Space(6);
            if (m_Selected != null)
            {
                DrawPersonaMeta(m_Selected);
                GUILayout.Space(6);
                DrawLettersList();
            }
            else
            {
                GUILayout.Label(UCL_CodeLocalize.Get("PersonaInspector.Hint.SelectPersona"), UCL_GUIStyle.LabelStyle);
            }
        }

        // 區塊職責：Persona 池 header + 全寬 PopupSearchCache 一行
        // 物理意義：UCL_GUILayout.PopupSearchCache 內建 search, 取代原 filter TextField + button list
        // 數值影響：選變化 → m_Selected 換 + RescanLettersForSelected; 不變則維持
        void DrawPersonaPicker()
        {
            GUILayout.Label(string.Format(UCL_CodeLocalize.Get("PersonaInspector.List.HeaderFmt"), m_Personas.Count), UCL_GUIStyle.LabelStyle);
            using (new GUILayout.HorizontalScope("box"))
            {
                if (m_Personas.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("PersonaInspector.Letters.Empty"), UCL_GUIStyle.LabelStyle);
                    return;
                }
                int curIdx = m_Selected == null ? -1 : m_Personas.FindIndex(p => p.Name == m_Selected.Name);
                if (curIdx < 0) curIdx = 0;
                int newIdx = UCL_GUILayout.PopupSearchCache(curIdx, m_PersonaLabels, m_PickerDic, "PersonaInspectorPicker", GUILayout.ExpandWidth(true));
                if (newIdx >= 0 && newIdx < m_Personas.Count)
                {
                    var picked = m_Personas[newIdx];
                    if (m_Selected == null || m_Selected.Name != picked.Name)
                    {
                        m_Selected = picked;
                        RescanLettersForSelected();
                    }
                }
            }
        }

        // 區塊職責：persona 完整 metadata 顯示
        void DrawPersonaMeta(PersonaInfo p)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"<b><color=#ffcc66>{p.Name}</color></b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.Label($"[{p.Agent}]", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    // 在線與否看 lock；registry status 不一致時並列顯示（漂移要看得見，不靜默）
                    string stateLabel = p.HasLock ? "online 🔒" : "offline";
                    if (p.HasLock != (p.Status == "online")) stateLabel += $" / registry 快取: {p.Status}";
                    GUILayout.Label($"({stateLabel})", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.Label($"wake#{p.WakeCount}", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(UCL_CodeLocalize.Get("PersonaInspector.Btn.OpenPersonaJson"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        OpenInExplorer(UCL_LettersPath.ProfileDir(p.Name));   // 2026-08-21：資料住 letters/<p>/profile/
                    }
                }
                LabelRow(UCL_CodeLocalize.Get("PersonaInspector.Field.Model"), p.Model);
                LabelRow(UCL_CodeLocalize.Get("PersonaInspector.Field.LayerRole"), p.LayerRole);
                LabelRow(UCL_CodeLocalize.Get("PersonaInspector.Field.CreatedAt"), p.CreatedAt);
                LabelRow(UCL_CodeLocalize.Get("PersonaInspector.Field.LastActive"), p.LastActive);
                if (!string.IsNullOrEmpty(p.ForkedFrom))
                {
                    LabelRow(UCL_CodeLocalize.Get("PersonaInspector.Field.ForkedFrom"), $"{p.ForkedFrom} @ {p.ForkedAt}");
                }
                if (p.ForkLineage.Count > 0)
                {
                    LabelRow(UCL_CodeLocalize.Get("PersonaInspector.Field.ForkLineage"), string.Join(" → ", p.ForkLineage));
                }
                LabelRow(UCL_CodeLocalize.Get("PersonaInspector.Field.VectorHistory"),
                         $"{p.VectorHistoryCount} entries — last hash={p.LastVectorHash} Δ={p.LastDeltaMag:F3} trigger={p.LastVectorTrigger}");
                if (p.LastSessionKeys.Count > 0)
                {
                    LabelRow(UCL_CodeLocalize.Get("PersonaInspector.Field.LastSessionKeys"),
                             string.Join(", ", p.LastSessionKeys.Select(k => k.Length > 36 ? k.Substring(0, 36) + "…" : k)));
                }
            }
        }

        // 區塊職責：letters list — 列該 persona 同名資料夾下全部 letter (單層結構)
        void DrawLettersList()
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("PersonaInspector.Letters.HeaderFmt"), m_SelectedLetters.Count), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
                if (m_Selected != null && GUILayout.Button(UCL_CodeLocalize.Get("PersonaInspector.Btn.OpenCanonicalFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    // 單層化後 = 直接開 letters/<persona>/ (原「canonical folder」概念退化為單一 persona 夾)
                    string personaDir = Path.Combine(m_LettersDir, m_Selected.Name);
                    OpenInExplorer(personaDir);
                }
            }
            using (new GUILayout.VerticalScope("box"))
            {
                if (m_SelectedLetters.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("PersonaInspector.Letters.Empty"), UCL_GUIStyle.LabelStyle);
                    return;
                }
                m_LettersScroll = GUILayout.BeginScrollView(m_LettersScroll, GUILayout.Height(UCL_GUIStyle.GetScaledSize(360)));
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("", GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    GUILayout.Label(UCL_CodeLocalize.Get("PersonaInspector.Col.File"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(300)));
                    GUILayout.Label(UCL_CodeLocalize.Get("PersonaInspector.Col.WrittenAt"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                    GUILayout.Label(UCL_CodeLocalize.Get("PersonaInspector.Col.Trigger"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                    GUILayout.Label(UCL_CodeLocalize.Get("PersonaInspector.Col.FmActor"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                }
                foreach (var l in m_SelectedLetters)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(UCL_CodeLocalize.Get("DocSearch.Preview"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))))
                        {
                            string abs = l.FilePath;
                            string rel = abs.Replace('\\', '/');
                            string root = UCL_RepoPath.RepoRoot.Replace('\\', '/');
                            if (rel.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            {
                                rel = rel.Substring(root.Length).TrimStart('/');
                            }
                            UCL_MarkdownViewerPage.Create(rel, abs);
                        }
                        // _latest.md 高亮置頂; 其餘普通顯示
                        bool isLatest = l.FileName == "_latest.md";
                        string fileLabel = isLatest ? $"<color=#66ff99>{l.FileName}</color>" : TruncStr(l.FileName, 36);
                        GUILayout.Label(fileLabel, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(300)));
                        GUILayout.Label(TruncTs(l.WrittenAt), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                        GUILayout.Label(l.Trigger, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                        GUILayout.Label(l.FrontmatterActor, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                    }
                }
                GUILayout.EndScrollView();
            }
        }

        // ==================== Actions ====================

        // 區塊職責：跨平台開 Explorer / Finder
        // 物理意義：Windows 用 explorer.exe /select 對 file / 直接開 dir
        static void OpenInExplorer(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                if (File.Exists(path))
                {
#if UNITY_EDITOR_WIN
                    UCL_ProcessRegistryService.StartAndRegister(
                        new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""),
                        PROC_TAG_EXPLORER, $"開啟並選取：{path}", nameof(UCL_PersonaInspectorPage));
#else
                    UCL_ProcessRegistryService.StartAndRegister(
                        new ProcessStartInfo { FileName = path, UseShellExecute = true },
                        PROC_TAG_EXPLORER, $"開啟：{path}", nameof(UCL_PersonaInspectorPage));
#endif
                }
                else if (Directory.Exists(path))
                {
                    UCL_ProcessRegistryService.StartAndRegister(
                        new ProcessStartInfo { FileName = path, UseShellExecute = true },
                        PROC_TAG_EXPLORER, $"開啟資料夾：{path}", nameof(UCL_PersonaInspectorPage));
                }
                else
                {
                    Debug.LogWarning($"[PersonaInspector] 路徑不存在: {path}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[PersonaInspector] open explorer failed: {e.Message}");
            }
        }

        // ==================== Helpers ====================

        static void LabelRow(string label, string val)
        {
            if (string.IsNullOrEmpty(val)) return;
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label($"<b>{label}</b>", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(140)));
                GUILayout.Label(val, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(true));
            }
        }

        static string TruncTs(string ts)
        {
            if (string.IsNullOrEmpty(ts)) return "";
            return ts.Length > 19 ? ts.Substring(0, 19) : ts;
        }

        static string TruncStr(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > maxLen ? s.Substring(0, maxLen) + "…" : s;
        }
    }
}
#endif // UNITY_EDITOR
