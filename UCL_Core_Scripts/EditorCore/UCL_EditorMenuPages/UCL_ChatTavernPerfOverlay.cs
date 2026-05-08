// 區塊職責：UCL_ChatTavernPage 效能取樣與 overlay 顯示器
// 物理意義：包一層 Stopwatch + UnityEngine.Profiling.Profiler.Begin/EndSample，
//          兩路同時走 — Stopwatch 給內建 overlay 顯示、Profiler 給 Unity Profiler 視窗看
// 數值影響：Sample(name) using-scope 一次約 < 1us 開銷（Stopwatch 啟停 + dict 查表 + Profiler API），
//          相對 IMGUI 一幀數 ms 級耗時 < 0.1% 噪音可忽略
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// UCL_ChatTavernPage 用的微觀效能取樣 + 內建 overlay 顯示。
    /// 用法：
    /// <code>
    /// using (UCL_ChatTavernPerfOverlay.Sample("DrawMessagesView"))
    /// {
    ///     // 被測程式碼
    /// }
    /// </code>
    /// 顯示：把 <see cref="ShowEnabled"/> 設 true 後，於 OnGUI 適當位置呼叫 <see cref="DrawOverlay"/>。
    /// </summary>
    public static class UCL_ChatTavernPerfOverlay
    {
        // 區塊職責：每個 sample name 的 ring buffer (size=N)，每幀記一筆耗時 us
        // 物理意義：rolling 平均代表「最近 N 幀典型耗時」；max 代表「最近的 spike」
        // 數值影響：N=60 → 1 秒視窗（60fps 下）；改大平滑、改小靈敏
        const int RingSize = 60;

        class SampleStats
        {
            public string name;
            public float[] ring = new float[RingSize];   // 微秒
            public int writeIdx = 0;
            public int filled = 0;                        // 第一輪填滿前的計數
            public long totalCalls = 0;                   // 累計呼叫次數（含本幀）

            public float Avg
            {
                get
                {
                    if (filled == 0) return 0f;
                    int n = filled;
                    float sum = 0f;
                    for (int i = 0; i < n; i++) sum += ring[i];
                    return sum / n;
                }
            }

            public float Max
            {
                get
                {
                    if (filled == 0) return 0f;
                    float mx = 0f;
                    for (int i = 0; i < filled; i++) if (ring[i] > mx) mx = ring[i];
                    return mx;
                }
            }
        }

        static readonly Dictionary<string, SampleStats> s_Stats = new Dictionary<string, SampleStats>(32);
        static readonly List<string> s_Order = new List<string>(32);   // 維持 sample 加入順序，overlay 顯示用

        /// <summary>是否啟用取樣 (false → Sample(name) 走 zero-cost 短路)。</summary>
        public static bool Enabled = false;

        /// <summary>是否在 OnGUI 顯示 overlay (即使 Enabled=true 也可只取樣不顯示，例如背景收 baseline)。</summary>
        public static bool ShowOverlay = false;

        /// <summary>
        /// 開始取樣一個 named scope。with using-block 自動結算。
        /// Enabled=false 時回傳 NullScope（zero alloc / zero work）。
        /// </summary>
        public static IDisposable Sample(string name)
        {
            if (!Enabled) return NullScope.Instance;
            return new SampleScope(name);
        }

        /// <summary>清空所有取樣資料（按「重置」按鈕用）。</summary>
        public static void Reset()
        {
            s_Stats.Clear();
            s_Order.Clear();
        }

        /// <summary>
        /// 內建 overlay GUI — 直接呼叫即可顯示在當前 GUILayout 流。
        /// 顯示每個 sample 的 avg / max（單位 ms）+ 呼叫次數。
        /// </summary>
        public static void DrawOverlay()
        {
            if (!ShowOverlay) return;

            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("⏱ <b>Perf Overlay</b>", new GUIStyle(GUI.skin.label) { richText = true });
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Reset", GUILayout.Width(60))) Reset();
                    Enabled = GUILayout.Toggle(Enabled, "Sample", GUILayout.Width(70));
                }

                if (s_Order.Count == 0)
                {
                    GUILayout.Label("(no samples yet — 進酒館頁面互動幾秒後再看)");
                    return;
                }

                GUILayout.Label("section | avg ms | max ms | calls", new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Italic });
                foreach (var nm in s_Order)
                {
                    if (!s_Stats.TryGetValue(nm, out var st)) continue;
                    float avgMs = st.Avg / 1000f;   // us → ms
                    float maxMs = st.Max / 1000f;
                    // 用顏色標 hot：> 2ms 標紅、> 0.5ms 標黃
                    string color = avgMs > 2f ? "red" : (avgMs > 0.5f ? "yellow" : "white");
                    GUILayout.Label(
                        $"<color={color}><b>{nm}</b></color>  avg=<b>{avgMs:F2}</b>  max={maxMs:F2}  calls={st.totalCalls}",
                        new GUIStyle(GUI.skin.label) { richText = true });
                }
                GUILayout.Label($"_window={RingSize} frames; 紅 > 2ms / 黃 > 0.5ms / 白 = OK_",
                    new GUIStyle(GUI.skin.label) { richText = true, fontStyle = FontStyle.Italic });
            }
        }

        /// <summary>把當前累計取樣寫到 Debug.Log（ConsoleLog 持久化用）。</summary>
        public static void DumpToLog()
        {
            if (s_Order.Count == 0)
            {
                UnityEngine.Debug.Log("[ChatTavernPerf] no samples");
                return;
            }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[ChatTavernPerf] section | avg_ms | max_ms | calls");
            foreach (var nm in s_Order)
            {
                if (!s_Stats.TryGetValue(nm, out var st)) continue;
                sb.AppendLine($"  {nm}  avg={(st.Avg / 1000f):F2}ms  max={(st.Max / 1000f):F2}ms  calls={st.totalCalls}");
            }
            UnityEngine.Debug.Log(sb.ToString());
        }

        // ===========================================================
        // 區塊：Sample 內部實作
        // ===========================================================

        sealed class SampleScope : IDisposable
        {
            readonly string m_Name;
            readonly long m_Start;
            bool m_Disposed;

            public SampleScope(string name)
            {
                m_Name = name;
                UnityEngine.Profiling.Profiler.BeginSample(name);
                m_Start = Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                if (m_Disposed) return;
                m_Disposed = true;
                long end = Stopwatch.GetTimestamp();
                UnityEngine.Profiling.Profiler.EndSample();
                // ticks → us
                double us = (end - m_Start) * 1_000_000.0 / Stopwatch.Frequency;
                Record(m_Name, (float)us);
            }
        }

        sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();
            public void Dispose() { }
        }

        static void Record(string name, float us)
        {
            if (!s_Stats.TryGetValue(name, out var st))
            {
                st = new SampleStats { name = name };
                s_Stats[name] = st;
                s_Order.Add(name);
            }
            st.ring[st.writeIdx] = us;
            st.writeIdx = (st.writeIdx + 1) % RingSize;
            if (st.filled < RingSize) st.filled++;
            st.totalCalls++;
        }
    }
}
#endif
