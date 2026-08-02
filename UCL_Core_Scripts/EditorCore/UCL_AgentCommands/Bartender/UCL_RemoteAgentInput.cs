// 區塊職責：**per-agent 的「點完 session 之後、打字之前」該做什麼** —— 每個桌面工具一個 case。
// 物理意義：點側邊清單選起 session 後，焦點會不會自己落到輸入框，是**各 app 各自的行為**，不是通則。
//          Codex(ChatGPT) 與 ClaudeCode 實測會自動 focus；Antigravity 2.0 沒有 Auto Focus 設定，
//          要補一段 Ctrl+L 才會跳回主輸入框（apex-one 2026-08-02 與 Tim 實測）。
// 數值影響：新增一個桌面工具＝在 Profiles 加一個 case，其餘流程不動；沒有 case 的一律視為「會自動 focus」，
//          也就是維持舊行為，不會因為漏加設定就整條線壞掉。
#if UNITY_EDITOR && UNITY_STANDALONE_WIN
using System.Collections.Generic;
using UCL.Core.EditorLib.AgentCommands;

namespace UCL.Core.EditorLib.AgentCommands.Bartender
{
    /// <summary>把焦點帶進輸入框的手段。</summary>
    public enum UCL_FocusMode
    {
        /// <summary>不必做什麼 —— 點完 session 焦點就在輸入框（Codex / ClaudeCode 實測）。</summary>
        None = 0,
        /// <summary>送一組快捷鍵。</summary>
        Hotkey,
        /// <summary>OCR 找輸入框的 placeholder 文字，點它。</summary>
        LocatePlaceholder,
    }

    /// <summary>某個桌面 agent 在「輸入前」需要的準備動作。</summary>
    public class UCL_AgentInputProfile
    {
        public UCL_FocusMode Mode = UCL_FocusMode.None;
        public ushort HotkeyVirtualKey;
        public bool Ctrl, Shift, Alt;
        public string HotkeyLabel = "";
        /// <summary>LocatePlaceholder 用：輸入框裡的提示文字，模糊比對（包含即可）。</summary>
        public string PlaceholderText = "";
        /// <summary>為什麼需要這一段 —— 寫在 profile 裡，讀 code 的人不必去翻聊天記錄。</summary>
        public string Note = "";

        public bool NeedsPreparation => Mode != UCL_FocusMode.None;
        public string ActionLabel => Mode switch
        {
            UCL_FocusMode.Hotkey => HotkeyLabel,
            UCL_FocusMode.LocatePlaceholder => $"找並點擊「{PlaceholderText}」",
            _ => "無",
        };
    }

    public static class UCL_RemoteAgentInput
    {
        const ushort VK_L = 0x4C;

        // 區塊職責：agent → 輸入前置動作的對照表。
        // 物理意義：這裡的每一條都該是**實測**出來的，不是猜的 —— 猜錯的代價是把快捷鍵送進一個
        //          它有別的意思的 app（Ctrl+L 在終端機系是清畫面、瀏覽器系是跳網址列）。
        // 數值影響：查不到 = 不做任何前置動作（維持舊行為）。
        static readonly Dictionary<UCL_ActualAgent, UCL_AgentInputProfile> Profiles =
            new Dictionary<UCL_ActualAgent, UCL_AgentInputProfile>
            {
                // Tim 2026-08-02 實測：這兩個點完 session 後焦點會自己進輸入框，不必多做任何事。
                [UCL_ActualAgent.Codex] = new UCL_AgentInputProfile
                { Mode = UCL_FocusMode.None, Note = "Tim 2026-08-02 實測：ChatGPT 桌面版點完 session 自動 focus" },
                [UCL_ActualAgent.ClaudeCode] = new UCL_AgentInputProfile
                { Mode = UCL_FocusMode.None, Note = "Tim 2026-08-02 實測：Claude Code 點完 session 自動 focus" },

                // Antigravity 2.0：先試過 Ctrl+L（apex-one 提案）——**Tim 2026-08-02 實測無效，已放棄**。
                // 改成「OCR 找輸入框自己的提示文字再點它」：比快捷鍵可靠的地方在於它有畫面證據 ——
                // 找不到就會失敗並留下 near-miss，而快捷鍵送出成功卻沒生效是靜默的（今天已經被騙過一次）。
                // 取最下方的命中：對話區裡也可能出現同一段字（例如有人把它貼進訊息），輸入框永遠在最下面。
                [UCL_ActualAgent.Antigravity] = new UCL_AgentInputProfile
                {
                    Mode = UCL_FocusMode.LocatePlaceholder,
                    PlaceholderText = "Ask anything",
                    Note = "Antigravity 2.0 無 Auto Focus；Ctrl+L 實測無效，改 OCR 找輸入框 placeholder 再點（Tim 2026-08-02）",
                },
            };

