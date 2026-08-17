// 區塊職責：自由時間管理頁 — 管理骰面上的活動項目（啟用 / 建議時間 / 顯示名稱 / 做法）。
// 物理意義：活動的**事實來源是 md frontmatter**（雙層：UCL_Core 共用層＋專案層，同 id 專案層覆蓋），
//          本頁直接改那些欄位，**不另存一份 override 設定** —— 兩個地方各自宣告 min_minutes 的話，
//          合併規則會變成看不見的隱式約定，而約定壞掉時不會有人喊（2026-08-14 同日血證：
//          展品 region 手填漂移、ClickArea 隱式索引鍵）。掃描共用 UCL_FreeTimeIO.ScanActivities，
//          與 Cmd_FreeTime 擲骰同一份實作 —— 兩份掃描器的漂移症狀是「頁面看到的跟實際擲出來的不一樣」。
// 數值影響：改 enabled / min_minutes / name / how 會就地改寫 md 的 frontmatter（正文不動）；
//          清單只在開頁 / Reload / 寫入後重掃，**不每幀掃磁碟**（IMGUI 每 repaint 都跑 ContentOnGUI）。
// 歷史：本頁一度還有「末段提示門檻」設定區。Tim 2026-08-14 拍板把末段提示整個拔掉，
//      該區隨之移除 —— 留一個沒有消費端的設定介面，會讓人以為那裡還有一道防護。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.EditorLib.AgentCommands.Awakening;
using UCL.Core.EditorLib.AgentCommands.FreeTime;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 自由時間管理頁 — 活動清單（啟用／建議時間／名稱／做法）。
    /// 入口在控制台；落子類操作都改檔案，不改執行中的 session。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_FreeTimeAdminPage.md")]
    public class UCL_FreeTimeAdminPage : UCL_CommonEditorPage
    {
        const string KeyActivitiesFold = "FreeTimeActivitiesFold";
        const string KeyNewActivityFold = "FreeTimeNewActivityFold";
        const string LogTag = "FreeTimeAdmin";

        // 區塊職責：折疊狀態專用容器 —— 刻意不與任何資料快取共用。
        // 血證（2026-07-29 Tim QA）：共用時 Reload 路徑的 Clear() 會把折疊值一併清掉，
        //          症狀是「按了某個按鈕就自動展開、而且收不起來」，看起來像 key 撞名。
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();

        // 區塊職責：下拉選單（PopupSearchCache）專用容器 —— 同樣不與折疊狀態共用。
        // 物理意義：兩者的生命週期不同（折疊值要活過 Reload，選單快取不必），
        //          共用一個容器就是把上面那條血證再踩一次。
        readonly UCL_ObjectDictionary m_PickerDic = new UCL_ObjectDictionary();

        List<UCL_FreeTimeActivity> m_Activities = new List<UCL_FreeTimeActivity>();
        // 下拉選單的選項與選中索引（Tim 2026-08-17：改為「選一項來編輯」，不再整頁列出所有活動）
        readonly List<string> m_ActivityOptions = new List<string>();
        int m_SelectedIdx;
        // 選中的活動 id —— 記 id 不記索引：Reload 後清單順序可能變（新增活動／改 id），
        // 只記索引會**安靜地切到另一個活動**，而畫面上看起來像什麼都沒發生。
        string m_SelectedId = "";
        // 編輯中的暫存值（key = 活動 id）—— TextField 每幀回傳字串，直接寫檔會變成每個按鍵都落一次盤
        readonly Dictionary<string, string> m_DraftMinMinutes = new Dictionary<string, string>();
        readonly Dictionary<string, string> m_DraftName = new Dictionary<string, string>();
        readonly Dictionary<string, string> m_DraftHow = new Dictionary<string, string>();
        string m_NewId = "";
        string m_NewName = "";
        string m_NewHow = "";
        string m_NewMinMinutes = "0";
        string m_Status = "";
        bool m_Loaded;

        public override string WindowName => "自由時間管理";
        //public override bool ShowInPageMenu => false;
        public static UCL_FreeTimeAdminPage Create() => UCL_EditorPage.Create<UCL_FreeTimeAdminPage>();

        public override void Init(UCL_GUIPageController iGUIPage)
        {
            base.Init(iGUIPage);
            Reload();
        }

        // 區塊職責：重讀設定與活動清單（開頁 / 按 Reload / 每次寫檔後）。
        // 物理意義：草稿一併清空 —— 檔案已是新事實，留著舊草稿會讓畫面顯示的值與檔案不一致，
        //          而使用者無從分辨那是「還沒存」還是「存失敗了」。
        void Reload()
        {
            m_Activities = UCL_FreeTimeIO.ScanActivities();
            m_DraftMinMinutes.Clear();
            m_DraftName.Clear();
            m_DraftHow.Clear();
            RebuildOptions();
            m_Loaded = true;
        }

        // 區塊職責：重建下拉選項，並把選取**依 id 對回去**（不是依索引）。
        // 物理意義：選項字串帶 kind／層級／啟用狀態 —— 下拉收合時只看得到一行，
        //          那一行要能回答「我選的是哪一個、它現在是什麼狀態」。
        // 數值影響：找不到原本的 id（被刪 / 改名）→ 退回第 0 項並更新 m_SelectedId，
        //          **不留一個指向不存在活動的索引**（那會讓後續編輯寫到別人的 md）。
        void RebuildOptions()
        {
            m_ActivityOptions.Clear();
            foreach (var a in m_Activities)
            {
                string aKind = a.kind == UCL_FreeTimeActivityKind.Default ? "" : $" [{a.kind}]";
                string aLayer = a.isProjectLayer ? "🏠" : "📦";
                m_ActivityOptions.Add($"{(a.enabled ? "" : "（停用）")}{aLayer} {a.id}{aKind}");
            }
            m_SelectedIdx = 0;
            if (string.IsNullOrEmpty(m_SelectedId)) { m_SelectedId = m_Activities.Count > 0 ? m_Activities[0].id : ""; return; }
            for (int i = 0; i < m_Activities.Count; i++)
                if (m_Activities[i].id == m_SelectedId) { m_SelectedIdx = i; return; }
            m_SelectedId = m_Activities.Count > 0 ? m_Activities[0].id : "";   // 原本那個沒了 → 誠實地換人
        }

        protected override void ContentOnGUI()
        {
            if (!m_Loaded) Reload();

            DrawActivitiesSection();
            GUILayout.Space(8);
            DrawNewActivitySection();

            if (!string.IsNullOrEmpty(m_Status))
            {
                GUILayout.Space(4);
                GUILayout.Label(m_Status, WrapLabelStyle);
            }
        }

        // ===========================================================
        // 區塊：活動清單 — 直接改 md frontmatter（正文不動）
        // 物理意義：兩層來源都列出來，並標明哪一筆是專案層覆蓋 —— 「同 id 專案層覆蓋共用層」
        //          這條規則若只寫在文件裡而畫面上看不出來，使用者會改到不生效的那一份。
        // ===========================================================
        void DrawActivitiesSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, KeyActivitiesFold, 21, iDefaultValue: true);
                    int aEnabled = 0;
                    foreach (var a in m_Activities) if (a.enabled) aEnabled++;
                    GUILayout.Label($"<b>🎲 活動清單</b>（啟用 {aEnabled} / 共 {m_Activities.Count}）",
                        UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("🔄 重新載入", UCL_GUIStyle.GetButtonStyle(Color.white),
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(120))))
                    {
                        Reload();
                        m_Status = $"🔄 已重掃：{m_Activities.Count} 項";
                    }
                    if (GUILayout.Button("📂 共用層", UCL_GUIStyle.GetButtonStyle(Color.white),
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(100))))
                        OpenDir(UCL_FreeTimeIO.GetSharedActivityDir());
                    if (GUILayout.Button("📂 專案層", UCL_GUIStyle.GetButtonStyle(Color.white),
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(100))))
                        OpenDir(UCL_FreeTimeIO.GetProjectActivityDir());
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                if (m_Activities.Count == 0)
                {
                    GUILayout.Label("掃不到任何活動 md —— 共用層與專案層都是空的（環境異常，不是正常狀態）。",
                        WrapLabelStyle);
                    return;
                }

                // 選一項來編輯（Tim 2026-08-17）—— 活動數量會長，整頁攤開時每一項都只露出一小截，
                // 反而看不清正在改哪一個。⚠ PopupSearchCache 選項為 0 會 LogError，上面已擋。
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("編輯活動", UCL_GUIStyle.LabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    int aNewIdx = UCL_GUILayout.PopupSearchCache(m_SelectedIdx, m_ActivityOptions, m_PickerDic,
                        "FreeTimeActivityPicker", GUILayout.Width(UCL_GUIStyle.GetScaledSize(320)));
                    if (aNewIdx != m_SelectedIdx && aNewIdx >= 0 && aNewIdx < m_Activities.Count)
                    {
                        m_SelectedIdx = aNewIdx;
                        m_SelectedId = m_Activities[aNewIdx].id;   // 以 id 為準，索引只是當下的位置
                    }
                    GUILayout.Label("📦 共用層／🏠 專案層（同 id 專案層覆蓋共用層）", UCL_GUIStyle.LabelStyle,
                        GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }

                if (m_SelectedIdx < 0 || m_SelectedIdx >= m_Activities.Count) return;
                DrawActivityRow(m_Activities[m_SelectedIdx]);
            }
        }

        void DrawActivityRow(UCL_FreeTimeActivity iAct)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    bool aNewEnabled = UCL_GUILayout.CheckBox(iAct.enabled);
                    if (aNewEnabled != iAct.enabled) WriteField(iAct, "enabled", aNewEnabled ? "true" : "false");
                    GUILayout.Label($"<b>{iAct.id}</b>", UCL_GUIStyle.LabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    GUILayout.Label(iAct.isProjectLayer ? "🏠 專案層" : "📦 共用層", UCL_GUIStyle.LabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(90)));
                    if (GUILayout.Button("📄 開啟 md", UCL_GUIStyle.GetButtonStyle(Color.white),
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(110))))
                        OpenDir(iAct.path);
                    GUILayout.FlexibleSpace();
                }

                DrawKindRow(iAct);

                // 建議時間：草稿 + 明確的「套用」，不邊打字邊寫檔
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("建議時間(分)", UCL_GUIStyle.LabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(100)));
                    string aKey = iAct.id;
                    if (!m_DraftMinMinutes.TryGetValue(aKey, out string aDraft)) aDraft = iAct.minMinutes.ToString();
                    m_DraftMinMinutes[aKey] = GUILayout.TextField(aDraft, UCL_GUIStyle.TextFieldStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                    bool aDirty = m_DraftMinMinutes[aKey] != iAct.minMinutes.ToString();
                    if (aDirty && GUILayout.Button("套用", UCL_GUIStyle.GetButtonStyle(Color.green),
                            GUILayout.Width(UCL_GUIStyle.GetScaledSize(70))))
                    {
                        if (int.TryParse(m_DraftMinMinutes[aKey], out int aMin) && aMin >= 0)
                            WriteField(iAct, "min_minutes", aMin.ToString());
                        else m_Status = $"✗ {iAct.id} 的建議時間需為 ≥0 的整數（got '{m_DraftMinMinutes[aKey]}'）";
                    }
                    if (aDirty) GUILayout.Label("（未套用）", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }

                GUILayout.Label("建議時間：剩餘時間不足時該活動會被排到骰面尾端並標明，"
                    + "<b>不隱藏</b>（做得成但不划算，資訊留著讓人自己判斷）。0 ＝ 不做時間感知排序。",
                    WrapLabelStyle);

                DrawDraftField(iAct, "顯示名稱", "name", iAct.name, m_DraftName);
                DrawDraftField(iAct, "做法(how)", "how", iAct.how, m_DraftHow);
            }
        }

        // ===========================================================
        // 區塊職責：kind（特殊邏輯標記）下拉 —— 寫回 md frontmatter 的 `kind` 欄位。
        // 物理意義：用 enum 下拉而不是文字欄，是因為打錯的標記**不會報錯也不會生效**
        //          （`live-strem` 這種 typo 會安靜地退回 Default）。下拉選單根本打不出那個值。
        // 數值影響：改完立刻寫檔＋重掃；每個 kind 附一行「它實際會做什麼」——
        //          一個只有名字沒有說明的標記，使用者只能猜它管什麼。
        // ===========================================================
        void DrawKindRow(UCL_FreeTimeActivity iAct)
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("特殊邏輯", UCL_GUIStyle.LabelStyle,
                    GUILayout.Width(UCL_GUIStyle.GetScaledSize(100)));
                var aNames = Enum.GetNames(typeof(UCL_FreeTimeActivityKind));
                int aCur = Array.IndexOf(aNames, iAct.kind.ToString());
                if (aCur < 0) aCur = 0;
                int aNew = UCL_GUILayout.Popup(aCur, aNames, m_PickerDic, $"Kind_{iAct.id}",
                    GUILayout.Width(UCL_GUIStyle.GetScaledSize(160)));
                if (aNew != aCur && aNew >= 0 && aNew < aNames.Length)
                    WriteField(iAct, "kind", aNames[aNew]);
                GUILayout.Label(KindHint(iAct.kind), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
            }
            // 標記打錯要在**設定它的地方**就看得到，不能只在骰面上才顯形。
            if (!string.IsNullOrEmpty(iAct.kindParseError))
                GUILayout.Label($"⚠ md 裡的 kind='{iAct.kindParseError}' 認不得，目前當一般活動處理"
                    + "（用上面的下拉重設一次即可寫回正確值）。", WrapLabelStyle);
        }

        static string KindHint(UCL_FreeTimeActivityKind iKind)
        {
            switch (iKind)
            {
                case UCL_FreeTimeActivityKind.StreamWatch:
                    return "沒開播 → 從骰面隱藏；開播 → 進優先層並附本場節目名";
                case UCL_FreeTimeActivityKind.Chess:
                    return "有未完成棋局且對手也在自由時間 → 進優先層（不隱藏，隨時可開新局）";
                default:
                    return "一般活動 —— 不走任何特殊邏輯";
            }
        }

        // 區塊職責：一列「草稿 → 套用」的字串欄位。共用一份，因為 name / how 的行為必須一致 ——
        //          兩份各寫一次，遲早只有一邊補上驗證。
        void DrawDraftField(UCL_FreeTimeActivity iAct, string iLabel, string iField,
                            string iCurrent, Dictionary<string, string> ioDraft)
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label(iLabel, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(100)));
                string aKey = iAct.id;
                if (!ioDraft.TryGetValue(aKey, out string aDraft)) aDraft = iCurrent ?? "";
                ioDraft[aKey] = GUILayout.TextField(aDraft, UCL_GUIStyle.TextFieldStyle,
                    GUILayout.MinWidth(UCL_GUIStyle.GetScaledSize(320)));
                if (ioDraft[aKey] != (iCurrent ?? "")
                    && GUILayout.Button("套用", UCL_GUIStyle.GetButtonStyle(Color.green),
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(70))))
                {
                    WriteField(iAct, iField, ioDraft[aKey]);
                }
            }
        }

        // 區塊職責：把一個欄位寫回 md frontmatter，並重掃讓畫面回到「檔案說了算」。
        // 物理意義：寫完立刻 Reload —— 不用記憶體值假裝已生效。**印 ✓ 不算數，讀回來才算。**
        void WriteField(UCL_FreeTimeActivity iAct, string iField, string iValue)
        {
            if (UCL_AwakeningService.WriteFrontmatterField(iAct.path, iField, iValue))
            {
                m_Status = $"✅ {iAct.id}.{iField} = {iValue}　→ {iAct.path}";
                Reload();
            }
            else m_Status = $"✗ {iAct.id}.{iField} 寫入失敗（詳見 Console）：{iAct.path}";
        }

        // ===========================================================
        // 區塊：新增活動（一律建在專案層）
        // 物理意義：共用層屬於 UCL_Core（跨專案），從專案的管理頁往那裡新增等於替別的專案做決定；
        //          專案層是本 repo 自己的東西，且同 id 會覆蓋共用層 —— 要改共用活動也走這裡。
        // 數值影響：只寫 frontmatter ＋ 一段待補正文；活動的說明文件仍要人自己寫
        //          （GUI 生得出欄位，生不出「這個活動是什麼」）。
        // ===========================================================
        void DrawNewActivitySection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, KeyNewActivityFold, 21, iDefaultValue: false);
                    GUILayout.Label("<b>➕ 新增活動（建在專案層）</b>", UCL_GUIStyle.LabelStyle,
                        GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                GUILayout.Label("id 用 kebab-case，會成為檔名與骰面識別；同 id 會覆蓋共用層同名活動。",
                    WrapLabelStyle);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("id", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(100)));
                    m_NewId = GUILayout.TextField(m_NewId, UCL_GUIStyle.TextFieldStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(220)));
                    GUILayout.Label("建議時間(分)", UCL_GUIStyle.LabelStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(100)));
                    m_NewMinMinutes = GUILayout.TextField(m_NewMinMinutes, UCL_GUIStyle.TextFieldStyle,
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(70)));
                    GUILayout.FlexibleSpace();
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("顯示名稱", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(100)));
                    m_NewName = GUILayout.TextField(m_NewName, UCL_GUIStyle.TextFieldStyle,
                        GUILayout.MinWidth(UCL_GUIStyle.GetScaledSize(320)));
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("做法(how)", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(100)));
                    m_NewHow = GUILayout.TextField(m_NewHow, UCL_GUIStyle.TextFieldStyle,
                        GUILayout.MinWidth(UCL_GUIStyle.GetScaledSize(320)));
                }
                if (GUILayout.Button("➕ 建立", UCL_GUIStyle.GetButtonStyle(Color.cyan),
                        GUILayout.Width(UCL_GUIStyle.GetScaledSize(120))))
                {
                    CreateActivity();
                }
            }
        }

        void CreateActivity()
        {
            string aId = (m_NewId ?? "").Trim();
            if (string.IsNullOrEmpty(aId)) { m_Status = "✗ id 不可空白"; return; }
            if (aId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { m_Status = $"✗ id 含非法檔名字元：{aId}"; return; }
            if (aId.StartsWith("_")) { m_Status = "✗ id 不可以 _ 開頭（底線開頭的 md 被視為說明檔，不算活動）"; return; }
            if (!int.TryParse(m_NewMinMinutes, out int aMin) || aMin < 0) { m_Status = $"✗ 建議時間需為 ≥0 的整數（got '{m_NewMinMinutes}'）"; return; }

            string aDir = UCL_FreeTimeIO.GetProjectActivityDir();
            string aPath = Path.Combine(aDir, aId + ".md");
            if (File.Exists(aPath)) { m_Status = $"✗ 已存在，未覆寫：{aPath}"; return; }

            try
            {
                Directory.CreateDirectory(aDir);
                string aName = string.IsNullOrWhiteSpace(m_NewName) ? aId : m_NewName.Trim();
                var aSb = new System.Text.StringBuilder();
                aSb.AppendLine("---");
                aSb.AppendLine($"id: {aId}");
                aSb.AppendLine($"name: {Quote(aName)}");
                aSb.AppendLine($"how: {Quote((m_NewHow ?? "").Trim())}");
                aSb.AppendLine("enabled: true");
                aSb.AppendLine($"min_minutes: {aMin}");
                aSb.AppendLine("---");
                aSb.AppendLine();
                aSb.AppendLine($"# {aName}");
                aSb.AppendLine();
                aSb.AppendLine("> ⚠ 正文待補 —— 這一段是給挑到這個活動的人看的「怎麼做」。");
                aSb.AppendLine("> GUI 生得出欄位，生不出「這個活動是什麼」。");
                File.WriteAllText(aPath, aSb.ToString(), new System.Text.UTF8Encoding(false));
                m_NewId = m_NewName = m_NewHow = "";
                m_NewMinMinutes = "0";
                Reload();
                m_Status = $"✅ 已建立：{aPath}（正文待補）";
            }
            catch (Exception e)
            {
                m_Status = $"✗ 建立失敗 {aPath}: {e.Message}";
            }
        }

        /// <summary>值含 `:`／`#`／前後空白時加引號（與 WriteFrontmatterField 同規則）。</summary>
        static string Quote(string iVal)
        {
            string aRaw = iVal ?? "";
            bool aNeed = aRaw.Contains(":") || aRaw.Contains("#") || aRaw != aRaw.Trim();
            return aNeed ? $"\"{aRaw.Replace("\"", "\\\"")}\"" : aRaw;
        }

        // 開資料夾一律走 UCL_ExplorerUtil（Process 有登記、路徑不存在會留 log），不自己開 process
        void OpenDir(string iPath)
        {
            if (string.IsNullOrEmpty(iPath)) { m_Status = "✗ 路徑為空（CorePath 解析不到？）"; return; }
            if (!UCL_ExplorerUtil.Open(iPath, LogTag)) m_Status = $"✗ 開啟失敗（詳見 Console）：{iPath}";
        }

        GUIStyle m_WrapLabelStyle;
        GUIStyle WrapLabelStyle => m_WrapLabelStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true };
    }
}
#endif
