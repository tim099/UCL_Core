// 區塊職責：遠端協作視窗控制 — 在遠端模式下，以 Win32 將已開啟的指定 IDE 帶到前景。
// 物理意義：此類別只處理「找視窗 / 切換前景」；不傳送鍵盤文字、不按 Enter，也不保存啟用狀態。
// 數值影響：使用者最後一次實體輸入後的 m_UserIdlePauseSeconds 內，一般切換一律拒絕；預設 60 秒。
#if UNITY_EDITOR && UNITY_STANDALONE_WIN
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Bartender
{
    /// <summary>
    /// 遠端模式的最小權限視窗協作控制器。runtime 開關（<see cref="Enabled"/>）在 domain reload /
    /// Editor 重啟後回到關閉，使用者每次從 Bartender Admin 明確啟動；
    /// 需要跨重編保留時，另外打開 <see cref="PersistEnabled"/>（**opt-in，預設關閉**，
    /// Tim 2026-08-13 要求：每次重 compile 都要手動開一次太痛）。
    /// </summary>
    [InitializeOnLoad]
    public static class UCL_RemoteWindowControl
    {
        const int DefaultUserIdlePauseSeconds = 60;
        const string DiagnosticFileName = "remote_window_last_test.md";
        /// <summary>永久開關的 EditorPrefs key（走 UCL_ProjectEditorPrefs → 自動附專案指紋，不會跨專案互汙）。</summary>
        const string EditorPrefsPersistKey = "UCL.RemoteWindow.PersistEnabled";
        static bool s_Enabled;
        static bool s_PauseOnUserInput = true;
        static int s_UserIdlePauseSeconds = DefaultUserIdlePauseSeconds;
        static string s_LastResult = "尚未測試";
        static IntPtr s_LastActivated = IntPtr.Zero;

        /// <summary>本次 Editor session 是否允許一般前景切換；本身是 runtime-only（不落檔）。</summary>
        public static bool Enabled => s_Enabled;

        // 區塊職責：永久開關 — 讓 runtime 開關在 domain reload / Editor 重啟後自動恢復。
        // 物理意義：runtime 開關會被重置是**刻意的護欄**（背景程序不該在沒人同意時搶走鍵鼠）。
        //          這顆是那條護欄的顯式豁免：使用者一次性簽名「這台機器上我要它一直開著」，
        //          於是每次重編不用再點一次。**預設 false** —— 會移動游標並按 Enter 的能力
        //          不該因為「裝了這個版本」就自己開起來（Tim 2026-08-13 明確指定預設關閉）。
        // 數值影響：唯一效果是載入時把 s_Enabled 設回 true；不改任何其他護欄
        //          （PauseOnUserInput / 使用者閒置秒數 / 送出前前景驗證全數照舊）。
        public static bool PersistEnabled
        {
            get => UCL_ProjectEditorPrefs.GetBool(EditorPrefsPersistKey, false);
            set => UCL_ProjectEditorPrefs.SetBool(EditorPrefsPersistKey, value);
        }

        // 區塊職責：載入時依永久開關恢復 runtime 開關。
        // 物理意義：**就地同步做，不得經 EditorApplication.delayCall。**
        //   ⚠ 本函式第一版就是用 delayCall 寫的（理由寫「照 UCL_AgentCommandWatcher 的先例」）——
        //   而那個先例正是同一天剛被抓出來的 bug：delayCall 是單次 schedule，Editor 在背景時
        //   domain reload 後那一拍可能永遠不來。實測血證（2026-08-13 22:22）：Tim 已把永久開關按成
        //   ON（EditorPrefs 讀回 0x1），reload 後 Enabled 仍是 false、恢復用的 log 一行都沒印 ——
        //   **抄的不是慣例，是還沒修掉的缺陷。**
        //   讀 EditorPrefs 與 Application.dataPath 在 cctor 內是安全的（純字串 + registry 讀取，
        //   不碰 UniTask / PlayerLoop / AssetDatabase），所以沒有延後的必要。
        // 數值影響：PersistEnabled=false 時完全不動作（與加本功能之前逐位元一致）；
        //   為 true 時 s_Enabled 在 cctor 當下即為 true，不存在「reload 後有一段視窗是關的」。
        static UCL_RemoteWindowControl()
        {
            try
            {
                if (!PersistEnabled) return;
                s_Enabled = true;
                UnityEngine.Debug.Log("[RemoteWindowControl] 永久開關為開 → 已自動恢復遠端視窗協作（重編後不需手動再開）。");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[RemoteWindowControl] 永久開關恢復失敗（維持關閉）: {e.Message}");
            }
        }

        /// <summary>一般自動切換是否在偵測到使用者鍵鼠操作後暫停；runtime-only，預設安全開啟。</summary>
        public static bool PauseOnUserInput
        {
            get => s_PauseOnUserInput;
            set => s_PauseOnUserInput = value;
        }

        /// <summary>使用者輸入後的保護暫停秒數；runtime-only，範圍限制避免誤填造成永久停止。</summary>
        public static int UserIdlePauseSeconds
        {
            get => s_UserIdlePauseSeconds;
            set => s_UserIdlePauseSeconds = Mathf.Clamp(value, 5, 3600);
        }

        public static string LastResult => s_LastResult;
        public static string DiagnosticPath => Path.Combine(UCL_BartenderIO.GetBartenderDir(), DiagnosticFileName);

        /// <summary>最後一筆 OS 實體輸入距今秒數；讀取失敗時回 0，採 fail-closed。</summary>
        public static double UserIdleSeconds
        {
            get
            {
                LASTINPUTINFO info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                if (!GetLastInputInfo(ref info)) return 0;
                uint elapsedMs = unchecked((uint)Environment.TickCount - info.dwTime);
                return elapsedMs / 1000.0;
            }
        }

        /// <summary>設定 runtime 開關；關閉時也中止進行中的輪循，確保不殘留延後切換。</summary>
        public static void SetEnabled(bool enabled)
        {
            s_Enabled = enabled;
        }

        /// <summary>
        /// 一般自動切換入口。使用者最近有操作時拒絕，以免遠端自動化與真人爭奪焦點。
        /// </summary>
        public static bool TryActivate(string targetName, out string result)
        {
            if (!s_Enabled)
            {
                result = "系統尚未啟動（每次 Editor session 都需在酒保後台手動開啟）";
                return false;
            }
            if (s_PauseOnUserInput && UserIdleSeconds < s_UserIdlePauseSeconds)
            {
                result = $"偵測到使用者操作，暫停切換；尚需靜置 {Math.Ceiling(s_UserIdlePauseSeconds - UserIdleSeconds)} 秒";
                return false;
            }
            return TryActivateWindow(targetName, out result);
        }

        /// <summary>
        /// 後台測試按鈕的單次、明示授權切換。它仍需 runtime 開關已開，但不受「剛點下按鈕」造成的 idle
        /// 保護阻擋；一般自動流程則必須走 <see cref="TryActivate"/> 並完整遵守 idle 保護。
        /// </summary>
        public static bool TryActivateExplicitly(string targetName, out string result)
        {
            if (!s_Enabled)
            {
                result = "請先在酒保後台啟動遠端視窗協作";
                s_LastResult = result;
                return false;
            }
            bool succeeded = TryActivateWindow(targetName, out result);
            s_LastResult = $"{targetName}：{result}";
            return succeeded;
        }

        // 區塊職責：把游標移到指定 virtual desktop 座標；本檔沒有、也不會有 click / 鍵盤送出 API。
        // 物理意義：SetCursorPos 只改游標位置，不產生任何滑鼠事件；即使落在錯的目標上也不會觸發動作。
        // 數值影響：越界座標由 Windows 自行 clamp 到最近的螢幕；回傳值只反映 Win32 呼叫是否被接受。
        public static bool TryMoveCursor(int x, int y, out string result)
        {
            if (!s_Enabled)
            {
                result = "請先在酒保後台啟動遠端視窗協作";
                return false;
            }
            bool moved = SetCursorPos(x, y);
            bool got = GetCursorPos(out POINT actual);
            result = moved && got
                ? $"游標已移到 ({actual.x}, {actual.y})"
                : $"游標移動失敗（SetCursorPos={moved}）";
            // 要求座標與實際座標不一致時說出來 —— 多螢幕 / DPI 落差會讓「呼叫成功」與「真的到位」分家。
            if (moved && got && (actual.x != x || actual.y != y))
                result = $"游標移到 ({actual.x}, {actual.y})，與要求的 ({x}, {y}) 不同（可能被螢幕邊界 clamp）";
            return moved && got && actual.x == x && actual.y == y;
        }

        /// <summary>最近一次成功帶到前景的視窗；供後續動作驗證「焦點還在我以為的地方」。</summary>
        public static IntPtr LastActivatedWindow => s_LastActivated;

        public static bool IsForeground(IntPtr window) => window != IntPtr.Zero && GetForegroundWindow() == window;

        // 區塊職責：前景驗證要不要有否決權。**預設不否決（Tim 2026-08-02 拍板）。**
        // 物理意義：真正的門是 OCR —— 視窗沒被帶到前面就不會露出來，`##persona##` 就掃不到，流程自己停住。
        //          OCR 是**畫面證據**，前景 handle 比對只是代理指標；而代理指標會誤判（同 app 的兄弟視窗、
        //          非同步切換的時間差），拿一個會誤判的代理去否決一個有證據的判斷，是本末倒置。
        // 數值影響：關閉時不因前景不符中止，只把實況寫進紀錄；要恢復否決權把這顆打開即可。
        public static bool StrictForegroundCheck = false;

        /// <summary>前景是否仍是預期視窗（同 app 的兄弟視窗也算）；關閉嚴格模式時一律放行但回報實況。</summary>
        public static bool ForegroundGuardPasses(IntPtr expected, out string note)
        {
            IntPtr current = GetForegroundWindow();
            if (expected != IntPtr.Zero && current == expected) { note = "前景仍是目標"; return true; }
            if (expected != IntPtr.Zero && current != IntPtr.Zero)
            {
                GetWindowThreadProcessId(expected, out uint expectedPid);
                GetWindowThreadProcessId(current, out uint currentPid);
                if (expectedPid != 0 && expectedPid == currentPid)
                {
                    note = "前景是同一個 app 的其他視窗（視為仍在目標）";
                    return true;
                }
            }
            note = $"前景已變成 {DescribeWindow(current)}";
            if (!StrictForegroundCheck) note += "（嚴格前景驗證已關閉 → 仍繼續）";
            return !StrictForegroundCheck;
        }

        public static string DescribeForeground() => DescribeWindow(GetForegroundWindow());

        // 區塊職責：在游標當前位置按一次左鍵（down + up）。
        // 物理意義：SendInput 送的是絕對意義上的「使用者點擊」，目標程式無從分辨 —— 所以呼叫端必須
        //          在按之前確認前景視窗仍是自己剛切過去的那個，否則就是往別人的視窗裡點。
        // 數值影響：不移動游標（座標由 TryMoveCursor 決定）；失敗只回 false，不重試、不改按鍵狀態。
        public static bool TryClickLeft(out string result)
        {
            if (!s_Enabled)
            {
                result = "請先在酒保後台啟動遠端視窗協作";
                return false;
            }
            var inputs = new INPUT[2];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].union.mouse.dwFlags = MOUSEEVENTF_LEFTDOWN;
            inputs[1].type = INPUT_MOUSE;
            inputs[1].union.mouse.dwFlags = MOUSEEVENTF_LEFTUP;
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            result = sent == inputs.Length ? "已按下左鍵" : $"左鍵送出不完整（{sent}/{inputs.Length}）";
            return sent == inputs.Length;
        }

        // 區塊職責：把一段文字當鍵盤輸入送給目前焦點所在的控制項。
        // 物理意義：走 KEYEVENTF_UNICODE 直接送字元，不經 virtual-key 對應 —— 鍵盤配置（注音／英數／
        //          日文）不同也送得出同一串字；'/' 這種在某些配置要組合鍵的字元也不會走鐘。
        // 數值影響：**本方法不送 Enter，也沒有送 Enter 的路徑**（Tim 2026-08-02 指定）。
        //          換行字元一律被濾掉，不會因為文字裡夾了 \n 就意外送出。
        // ⚠ 2026-08-02 實測：整串字一次 SendInput 送出（字間零延遲）會**掉字** —— `/ucl-ding` 進去變成
        //   `/uclding`。原因不是某個字元特別，是時序：打 `/` 之後目標 app 的指令選單會跳出來並隨每個字
        //   重新過濾，UI 重繪的那一瞬間正在飛過去的字就沒被收下。SendInput 依然回報「全部送出」——
        //   又一次「Windows 收下 ≠ app 收到」。解法是逐字送 + 字間留間隔。
        public static bool TryTypeText(string text, float perCharDelaySeconds, out string result)
        {
            if (!s_Enabled)
            {
                result = "請先在酒保後台啟動遠端視窗協作";
                return false;
            }
            if (string.IsNullOrEmpty(text))
            {
                result = "沒有要輸入的文字";
                return false;
            }
            int delayMs = Mathf.Clamp(Mathf.RoundToInt(perCharDelaySeconds * 1000f), 0, 500);
            int skipped = 0, sentChars = 0, failedChars = 0;
            foreach (char c in text)
            {
                if (c == '\r' || c == '\n') { skipped++; continue; }   // 絕不送出換行
                var pair = new[] { MakeUnicodeKey(c, false), MakeUnicodeKey(c, true) };
                if (SendInput((uint)pair.Length, pair, Marshal.SizeOf<INPUT>()) == pair.Length) sentChars++;
                else failedChars++;
                if (delayMs > 0) System.Threading.Thread.Sleep(delayMs);
            }
            if (sentChars == 0 && failedChars == 0)
            {
                result = "文字內容只有換行，已全部略過（本流程不送 Enter）";
                return false;
            }
            string clean = text.Replace("\r", "").Replace("\n", "");
            string note = skipped > 0 ? $"（略過 {skipped} 個換行字元）" : "";
            result = failedChars == 0
                ? $"已逐字輸入「{clean}」{sentChars} 字 / 每字間隔 {delayMs}ms{note} — 仍需目測確認對方收到幾個字"
                : $"文字送出不完整（成功 {sentChars} / 失敗 {failedChars}）{note}";
            return failedChars == 0;
        }

        // 區塊職責：以剪貼簿 + Ctrl+V 一次貼入整串文字（逐字輸入的替代路徑）。
        // 物理意義：逐字輸入是**一顆一顆**送 KEYEVENTF_UNICODE，而目標端在 `/ucl` 之後會跳出
        //          slash 指令自動完成清單並開始過濾 —— UI 重繪那一瞬吃掉一顆鍵，字就少了。
        //          血證兩筆（summit 2026-08-03、basecamp 2026-08-13）**掉的都是同一個字元、同一個位置**：
        //          `/ucl-ding` 的那個 `-` → 對方收到 `/uclding` → Unknown command。
        //          加大字間隔只降低機率（Tim 觀察到「有改善」符合此模型）；貼上是**一次事件**，
        //          把成因整個拿掉而不是把機率調小。
        // 數值影響：不再有 perCharDelay（整串一次進去）；改為貼上後等 restoreDelayMs 才還原剪貼簿 ——
        //          太早還原會跟目標 app 讀取剪貼簿搶時間。
        // ⚠ 副作用誠實寫在這裡：**會動到使用者的系統剪貼簿**。用後即還原（含失敗路徑），
        //   但還原前有一段短暫窗口內容是通知文字；這是選這條路的已知代價，不是 bug。
        public static bool TryPasteText(string text, int restoreDelayMs, out string result)
        {
            if (!s_Enabled)
            {
                result = "請先在酒保後台啟動遠端視窗協作";
                return false;
            }
            if (string.IsNullOrEmpty(text))
            {
                result = "沒有要輸入的文字";
                return false;
            }
            string previous = null;
            try
            {
                previous = GUIUtility.systemCopyBuffer;   // 先存 —— 還原不了就等於偷了使用者的剪貼簿
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[RemoteWindowControl] 讀取原剪貼簿失敗（將不還原）: {e.Message}");
            }
            try
            {
                GUIUtility.systemCopyBuffer = text;
                if (!TrySendHotkey(VK_V, ctrl: true, shift: false, alt: false, out string hotkeyResult))
                {
                    result = $"貼上失敗（Ctrl+V 未送出：{hotkeyResult}）";
                    return false;
                }
                System.Threading.Thread.Sleep(Mathf.Clamp(restoreDelayMs, 0, 5000));
                // ⚠ 這裡回報的只是「Ctrl+V 已送出」—— 跟 Enter 那條同一課：
                //   SendInput 成功不證明目標 app 貼進去了。真正的驗收仍是對方視窗上看得到的字。
                result = $"已貼上「{text}」（{text.Length} 字，一次送出，無逐字掉字風險）"
                       + " — 仍需目測確認對方輸入框內容";
                return true;
            }
            catch (Exception e)
            {
                result = $"貼上發生例外：{e.Message}";
                return false;
            }
            finally
            {
                if (previous != null)
                {
                    try { GUIUtility.systemCopyBuffer = previous; }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogWarning($"[RemoteWindowControl] 還原剪貼簿失敗（內容仍是通知文字）: {e.Message}");
                    }
                }
            }
        }

        // 區塊職責：送出一次 Enter。**這是全檔唯一會「送出」的動作，刻意獨立成一支具名方法。**
        // 物理意義：送出是不可逆的一步 —— 訊息一旦發出去就收不回。所以它不藏在 TryTypeText 裡的換行，
        //          也不是某個 bool 參數，而是呼叫端必須明確寫出 TrySendEnter 才會發生的事。
        // 數值影響：只送 VK_RETURN down/up，不改任何 modifier；手動測試流程從不呼叫它，
        //          只有自動通知（酒保 ding）在使用者明示開啟後才走這裡。
        // ⚠ 2026-08-02 實測：只填 wVk（wScan=0）時 SendInput 回報全部送出，但目標 app（Electron 系）沒有反應。
        //   Chromium 是從**掃描碼**建 DOM 鍵盤事件的，掃描碼 0 會讓它算出空的 event.code；
        //   所以這裡補上 MapVirtualKey 取得的實體掃描碼（Enter = 0x1C），讓這顆鍵長得跟真鍵盤按下來的一樣。
        //   ——「SendInput 回 true」只證明 Windows 收下了，不證明對方處理了。
        public static bool TrySendEnter(int presses, float gapSeconds, out string result)
        {
            if (!s_Enabled)
            {
                result = "請先在酒保後台啟動遠端視窗協作";
                return false;
            }
            presses = Mathf.Clamp(presses, 1, 5);
            ushort scan = (ushort)MapVirtualKey(VK_RETURN, MAPVK_VK_TO_VSC);
            int okCount = 0;
            for (int i = 0; i < presses; i++)
            {
                if (i > 0 && gapSeconds > 0f)
                    System.Threading.Thread.Sleep(Mathf.Clamp(Mathf.RoundToInt(gapSeconds * 1000f), 0, 5000));
                var inputs = new INPUT[2];
                inputs[0].type = INPUT_KEYBOARD;
                inputs[0].union.keyboard.wVk = VK_RETURN;
                inputs[0].union.keyboard.wScan = scan;
                inputs[1].type = INPUT_KEYBOARD;
                inputs[1].union.keyboard.wVk = VK_RETURN;
                inputs[1].union.keyboard.wScan = scan;
                inputs[1].union.keyboard.dwFlags = KEYEVENTF_KEYUP;
                if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length) okCount++;
            }
            result = okCount == presses
                ? $"已送出 Enter ×{presses}（scan=0x{scan:X2}）— 注意：這只代表 Windows 收下，不代表對方送出了"
                : $"Enter 送出不完整（{okCount}/{presses} 次成功）";
            return okCount == presses;
        }

        // 區塊職責：送一組 modifier + 主鍵的快捷鍵（例如 Ctrl+L）。
        // 物理意義：按下順序 modifier→主鍵、放開順序主鍵→modifier；順序顛倒會讓某些 app 收到裸主鍵。
        //          一律補掃描碼，理由同 TrySendEnter —— 掃描碼 0 在 Chromium 系會算出空的 event.code。
        // 數值影響：只送這一組鍵，不碰剪貼簿、不改輸入法狀態；失敗只回 false。
        public static bool TrySendHotkey(ushort virtualKey, bool ctrl, bool shift, bool alt, out string result)
        {
            if (!s_Enabled)
            {
                result = "請先在酒保後台啟動遠端視窗協作";
                return false;
            }
            var list = new List<INPUT>();
            if (ctrl) list.Add(MakeVirtualKey(VK_CONTROL, false));
            if (shift) list.Add(MakeVirtualKey(VK_SHIFT, false));
            if (alt) list.Add(MakeVirtualKey(VK_MENU, false));
            list.Add(MakeVirtualKey(virtualKey, false));
            list.Add(MakeVirtualKey(virtualKey, true));
            if (alt) list.Add(MakeVirtualKey(VK_MENU, true));
            if (shift) list.Add(MakeVirtualKey(VK_SHIFT, true));
            if (ctrl) list.Add(MakeVirtualKey(VK_CONTROL, true));
            var array = list.ToArray();
            uint sent = SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
            string label = $"{(ctrl ? "Ctrl+" : "")}{(shift ? "Shift+" : "")}{(alt ? "Alt+" : "")}0x{virtualKey:X2}";
            result = sent == array.Length
                ? $"已送出 {label}（同樣只代表 Windows 收下）"
                : $"{label} 送出不完整（{sent}/{array.Length}）";
            return sent == array.Length;
        }

        static INPUT MakeVirtualKey(ushort virtualKey, bool keyUp)
        {
            var input = new INPUT { type = INPUT_KEYBOARD };
            input.union.keyboard.wVk = virtualKey;
            input.union.keyboard.wScan = (ushort)MapVirtualKey(virtualKey, MAPVK_VK_TO_VSC);
            if (keyUp) input.union.keyboard.dwFlags = KEYEVENTF_KEYUP;
            return input;
        }

        static INPUT MakeUnicodeKey(char c, bool keyUp)
        {
            var input = new INPUT { type = INPUT_KEYBOARD };
            input.union.keyboard.wScan = c;
            input.union.keyboard.dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0u);
            return input;
        }

        public static bool TryGetCursor(out int x, out int y)
        {
            x = y = 0;
            if (!GetCursorPos(out POINT p)) return false;
            x = p.x; y = p.y;
            return true;
        }

        /// <summary>供 locator 在完成一次完整測試後，把結果掛回後台的狀態行。</summary>
        public static void SetLastResult(string result) => s_LastResult = result;

        // 區塊職責：列舉可見 top-level window，以標題或程序名稱比對指定工具後呼叫 SetForegroundWindow。
        // 物理意義：Win32 視窗與 process metadata 比圖像可靠，無 DPI / icon badge / 深淺主題相依性。
        // 數值影響：只接受可見且非空標題的視窗；沒有唯一命中時保留目前焦點、不嘗試猜測點擊座標。
        static bool TryActivateWindow(string targetName, out string result)
        {
            IntPtr processMatched = IntPtr.Zero;
            string processMatchedTitle = "";
            IntPtr titleFallback = IntPtr.Zero;
            string titleFallbackTitle = "";
            var diagnostics = new List<string>();
            var visibleWindows = new List<string>();
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window)) return true;
                GetWindowThreadProcessId(window, out uint processId);
                string processName = GetProcessName(processId);
                string title = GetWindowTitle(window);
                visibleWindows.Add(DescribeWindow(window, processId, processName, title));
                bool processHit = IsTargetProcess(processName, targetName);
                bool titleHit = ContainsIgnoreCase(title, targetName);
                if (processHit || titleHit)
                    diagnostics.Add($"- hwnd: `0x{window.ToInt64():X}` | pid: `{processId}` | process: `{processName}` | process-hit: `{processHit}` | title-hit: `{titleHit}` | title: `{title}`");
                if (processHit)
                {
                    processMatched = window;
                    processMatchedTitle = title;
                    return false; // process identity is definitive; don't let a later title match replace it.
                }
                if (titleFallback == IntPtr.Zero && titleHit)
                {
                    titleFallback = window;
                    titleFallbackTitle = title;
                }
                return true;
            }, IntPtr.Zero);

            IntPtr matched = processMatched != IntPtr.Zero ? processMatched : titleFallback;
            string matchedTitle = processMatched != IntPtr.Zero ? processMatchedTitle : titleFallbackTitle;

            if (matched == IntPtr.Zero)
            {
                result = "找不到可見視窗";
                WriteDiagnostic(targetName, diagnostics, visibleWindows, result, IntPtr.Zero, false, GetForegroundWindow(), GetForegroundWindow());
                return false;
            }
            IntPtr foregroundBefore = GetForegroundWindow();
            bool requestAccepted = TryBringToForeground(matched, foregroundBefore);
            bool success = WaitForForeground(matched, out IntPtr foregroundAfter, out string how);
            // 成功時記**實際**在前景的那個 hwnd（可能是同 app 的兄弟視窗）—— 記成 matched 的話，
            // 後續「焦點還在我以為的地方嗎」會對著一個沒在前景的 handle 比，永遠不成立。
            s_LastActivated = success ? foregroundAfter : IntPtr.Zero;
            result = success
                ? $"已切換到「{matchedTitle}」（{how}）"
                : $"找到「{matchedTitle}」，但前景仍是 {DescribeWindow(foregroundAfter)}（Win32 request={requestAccepted}）";
            WriteDiagnostic(targetName, diagnostics, visibleWindows, result, matched, success, foregroundBefore, foregroundAfter);
            return success;
        }

        // 區塊職責：等前景真的變成目標視窗（或同一個 app 的其他視窗），最多等 ForegroundWaitMs。
        // 物理意義：**SetForegroundWindow 是非同步的** —— 呼叫完立刻讀 GetForegroundWindow 常常還是舊的，
        //          於是判成「切換失敗」，但使用者眼睛看到的是切成功了（2026-08-02 Tim 回報）。
        //          這是「外觀 FAIL ≠ 真的 FAIL」的反向層次混淆：不是動作沒發生，是我量得太早。
        // 數值影響：同 app 的兄弟視窗也算成功 —— 我們要的是「那個 app 到前面來了」，不是「剛好是我列舉到的
        //          那個 hwnd」；app 常把焦點交給另一個 top-level 視窗（登入框、子視窗）。
        const int ForegroundWaitMs = 1500;
        const int ForegroundPollMs = 25;

        static bool WaitForForeground(IntPtr target, out IntPtr foreground, out string how)
        {
            GetWindowThreadProcessId(target, out uint targetPid);
            int waited = 0;
            while (true)
            {
                foreground = GetForegroundWindow();
                if (foreground == target) { how = waited == 0 ? "即時到位" : $"等 {waited}ms 到位"; return true; }
                if (foreground != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(foreground, out uint pid);
                    if (pid == targetPid && pid != 0)
                    {
                        how = $"同一個 app 的其他視窗到前景（pid={pid}，等 {waited}ms）";
                        return true;
                    }
                }
                if (waited >= ForegroundWaitMs) { how = $"等滿 {ForegroundWaitMs}ms 仍未到位"; return false; }
                System.Threading.Thread.Sleep(ForegroundPollMs);
                waited += ForegroundPollMs;
            }
        }

        // 區塊職責：用 restore + bring-to-top + 暫時 AttachThreadInput 完成 Windows 前景切換，並由 caller 做真實前景驗證。
        // 物理意義：SetForegroundWindow 回 true 只代表 request 被接受，未保證視窗實際蓋到目前焦點；attach 可處理同桌面同權限程序。
        // 數值影響：任何 Win32 呼叫失敗都只回 false，沒有座標點擊或鍵盤 fallback；跨權限桌面仍安全地失敗。
        static bool TryBringToForeground(IntPtr target, IntPtr foregroundBefore)
        {
            uint targetThread = GetWindowThreadProcessId(target, out _);
            uint foregroundThread = foregroundBefore == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foregroundBefore, out _);
            bool attached = false;
            try
            {
                if (foregroundThread != 0 && foregroundThread != targetThread)
                    attached = AttachThreadInput(foregroundThread, targetThread, true);
                ShowWindowAsync(target, ShowRestore);
                bool broughtToTop = BringWindowToTop(target);
                bool foregroundRequested = SetForegroundWindow(target);
                SetFocus(target);
                return broughtToTop || foregroundRequested;
            }
            finally
            {
                if (attached) AttachThreadInput(foregroundThread, targetThread, false);
            }
        }

        // 區塊職責：將每次明示測試的 Win32 中介資料落成可直接開啟的 UTF-8 Markdown。
        // 物理意義：UI 顯示的一行結果不足以判斷錯配是標題、process、PID 或 Windows 前景政策造成；檔案保留候選真相。
        // 數值影響：每次覆寫一份最新測試，不累積含本機視窗標題的歷史資料；寫檔失敗不影響切換結果。
        static void WriteDiagnostic(string targetName, List<string> candidates, List<string> visibleWindows, string result, IntPtr selectedWindow, bool foregroundSucceeded, IntPtr foregroundBefore, IntPtr foregroundAfter)
        {
            try
            {
                UCL_BartenderIO.EnsureBartenderDir();
                var text = new StringBuilder();
                text.AppendLine("# 遠端視窗協作測試診斷");
                text.AppendLine();
                text.AppendLine($"- 時間（UTC）：`{DateTime.UtcNow:O}`");
                text.AppendLine($"- 指定 agent：`{targetName}`");
                text.AppendLine($"- 使用者靜置秒數：`{UserIdleSeconds:0.000}`");
                text.AppendLine($"- 切換前 foreground：{DescribeWindow(foregroundBefore)}");
                text.AppendLine($"- 切換後 foreground：{DescribeWindow(foregroundAfter)}");
                text.AppendLine($"- 選定 hwnd：`{(selectedWindow == IntPtr.Zero ? "none" : $"0x{selectedWindow.ToInt64():X}")}`");
                text.AppendLine($"- SetForegroundWindow：`{foregroundSucceeded}`");
                text.AppendLine($"- 結果：{result}");
                text.AppendLine();
                text.AppendLine("## 可見候選視窗");
                text.AppendLine(candidates.Count == 0 ? "（無 process / title 命中候選）" : string.Join("\n", candidates));
                text.AppendLine();
                text.AppendLine("## 全部可見 top-level 視窗");
                text.AppendLine(visibleWindows.Count == 0 ? "（無法列舉）" : string.Join("\n", visibleWindows));
                File.WriteAllText(DiagnosticPath, text.ToString(), new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning($"[RemoteWindowControl] 診斷檔寫入失敗: {exception.Message}");
            }
        }

        static string GetWindowTitle(IntPtr window)
        {
            int length = GetWindowTextLength(window);
            if (length <= 0) return "";
            var buffer = new StringBuilder(length + 1);
            GetWindowText(window, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        static string GetProcessName(uint processId)
        {
            try { return Process.GetProcessById((int)processId).ProcessName ?? ""; }
            catch { return ""; }
        }

        static string DescribeWindow(IntPtr window)
        {
            if (window == IntPtr.Zero) return "`none`";
            GetWindowThreadProcessId(window, out uint processId);
            return DescribeWindow(window, processId, GetProcessName(processId), GetWindowTitle(window));
        }

        static string DescribeWindow(IntPtr window, uint processId, string processName, string title) =>
            $"`0x{window.ToInt64():X}` | pid=`{processId}` | process=`{processName}` | title=`{title}`";

        static bool ContainsIgnoreCase(string source, string value) =>
            !string.IsNullOrEmpty(source) && source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

        // 區塊職責：將人類顯示名稱對齊實際 process basename，作為比視窗標題更可靠的第一優先識別。
        // 物理意義：Claude Code 的 Windows process 通常叫 claude.exe；反過來，工作階段標題可含其他 agent 名稱。
        // 數值影響：只有完全等於既定 basename 才當 definitive hit；標題僅在找不到該程序時才作 fallback。
        static bool IsTargetProcess(string processName, string targetName)
        {
            if (string.IsNullOrEmpty(processName)) return false;
            if (targetName == "Antigravity") return string.Equals(processName, "Antigravity", StringComparison.OrdinalIgnoreCase);
            // Codex Desktop 在本機可見主視窗由 ChatGPT.exe 擁有；codex.exe 則是無視窗的 command / host process。
            if (targetName == "Codex") return string.Equals(processName, "codex", StringComparison.OrdinalIgnoreCase)
                || string.Equals(processName, "ChatGPT", StringComparison.OrdinalIgnoreCase);
            if (targetName == "Claude Code") return string.Equals(processName, "claude", StringComparison.OrdinalIgnoreCase);
            return string.Equals(processName, targetName, StringComparison.OrdinalIgnoreCase);
        }

        delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        const int ShowRestore = 9;
        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x; public int y; }
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);

        // SendInput 結構 — 只用到 mouse 與 keyboard 兩種，union 大小由最大成員決定，不可拆開宣告。
        const int INPUT_MOUSE = 0;
        const int INPUT_KEYBOARD = 1;
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const uint KEYEVENTF_UNICODE = 0x0004;
        const ushort VK_RETURN = 0x0D;
        const uint MAPVK_VK_TO_VSC = 0;
        const ushort VK_CONTROL = 0x11;
        const ushort VK_SHIFT = 0x10;
        const ushort VK_MENU = 0x12;   // Alt
        const ushort VK_V = 0x56;      // 剪貼簿貼上用（Ctrl+V）
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

        [StructLayout(LayoutKind.Explicit)]
        struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mouse;
            [FieldOffset(0)] public KEYBDINPUT keyboard;
            [FieldOffset(0)] public HARDWAREINPUT hardware;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public int type; public INPUTUNION union; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr window);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach);
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    }
}
#endif
