
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.Page
{
    // 區塊職責：Persona Inspector / Letters Debug Page — 列 persona registry, 顯示 metadata,
    //          scan baton/letters/<actor>/<persona>/ 找該 persona 散落到哪些 actor folder 下、
    //          標出 canonical vs misrouted, 點 letter 顯示 body。
    // 物理意義：letter loss 多源於重構 actor naming 後沒 migration (e.g. claude-da-xiaojie ↔ 直接 actor=agent 改名),
    //          純看 _latest.md 看不出散落位置。本 page 把 persona ↔ actor 多對多關係視覺化以利 debug.
    // 數值影響：純 read-only — 不寫 file, 不改 registry。只開 explorer / 顯示 body / copy path。
    //
    // 設計理由 (Tim 2026-05-14 拍板):
    //   crest-001 wake#15 醒來印「無 letter」, 實際 letters 還在 claude-da-xiaojie/crest-001/, 同時 Zeta-da-xiaojie/crest-001/
    //   也有 misrouted 一份。awakening.py 只看 canonical actor folder, 拿不到散落到別 actor 的 letter, 也沒可視化工具
    //   讓 Tim 一眼看出哪些 letter 沒被 migration 到正確位置。本 page 補上「跨 actor folder 找同 persona 信」+ 標誌
    //   migration 殘留。
    [HelpURL("ucl_core:Docs~/zh-Hant/Plan/Plan_Awakening_Init_Protocol.md")]
    public class UCL_PersonaInspectorPage : UCL_CommonEditorPage
    {
        public override string WindowName => UCL_CodeLocalize.Get("PersonaInspector.Title");
        public override bool ShowInPageMenu => true;
        public static UCL_PersonaInspectorPage Create() => UCL_EditorPage.Create<UCL_PersonaInspectorPage>();

        // 區塊職責：Persona registry 完整 entry
        // 物理意義：對齊 AwakenInit/personas/<name>.json 全部欄位 (含 fork lineage / session keys / vector history 簡記)
        public class PersonaInfo
        {
            public string Name = "";
            public string Agent = "";
            public string Model = "";
            public string Status = "";
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
            public string CanonicalActor = "";   // 由 agent → agent_banks 查
        }

        // 區塊職責：單封 letter 紀錄
        // 物理意義：letter 檔在 baton/letters/<folderActor>/<persona>/<ts>.md, frontmatter 含 actor/written_by_persona/written_at/trigger
        public class LetterEntry
        {
            public string FilePath = "";        // 絕對路徑
            public string FileName = "";        // 顯示用
            public string FolderActor = "";     // 所在 folder 的 actor 名
            public string Persona = "";         // 所在 folder 的 persona 名
            public string FrontmatterActor = "";  // letter 自報的 actor
            public string FrontmatterPersona = ""; // letter 自報的 written_by_persona
            public string WrittenAt = "";
            public string Trigger = "";
            public long FileSize = 0;
            public bool IsCanonical = true;     // FolderActor == persona.CanonicalActor
            public bool IsMisrouted = false;    // 檔名 prefix "misrouted_" 或 FolderActor != FrontmatterActor
        }

        // 區塊職責：orphan folder — letters/<X>/ 但 X 不在 agent_banks values
        // 物理意義：抓 migration bug 殘留 (e.g. "antigravity-da-xiaojie-da-xiaojie" 雙後綴)
        public class OrphanFolder
        {
            public string ActorFolder = "";
            public List<string> PersonaSubfolders = new List<string>();
            public int TotalLetters = 0;
        }

        // ---- 快取 ----
        List<PersonaInfo> m_Personas = new List<PersonaInfo>();
        Dictionary<string, string> m_AgentBanks = new Dictionary<string, string>();   // agent → bank
        HashSet<string> m_CanonicalActors = new HashSet<string>();                    // bank 名集合
        List<LetterEntry> m_SelectedLetters = new List<LetterEntry>();
        List<OrphanFolder> m_Orphans = new List<OrphanFolder>();

        PersonaInfo m_Selected = null;
        Vector2 m_LettersScroll = Vector2.zero;
        Vector2 m_OrphanScroll = Vector2.zero;
        bool m_ShowOrphans = true;
        // PopupSearchCache 用 — labels 跟 personas 同步, picker dic 存 search state
        List<string> m_PersonaLabels = new List<string>();
        readonly UCL_ObjectDictionary m_PickerDic = new UCL_ObjectDictionary();

        // ---- 路徑 ----
        string m_AgentCommandsDir = "";
        string m_PersonasDir = "";
        string m_RegistryMetaPath = "";
        string m_LettersDir = "";

        public override void Init(UCL_GUIPageController p_Controller)
        {
            base.Init(p_Controller);
            // 區塊：路徑解析 — 用 UCL_RepoPath.AgentCommandsDir 撈 cross-project 共用 awakening state
            m_AgentCommandsDir = UCL_RepoPath.AgentCommandsDir;
            m_PersonasDir = Path.Combine(m_AgentCommandsDir, "AwakenInit", "personas");
            m_RegistryMetaPath = Path.Combine(m_AgentCommandsDir, "AwakenInit", "_registry_meta.json");
            m_LettersDir = Path.Combine(m_AgentCommandsDir, "ChatTavern", "baton", "letters");
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

        // 區塊職責：載入 agent_banks + persona registry
        // 數值影響：刷新 m_AgentBanks / m_CanonicalActors / m_Personas / m_Orphans
        void LoadData()
        {
            m_AgentBanks.Clear();
            m_CanonicalActors.Clear();
            m_Personas.Clear();
            m_Orphans.Clear();
            // 區塊：讀 registry meta 拿 agent → bank 映射
            if (File.Exists(m_RegistryMetaPath))
            {
                try
                {
                    var jd = JsonData.ParseJson(File.ReadAllText(m_RegistryMetaPath));
                    if (jd.IsObject && jd.Contains("agent_banks"))
                    {
                        var banks = jd["agent_banks"];
                        if (banks.IsObject && banks.Dic != null)
                        {
                            foreach (var key in banks.Dic.Keys)
                            {
                                string bank = banks[key].GetString();
                                m_AgentBanks[key] = bank;
                                m_CanonicalActors.Add(bank);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PersonaInspector] parse registry_meta failed: {e.Message}");
                }
            }

            // 區塊：scan personas — 反序列化全部 metadata
            if (Directory.Exists(m_PersonasDir))
            {
                foreach (var pf in Directory.GetFiles(m_PersonasDir, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(pf);
                    if (name.StartsWith("_") || name.StartsWith(".")) continue;
                    try
                    {
                        var jd = JsonData.ParseJson(File.ReadAllText(pf));
                        if (!jd.IsObject || jd.Dic == null) continue;
                        var info = new PersonaInfo
                        {
                            Name = name,
                            Agent = jd.GetString("agent", ""),
                            Model = jd.GetString("model", ""),
                            Status = jd.GetString("status", ""),
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
                        info.CanonicalActor = ResolveCanonicalActor(info.Agent);
                        m_Personas.Add(info);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[PersonaInspector] parse persona {pf} failed: {e.Message}");
                    }
                }
                m_Personas.Sort((a, b) => b.WakeCount.CompareTo(a.WakeCount));
            }

            // 區塊：rebuild PopupSearchCache labels — 含 agent + wake# 方便搜尋
            m_PersonaLabels.Clear();
            foreach (var p in m_Personas)
            {
                string icon = p.Status == "online" ? " 🟢" : "";
                m_PersonaLabels.Add($"{p.Name} [{p.Agent}] w#{p.WakeCount}{icon}");
            }

            // 區塊：scan letters root, 找 orphan actor folder (不在 agent_banks values)
            // 物理意義：migration bug 殘留 (e.g. 雙後綴 / 舊命名遺孤)
            if (Directory.Exists(m_LettersDir))
            {
                foreach (var actorDir in Directory.GetDirectories(m_LettersDir))
                {
                    string actor = Path.GetFileName(actorDir);
                    if (m_CanonicalActors.Contains(actor)) continue;
                    // 跳過特殊系統資料夾 (cross-agent / _unassigned 等以 _ 開頭)
                    if (actor.StartsWith("_") || actor == "cross-agent") continue;
                    var orphan = new OrphanFolder { ActorFolder = actor };
                    foreach (var personaDir in Directory.GetDirectories(actorDir))
                    {
                        string pname = Path.GetFileName(personaDir);
                        if (pname.StartsWith("_")) continue;
                        orphan.PersonaSubfolders.Add(pname);
                        try
                        {
                            orphan.TotalLetters += Directory.GetFiles(personaDir, "*.md").Length;
                        }
                        catch { }
                    }
                    if (orphan.PersonaSubfolders.Count > 0)
                    {
                        m_Orphans.Add(orphan);
                    }
                }
            }

            // 自動 reselect 之前選的 persona (LoadData 後)
            if (m_Selected != null)
            {
                var still = m_Personas.Find(p => p.Name == m_Selected.Name);
                m_Selected = still;
                if (still != null) RescanLettersForSelected();
            }
        }

        // 區塊職責：agent → canonical bank actor
        // 物理意義：對齊 awakening.py resolve_bank_account fallback — 認不出走 "{agent}-da-xiaojie"
        string ResolveCanonicalActor(string agent)
        {
            if (string.IsNullOrEmpty(agent)) return "";
            if (m_AgentBanks.TryGetValue(agent, out var bank)) return bank;
            // case-insensitive 二次嘗試
            foreach (var kv in m_AgentBanks)
            {
                if (string.Equals(kv.Key, agent, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            return $"{agent.ToLowerInvariant()}-da-xiaojie";
        }

        // 區塊職責：對 m_Selected scan baton/letters/*/<persona>/ 找所有散落 letter
        void RescanLettersForSelected()
        {
            m_SelectedLetters.Clear();
            if (m_Selected == null) return;
            if (!Directory.Exists(m_LettersDir)) return;
            string targetPersona = m_Selected.Name;

            foreach (var actorDir in Directory.GetDirectories(m_LettersDir))
            {
                string actor = Path.GetFileName(actorDir);
                string personaPath = Path.Combine(actorDir, targetPersona);
                if (!Directory.Exists(personaPath)) continue;

                foreach (var file in Directory.GetFiles(personaPath, "*.md"))
                {
                    var entry = new LetterEntry
                    {
                        FilePath = file,
                        FileName = Path.GetFileName(file),
                        FolderActor = actor,
                        Persona = targetPersona,
                    };
                    try
                    {
                        var info = new FileInfo(file);
                        entry.FileSize = info.Length;
                    }
                    catch { }
                    ParseLetterFrontmatter(file, entry);
                    entry.IsCanonical = (actor == m_Selected.CanonicalActor);
                    entry.IsMisrouted = !entry.IsCanonical
                                        || entry.FileName.StartsWith("misrouted_")
                                        || (!string.IsNullOrEmpty(entry.FrontmatterActor) && entry.FrontmatterActor != actor);
                    m_SelectedLetters.Add(entry);
                }
            }
            // 排序：canonical 先 → _latest.md 永遠置頂 (per Tim 2026-05-14 拍板) → 內部按 WrittenAt desc
            m_SelectedLetters.Sort((a, b) =>
            {
                int byCanon = b.IsCanonical.CompareTo(a.IsCanonical);
                if (byCanon != 0) return byCanon;
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
        // 區塊職責：全寬縱向佈局 — Persona 池 header → 全寬 picker → metadata → letters (大空間) → body → orphan
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
            GUILayout.Space(8);
            DrawOrphanSection();
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
                    GUILayout.Label($"({p.Status})", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.Label($"wake#{p.WakeCount}", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(UCL_CodeLocalize.Get("PersonaInspector.Btn.OpenPersonaJson"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        OpenInExplorer(Path.Combine(m_PersonasDir, p.Name + ".json"));
                    }
                }
                LabelRow(UCL_CodeLocalize.Get("PersonaInspector.Field.Model"), p.Model);
                LabelRow(UCL_CodeLocalize.Get("PersonaInspector.Field.LayerRole"), p.LayerRole);
                LabelRow(UCL_CodeLocalize.Get("PersonaInspector.Field.CanonicalActor"), p.CanonicalActor);
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

        // 區塊職責：letters list — 列散落到各 actor folder 下的本 persona letters
        void DrawLettersList()
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("PersonaInspector.Letters.HeaderFmt"), m_SelectedLetters.Count), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                int misroutedCount = m_SelectedLetters.Count(l => l.IsMisrouted);
                if (misroutedCount > 0)
                {
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("PersonaInspector.Letters.MisroutedFmt"), misroutedCount), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                }
                GUILayout.FlexibleSpace();
                if (m_Selected != null && GUILayout.Button(UCL_CodeLocalize.Get("PersonaInspector.Btn.OpenCanonicalFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    string canonicalDir = Path.Combine(m_LettersDir, m_Selected.CanonicalActor, m_Selected.Name);
                    OpenInExplorer(canonicalDir);
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
                    GUILayout.Label("", GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    GUILayout.Label(UCL_CodeLocalize.Get("PersonaInspector.Col.Marker"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    GUILayout.Label(UCL_CodeLocalize.Get("PersonaInspector.Col.FolderActor"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(220)));
                    GUILayout.Label(UCL_CodeLocalize.Get("PersonaInspector.Col.File"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(260)));
                    GUILayout.Label(UCL_CodeLocalize.Get("PersonaInspector.Col.WrittenAt"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    GUILayout.Label(UCL_CodeLocalize.Get("PersonaInspector.Col.Trigger"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    GUILayout.Label(UCL_CodeLocalize.Get("PersonaInspector.Col.FmActor"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
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
                        string marker = l.IsCanonical
                            ? "<color=#66ff99>✓</color>"
                            : (l.IsMisrouted ? "<color=#ff9966>⚠</color>" : "<color=#ffaa66>?</color>");
                        GUILayout.Label(marker, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                        string folderLabel = l.IsCanonical ? l.FolderActor : $"<color=#ff9966>{l.FolderActor}</color>";
                        GUILayout.Label(folderLabel, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(220)));
                        GUILayout.Label(TruncStr(l.FileName, 32), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(260)));
                        GUILayout.Label(TruncTs(l.WrittenAt), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                        GUILayout.Label(l.Trigger, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                        string fmActor = l.FrontmatterActor;
                        if (!string.IsNullOrEmpty(fmActor) && fmActor != l.FolderActor)
                        {
                            fmActor = $"<color=#ff9966>{fmActor}</color>";
                        }
                        GUILayout.Label(fmActor, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    }
                }
                GUILayout.EndScrollView();
            }
        }

        // 區塊職責：orphan folder section — letters/<X>/ X 不在 agent_banks values 的全部列出
        // 物理意義：抓 migration 殘留, e.g. 雙後綴 "antigravity-da-xiaojie-da-xiaojie"
        void DrawOrphanSection()
        {
            using (new GUILayout.HorizontalScope())
            {
                m_ShowOrphans = GUILayout.Toggle(m_ShowOrphans, "", UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(28)));
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("PersonaInspector.Orphan.HeaderFmt"), m_Orphans.Count), UCL_GUIStyle.LabelStyle);
            }
            if (!m_ShowOrphans) return;
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("PersonaInspector.Orphan.Desc"), UCL_GUIStyle.LabelStyle);
                if (m_Orphans.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("PersonaInspector.Orphan.Empty"), UCL_GUIStyle.LabelStyle);
                    return;
                }
                m_OrphanScroll = GUILayout.BeginScrollView(m_OrphanScroll, GUILayout.Height(UCL_GUIStyle.GetScaledSize(180)));
                foreach (var o in m_Orphans)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(UCL_CodeLocalize.Get("PersonaInspector.Btn.Open"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60))))
                        {
                            OpenInExplorer(Path.Combine(m_LettersDir, o.ActorFolder));
                        }
                        GUILayout.Label($"<color=#ff9966>{o.ActorFolder}</color>", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(280)));
                        GUILayout.Label(string.Format(UCL_CodeLocalize.Get("PersonaInspector.Orphan.PersonaCountFmt"), o.PersonaSubfolders.Count, o.TotalLetters), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                        GUILayout.Label(string.Join(", ", o.PersonaSubfolders), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(true));
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
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
#else
                    Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
#endif
                }
                else if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
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