        public static UCL_AgentInputProfile Get(UCL_ActualAgent agent) =>
            Profiles.TryGetValue(agent, out var profile) ? profile : new UCL_AgentInputProfile();

        /// <summary>
        /// 執行該 agent 的輸入前置動作。回傳描述字串（無論有沒有動作都回，讓紀錄看得出走了哪條路）。
        /// </summary>
        /// <param name="focusDelaySeconds">送出快捷鍵後等多久再打字 —— 焦點切換需要時間，太快打字會落在舊焦點上。</param>
        public static string PrepareInput(UCL_ActualAgent agent, UCL_PersonaLocateOptions options)
        {
            var profile = Get(agent);
            float delay = options?.FocusDelaySec ?? 0.5f;
            switch (profile.Mode)
            {
                case UCL_FocusMode.None:
                    return "輸入前置：不需要（此 agent 點完會自動 focus）";

                case UCL_FocusMode.Hotkey:
                    if (!UCL_RemoteWindowControl.TrySendHotkey(profile.HotkeyVirtualKey, profile.Ctrl,
                                                               profile.Shift, profile.Alt, out string hotkeyResult))
                        return $"輸入前置：{profile.HotkeyLabel} 送出失敗（{hotkeyResult}）";
                    Sleep(delay);
                    return $"輸入前置：{profile.HotkeyLabel} 已送出並等 {delay:0.##}s";

                case UCL_FocusMode.LocatePlaceholder:
                    return LocateAndClickPlaceholder(profile, options, delay);
            }
            return "輸入前置：未知模式";
        }

        // 區塊職責：OCR 找輸入框的提示文字 → 移游標 → 點下去，把焦點放進輸入框。
        // 物理意義：placeholder 是輸入框自己畫出來的字，找到它＝找到輸入框；比「賭焦點會自己跑進去」
        //          與「送一顆可能沒生效的快捷鍵」都可靠，因為**失敗會有畫面證據**（near-miss 留在結果裡）。
        // 數值影響：掃整塊選定螢幕（輸入框在視窗底部，套用主流程那個左側矩形會直接掃不到）；
        //          取最下方命中 —— 對話區可能出現同一段字，輸入框永遠在最下面。
        static string LocateAndClickPlaceholder(UCL_AgentInputProfile profile, UCL_PersonaLocateOptions options, float delay)
        {
            var probe = new UCL_PersonaLocateOptions
            {
                Monitor = options?.Monitor ?? "all",
                RegionX = 0f, RegionY = 0f, RegionW = 1f, RegionH = 1f,
                InitialDelaySec = 0f,
                Attempts = 2,
                AttemptDelaySec = 0.4f,
                SelectPolicy = "bottommost",
                MatchMode = "contains",
                MatchIndex = -1,
            };
            var result = UCL_RemotePersonaLocator.Locate(profile.PlaceholderText, probe);
            if (!result.Ok || result.Selected == null)
                return $"輸入前置：找不到輸入框（比對「{profile.PlaceholderText}」— {result.Reason}）";
            var box = result.Selected;
            if (!UCL_RemoteWindowControl.TryMoveCursor(box.CenterX, box.CenterY, out string moveResult))
                return $"輸入前置：找到輸入框但游標沒到位（{moveResult}）";
            if (!UCL_RemoteWindowControl.TryClickLeft(out string clickResult))
                return $"輸入前置：找到輸入框但點擊失敗（{clickResult}）";
            Sleep(delay);
            return $"輸入前置：已點擊輸入框 ({box.CenterX}, {box.CenterY})「{box.Text}」並等 {delay:0.##}s";
        }

        static void Sleep(float seconds)
        {
            if (seconds <= 0f) return;
            System.Threading.Thread.Sleep(UnityEngine.Mathf.Clamp(
                UnityEngine.Mathf.RoundToInt(seconds * 1000f), 0, 5000));
        }
    }
}
#endif
