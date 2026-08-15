// 區塊職責：OCR worker 的**獨立行程 supervisor**（C# 端唯一管理者，遷移階段 2）。
//          起停 `subtitle_ocr.py --serve`、依設定變更重起、依**產物水位**判定停滯並重起。
// 物理意義：OCR 原本是 screenstream_daemon 內的 thread pool，由 capture loop 每寫一張 frame
//          `submit()` 一次（記憶體交棒）。拆出去之後改成**掃目錄**，於是：
//          ① daemon 不必再擁有 OCR 的生命週期（設定改動不再靠它輪詢重起）
//          ② C# 看得到的不再只是「daemon 活著」，而是「OCR 這一項有沒有在產出」
// 數值影響：心跳＝`_screenstream/ocr/` 內最新檔的 mtime（OCR cache 檔名是 frame_NNNN.json，
//          沒有內容時刻可讀 ⇒ 這裡只能用 mtime；與 STT 用檔名 epoch 的理由不同，刻意寫明）。
//          停滯門檻＝OCR_STALL_SEC，且**只在錄影中才判定**（沒在錄當然沒有新 frame 可 OCR）。
// ⚠ 與 UCL_SttWorkerSupervisor 同一套紀律：python 端不自我重起，決策點只留這裡一個。
#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using UCL.Core.JsonLib;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.AgentCommands.MediaAdmin
{
    /// <summary>
    /// 字幕 OCR 常駐 worker 的 C# supervisor（`subtitle_ocr.py --serve` 一顆獨立行程）。
    /// <para>期望狀態＝`enabled && ocr_enabled`（沒在錄影就不需要 OCR）；心跳讀 `ocr/` 產出。</para>
    /// </summary>
    [InitializeOnLoad]
    public static class UCL_OcrWorkerSupervisor
    {
        public const string TAG = "screenstream_ocr";

        const double TICK_SEC = 5.0;

        /// <summary>停滯門檻（秒）。OCR 每秒該吃一張 frame，60s 沒有任何新產出即異常。</summary>
        const double OCR_STALL_SEC = 60.0;

        /// <summary>剛起來時給的寬限（載入 RapidOCR engine 要時間）。</summary>
        const double WARMUP_SEC = 45.0;

        const int RESTART_ESCALATE = 3;

        static Process s_Proc;
        static double s_NextTick, s_LastProduct, s_LastProgressAt, s_SpawnedAt;
        static string s_RunningSig = "";
        static int s_StallRestarts;
        static string s_LastNote = "(尚未巡檢)";

        public static string StatusLine => s_LastNote;

        static UCL_OcrWorkerSupervisor() { EditorApplication.update += Tick; }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < s_NextTick) return;
            s_NextTick = EditorApplication.timeSinceStartup + TICK_SEC;
            try
            {
                var aCfg = ReadConfig();
                bool aRec = aCfg != null && aCfg.Contains("enabled") && (bool)aCfg["enabled"];
                bool aOcr = aCfg != null && aCfg.Contains("ocr_enabled") && (bool)aCfg["ocr_enabled"];
                bool aWant = aRec && aOcr;
                string aSig = Signature(aCfg);
                bool aAlive = s_Proc != null && !s_Proc.HasExited;

                if (!aWant)
                {
                    if (aAlive) Stop(aRec ? "OCR 關閉（ocr_enabled=false）" : "未在錄影");
                    s_LastNote = aRec ? "OCR 未啟用（ocr_enabled=false）" : "未在錄影 —— OCR 不需要運行";
                    return;
                }
                if (!aAlive) { Spawn(aCfg, aSig, "未在運行"); return; }
                if (aSig != s_RunningSig) { Stop("設定變更"); Spawn(aCfg, aSig, "設定變更"); return; }

                // ── 心跳：產物水位（ocr/ 最新檔 mtime）──────────────
                double aProd = ProductWatermark();
                if (aProd > s_LastProduct)
                {
                    s_LastProduct = aProd;
                    s_LastProgressAt = EditorApplication.timeSinceStartup;
                    s_StallRestarts = 0;
                }
                double aSince = EditorApplication.timeSinceStartup - s_LastProgressAt;
                double aUp = EditorApplication.timeSinceStartup - s_SpawnedAt;
                // ⚠ 暖機期不判停滯：RapidOCR engine 載入要時間，否則會變成「一起來就被自己殺掉」的迴圈。
                if (aUp > WARMUP_SEC && aSince > OCR_STALL_SEC)
                {
                    s_StallRestarts++;
                    string aMsg = $"[UCL_OCR] ⚠ 停滯 {aSince:F0}s（門檻 {OCR_STALL_SEC:F0}s，最後產出 "
                                + $"{(s_LastProduct > 0 ? FromEpochLocal(s_LastProduct).ToString("HH:mm:ss") : "從未產出")}）"
                                + $" → 重起第 {s_StallRestarts} 次";
                    if (s_StallRestarts >= RESTART_ESCALATE)
                        Debug.LogError(aMsg + "　—— 連續重起無效，去查引擎或 frames 目錄，不是抖動");
                    else Debug.LogWarning(aMsg);
                    Stop("產出停滯"); Spawn(aCfg, aSig, "停滯重起");
                    return;
                }

                s_LastNote = $"OCR 運行中 PID={s_Proc.Id}｜產物 "
                           + (s_LastProduct > 0 ? $"{FromEpochLocal(s_LastProduct):HH:mm:ss}（{aSince:F0}s 前）"
                                                : $"**尚未產出**（已等 {aSince:F0}s／暖機 {Math.Max(0, WARMUP_SEC - aUp):F0}s）")
                           + (s_StallRestarts > 0 ? $"｜已重起 {s_StallRestarts} 次" : "");
            }
            catch (Exception e)
            {
                s_LastNote = $"⚠ supervisor 巡檢失敗：{e.Message}";
                Debug.LogWarning($"[UCL_OCR] 巡檢失敗：{e.Message}");
            }
        }

        /// <summary>
        /// `ocr/` 最新**產物**檔 mtime。⚠ 這裡只能用 mtime（cache 檔名是 frame 序號，不帶時刻）。
        /// <para>🩸 只能數 `frame_*.json`：同目錄的 `_status.json` 是 pool **每 0.5 秒重寫一次**的狀態檔，
        /// 把它算進來 ⇒ 心跳量到的是「pool 還活著」而不是「它產出了什麼」——
        /// 那正是本次遷移要拿掉的那把尺（2026-08-15 紅路實測：清空所有產物而停滯偵測完全不觸發）。</para>
        /// </summary>
        static double ProductWatermark()
        {
            try
            {
                var aDir = new DirectoryInfo(Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", "ocr"));
                if (!aDir.Exists) return 0;
                double aMax = 0;
                foreach (var f in aDir.GetFiles("frame_*.json"))
                {
                    double t = (f.LastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                    if (t > aMax) aMax = t;
                }
                return aMax;
            }
            catch { return 0; }
        }

        static void Spawn(JsonData iCfg, string iSig, string iWhy)
        {
            string aScript = Path.Combine(UCL_RepoPath.UnityProjectRoot, UCL_EditorPath.CorePath,
                                          "Tools~", "AgentCommands", "subtitle_ocr.py").Replace('\\', '/');
            if (!File.Exists(aScript)) { s_LastNote = $"⚠ 找不到 {aScript}"; Debug.LogWarning($"[UCL_OCR] {s_LastNote}"); return; }
            string aPy = UCL_ScreenStreamDaemon.ResolvePythonPublic();
            if (string.IsNullOrEmpty(aPy)) { s_LastNote = "⚠ PATH 內找不到 python"; Debug.LogWarning($"[UCL_OCR] {s_LastNote}"); return; }

            int aKilled = UCL_ProcessRegistryService.KillAllByTag(TAG);
            if (aKilled > 0) Debug.LogWarning($"[UCL_OCR] singleton guard：收掉 {aKilled} 顆殘留（防兩顆併寫同一份 cache）");

            string aRoot = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream");
            double aYb = ReadDouble(iCfg, "ocr_y_bottom_pct", 0.0);
            double aH = ReadDouble(iCfg, "ocr_h_pct", 0.12);
            string aExtra = BuildExtraRegionsJson(iCfg);
            var aArgs = new System.Text.StringBuilder();
            aArgs.Append('"').Append(aScript).Append("\" --serve");
            aArgs.Append(" --frames-dir \"").Append(Path.Combine(aRoot, "frames").Replace('\\', '/')).Append('"');
            aArgs.Append(" --cache-dir \"").Append(Path.Combine(aRoot, "ocr").Replace('\\', '/')).Append('"');
            aArgs.Append(" --y-bottom-pct ").Append(Inv(aYb));
            aArgs.Append(" --h-pct ").Append(Inv(aH));
            if (!string.IsNullOrEmpty(aExtra)) aArgs.Append(" --extra-regions \"").Append(aExtra.Replace("\"", "\\\"")).Append('"');
            aArgs.Append(" --min-confidence ").Append(Inv(ReadDouble(iCfg, "ocr_min_conf", 0.5)));
            aArgs.Append(" --workers ").Append((int)ReadDouble(iCfg, "ocr_workers", 2));
            bool aAdaptive = !iCfg.Contains("ocr_adaptive") || (bool)iCfg["ocr_adaptive"];
            if (!aAdaptive) aArgs.Append(" --no-adaptive");

            try
            {
                var aPsi = new ProcessStartInfo
                {
                    FileName = aPy, Arguments = aArgs.ToString(),
                    UseShellExecute = false, CreateNoWindow = true,
                    WorkingDirectory = UCL_RepoPath.RepoRoot,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                };
                var aProc = new Process { StartInfo = aPsi, EnableRaisingEvents = true };
                aProc.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.Log($"[UCL_OCR] {e.Data}"); };
                aProc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.LogWarning($"[UCL_OCR] {e.Data}"); };
                aProc.Start(); aProc.BeginOutputReadLine(); aProc.BeginErrorReadLine();
                s_Proc = aProc; s_RunningSig = iSig;
                s_SpawnedAt = EditorApplication.timeSinceStartup;
                s_LastProgressAt = EditorApplication.timeSinceStartup;
                UCL_ProcessRegistryService.Register(aProc, TAG,
                    $"字幕 OCR 常駐 worker（subtitle_ocr.py --serve, workers={(int)ReadDouble(iCfg, "ocr_workers", 2)}）",
                    nameof(UCL_OcrWorkerSupervisor));
                Debug.Log($"[UCL_OCR] spawned PID={aProc.Id}（{iWhy}）");
                s_LastNote = $"OCR 已啟動 PID={aProc.Id}（{iWhy}）";
            }
            catch (Exception e)
            {
                s_LastNote = $"⚠ spawn 失敗：{e.Message}";
                Debug.LogWarning($"[UCL_OCR] spawn fail: {e.Message}");
            }
        }

        static void Stop(string iWhy)
        {
            try
            {
                if (s_Proc != null && !s_Proc.HasExited)
                {
                    Debug.Log($"[UCL_OCR] 停止 PID={s_Proc.Id}（{iWhy}）");
                    s_Proc.Kill();
                }
            }
            catch (Exception e) { Debug.LogWarning($"[UCL_OCR] kill fail: {e.Message}"); }
            finally { UCL_ProcessRegistryService.KillAllByTag(TAG); s_Proc = null; s_RunningSig = ""; }
        }

        // 只放「改了就必須重起」的欄位（pool 的 regions/conf/workers 綁建構子，中途不可熱改）
        static string Signature(JsonData c)
            => $"{ReadDouble(c, "ocr_y_bottom_pct", 0):F4}|{ReadDouble(c, "ocr_h_pct", 0):F4}|"
             + $"{ReadDouble(c, "ocr_min_conf", 0.5):F4}|{(int)ReadDouble(c, "ocr_workers", 2)}|"
             + $"{(c != null && c.Contains("ocr_adaptive") ? ((bool)c["ocr_adaptive"]).ToString() : "True")}|"
             + BuildExtraRegionsJson(c);

        /// <summary>把 config 的 `ocr_extra_regions` 轉成 python 端吃的 `[[y,h],…]`。
        /// ⚠ 只帶 y/h —— 目前 CLI 的 extra-regions 是兩元素格式；x/w 由主帶參數決定。</summary>
        static string BuildExtraRegionsJson(JsonData c)
        {
            try
            {
                if (c == null || !c.Contains("ocr_extra_regions")) return "";
                var aArr = c["ocr_extra_regions"];
                if (aArr == null || aArr.Count <= 0) return "";
                var aSb = new System.Text.StringBuilder("[");
                for (int i = 0; i < aArr.Count; i++)
                {
                    var e = aArr[i];
                    if (i > 0) aSb.Append(',');
                    aSb.Append('[').Append(Inv(ReadDouble(e, "y_bottom_pct", 0)))
                       .Append(',').Append(Inv(ReadDouble(e, "h_pct", 0.12))).Append(']');
                }
                return aSb.Append(']').ToString();
            }
            catch { return ""; }
        }

        static string Inv(double v) => v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

        static JsonData ReadConfig()
        {
            string aPath = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", "_config.json");
            if (!File.Exists(aPath)) return null;
            return JsonData.ParseJson(File.ReadAllText(aPath, System.Text.Encoding.UTF8));
        }

        static double ReadDouble(JsonData d, string k, double def)
        {
            try { return d != null && d.Contains(k) ? double.Parse(d[k].ToString(), System.Globalization.CultureInfo.InvariantCulture) : def; }
            catch { return def; }
        }

        static DateTime FromEpochLocal(double iEp)
            => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(iEp).ToLocalTime();
    }
}
#endif
