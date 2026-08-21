// 區塊職責：relationship 後台頁 —— 看某位 persona 對某個對象的關係。
// 物理意義：新系統（letters/<persona>/relationship/）的 UI；舊 UCL_AffinitySystemPage 的接班人。
// 數值影響：唯讀 —— 只讀 relationship 資料，不寫檔（舊 affinity 一次性遷移入口已於 2026-08-19 移除）。
// 設計沿革：Plan_Relationship_System.md（Tim 2026-08-18 拍板）。
// 2026-08-18 calli
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UCL.Core.EditorLib.AgentCommands.Relationship;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 關係管理頁 —— persona / 對象兩層下拉。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/Plan/Plan_Relationship_System.md")]
    public class UCL_RelationshipPage : UCL_CommonEditorPage
    {
        public override string WindowName => "關係（Relationship）";
        protected override bool ShowBackButton => true;
        public override bool ShowInPageMenu => true;

        // PopupSearchCache 的快取容器。
        // ⚠ 折疊狀態不共用這一個 —— 資料重載路徑上的 Clear() 會把折疊值一併清掉
        //   （症狀是「收不起來」，看起來像 key 撞名）。
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();

        readonly List<string> m_Personas = new();
        readonly List<string> m_Targets = new();
        int m_PersonaIdx = 0, m_TargetIdx = 0;
        bool m_Loaded = false;

        // ===========================================================
        // 區塊職責：當前所選 (persona, target) 的資料快取。
        // 物理意義：IMGUI 每秒重畫數十次 —— **Draw 裡不准碰磁碟**。
        //          舊版每幀 File.ReadAllText 一次 `_current.md`；換成事件／看法全讀之後
        //          那個代價會乘上檔案數（summit 那組是 37 + 36 個檔）。
        // 數值影響：只在「選擇變了」或按 Refresh 時重讀；m_CacheKey 是 persona|target。
        // ===========================================================
        string m_CacheKey = null;
        UCL_RelationshipCurrent m_Cur;
        List<UCL_RelationshipEvent> m_Events;
        List<UCL_RelationshipOpinion> m_Opinions;
        /// <summary>重算出來的向量（拿事件流 ＋ 期初餘額）—— 用來對帳存值有沒有漂掉。</summary>
        Dictionary<string, float> m_Recomputed;
        int m_RecomputedScore;

        // 顯示開關：raw 數值預設關（「非文字化」是這套 UI 的原則 —— 條看得出結構，數字看不出）
        bool m_ShowRaw = false;

        // 區塊職責：事件／看法一頁幾筆（Tim 2026-08-21：改成分頁，每頁 10 筆）
        // 物理意義：分頁走 UCL_GUILayout.DrawSelectPage（全專案共用的翻頁列：|< < n/N > >|，
        //          頁數 ≥10 時自動換成可輸入頁碼）—— 不自己刻第二套翻頁 UI，也不用
        //          「預覽 N 筆 ＋ 展開全部」那種二態，那個在 37 筆時等於只有「太少」跟「太多」。
        // 數值影響：翻頁狀態存在 m_FoldDic 的子字典裡；只有 itemsCount > 每頁筆數時才畫翻頁列。
        const int ItemsPerPage = 10;
        const string PageKeyEvents = "EventsPage";
        const string PageKeyOpinions = "OpinionsPage";

        // 區塊職責：8 軸的顯示名稱。
        // ⚠ 順序必須對齊 UCL_RelationshipAxes.Names —— 這裡是**用索引對上去**的，
        //   重排不會編譯錯、不會執行錯，只會讓每條 bar 掛在別的軸的標籤下。
        // 註：本頁其他字串也是直接寫中文（calli 2026-08-18 的既有慣例），故不另開 localize key。
        static readonly string[] AxisLabels =
        {
            "信任", "好感", "尊重", "興趣",
            "惱怒", "依賴", "欣賞", "忠誠",
        };

        GUIStyle m_SmallStyle;
        GUIStyle SmallStyle
        {
            get
            {
                if (m_SmallStyle == null)
                    m_SmallStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
                return m_SmallStyle;
            }
        }

        // 標題／軸變動用 —— richText 讓 <b> / <color> 生效（不開的話會直接印出 tag）
        GUIStyle m_RichStyle;
        GUIStyle RichStyle
        {
            get
            {
                if (m_RichStyle == null)
                    m_RichStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { richText = true, wordWrap = true };
                return m_RichStyle;
            }
        }

        public static UCL_RelationshipPage Create()
        {
            var page = new UCL_RelationshipPage();
            UCL_GUIPageController.CurrentRenderIns.Push(page);
            return page;
        }

        // ===========================================================
        // 區塊職責：persona 選擇器放 TopBar（照 UCL_BankAdminPage，Tim 指定）。
        // 物理意義：persona 是**整頁的視角**不是某一區的參數 —— 放內容區的話，
        //          捲到下面就看不到「現在在看誰」，而那正是最容易看錯資料的時候。
        // ===========================================================
        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button("🔄 Refresh", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                LoadData();
                m_CacheKey = null;   // 連當前對象的事件／看法一起重讀（外部剛動過檔才會按這顆）
            }
            m_ShowRaw = GUILayout.Toggle(m_ShowRaw, "顯示原始數值", UCL_GUIStyle.ButtonStyle,
                GUILayout.ExpandWidth(false));
            if (m_Personas.Count > 0)
            {
                int aNew = UCL_GUILayout.PopupSearchCache(m_PersonaIdx, m_Personas, m_Dic,
                    "RelationshipPersonaPicker", GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                if (aNew != m_PersonaIdx && aNew >= 0 && aNew < m_Personas.Count)
                {
                    m_PersonaIdx = aNew;
                    LoadTargets();
                    GUI.FocusControl(null);
                }
            }
        }

        void LoadData()
        {
            m_Personas.Clear();
            string aLetters = UCL_LettersPath.Root;      // letters 的正式入口，見 UCL_RelationshipIO 註解
            if (Directory.Exists(aLetters))
            {
                foreach (var d in Directory.GetDirectories(aLetters))
                {
                    // 只列真的有 relationship 資料的 —— 列出 30 個空資料夾等於把選單變成雜訊
                    if (Directory.Exists(Path.Combine(d, UCL_RelationshipIO.DIR_NAME)))
                        m_Personas.Add(Path.GetFileName(d));
                }
            }
            m_Personas.Sort(string.CompareOrdinal);
            if (m_PersonaIdx >= m_Personas.Count) m_PersonaIdx = 0;
            LoadTargets();
            m_Loaded = true;
        }

        void LoadTargets()
        {
            m_Targets.Clear();
            if (m_PersonaIdx < 0 || m_PersonaIdx >= m_Personas.Count) return;
            string d = UCL_RelationshipIO.PersonaDir(m_Personas[m_PersonaIdx]);
            if (!Directory.Exists(d)) return;
            foreach (var t in Directory.GetDirectories(d)) m_Targets.Add(Path.GetFileName(t));
            m_Targets.Sort(string.CompareOrdinal);
            if (m_TargetIdx >= m_Targets.Count) m_TargetIdx = 0;
        }

        protected override void ContentOnGUI()
        {
            if (!m_Loaded) LoadData();

            if (m_Personas.Count == 0)
            {
                GUILayout.Label("（還沒有任何 relationship 資料 —— 用 run_cmd run Relationship 寫第一筆事件）", SmallStyle);
                return;
            }

            // ── 對象下拉（Tim 2026-08-18：原本全部攤開，改成選一個看一個）──────
            // 一位 persona 可能有十幾個對象，全攤開的話要捲很久才看得到想看的那個，
            // 而「捲很久」實務上等於「只看得到最上面那幾個」。
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("對象：", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                if (m_Targets.Count > 0)
                {
                    // ⚠ PopupSearchCache 選項為 0 時會 LogError ⇒ 這裡先擋掉才畫
                    int aNew = UCL_GUILayout.PopupSearchCache(m_TargetIdx, m_Targets, m_Dic,
                        "RelationshipTargetPicker", GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                    if (aNew != m_TargetIdx && aNew >= 0 && aNew < m_Targets.Count)
                    {
                        m_TargetIdx = aNew;
                        GUI.FocusControl(null);
                    }
                }
                else GUILayout.Label("（這位還沒有任何對象）", SmallStyle);
                GUILayout.FlexibleSpace();
            }

            if (m_Targets.Count == 0) return;
            DrawTarget(m_Personas[m_PersonaIdx], m_Targets[m_TargetIdx]);
        }

        // ===========================================================
        // 區塊職責：確保 (persona, target) 的資料在快取裡（選擇變了才重讀）。
        // 物理意義：重算一次向量拿來跟存值對帳 —— 期初餘額要餵進去，
        //          否則遷移進來的那些人會「重算比存值低」而那不是漂移，是我少給了輸入。
        // 數值影響：純讀 + 純算。
        // ===========================================================
        void EnsureLoaded(string iPersona, string iTarget)
        {
            string aKey = iPersona + "|" + iTarget;
            if (m_CacheKey == aKey) return;
            m_CacheKey = aKey;
            m_Cur = UCL_RelationshipIO.LoadCurrent(iPersona, iTarget);
            m_Events = UCL_RelationshipIO.LoadEvents(iPersona, iTarget);
            m_Opinions = UCL_RelationshipIO.LoadOpinions(iPersona, iTarget);
            // 新到舊：看關係要先看最近發生什麼（舊資料的 at 是空字串，會沉到最後）
            m_Events.Sort((a, b) => string.CompareOrdinal(b.at ?? "", a.at ?? ""));
            m_Opinions.Sort((a, b) => string.CompareOrdinal(b.at ?? "", a.at ?? ""));
            m_Recomputed = UCL_RelationshipCurrent.Recompute(m_Events, m_Cur?.opening_balance);
            m_RecomputedScore = UCL_RelationshipAxes.SurfaceScore(m_Recomputed);
            // 換人就回第一頁 —— DrawSelectPage 自己只會 clamp（第 4 頁換到只有 1 頁的人是對的，
            // 但換到同樣有 4 頁的人時會停在第 4 頁，那不是「我剛翻到那裡」而是上一個人的殘留）。
            m_FoldDic.GetSubDic(PageKeyEvents).Clear();
            m_FoldDic.GetSubDic(PageKeyOpinions).Clear();
        }

        void DrawTarget(string iPersona, string iTarget)
        {
            EnsureLoaded(iPersona, iTarget);
            string aCurPath = UCL_RelationshipIO.CurrentPath(iPersona, iTarget);
            if (m_Cur == null)
            {
                GUILayout.Label($"⚠ 缺 `_current.md`：{aCurPath}", SmallStyle);
                GUILayout.Label($"（事件 {m_Events.Count} 筆仍在 —— `_current.md` 是可重建的投影，"
                    + "跑 run_cmd run Relationship 的 rebuild 就會長回來）", SmallStyle);
                return;
            }

            // ── 標題列：誰對誰、tier / 分數、最後更新 ─────────────────────
            // ⚠ 這裡**刻意不再貼 `_current.md` 全文**（Tim 2026-08-21）——
            //   那份檔案的 frontmatter 與正文本身就重複講了一次分數與筆數，
            //   再加上下面的條與清單就是同一組數字講三遍。要看原檔按下面那顆按鈕。
            using (new GUILayout.VerticalScope(GUI.skin.box))
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"<b>{iPersona} → {iTarget}</b>", RichStyle, GUILayout.ExpandWidth(false));
                    var aStyle = new GUIStyle(UCL_GUIStyle.LabelStyle);
                    aStyle.normal.textColor = TierColor(m_Cur.surface_score);
                    GUILayout.Label($"　{m_Cur.tier}　{m_Cur.surface_score:+0;-0;0} / 100", aStyle,
                        GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                    if (!string.IsNullOrEmpty(m_Cur.last_updated))
                        GUILayout.Label($"<i>{m_Cur.last_updated}</i>", RichStyle, GUILayout.ExpandWidth(false));
                }

                // 總分條（-100 ~ +100）
                DrawBar(m_Cur.surface_score / 100f, TierColor(m_Cur.surface_score), TierColor(m_Cur.surface_score),
                    "總分", m_ShowRaw ? m_Cur.surface_score.ToString() : "");

                GUILayout.Space(4);
                DrawAxisBars();

                // 對帳：存值 vs 從事件流重算 —— 只在不一致時出聲
                // 物理意義：**事件是事實來源，存值是投影** ⇒ 兩者對不上代表投影過期或有人手改了存值。
                //          舊系統把兩者並列在同一個檔裡，實測 108 筆有 3 筆已經對不上而沒有任何機制會叫。
                if (m_RecomputedScore != m_Cur.surface_score)
                {
                    GUILayout.Label($"⚠ 存值 {m_Cur.surface_score} 與事件流重算 {m_RecomputedScore} 不一致"
                        + $"（事件 {m_Events.Count} 筆；`_current.md` 是可重建的投影 ⇒ 重建一次即可對上）",
                        UCL_GUIStyle.GetLabelStyle(new Color(1f, 0.7f, 0.3f)));
                }
                if (m_Cur.opening_balance != null && m_Cur.opening_balance.Count > 0)
                {
                    GUILayout.Label("ℹ 這組帶期初餘額（遷移反推、沒有對應事件）—— 條裡有一段查不到出處",
                        SmallStyle);
                }

                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("開啟 _current.md", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        UCL_MarkdownViewerPage.Create(aCurPath, aCurPath);
                    if (GUILayout.Button("開啟資料夾", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        UnityEditor.EditorUtility.RevealInFinder(UCL_RelationshipIO.TargetDir(iPersona, iTarget));
                    GUILayout.FlexibleSpace();
                }
            }

            DrawEvents(iPersona, iTarget);
            DrawOpinions(iPersona, iTarget);
        }

        // ===========================================================
        // 區塊職責：8 軸情感條 —— 「非文字化」的關係結構。
        // 物理意義：每軸一條，中線是 0，右正左負；值域 [-1,1]。
        //          irritation 特殊：它的正向是「惱怒累積」（壞事）⇒ 顏色反過來，
        //          否則一條長長的綠色會把「他很煩我」畫成好消息。
        // 數值影響：純繪製；raw 數字只在 m_ShowRaw 打開時顯示。
        // ===========================================================
        void DrawAxisBars()
        {
            for (int i = 0; i < UCL_RelationshipAxes.Names.Length; i++)
            {
                string aAxis = UCL_RelationshipAxes.Names[i];
                string aLabel = (i < AxisLabels.Length) ? AxisLabels[i] : aAxis;
                m_Cur.emotion_vector.TryGetValue(aAxis, out float aVal);
                aVal = Mathf.Clamp(aVal, UCL_RelationshipAxes.MIN, UCL_RelationshipAxes.MAX);

                bool aIsIrritation = (aAxis == "irritation");
                Color aPos = aIsIrritation ? new Color(1f, 0.5f, 0.4f) : new Color(0.4f, 0.85f, 0.5f);
                Color aNeg = aIsIrritation ? new Color(0.4f, 0.85f, 0.5f) : new Color(1f, 0.5f, 0.5f);

                // 重算與存值對不上的那一軸標一顆 ⚠ —— 對帳要指到軸，不然只知道總分不對、不知道哪裡不對
                string aSuffix = "";
                if (m_Recomputed != null && m_Recomputed.TryGetValue(aAxis, out float aRe)
                    && Mathf.Abs(aRe - aVal) > 0.0051f)
                {
                    aSuffix = m_ShowRaw ? $"⚠ 重算 {aRe:0.##}" : "⚠";
                }
                DrawBar(aVal, aPos, aNeg, aLabel, (m_ShowRaw ? aVal.ToString("+0.00;-0.00; 0.00") : "") + aSuffix);
            }
        }

        // 區塊職責：畫一條「中線置中」的雙向條。
        // 物理意義：iValue 已正規化到 [-1,1]（總分條是 score/100）。
        // 數值影響：純繪製。bar 半寬固定，跟著 DPI 縮放（寫死像素在高 DPI 下會壞）。
        void DrawBar(float iValue, Color iPos, Color iNeg, string iLabel, string iRight)
        {
            float aHalf = UCL_GUIStyle.GetScaledSize(90);
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label(iLabel, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(56)));
                Rect aRect = GUILayoutUtility.GetRect(aHalf * 2 + 4, 14, GUILayout.ExpandWidth(false));
                aRect.width = aHalf * 2 + 4;
                UnityEditor.EditorGUI.DrawRect(new Rect(aRect.x, aRect.y + 5, aRect.width, 4),
                    new Color(0.25f, 0.25f, 0.25f, 0.5f));
                UnityEditor.EditorGUI.DrawRect(new Rect(aRect.x + aHalf, aRect.y, 1, aRect.height),
                    new Color(0.6f, 0.6f, 0.6f));
                float v = Mathf.Clamp(iValue, -1f, 1f);
                if (v > 0)
                    UnityEditor.EditorGUI.DrawRect(new Rect(aRect.x + aHalf + 1, aRect.y + 3, aHalf * v, 8), iPos);
                else if (v < 0)
                    UnityEditor.EditorGUI.DrawRect(new Rect(aRect.x + aHalf - aHalf * -v, aRect.y + 3, aHalf * -v, 8), iNeg);
                if (!string.IsNullOrEmpty(iRight))
                {
                    GUILayout.Space(6);
                    GUILayout.Label(iRight, SmallStyle, GUILayout.ExpandWidth(false));
                }
                GUILayout.FlexibleSpace();
            }
        }

        // ===========================================================
        // 區塊職責：事件清單 —— **內文**（reason）＋ 這筆動了哪幾軸。
        // 物理意義：分數是投影，事件才是事實 ⇒ 這一區是「為什麼是這個分數」的答案。
        //          軸變動只畫方向（↑↓）不寫數字，除非打開原始數值 —— 同「非文字化」原則。
        // 數值影響：純繪製；分頁每頁 ItemsPerPage 筆（走 DrawSelectPage，只有超過一頁才畫翻頁列）。
        // ===========================================================
        void DrawEvents(string iPersona, string iTarget)
        {
            using (new GUILayout.VerticalScope(GUI.skin.box))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "Fold.Events", 21, iDefaultValue: true);
                    GUILayout.Label($"<b>事件</b>　{m_Events.Count} 筆", RichStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;
                if (m_Events.Count == 0)
                {
                    GUILayout.Label("（還沒有任何事件 —— 有事發生時走 run_cmd run Relationship 記一筆）", SmallStyle);
                    return;
                }
                var aPage = UCL_GUILayout.DrawSelectPage(
                    m_FoldDic.GetSubDic(PageKeyEvents), m_Events.Count, ItemsPerPage);
                int aEnd = Mathf.Min(m_Events.Count, aPage.startIndex + ItemsPerPage);
                for (int i = aPage.startIndex; i < aEnd; i++)
                {
                    var e = m_Events[i];
                    using (new GUILayout.VerticalScope(GUI.skin.box))
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label(FormatAt(e.at), SmallStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(130)));
                            GUILayout.Label(FormatDeltas(e.axis_deltas), RichStyle, GUILayout.ExpandWidth(false));
                            GUILayout.FlexibleSpace();
                            // source：live＝當場記的，其他值是遷移進來的來源專案名
                            if (!string.IsNullOrEmpty(e.source) && e.source != "live")
                                GUILayout.Label($"<i>{e.source}</i>", RichStyle, GUILayout.ExpandWidth(false));
                        }
                        if (!string.IsNullOrEmpty(e.reason)) GUILayout.Label(e.reason, SmallStyle);
                    }
                }
            }
        }

        // 區塊職責：看法清單 —— 純文字，與向量解耦（沒有 axis_deltas）。
        // 物理意義：舊資料的 at 是**空的**（沒有時戳，不是漏填）⇒ 顯示成「(無時戳)」，不猜時間。
        void DrawOpinions(string iPersona, string iTarget)
        {
            using (new GUILayout.VerticalScope(GUI.skin.box))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "Fold.Opinions", 21, iDefaultValue: false);
                    GUILayout.Label($"<b>看法</b>　{m_Opinions.Count} 則", RichStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!aShow || m_Opinions.Count == 0) return;
                var aPage = UCL_GUILayout.DrawSelectPage(
                    m_FoldDic.GetSubDic(PageKeyOpinions), m_Opinions.Count, ItemsPerPage);
                int aEnd = Mathf.Min(m_Opinions.Count, aPage.startIndex + ItemsPerPage);
                for (int i = aPage.startIndex; i < aEnd; i++)
                {
                    var o = m_Opinions[i];
                    using (new GUILayout.VerticalScope(GUI.skin.box))
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label(string.IsNullOrEmpty(o.at) ? "(無時戳)" : FormatAt(o.at),
                                SmallStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(130)));
                            if (o.origin != null && o.origin.Count > 0)
                                GUILayout.Label($"<i>{string.Join(" ", o.origin)}</i>", RichStyle, GUILayout.ExpandWidth(false));
                            GUILayout.FlexibleSpace();
                        }
                        if (!string.IsNullOrEmpty(o.text)) GUILayout.Label(o.text, SmallStyle);
                    }
                }
            }
        }

        // 區塊職責：UTC ISO → 當地時間（顯示用）。
        // 物理意義：資料一律存 UTC（跨機器可比），人看的是自己的時鐘。
        // 數值影響：解析失敗**原樣回傳**，不吞掉 —— 空白會讓人以為「沒有時間」。
        static string FormatAt(string iIso)
        {
            if (string.IsNullOrEmpty(iIso)) return "";
            if (System.DateTime.TryParse(iIso, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var aDt))
                return aDt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return iIso;
        }

        // 這筆事件動了哪幾軸：只給方向（↑↓），數字要看就開「顯示原始數值」。
        string FormatDeltas(Dictionary<string, float> iDeltas)
        {
            if (iDeltas == null || iDeltas.Count == 0) return "<i>（無軸變動）</i>";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < UCL_RelationshipAxes.Names.Length; i++)
            {
                string aAxis = UCL_RelationshipAxes.Names[i];
                if (!iDeltas.TryGetValue(aAxis, out float d) || Mathf.Abs(d) < 0.0001f) continue;
                string aLabel = (i < AxisLabels.Length) ? AxisLabels[i] : aAxis;
                bool aGood = (aAxis == "irritation") ? d < 0 : d > 0;
                string aColor = aGood ? "#66DD88" : "#FF8888";
                string aNum = m_ShowRaw ? d.ToString("+0.##;-0.##") : (d > 0 ? "↑" : "↓");
                if (sb.Length > 0) sb.Append(' ');
                sb.Append($"<color={aColor}>{aLabel}{aNum}</color>");
            }
            return sb.Length > 0 ? sb.ToString() : "<i>（無軸變動）</i>";
        }

        // 區塊職責：tier → 顏色（沿用舊 UCL_AffinitySystemPage 的 5 段色階，門檻同 UCL_RelationshipAxes.Tier）
        // ⚠ 門檻寫在兩個地方（這裡與 Tier()）——顏色與文字若不同步，畫面會出現「信任」配橘色這種矛盾。
        //   之所以沒抽共用：Tier() 回字串、這裡要 Color，抽出來得多一層對照表；
        //   改門檻時**兩處一起改**，兩邊都用同一組數字（51 / 11 / -9 / -49）。
        static Color TierColor(int iScore)
        {
            if (iScore >= 51) return new Color(0.3f, 0.8f, 1f);    // 信任
            if (iScore >= 11) return new Color(0.4f, 0.9f, 0.4f);  // 在意
            if (iScore >= -9) return Color.white;                  // 普通
            if (iScore >= -49) return new Color(1f, 0.7f, 0.3f);   // 冷淡
            return new Color(1f, 0.4f, 0.4f);                      // 厭惡
        }

    }
}
#endif
