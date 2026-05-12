#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UCL.Core.JsonLib;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;

namespace UCL.Core.EditorLib.Page
{
    // 區塊職責：好感度系統 UI — 矩陣總覽 + per-persona 詳情 + 8 軸隱藏情感向量視覺化
    // 物理意義：讀 `ChatTavern/affinity/<persona>/relations.json` 各檔（schema v2），把
    //          多維度 hidden emotion_vector 用 bar 圖呈現，非單純 1D 文字分數
    // 數值影響：純唯讀 UI；資料寫入走 Python `_lib/affinity_manager.py`
    //
    // Schema reference: AgentCommands/_lib/affinity_manager.py EMOTION_AXES + EMOTION_WEIGHTS
    [HelpURL("ucl_core:Docs~/{lang}/Mechanics/Affinity_System.md")]
    public class UCL_AffinitySystemPage : UCL_CommonEditorPage
    {
        public override string WindowName => "Affinity System";

        // 將此頁面註冊進 EditorMenu 的 Page Picker 下拉選單中
        public override bool ShowInPageMenu => true;

        public static UCL_AffinitySystemPage Create() => UCL_EditorPage.Create<UCL_AffinitySystemPage>();

        // 區塊職責：常數定義 — 8 軸情感維度 + 顯示中文標籤
        // 物理意義：對齊 Python `affinity_manager.py` EMOTION_AXES 順序，C# 端讀檔以索引取軸
        // 數值影響：修改順序會搞錯 emotion_vector 對應軸，必須跟 Python 端同步改
        static readonly string[] EmotionAxes = new[]
        {
            "trust", "affection", "respect", "interest",
            "irritation", "dependence", "admiration", "loyalty",
        };

        static readonly string[] EmotionAxisLabels = new[]
        {
            "信任", "親密", "敬重", "在意",
            "煩躁", "依賴", "欣賞", "忠誠",
        };

        // 區塊職責：emotion vector 記錄結構
        // 物理意義：vector 8 軸 in [-1, 1]，surface_score in [-100, 100] 由 weighted_sum 算出
        public class Record
        {
            public float[] Vector = new float[EmotionAxes.Length];   // 8 軸 hidden state
            public int SurfaceScore;                                  // 表面總分 (-100~100)
            public string Tier = "";                                  // 5 段 tier 標籤 (信任/在意/普通/冷淡/厭惡)
            public List<string> Opinions = new List<string>();
            public string LastUpdated = "";
            public List<HistoryEntry> RecentHistory = new List<HistoryEntry>();  // 最近 5 筆
        }

        public class HistoryEntry
        {
            public string Reason = "";
            public string At = "";
            public Dictionary<string, float> AxisDeltas = new Dictionary<string, float>();
        }

        // 區塊職責：快取資料 — 全 persona × target 對映
        // 物理意義：matrix view 需要全域對齊；details view 切某 persona 子集
        Dictionary<string, Dictionary<string, Record>> m_Data = new Dictionary<string, Dictionary<string, Record>>();
        List<string> m_AllPersonas = new List<string>();
        List<string> m_AllTargets = new List<string>();

        string m_AffinityDir;
        string m_SelectedPersona = "";
        readonly UCL_ObjectDictionary m_PickerDic = new UCL_ObjectDictionary();
        Vector2 m_MatrixScroll = Vector2.zero;
        bool m_ShowRawVector = false;  // toggle：是否顯示原始浮點數 (debug)

