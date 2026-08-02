// 區塊職責：遠端 persona routing 的 C# 指揮端 — 切視窗 → 叫 python 判讀 token 座標 → 只移動游標。
// 物理意義：判讀（OCR）在 python，操控（視窗 / 游標）全在 C#（Tim 2026-08-02 拍板）。本檔不含任何
//          click / 鍵盤 API，連 P/Invoke 都沒有；要誤點也沒有可呼叫的東西。
// 數值影響：python 一次冷啟動含 RapidOCR 載入約 3-6 秒，逾時上限 90 秒；逾時即 kill 並解除註冊。
#if UNITY_EDITOR && UNITY_STANDALONE_WIN
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using UCL.Core.JsonLib;
using UCL.Core.EditorLib.AgentCommands;

namespace UCL.Core.EditorLib.AgentCommands.Bartender
{
    /// <summary>OCR 命中的單一文字塊；座標已是 virtual desktop 實體像素。</summary>
    public class UCL_PersonaOcrMatch
    {
        public string Text = "";
        public float Confidence;
        public int CenterX;
        public int CenterY;
        public int Left, Top, Right, Bottom;

        public string Describe(int index) =>
            $"[{index}] ({CenterX}, {CenterY}) conf={Confidence:0.00} 「{Text}」";
    }

    /// <summary>一次定位的完整結果 —— 成功與失敗都帶得走診斷，不只回一個 bool。</summary>
    public class UCL_PersonaOcrResult
    {
        public bool Ok;
        public string Reason = "";
        public string Token = "";
        public int ExitCode = -1;
        public int SelectedIndex = -1;
        public List<UCL_PersonaOcrMatch> Matches = new List<UCL_PersonaOcrMatch>();
        public List<UCL_PersonaOcrMatch> NearMisses = new List<UCL_PersonaOcrMatch>();
        public string RawStdout = "";
        public string RawStderr = "";

        public UCL_PersonaOcrMatch Selected =>
            SelectedIndex >= 0 && SelectedIndex < Matches.Count ? Matches[SelectedIndex] : null;
    }

    /// <summary>一塊實體螢幕；座標與 ScreenStream 的 _monitors.json 同一個 virtual desktop 空間。</summary>
    public class UCL_MonitorInfo
    {
        public int Index;
        public int X, Y, Width, Height;
        public bool Primary;
        public string Label => $"{Index}{(Primary ? "*" : "")} {Width}x{Height} @({X},{Y})";
    }

    // 區塊職責：一次定位測試的全部可調參數。
    // 物理意義：掃描範圍是**矩形**（不是字幕帶那種只有上下的橫帶）—— session 清單固定在視窗左側，
    //          掃全桌面既慢又會把別的視窗上的同名文字一起撈進來。
    // 數值影響：Attempts 含第一次；命中即跳出。延遲存在的理由是視窗剛被帶到前景時還沒重繪完。
    public class UCL_PersonaLocateOptions
    {
        public string Monitor = "all";
        public float RegionX = 0f, RegionY = 0f, RegionW = 1f, RegionH = 1f;
        public float InitialDelaySec = 0.8f;
        public int Attempts = 3;
        public float AttemptDelaySec = 0.6f;
        public int MatchIndex = -1;
        /// <summary>多重命中的選擇政策：leftmost（預設）/ topmost / strict。</summary>
        public string SelectPolicy = "leftmost";
        // 區塊職責：移到目標後的後續動作（Tim 2026-08-02 指定的下一步）。
        // 物理意義：按左鍵＝把 session 選起來；輸入文字＝在輸入框裡打指令。**沒有送 Enter 的路徑**，
        //          不是「預設關閉」而是整條 code path 不存在 —— 要送出永遠是人自己按。
        // 數值影響：兩段延遲存在的理由是點擊後 UI 需要時間切 session、輸入框需要時間拿到焦點。
        public bool ClickAfterMove;
        public float ClickDelaySec = 0.3f;
        public string TypeText = "/ucl-ding";
        public bool TypeAfterClick;
        public float TypeDelaySec = 0.6f;
        /// <summary>per-agent 輸入前置動作完成後的等待（只有需要前置的 agent 會用到，如 Antigravity）。</summary>
        public float FocusDelaySec = 0.5f;
        /// <summary>比對方式：delimiter（##name## 用，預設）/ contains（找 UI 固定文字用）。</summary>
        public string MatchMode = "delimiter";
        /// <summary>逐字輸入時每個字之間的間隔 —— 零延遲會在對方 UI 重繪時掉字（2026-08-02 實測）。</summary>
        public float TypeCharDelaySec = 0.03f;

