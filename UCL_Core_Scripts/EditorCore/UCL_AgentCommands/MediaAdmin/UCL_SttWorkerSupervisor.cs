// 區塊職責：STT worker 的**獨立行程 supervisor**（C# 端唯一管理者）。
//          起停 `audio_transcribe.py serve`、依設定變更重起、依**產物水位**判定停滯並重起。
// 物理意義：STT 原本是 screenstream_daemon 內的一條 thread —— 對 C# 而言整顆 daemon 只有一個 PID，
//          能回答的只有「活著」。而 2026-07-27 的真實故障是**活著而且兩小時什麼都沒產出**
//          （擷取失敗重試迴圈 thread 不死 ⇒ 「thread dead?」偵測永遠不觸發 ⇒ 靜默停擺）。
//          ⇒ 拆成獨立行程之後，判定必須換一把尺：**看它產出了什麼，不看它是否活著。**
// 數值影響：心跳＝`_screenstream/stt/` 內最新 chunk 的**檔名 epoch**（不是 mtime —— 檔名是
//          內容代表的時刻，mtime 只是被寫下的時刻，補寫/搬移時兩者會分家）。
//          停滯門檻＝ max(chunk_sec × STALL_CHUNKS, STALL_FLOOR_SEC)，給模型載入與長句留餘裕。
// ⚠ 單一 supervisor 原則：python 端**不做自我重起**（serve 模式已拿掉那層），
//   重起只有這裡一個決策點。兩層都在猜，會出現「誰重起的」永遠查不清楚。
// ⚠ 這支只管 STT。擷取與 OCR 仍在 screenstream_daemon（見 Plan_StreamWatch_Cmd 的遷移階段）。
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
    /// STT 常駐 worker 的 C# supervisor（`audio_transcribe.py serve` 一顆獨立行程）。
    /// <para>期望狀態讀自 `_screenstream/_config.json` 的 `stt_enabled`；
    /// 心跳讀自 `stt/` 產出水位。UI 入口：UCL_ProcessAdminPage / UCL_ScreenStreamPage。</para>
    /// </summary>
    [InitializeOnLoad]
    public static class UCL_SttWorkerSupervisor
    {
        /// <summary>Process 註冊 tag —— 與 daemon 分開，Process 管理頁才看得到兩行而不是一行。</summary>
        public const string TAG = "screenstream_stt";

        /// <summary>巡檢間隔（秒）。設定變更與停滯偵測都靠這一拍，不另開 thread。</summary>
        const double TICK_SEC = 5.0;

        /// <summary>停滯門檻＝chunk 秒數的幾倍（模型載入＋長句轉錄要留餘裕）。</summary>
        const double STALL_CHUNKS = 4.0;

        /// <summary>停滯門檻下限（秒）—— chunk 設很小時不要變成秒殺重起。</summary>
        const double STALL_FLOOR_SEC = 90.0;

        /// <summary>連續重起幾次仍無產出就升級成 Error（不再靜靜地無限重試）。</summary>
        const int RESTART_ESCALATE = 3;

        static Process s_Proc;
        static double s_NextTick;
        static string s_RunningSig = "";        // 運行中那顆的設定簽章（model/lang/prompt/chunk）
        static double s_LastWatermark;          // 上次看到的產出水位（epoch 秒）
        static double s_LastProgressAt;         // 水位最後推進的**本機**時刻（EditorApplication.timeSinceStartup）
        static int s_StallRestarts;
        static string s_LastNote = "(尚未巡檢)";

        /// <summary>給 Editor 頁顯示用的一行現況（**印讀數，不印綠燈**）。</summary>
        public static string StatusLine => s_LastNote;

        static UCL_SttWorkerSupervisor()
        {
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < s_NextTick) return;
            s_NextTick = EditorApplication.timeSinceStartup + TICK_SEC;

            try
            {
                var aCfg = ReadConfig();
                bool aWant = aCfg != null && aCfg.Contains("stt_enabled") && (bool)aCfg["stt_enabled"];
                string aSig = Signature(aCfg);
                bool aAlive = s_Proc != null && !s_Proc.HasExited;

                if (!aWant)
                {
                    if (aAlive) { Stop("設定關閉（stt_enabled=false）"); }
                    s_LastNote = "STT 未啟用（stt_enabled=false）";
                    return;
                }

                if (!aAlive) { Spawn(aCfg, aSig, "未在運行"); return; }

                // 設定變更 ⇒ 重起套用（取代 python 端的 T-STT-AutoRestart：決策點只留一個）
                if (aSig != s_RunningSig) { Stop("設定變更"); Spawn(aCfg, aSig, "設定變更"); return; }

                // ── 心跳：看產出水位，不看 alive ──────────────────
                double aWm = ProductWatermark();
                if (aWm > s_LastWatermark)
                {
                    s_LastWatermark = aWm;
                    s_LastProgressAt = EditorApplication.timeSinceStartup;
                    s_StallRestarts = 0;
                }
                double aChunk = ReadDouble(aCfg, "stt_chunk_sec", 15.0);
                double aLimit = Math.Max(aChunk * STALL_CHUNKS, STALL_FLOOR_SEC);
                double aSince = EditorApplication.timeSinceStartup - s_LastProgressAt;

                if (aSince > aLimit)
                {
                    s_StallRestarts++;
                    string aLvl = s_StallRestarts >= RESTART_ESCALATE ? "ERROR" : "WARN";
                    string aMsg = $"[UCL_STT] ⚠ 停滯 {aSince:F0}s（門檻 {aLimit:F0}s，水位 "
                                + $"{(s_LastWatermark > 0 ? FromEpochLocal(s_LastWatermark).ToString("HH:mm:ss") : "從未產出")}）"
                                + $" → 重起第 {s_StallRestarts} 次";
                    if (aLvl == "ERROR") Debug.LogError(aMsg + "　—— 連續重起無效，這不是抖動，去查環境（音訊裝置／模型）");
                    else Debug.LogWarning(aMsg);
                    Stop("產出停滯");
                    Spawn(aCfg, aSig, "停滯重起");
                    return;
                }

                s_LastNote = $"STT 運行中 PID={s_Proc.Id}｜水位 "
                           + (s_LastWatermark > 0 ? $"{FromEpochLocal(s_LastWatermark):HH:mm:ss}（{aSince:F0}s 前推進）"
                                                  : $"**尚未產出**（已等 {aSince:F0}s／門檻 {aLimit:F0}s）")
                           + (s_StallRestarts > 0 ? $"｜已重起 {s_StallRestarts} 次" : "");
            }
            catch (Exception e)
            {
                // 巡檢自己壞掉不可以靜默 —— 那會讓「沒在監督」跟「一切正常」同形
                s_LastNote = $"⚠ supervisor 巡檢失敗：{e.Message}";
                Debug.LogWarning($"[UCL_STT] 巡檢失敗：{e.Message}");
            }
        }

        // ===========================================================
        // 區塊：產出水位 —— `stt/stt_<epochMs>.json` 的**檔名**最大值
        // ⚠ 用檔名不用 mtime：檔名是內容代表的時刻，mtime 只是它被寫下的時刻。
        //   （同 Cmd_StreamWatch.SensorWatermark 的判準，兩處必須一致，否則兩把尺會給出兩個答案。）
        // ===========================================================
        static double ProductWatermark()
        {
            try
            {
                var aDir = new DirectoryInfo(Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", "stt"));
                if (!aDir.Exists) return 0;
                double aMax = 0;
                foreach (var f in aDir.GetFiles("stt_*.json"))
                {
                    string aStem = Path.GetFileNameWithoutExtension(f.Name);
                    int aUs = aStem.IndexOf('_');
                    if (aUs >= 0 && long.TryParse(aStem.Substring(aUs + 1), out long aMs))
                    {
                        double t = aMs / 1000.0;
                        if (t > aMax) aMax = t;
                    }
                }
                return aMax;
            }
            catch { return 0; }
        }

        static void Spawn(JsonData iCfg, string iSig, string iWhy)
        {
            string aScript = Path.Combine(UCL_RepoPath.UnityProjectRoot, UCL_EditorPath.CorePath,
                                          "Tools~", "AgentCommands", "audio_transcribe.py").Replace('\\', '/');
            if (!File.Exists(aScript)) { s_LastNote = $"⚠ 找不到 {aScript}"; Debug.LogWarning($"[UCL_STT] {s_LastNote}"); return; }
            string aPy = UCL_ScreenStreamDaemon.ResolvePythonPublic();
            if (string.IsNullOrEmpty(aPy)) { s_LastNote = "⚠ PATH 內找不到 python"; Debug.LogWarning($"[UCL_STT] {s_LastNote}"); return; }

            // Singleton：先收掉同 tag 殘留（domain reload 掉了 Process 物件也還在的那種）
            int aKilled = UCL_ProcessRegistryService.KillAllByTag(TAG);
            if (aKilled > 0) Debug.LogWarning($"[UCL_STT] singleton guard：收掉 {aKilled} 顆殘留（防兩顆併寫同一份 cache）");

            string aModel = ReadStr(iCfg, "stt_model", "small");
            string aLang = ReadStr(iCfg, "stt_lang", "");
            string aPrompt = ReadStr(iCfg, "stt_prompt", "");
            double aChunk = ReadDouble(iCfg, "stt_chunk_sec", 15.0);
            var aArgs = new System.Text.StringBuilder();
            aArgs.Append('"').Append(aScript).Append("\" serve");
            aArgs.Append(" --model ").Append(aModel);
            if (!string.IsNullOrEmpty(aLang)) aArgs.Append(" --lang ").Append(aLang);
            if (!string.IsNullOrEmpty(aPrompt)) aArgs.Append(" --prompt \"").Append(aPrompt.Replace("\"", "'")).Append('"');
            aArgs.Append(" --chunk ").Append(aChunk.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            aArgs.Append(" --rms-gate ").Append(ReadDouble(iCfg, "stt_rms_gate", 0.005).ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            aArgs.Append(" --no-speech-max ").Append(ReadDouble(iCfg, "stt_no_speech_max", 0.6).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            aArgs.Append(" --logprob-min ").Append(ReadDouble(iCfg, "stt_logprob_min", -1.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

            try
            {
                var aPsi = new ProcessStartInfo
                {
                    FileName = aPy,
                    Arguments = aArgs.ToString(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = UCL_RepoPath.RepoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                };
                var aProc = new Process { StartInfo = aPsi, EnableRaisingEvents = true };
                aProc.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.Log($"[UCL_STT] {e.Data}"); };
                aProc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.LogWarning($"[UCL_STT] {e.Data}"); };
                aProc.Start();
                aProc.BeginOutputReadLine();
                aProc.BeginErrorReadLine();
                s_Proc = aProc;
                s_RunningSig = iSig;
                // 重起後**不重置水位基準線**只重置計時 —— 水位本身是磁碟事實，不該因為換了行程就歸零。
                s_LastProgressAt = EditorApplication.timeSinceStartup;
                UCL_ProcessRegistryService.Register(aProc, TAG,
                    $"STT 常駐 worker（audio_transcribe.py serve, model={aModel}）", nameof(UCL_SttWorkerSupervisor));
                Debug.Log($"[UCL_STT] spawned PID={aProc.Id}（{iWhy}）model={aModel} chunk={aChunk}s");
                s_LastNote = $"STT 已啟動 PID={aProc.Id}（{iWhy}）";
            }
            catch (Exception e)
            {
                s_LastNote = $"⚠ spawn 失敗：{e.Message}";
                Debug.LogWarning($"[UCL_STT] spawn fail: {e.Message}");
            }
        }

        static void Stop(string iWhy)
        {
            try
            {
                if (s_Proc != null && !s_Proc.HasExited)
                {
                    Debug.Log($"[UCL_STT] 停止 PID={s_Proc.Id}（{iWhy}）");
                    s_Proc.Kill();
                }
            }
            catch (Exception e) { Debug.LogWarning($"[UCL_STT] kill fail: {e.Message}"); }
            finally
            {
                UCL_ProcessRegistryService.KillAllByTag(TAG);   // 記錄檔一併收乾淨
                s_Proc = null; s_RunningSig = "";
            }
        }

        // 設定簽章 —— 只放「改了就必須重起」的欄位。放太多會變成一直重起，放太少會靜默沿用舊值
        // （🩸 血證：換片後 stt_lang/stt_prompt 殘留上一場，whisper 幻聽出舊片人名）。
        static string Signature(JsonData iCfg)
            => $"{ReadStr(iCfg, "stt_model", "")}|{ReadStr(iCfg, "stt_lang", "")}|"
             + $"{ReadStr(iCfg, "stt_prompt", "")}|{ReadDouble(iCfg, "stt_chunk_sec", 15.0):F1}";

        static JsonData ReadConfig()
        {
            string aPath = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", "_config.json");
            if (!File.Exists(aPath)) return null;
            return JsonData.ParseJson(File.ReadAllText(aPath, System.Text.Encoding.UTF8));
        }

        static string ReadStr(JsonData d, string k, string def)
            => d != null && d.Contains(k) ? (d[k].ToString() ?? def) : def;

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