        public override void Init(UCL_GUIPageController p_Controller)
        {
            base.Init(p_Controller);
            // 區塊：路徑解析 — 新 schema v2 在 `ChatTavern/affinity/` 子資料夾
            // 物理意義：相容舊邏輯 — 若舊 affinity_registry.json 仍存在但 affinity/ 不存在，
            //          UI 印提示要求跑 python migrate；不在 C# 端 auto-migrate（C# read-only）
            m_AffinityDir = Path.Combine(UCL_ChatTavernIO.GetTavernDir(), "affinity");
            LoadData();
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button("🔄 Refresh", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                LoadData();
            }
            m_ShowRawVector = GUILayout.Toggle(m_ShowRawVector, "Show raw vector", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
        }

        // 區塊職責：載入並反序列化 — 掃 affinity/ 下每個 persona 資料夾的 relations.json
        // 物理意義：schema v2 — 每 persona 一檔，含 emotion_vector (8 軸 float list) + surface_score + opinions + history
        // 數值影響：更新 m_Data / m_AllPersonas / m_AllTargets 全域快取
        void LoadData()
        {
            m_Data.Clear();
            m_AllPersonas.Clear();
            m_AllTargets.Clear();
            if (!Directory.Exists(m_AffinityDir)) return;

            foreach (var personaDir in Directory.GetDirectories(m_AffinityDir))
            {
                string persona = Path.GetFileName(personaDir);
                string relationsPath = Path.Combine(personaDir, "relations.json");
                if (!File.Exists(relationsPath)) continue;

                try
                {
                    string json = File.ReadAllText(relationsPath);
                    var jd = JsonData.ParseJson(json);
                    if (!jd.IsObject || jd.Dic == null) continue;
                    if (!jd.Dic.ContainsKey("targets")) continue;

                    var targetsNode = jd.Dic["targets"];
                    if (!targetsNode.IsObject || targetsNode.Dic == null) continue;

                    var pDict = new Dictionary<string, Record>();
                    m_Data[persona] = pDict;
                    if (!m_AllPersonas.Contains(persona)) m_AllPersonas.Add(persona);

                    foreach (var tKey in targetsNode.Keys)
                    {
                        if (!m_AllTargets.Contains(tKey)) m_AllTargets.Add(tKey);

                        var tNode = targetsNode.Dic[tKey];
                        var rec = new Record();

                        // 區塊：parse emotion_vector — list of 8 float
                        if (tNode.Dic != null && tNode.Dic.ContainsKey("emotion_vector"))
                        {
                            var vecNode = tNode.Dic["emotion_vector"];
                            if (vecNode.IsArray)
                            {
                                var vecList = (System.Collections.IList)vecNode;
                                for (int i = 0; i < EmotionAxes.Length && i < vecList.Count; i++)
                                {
                                    var item = vecList[i] as JsonData;
                                    if (item != null)
                                    {
                                        // GetFloat 不存在 → 用 GetDouble cast
                                        try { rec.Vector[i] = (float)item.GetDouble(); }
                                        catch { rec.Vector[i] = 0f; }
                                    }
                                }
                            }
                        }

                        rec.SurfaceScore = tNode.GetInt("surface_score", 0);
                        rec.Tier = tNode.GetString("tier", "");
                        rec.LastUpdated = tNode.GetString("last_updated", "");

                        // opinions
                        if (tNode.Dic != null && tNode.Dic.ContainsKey("opinions"))
                        {
                            var opNode = tNode.Dic["opinions"];
                            if (opNode.IsArray)
                            {
                                var opList = (System.Collections.IList)opNode;
                                for (int i = 0; i < opList.Count; i++)
                                {
                                    var op = opList[i] as JsonData;
                                    if (op != null) rec.Opinions.Add(op.GetString());
                                }
                            }
                        }

                        // 區塊：history (取最後 5 筆顯示 — UI 不放完整歷史防 page 爆)
                        if (tNode.Dic != null && tNode.Dic.ContainsKey("history"))
                        {
                            var hNode = tNode.Dic["history"];
                            if (hNode.IsArray)
                            {
                                var hList = (System.Collections.IList)hNode;
                                int start = System.Math.Max(0, hList.Count - 5);
                                for (int i = start; i < hList.Count; i++)
                                {
                                    var hItem = hList[i] as JsonData;
                                    if (hItem == null) continue;
                                    var he = new HistoryEntry
                                    {
                                        Reason = hItem.GetString("reason", ""),
                                        At = hItem.GetString("at", ""),
                                    };
                                    if (hItem.Dic != null && hItem.Dic.ContainsKey("axis_deltas"))
                                    {
                                        var ad = hItem.Dic["axis_deltas"];
                                        if (ad.IsObject && ad.Dic != null)
                                        {
                                            foreach (var k in ad.Keys)
                                            {
                                                try { he.AxisDeltas[k] = (float)ad.Dic[k].GetDouble(); }
                                                catch { }
                                            }
                                        }
                                    }
                                    rec.RecentHistory.Add(he);
                                }
                            }
                        }

                        pDict[tKey] = rec;
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[AffinitySystem] LoadData fail for persona='{persona}': {ex.Message}");
                }
            }

            // 排序穩定（C# Dictionary 無保序）
            m_AllPersonas.Sort();
            m_AllTargets.Sort();

            if ((string.IsNullOrEmpty(m_SelectedPersona) || !m_AllPersonas.Contains(m_SelectedPersona))
                && m_AllPersonas.Count > 0)
            {
                m_SelectedPersona = m_AllPersonas[0];
            }
        }

        protected override void ContentOnGUI()
        {
            if (m_AllPersonas.Count == 0)
            {
                GUILayout.Label($"無好感度資料 — `{m_AffinityDir}` 不存在或空。\n首次使用請跑：\n`python -m _lib.affinity_manager migrate`", UCL_GUIStyle.LabelStyle);
                return;
            }

            DrawMatrix();
            GUILayout.Space(16);
            DrawDetails();
        }

        // 區塊職責：總覽矩陣 — Persona × Target 的 surface_score
        // 物理意義：1D surface 適合一眼掃全局；深度看情感結構走 DrawDetails 的 bar 圖
        // 數值影響：純 UI 繪製
        void DrawMatrix()
        {
            GUILayout.Label("<b>Affinity Matrix (好感度矩陣 — 表面總分)</b>", UCL_GUIStyle.LabelStyle);
            using (new GUILayout.VerticalScope("box"))
            {
                m_MatrixScroll = GUILayout.BeginScrollView(m_MatrixScroll, GUILayout.Height(UCL_GUIStyle.GetScaledSize(220)));

                // 表頭
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Persona \\ Target", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(150)));
                    foreach (var target in m_AllTargets)
                    {
                        GUILayout.Label(target, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    }
                }

                // 資料列
                foreach (var persona in m_AllPersonas)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label(persona, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(150)));

                        var dict = m_Data[persona];
                        foreach (var target in m_AllTargets)
                        {
                            if (dict.TryGetValue(target, out var rec))
                            {
                                var style = new GUIStyle(UCL_GUIStyle.LabelStyle);
                                style.normal.textColor = GetTierColor(rec.SurfaceScore);
                                GUILayout.Label($"{rec.SurfaceScore} ({rec.Tier})", style, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                            }
                            else
                            {
                                GUILayout.Label("-", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                            }
                        }
                    }
                }
                GUILayout.EndScrollView();
            }
        }

        // 區塊職責：所選 persona 的所有 target 詳情 — 含 8 軸情感 bar 圖
        // 物理意義：「非文字化的隱藏好感矩陣」— 每軸畫 bar 而非寫數字，正色綠 / 負色紅
        // 數值影響：純 UI 繪製
        void DrawDetails()
        {
            GUILayout.Label("<b>Persona Details (情感結構 — 8 軸隱藏向量)</b>", UCL_GUIStyle.LabelStyle);

            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label("Select Persona:", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                int curIdx = m_AllPersonas.IndexOf(m_SelectedPersona);
                if (curIdx < 0) curIdx = 0;
                int newIdx = UCL_GUILayout.PopupSearchCache(curIdx, m_AllPersonas, m_PickerDic, "PersonaPicker", GUILayout.Width(UCL_GUIStyle.GetScaledSize(250)));
                if (newIdx >= 0 && newIdx < m_AllPersonas.Count) m_SelectedPersona = m_AllPersonas[newIdx];
                GUILayout.FlexibleSpace();
            }

            if (string.IsNullOrEmpty(m_SelectedPersona) || !m_Data.ContainsKey(m_SelectedPersona)) return;

            var dict = m_Data[m_SelectedPersona];
            foreach (var kvp in dict)
            {
                var target = kvp.Key;
                var rec = kvp.Value;

                using (new GUILayout.VerticalScope("box"))
                {
                    // 標題列：target + surface_score + tier
                    using (new GUILayout.HorizontalScope())
                    {
                        GUILayout.Label($"<b>Target:</b> {target}", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                        var style = new GUIStyle(UCL_GUIStyle.LabelStyle);
                        style.normal.textColor = GetTierColor(rec.SurfaceScore);
                        GUILayout.Label($"<b>Surface:</b> {rec.SurfaceScore} ({rec.Tier})", style);
                        GUILayout.FlexibleSpace();
                        if (!string.IsNullOrEmpty(rec.LastUpdated))
                        {
                            GUILayout.Label($"<i>{rec.LastUpdated}</i>", UCL_GUIStyle.LabelStyle);
                        }
                    }

                    GUILayout.Space(4);
                    DrawEmotionBars(rec);

                    // Opinions
                    if (rec.Opinions != null && rec.Opinions.Count > 0)
                    {
                        GUILayout.Space(6);
                        GUILayout.Label("<i>Opinions:</i>", UCL_GUIStyle.LabelStyle);
                        foreach (var op in rec.Opinions)
                        {
                            GUILayout.Label($" • {op}", UCL_GUIStyle.LabelStyle);
                        }
                    }

                    // Recent history
                    if (rec.RecentHistory != null && rec.RecentHistory.Count > 0)
                    {
                        GUILayout.Space(6);
                        GUILayout.Label($"<i>Recent {rec.RecentHistory.Count} events:</i>", UCL_GUIStyle.LabelStyle);
                        foreach (var h in rec.RecentHistory)
                        {
                            // 簡短列出觸發軸（不寫具體 delta 數字，避免「文字化」）
                            var axesTouched = new List<string>();
                            foreach (var ad in h.AxisDeltas)
                            {
                                if (System.Math.Abs(ad.Value) < 0.001f) continue;
                                int idx = System.Array.IndexOf(EmotionAxes, ad.Key);
                                string axisLabel = (idx >= 0 && idx < EmotionAxisLabels.Length) ? EmotionAxisLabels[idx] : ad.Key;
                                string arrow = ad.Value > 0 ? "↑" : "↓";
                                axesTouched.Add($"{axisLabel}{arrow}");
                            }
                            string axesStr = axesTouched.Count > 0 ? $"[{string.Join(" ", axesTouched)}] " : "";
                            GUILayout.Label($" • {axesStr}{h.Reason}  <i>({h.At})</i>", UCL_GUIStyle.LabelStyle);
                        }
                    }
                }
            }
        }

        // 區塊職責：把 8 軸 hidden vector 用 bar 圖呈現 — 「非文字化」視覺
        // 物理意義：每軸一條 bar，中線是 0，向右 = 正值（綠），向左 = 負值（紅）；
        //          irritation 軸特殊（正向 = 煩躁累積，仍用紅色以區別）
        // 數值影響：純 UI；bar 寬度 = |value| * MaxBarWidth
        void DrawEmotionBars(Record rec)
        {
            const float maxBarHalfWidth = 80f;     // 中心向兩邊各最多 80px
            float halfWidth = UCL_GUIStyle.GetScaledSize(maxBarHalfWidth);
            float labelWidth = UCL_GUIStyle.GetScaledSize(60);
            float numericWidth = UCL_GUIStyle.GetScaledSize(50);

            for (int i = 0; i < EmotionAxes.Length; i++)
            {
                string axisName = EmotionAxes[i];
                string axisLabel = EmotionAxisLabels[i];
                float value = (i < rec.Vector.Length) ? rec.Vector[i] : 0f;
                value = Mathf.Clamp(value, -1f, 1f);

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(axisLabel, UCL_GUIStyle.LabelStyle, GUILayout.Width(labelWidth));

                    // 區塊：bar 渲染 — 用 GUILayout box 兩段 (負區 / 正區) 拼成中線置中
                    Rect rect = GUILayoutUtility.GetRect(halfWidth * 2 + 4, 14, GUILayout.ExpandWidth(false));
                    rect.width = halfWidth * 2 + 4;

                    // 背景（中性灰）
                    EditorGUI.DrawRect(new Rect(rect.x, rect.y + 5, rect.width, 4), new Color(0.25f, 0.25f, 0.25f, 0.5f));
                    // 中線
                    EditorGUI.DrawRect(new Rect(rect.x + halfWidth, rect.y, 1, rect.height), new Color(0.6f, 0.6f, 0.6f));

                    // 軸特殊性：irritation 正向是「煩躁累積」(壞) → 反色
                    bool isIrritation = (axisName == "irritation");
                    Color posColor = isIrritation ? new Color(1f, 0.5f, 0.4f) : new Color(0.4f, 0.85f, 0.5f);
                    Color negColor = isIrritation ? new Color(0.4f, 0.85f, 0.5f) : new Color(1f, 0.5f, 0.5f);

                    if (value > 0)
                    {
                        float barW = halfWidth * value;
                        EditorGUI.DrawRect(new Rect(rect.x + halfWidth + 1, rect.y + 3, barW, 8), posColor);
                    }
                    else if (value < 0)
                    {
                        float barW = halfWidth * (-value);
                        EditorGUI.DrawRect(new Rect(rect.x + halfWidth - barW, rect.y + 3, barW, 8), negColor);
                    }

                    // 可選：raw 數字（debug toggle 開才印 — 預設「非文字化」）
                    if (m_ShowRawVector)
                    {
                        GUILayout.Space(4);
                        GUILayout.Label(value.ToString("+0.00;-0.00; 0.00"), UCL_GUIStyle.LabelStyle, GUILayout.Width(numericWidth));
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }

        // 區塊職責：surface_score → 顏色映射（沿用 v1 5-tier 色階）
        // 物理意義：信任(青) / 在意(綠) / 普通(白) / 冷淡(橘) / 厭惡(紅)
        Color GetTierColor(int score)
        {
            if (score >= 51) return new Color(0.3f, 0.8f, 1f);   // 信任
            if (score >= 11) return new Color(0.4f, 0.9f, 0.4f);  // 在意
            if (score >= -9) return Color.white;                  // 普通
            if (score >= -49) return new Color(1f, 0.7f, 0.3f);   // 冷淡
            return new Color(1f, 0.4f, 0.4f);                     // 厭惡
        }
    }
}
#endif