        public bool IsFullRegion => RegionX <= 0f && RegionY <= 0f && RegionW >= 1f && RegionH >= 1f;

        public string RegionArg =>
            IsFullRegion ? "" : $"{RegionX:0.####},{RegionY:0.####},{RegionW:0.####},{RegionH:0.####}";
    }

    // 區塊職責：把定位設定（螢幕 / 矩形 / 延遲 / 重試 / 選擇政策 / 上次測試對象）存成檔，跨 Editor session 存活。
    // 物理意義：這些值是「這台機器的桌面長怎樣」的描述，不是暫時狀態；每次重開都重調一遍是純消耗。
    // 數值影響：明示按鈕才寫檔（不自動存），讀取失敗一律退回預設值，不讓壞檔擋住整個後台。
    public static class UCL_RemotePersonaLocateConfig
    {
        const string FileName = "remote_persona_locate_config.json";

        public static string Path_ =>
            System.IO.Path.Combine(UCL_BartenderIO.GetBartenderDir(), FileName).Replace('\\', '/');

        public static bool Save(UCL_PersonaLocateOptions options, string persona, out string error)
        {
            error = "";
            try
            {
                UCL_BartenderIO.EnsureBartenderDir();
                var data = new JsonData();
                data["monitor"] = new JsonData(options.Monitor);
                data["region_x"] = new JsonData(options.RegionX);
                data["region_y"] = new JsonData(options.RegionY);
                data["region_w"] = new JsonData(options.RegionW);
                data["region_h"] = new JsonData(options.RegionH);
                data["initial_delay_sec"] = new JsonData(options.InitialDelaySec);
                data["attempts"] = new JsonData(options.Attempts);
                data["attempt_delay_sec"] = new JsonData(options.AttemptDelaySec);
                data["select_policy"] = new JsonData(options.SelectPolicy);
                data["click_after_move"] = new JsonData(options.ClickAfterMove);
                data["click_delay_sec"] = new JsonData(options.ClickDelaySec);
                data["type_after_click"] = new JsonData(options.TypeAfterClick);
                data["type_text"] = new JsonData(options.TypeText ?? "");
                data["type_delay_sec"] = new JsonData(options.TypeDelaySec);
                data["focus_delay_sec"] = new JsonData(options.FocusDelaySec);
                data["type_char_delay_sec"] = new JsonData(options.TypeCharDelaySec);
                data["last_persona"] = new JsonData(persona ?? "");
                File.WriteAllText(Path_, data.ToJsonBeautify(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>讀回設定；檔案不存在或壞檔時回 false 並保留傳入物件的預設值。</summary>
        public static bool Load(UCL_PersonaLocateOptions options, out string persona)
        {
            persona = "";
            try
            {
                if (!File.Exists(Path_)) return false;
                var data = JsonData.ParseJson(File.ReadAllText(Path_));
                if (data == null) return false;
                options.Monitor = data.GetString("monitor", options.Monitor);
                options.RegionX = data.GetFloat("region_x", options.RegionX);
                options.RegionY = data.GetFloat("region_y", options.RegionY);
                options.RegionW = data.GetFloat("region_w", options.RegionW);
                options.RegionH = data.GetFloat("region_h", options.RegionH);
                options.InitialDelaySec = data.GetFloat("initial_delay_sec", options.InitialDelaySec);
                options.Attempts = data.GetInt("attempts", options.Attempts);
                options.AttemptDelaySec = data.GetFloat("attempt_delay_sec", options.AttemptDelaySec);
                options.SelectPolicy = data.GetString("select_policy", options.SelectPolicy);
                options.ClickAfterMove = data.GetBool("click_after_move", options.ClickAfterMove);
                options.ClickDelaySec = data.GetFloat("click_delay_sec", options.ClickDelaySec);
                options.TypeAfterClick = data.GetBool("type_after_click", options.TypeAfterClick);
                options.TypeText = data.GetString("type_text", options.TypeText);
                options.TypeDelaySec = data.GetFloat("type_delay_sec", options.TypeDelaySec);
                options.FocusDelaySec = data.GetFloat("focus_delay_sec", options.FocusDelaySec);
                options.TypeCharDelaySec = data.GetFloat("type_char_delay_sec", options.TypeCharDelaySec);
                persona = data.GetString("last_persona", "");
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[RemotePersonaLocator] 讀取定位設定失敗，改用預設值: {e.Message}");
                return false;
            }
        }
    }

    public static class UCL_RemotePersonaLocator
    {
        const string ProcessTag = "persona_ocr_locate";
        const int DefaultTimeoutSec = 90;
        const int QuickTimeoutSec = 20;     // 預覽 / 列舉不載 OCR 模型，秒級就該回來
        const string DiagnosticFileName = "remote_persona_last_test.md";
        const string PreviewFileName = "remote_persona_preview.png";

        public static string PreviewPath =>
            Path.Combine(UCL_BartenderIO.GetBartenderDir(), PreviewFileName).Replace('\\', '/');

        public static string ScriptPath =>
            Path.Combine(UCL_RepoPath.UnityProjectRoot, UCL_EditorPath.CorePath,
                         "Tools~", "AgentCommands", "persona_ocr_locate.py").Replace('\\', '/');

        public static string DiagnosticPath =>
            Path.Combine(UCL_BartenderIO.GetBartenderDir(), DiagnosticFileName);

        /// <summary>最近一次測試的結果，供後台顯示候選清單。</summary>
        public static UCL_PersonaOcrResult LastResult { get; private set; }

        /// <summary>
        /// 完整一輪測試：切到該 persona 的 actual_agent 視窗 → OCR 找 <c>##persona##</c> → 移動游標。
        /// 任一步失敗就停在該步，不會「找不到就隨便移一下」。
        /// </summary>
        /// <param name="matchIndex">多重命中時要選第幾個；-1 表示不指定（多重命中即停止並列出候選）。</param>
        public static bool RunCursorTest(UCL_PersonaLockInfo lockInfo, UCL_PersonaLocateOptions options, out string summary)
        {
            options ??= new UCL_PersonaLocateOptions();
            if (lockInfo == null)
            {
                summary = "沒有選定 persona（或該 persona 的 lock 已消失）";
                UCL_RemoteWindowControl.SetLastResult(summary);
                return false;
            }
            if (lockInfo.ActualAgent == UCL_ActualAgent.None)
            {
                summary = $"{lockInfo.Persona} 的 actual_agent 是空的（原始值「{lockInfo.ActualAgentRaw}」）；請先在登入狀態頁套用";
                UCL_RemoteWindowControl.SetLastResult(summary);
                return false;
            }

            string windowTarget = UCL_ActualAgentUtility.ToWindowTarget(lockInfo.ActualAgent);
            // 切換「失敗」不再中止（Tim 2026-08-02 拍板）：真正的門是下一步的 OCR ——
            // 視窗沒到前面就不會露出來、token 就掃不到，流程自己會停。前景 handle 比對只是代理指標，
            // 而它會因非同步切換與同 app 兄弟視窗而誤判，拿它否決有畫面證據的判斷是本末倒置。
            bool activated = UCL_RemoteWindowControl.TryActivateExplicitly(windowTarget, out string activateResult);
            if (!activated && UCL_RemoteWindowControl.StrictForegroundCheck)
            {
                summary = $"切換視窗失敗：{activateResult}";
                UCL_RemoteWindowControl.SetLastResult(summary);
                WriteDiagnostic(lockInfo, windowTarget, activateResult, null, summary);
                return false;
            }

            var result = Locate(lockInfo.SessionToken, options);
            LastResult = result;
            if (!result.Ok)
            {
                summary = $"OCR 定位失敗：{result.Reason}";
                UCL_RemoteWindowControl.SetLastResult(summary);
                WriteDiagnostic(lockInfo, windowTarget, activateResult, result, summary);
                return false;
            }

            var target = result.Selected;
            bool moved = UCL_RemoteWindowControl.TryMoveCursor(target.CenterX, target.CenterY, out string moveResult);
            string actionResult = moved
                ? RunPostMoveActions(options, lockInfo.ActualAgent)
                : "（游標沒到位，後續動作全部略過）";
            summary = $"{lockInfo.Persona} → {windowTarget}｜{result.Reason}｜{moveResult}｜{actionResult}";
            UCL_RemoteWindowControl.SetLastResult(summary);
            WriteDiagnostic(lockInfo, windowTarget, activateResult, result, summary);
            return moved;
        }

        // 區塊職責：游標到位之後的按左鍵 / 輸入文字。
        // 物理意義：SendInput 送出的是與真人無法分辨的輸入 —— 所以每一步之前都重新確認「前景視窗仍是
        //          我剛切過去的那一個」。焦點被搶走時停手，而不是往別人的視窗裡點下去。
        // 數值影響：不做這個檢查最壞情況是把 /ucl-ding 打進別人的聊天框並留下痕跡；檢查成本是一次
        //          GetForegroundWindow。Enter 永遠不送 —— 這裡沒有那條路徑。
        static string RunPostMoveActions(UCL_PersonaLocateOptions options, UCL_ActualAgent agent)
        {
            if (!options.ClickAfterMove) return "未啟用點擊（只移動游標）";
            var expected = UCL_RemoteWindowControl.LastActivatedWindow;
            if (!UCL_RemoteWindowControl.ForegroundGuardPasses(expected, out string guardNote))
                return $"⚠ {guardNote}，為避免點進別人的視窗而中止";

            if (options.ClickDelaySec > 0f) Sleep(options.ClickDelaySec);
            if (!UCL_RemoteWindowControl.TryClickLeft(out string clickResult))
                return $"點擊失敗：{clickResult}";
            if (!options.TypeAfterClick || string.IsNullOrEmpty(options.TypeText))
                return $"{clickResult}（未啟用文字輸入）";

            if (options.TypeDelaySec > 0f) Sleep(options.TypeDelaySec);
            // per-agent 前置：有些桌面工具點完 session 焦點不會自己進輸入框（Antigravity 2.0），要補一段。
            string prepare = UCL_RemoteAgentInput.PrepareInput(agent, options);
            // 點擊可能切換了 session / 開了新視窗，打字前再確認一次焦點歸屬。
            if (!UCL_RemoteWindowControl.ForegroundGuardPasses(expected, out string guardNote2))
                return $"{clickResult}；但{guardNote2}，不輸入文字";
            UCL_RemoteWindowControl.TryTypeText(options.TypeText, options.TypeCharDelaySec, out string typeResult);
            return $"{clickResult}；{prepare}；{typeResult}（未送出 Enter — 手動測試流程沒有送出的路徑）";
        }

        static void Sleep(float seconds) =>
            System.Threading.Thread.Sleep(Mathf.Clamp(Mathf.RoundToInt(seconds * 1000f), 0, 10000));

        /// <summary>
        /// 只跑 OCR 判讀，不切視窗也不動游標 —— 給「先看畫面上有幾個候選」用。
        /// </summary>
        public static UCL_PersonaOcrResult Locate(string token, UCL_PersonaLocateOptions options,
                                                  int timeoutSec = DefaultTimeoutSec)
        {
            options ??= new UCL_PersonaLocateOptions();
            var result = new UCL_PersonaOcrResult { Token = token };
            var args = new StringBuilder($"--token \"{token}\"");
            args.Append($" --monitor {options.Monitor}");
            if (!options.IsFullRegion) args.Append($" --region {options.RegionArg}");
            if (options.InitialDelaySec > 0f) args.Append($" --initial-delay {options.InitialDelaySec:0.###}");
            args.Append($" --attempts {Math.Max(1, options.Attempts)}");
            args.Append($" --attempt-delay {Math.Max(0f, options.AttemptDelaySec):0.###}");
            if (options.MatchIndex >= 0) args.Append($" --index {options.MatchIndex}");
            if (!string.IsNullOrEmpty(options.SelectPolicy)) args.Append($" --select {options.SelectPolicy}");
            if (!string.IsNullOrEmpty(options.MatchMode)) args.Append($" --match {options.MatchMode}");

            if (!RunScript(args.ToString(), timeoutSec, out string stdout, out string stderr,
                           out int exitCode, out string error))
            {
                result.Reason = error;
                result.RawStderr = stderr;
                return result;
            }
            result.ExitCode = exitCode;
            result.RawStdout = stdout;
            result.RawStderr = stderr;
            ParseInto(result);
            return result;
        }

        /// <summary>
        /// 抓一張選定螢幕的縮圖當 rect 預覽底圖 —— 不跑 OCR，所以按下去是秒級回應。
        /// 預覽刻意抓「整塊螢幕」而不是 rect 內：拿裁好的圖去調裁切範圍，永遠看不到自己漏掉了什麼。
        /// </summary>
        public static bool CapturePreview(string monitor, int maxWidth, out string error)
        {
            UCL_BartenderIO.EnsureBartenderDir();
            string args = $"--monitor {monitor} --preview \"{PreviewPath}\" --preview-max-width {maxWidth}";
            if (!RunScript(args, QuickTimeoutSec, out string stdout, out string stderr, out _, out error))
                return false;
            try
            {
                var data = JsonData.ParseJson(stdout);
                if (data != null && data.GetBool("ok", false)) { error = ""; return true; }
                error = data == null ? $"預覽輸出不是合法 JSON：{Truncate(stdout, 200)}"
                                     : data.GetString("reason", "預覽失敗");
            }
            catch (Exception e) { error = $"解析預覽輸出失敗：{e.Message}"; }
            if (string.IsNullOrEmpty(error) && !string.IsNullOrEmpty(stderr)) error = Truncate(stderr, 200);
            return false;
        }

        /// <summary>列舉實體螢幕；由 python 端以 Win32 取得，與 ScreenStream 同一個座標系。</summary>
        public static List<UCL_MonitorInfo> ListMonitors()
        {
            var list = new List<UCL_MonitorInfo>();
            if (!RunScript("--list-monitors", QuickTimeoutSec, out string stdout, out _, out _, out _))
                return list;
            try
            {
                var data = JsonData.ParseJson(stdout);
                var arr = data?.Get("monitors");
                if (arr == null || !arr.IsArray) return list;
                for (int i = 0; i < arr.Count; i++)
                {
                    var m = arr[i];
                    if (m == null) continue;
                    list.Add(new UCL_MonitorInfo
                    {
                        Index = m.GetInt("index", i),
                        X = m.GetInt("x", 0),
                        Y = m.GetInt("y", 0),
                        Width = m.GetInt("w", 0),
                        Height = m.GetInt("h", 0),
                        Primary = m.GetBool("primary", false),
                    });
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[RemotePersonaLocator] 解析 monitor 清單失敗: {e.Message}");
            }
            return list;
        }

        // 區塊職責：所有 python 呼叫的單一出口 —— UTF-8、非阻塞讀兩條 pipe、逾時 kill、PID 進註冊中心。
        // 物理意義：兩條 pipe 都要掛 handler 再 WaitForExit，否則 stdout 塞滿時雙方互等。
        // 數值影響：逾時即 Kill 並回 false；finally 一定 Unregister，不留孤兒記錄。
        static bool RunScript(string arguments, int timeoutSec,
                              out string stdout, out string stderr, out int exitCode, out string error)
        {
            stdout = stderr = error = "";
            exitCode = -1;
            string scriptPath = ScriptPath;
            if (!File.Exists(scriptPath)) { error = $"找不到判讀腳本：{scriptPath}"; return false; }
            string pythonExe = ResolvePython();
            if (string.IsNullOrEmpty(pythonExe)) { error = "PATH 中找不到 python"; return false; }

            var outBuf = new StringBuilder();
            var errBuf = new StringBuilder();
            Process proc = null;
            int pid = -1;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"\"{scriptPath}\" {arguments}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = UCL_RepoPath.RepoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                proc = new Process { StartInfo = psi };
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) outBuf.AppendLine(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) errBuf.AppendLine(e.Data); };
                proc.Start();
                pid = proc.Id;
                UCL_ProcessRegistryService.Register(proc, ProcessTag,
                    "persona session token OCR 判讀 (persona_ocr_locate.py)", nameof(UCL_RemotePersonaLocator),
                    allowMultiple: true);
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                if (!proc.WaitForExit(timeoutSec * 1000))
                {
                    try { proc.Kill(); } catch { /* 已自行結束 */ }
                    stderr = errBuf.ToString().Trim();
                    error = $"判讀逾時（>{timeoutSec}s）已中止";
                    return false;
                }
                exitCode = proc.ExitCode;
            }
            catch (Exception e)
            {
                error = $"啟動判讀腳本失敗：{e.Message}";
                return false;
            }
            finally
            {
                if (pid > 0) UCL_ProcessRegistryService.Unregister(pid, ProcessTag);
                proc?.Dispose();
            }
            stdout = outBuf.ToString().Trim();
            stderr = errBuf.ToString().Trim();
            return true;
        }

        // 區塊職責：把 python 的單行 JSON 轉成 C# 結果物件。
        // 物理意義：exit code 與 JSON 的 ok 是兩個獨立證據；兩者不一致時以 JSON 為準並把 exit code 留在結果裡，
        //          因為看得到不一致才查得出是誰在說謊。
        static void ParseInto(UCL_PersonaOcrResult result)
        {
            if (string.IsNullOrEmpty(result.RawStdout))
            {
                result.Reason = string.IsNullOrEmpty(result.RawStderr)
                    ? "判讀腳本沒有輸出"
                    : $"判讀腳本沒有輸出，stderr：{Truncate(result.RawStderr, 300)}";
                return;
            }
            try
            {
                var data = JsonData.ParseJson(result.RawStdout);
                if (data == null)
                {
                    result.Reason = $"輸出不是合法 JSON：{Truncate(result.RawStdout, 300)}";
                    return;
                }
                result.Ok = data.GetBool("ok", false);
                result.Reason = data.GetString("reason", "");
                result.SelectedIndex = data.GetInt("selected_index", -1);
                ReadMatches(data.Get("matches"), result.Matches);
                ReadMatches(data.Get("near_misses"), result.NearMisses);
                if (result.Ok && result.Selected == null)
                {
                    // JSON 說成功卻指不到任何一個候選 = 資料自相矛盾，寧可當失敗也不要拿一個猜的座標去移游標。
                    result.Ok = false;
                    result.Reason = $"判讀結果自相矛盾（ok=true 但 selected_index={result.SelectedIndex} / 命中 {result.Matches.Count} 筆）";
                }
            }
            catch (Exception e)
            {
                result.Ok = false;
                result.Reason = $"解析判讀輸出失敗：{e.Message}";
            }
        }

        static void ReadMatches(JsonData array, List<UCL_PersonaOcrMatch> into)
        {
            if (array == null || !array.IsArray) return;
            for (int i = 0; i < array.Count; i++)
            {
                var item = array[i];
                if (item == null) continue;
                into.Add(new UCL_PersonaOcrMatch
                {
                    Text = item.GetString("text", ""),
                    Confidence = item.GetFloat("confidence", 0f),
                    CenterX = item.GetInt("center_x", 0),
                    CenterY = item.GetInt("center_y", 0),
                    Left = item.GetInt("screen_left", 0),
                    Top = item.GetInt("screen_top", 0),
                    Right = item.GetInt("screen_right", 0),
                    Bottom = item.GetInt("screen_bottom", 0),
                });
            }
        }

        // 區塊職責：把每次測試的中介資料落成 UTF-8 Markdown；UI 一行結果不足以判斷是哪一步歪掉。
        // 數值影響：每次覆寫最新一份，不累積歷史；不保存截圖（python 端 --save-shot 預設關閉）。
        static void WriteDiagnostic(UCL_PersonaLockInfo lockInfo, string windowTarget, string activateResult,
                                    UCL_PersonaOcrResult result, string summary)
        {
            try
            {
                UCL_BartenderIO.EnsureBartenderDir();
                var text = new StringBuilder();
                text.AppendLine("# 遠端 persona 定位測試診斷");
                text.AppendLine();
                text.AppendLine($"- 時間（UTC）：`{DateTime.UtcNow:O}`");
                text.AppendLine($"- persona：`{lockInfo.Persona}`｜agent（顯示歸屬）：`{lockInfo.Agent}`｜bank：`{lockInfo.BankAccount}`");
                text.AppendLine($"- actual_agent：`{lockInfo.ActualAgentRaw}` → 視窗目標 `{windowTarget}`");
                text.AppendLine($"- session token：`{lockInfo.SessionToken}`");
                text.AppendLine($"- 切換視窗：{activateResult}");
                text.AppendLine($"- 結論：{summary}");
                text.AppendLine();
                if (result == null)
                {
                    text.AppendLine("（未進入 OCR 階段）");
                }
                else
                {
                    text.AppendLine($"- exit code：`{result.ExitCode}`｜ok：`{result.Ok}`｜selected_index：`{result.SelectedIndex}`");
                    text.AppendLine();
                    text.AppendLine($"## 命中（{result.Matches.Count}）");
                    text.AppendLine(result.Matches.Count == 0 ? "（無）" : DescribeAll(result.Matches));
                    text.AppendLine();
                    text.AppendLine($"## 近似但未命中（{result.NearMisses.Count}）");
                    text.AppendLine(result.NearMisses.Count == 0 ? "（無）" : DescribeAll(result.NearMisses));
                    if (!string.IsNullOrEmpty(result.RawStderr))
                    {
                        text.AppendLine();
                        text.AppendLine("## stderr");
                        text.AppendLine("```");
                        text.AppendLine(Truncate(result.RawStderr, 2000));
                        text.AppendLine("```");
                    }
                }
                File.WriteAllText(DiagnosticPath, text.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[RemotePersonaLocator] 診斷檔寫入失敗: {e.Message}");
            }
        }

        static string DescribeAll(List<UCL_PersonaOcrMatch> matches)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                sb.AppendLine($"- `[{i}]` 中心 `({m.CenterX}, {m.CenterY})`｜box `({m.Left},{m.Top})-({m.Right},{m.Bottom})`｜conf `{m.Confidence:0.000}`｜文字：`{m.Text}`");
            }
            return sb.ToString().TrimEnd();
        }

        static string Truncate(string text, int max) =>
            string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max) + "…";

        // 與 UCL_ScreenStreamDaemon 同一套解析順序：PATH 內 python → python3，跑 --version 驗真的能執行。
        static string ResolvePython()
        {
            foreach (string candidate in new[] { "python", "python3" })
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments = "--version",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null) continue;
                    if (!proc.WaitForExit(4000)) { try { proc.Kill(); } catch { } continue; }
                    if (proc.ExitCode == 0) return candidate;
                }
                catch { /* 換下一個候選 */ }
            }
            return "";
        }
    }
}
#endif
