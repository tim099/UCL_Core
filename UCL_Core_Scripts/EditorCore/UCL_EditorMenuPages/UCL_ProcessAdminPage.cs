// 區塊職責: Process 管理頁 — UCL_ProcessRegistryService 的 UI 入口, 列出所有 C# 註冊的 child process,
//            即時身分驗證 (Alive / Dead / PidReused / Unknown), 提供防誤殺 kill 與殘留記錄清理。
// 物理意義: 「多顆 daemon 併跑互踩」「recompile 後孤兒 process」「PID 被回收誤殺別人」的可視化與處置台
//            (2026-07-27 Tim 拍板 — 配套 UCL_ProcessRegistryService)。
// 2026-07-27 summit
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// Process 管理頁 — 檢視/處置所有經 UCL_ProcessRegistryService 註冊的 child process。
    /// kill 一律走身分三重驗證 (PID + name + start time), PID 已易主時拒絕動手。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_ProcessAdminPage.md")]
    public class UCL_ProcessAdminPage : UCL_CommonEditorPage
    {
        public override string WindowName => "Process 管理";
        protected override bool ShowBackButton => true;
        public override bool ShowInPageMenu => true;

        // 區塊職責: 顯示快取 — 每 REFRESH_INTERVAL 秒 LoadAll + Validate 一次, 不每 OnGUI 都打 OS API
        List<(UCL_ProcessRecord rec, UCL_ProcessStatus status)> m_Rows = new();
        double m_LastRefresh = -1.0;
        const double REFRESH_INTERVAL_SEC = 2.0;

        // kill 二段確認 (仿 ScreenStreamPage 錄影 toggle): 第一次點 = arm, 5s 內再點同一顆才真 kill
        int m_ArmedKillPid = -1;
        double m_ArmedKillTime = -1.0;

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

        public static UCL_ProcessAdminPage Create()
        {
            var page = new UCL_ProcessAdminPage();
            UCL_GUIPageController.CurrentRenderIns.Push(page);
            return page;
        }

        void Refresh()
        {
            m_Rows = UCL_ProcessRegistryService.LoadAllWithStatus();
        }

        protected override void ContentOnGUI()
        {
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (now - m_LastRefresh > REFRESH_INTERVAL_SEC)
            {
                Refresh();
                m_LastRefresh = now;
            }

            GUILayout.Space(10);
            GUILayout.Label("🧩 Process 管理", new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold });
            GUILayout.Label("C# 端開啟的 child process 註冊中心 — kill 前做 PID+name+start_time 三重身分驗證, PID 被回收再發時拒絕動手 (防誤殺)。");
            GUILayout.Space(6);

            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("🔄 立即重新整理", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    Refresh();
                    m_LastRefresh = now;
                }
                if (GUILayout.Button("🧹 清理失效記錄 (Dead/PidReused)", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    int n = UCL_ProcessRegistryService.CleanupStale();
                    Debug.Log($"[UCL_ProcessAdminPage] cleanup: 清除 {n} 筆失效記錄");
                    Refresh();
                }
                if (GUILayout.Button("📂 開啟資料夾", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    try
                    {
                        string dir = UCL_ProcessRegistryService.RegistryDir;
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        UnityEditor.EditorUtility.RevealInFinder(dir);
                    }
                    catch (Exception e) { Debug.LogWarning($"[UCL_ProcessAdminPage] 開啟資料夾失敗: {e.Message}"); }
                }
            }
            GUILayout.Space(8);

            if (m_Rows.Count == 0)
            {
                GUILayout.Label("（目前沒有註冊記錄 — C# spawn 端經 UCL_ProcessRegistryService.Register 註冊後會出現在這裡）",
                    new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Italic });
                return;
            }

            // arm 過期自動解除
            if (m_ArmedKillPid >= 0 && now - m_ArmedKillTime > 5.0) m_ArmedKillPid = -1;

            var oldColor = GUI.backgroundColor;
            foreach (var (rec, status) in m_Rows)
            {
                GUI.backgroundColor = status switch
                {
                    UCL_ProcessStatus.Alive => new Color(0.25f, 0.45f, 0.25f),
                    UCL_ProcessStatus.Dead => new Color(0.35f, 0.35f, 0.35f),
                    UCL_ProcessStatus.PidReused => new Color(0.6f, 0.35f, 0.1f),
                    _ => new Color(0.45f, 0.4f, 0.2f),
                };
                using (new GUILayout.VerticalScope(GUI.skin.box))
                {
                    GUI.backgroundColor = oldColor;
                    using (new GUILayout.HorizontalScope())
                    {
                        string icon = status switch
                        {
                            UCL_ProcessStatus.Alive => "🟢 ALIVE",
                            UCL_ProcessStatus.Dead => "⚫ DEAD",
                            UCL_ProcessStatus.PidReused => "🟠 PID_REUSED (PID 已易主)",
                            _ => "🟡 UNKNOWN (無法驗證)",
                        };
                        GUILayout.Label($"{icon}  [{rec.tag}]  PID {rec.pid}",
                            new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold });
                        GUILayout.FlexibleSpace();

                        if (status == UCL_ProcessStatus.Alive)
                        {
                            bool armed = m_ArmedKillPid == rec.pid;
                            GUI.backgroundColor = armed ? new Color(0.9f, 0.4f, 0.1f) : new Color(0.7f, 0.25f, 0.25f);
                            string killLabel = armed
                                ? $"⚠ 再點確認 kill ({Math.Max(0, 5.0 - (now - m_ArmedKillTime)):F0}s)"
                                : "⏹ Kill";
                            if (GUILayout.Button(killLabel, UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            {
                                if (!armed)
                                {
                                    m_ArmedKillPid = rec.pid;
                                    m_ArmedKillTime = now;
                                }
                                else
                                {
                                    m_ArmedKillPid = -1;
                                    if (UCL_ProcessRegistryService.KillRegistered(rec, out string err))
                                        Debug.Log($"[UCL_ProcessAdminPage] killed [{rec.tag}] PID {rec.pid}");
                                    else
                                        Debug.LogWarning($"[UCL_ProcessAdminPage] kill 拒絕/失敗 [{rec.tag}] PID {rec.pid}: {err}");
                                    Refresh();
                                }
                            }
                            GUI.backgroundColor = oldColor;
                        }
                        else
                        {
                            // Dead / PidReused / Unknown → 只允許移除記錄檔, 不提供 kill (防誤殺)
                            if (GUILayout.Button("🗑 移除記錄", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            {
                                try
                                {
                                    if (!string.IsNullOrEmpty(rec.source_file) && File.Exists(rec.source_file))
                                        File.Delete(rec.source_file);
                                }
                                catch (Exception e) { Debug.LogWarning($"[UCL_ProcessAdminPage] 移除記錄失敗: {e.Message}"); }
                                Refresh();
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(rec.description))
                        GUILayout.Label($"  {rec.description}", SmallStyle);
                    GUILayout.Label($"  name: {rec.process_name} | start: {FmtLocal(rec.start_time_utc)} | "
                        + $"registered_by: {rec.registered_by} @ {FmtLocal(rec.registered_at_utc)}", SmallStyle);
                    if (!string.IsNullOrEmpty(rec.command_line))
                    {
                        string cmd = rec.command_line.Length > 160
                            ? rec.command_line.Substring(0, 160) + "…" : rec.command_line;
                        GUILayout.Label($"  cmd: {cmd}", SmallStyle);
                    }
                }
                GUILayout.Space(2);
            }
            GUI.backgroundColor = oldColor;

            GUILayout.Space(10);
            GUILayout.Label("📂 記錄檔: AgentCommands/_process_registry/<tag>_<pid>.json (每 process 單檔, domain reload 不失)",
                SmallStyle);
        }

        // UTC ISO 字串 → 本地 HH:mm:ss (顯示用; 解析失敗原樣印)
        static string FmtLocal(string isoUtc)
        {
            if (string.IsNullOrEmpty(isoUtc)) return "-";
            if (DateTime.TryParse(isoUtc, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                return t.ToLocalTime().ToString("MM-dd HH:mm:ss");
            return isoUtc;
        }
    }
}
#endif
