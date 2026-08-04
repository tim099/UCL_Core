// 區塊職責：wait 期間的酒保 NPC —— 長時間等待時隨機插一句話，緩解沉默。
// 物理意義：本檔是 `Tools~/AgentCommands/tavern_handshake.py` 的酒保區塊固化上來的版本
//          （Tim 2026-08-04：「系統性功能都儘量固化到 C# server 端，python 端主要放工具 & API 層」）。
//          搬上來之後 wait 的一切副作用都發生在 Editor 內：不再 spawn 子 process 去發訊息，
//          也不再依賴「發起 wait 的那個 python 還活著」才有酒保。
// 數值影響：觸發時以 tavern-keeper 身分 append 一則訊息；連喝計數 / cooldown 寫 _bartender_state.json
//          （沿用 python 版的同一份檔與同一組欄位，不另立門戶）。
// 設計取捨：
//   - **訊息一定帶 `kind: atmosphere`**：wait 命中判定靠這個標記把「勸酒」跟「酒保的系統廣播」
//     分開（UCL_ChatTavernIO.IsBartenderAtmosphere）。少了它，酒保自己發的勸酒會終止別人的 wait，
//     而系統廣播也會被誤當勸酒 —— 2026-08-04 client 版實測到的 F2 就是這隻。
//   - **抽不到台詞就安靜跳過**：酒保是氣氛不是功能，缺台詞檔不該讓 wait 出錯。
// @doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_Tavern.md
// @doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/Workflows/ChatTavern_Workflow.md
// @doc-sync: Assets/Plugins/UCL_Core/Skills~/ucl-chat-tavern/reference/re-entry.md
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;
using Random = UnityEngine.Random;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>wait 期間的酒保插話（自 tavern_handshake.py 固化）。</summary>
    public static class UCL_TavernWaitNpc
    {
        // 三個旋鈕的真相源是 UCL_TavernWaitSettings（後台可改、落檔）。
        // 這裡只做轉呼叫，不留第二份預設值 —— 兩份預設值遲早會不一樣，而且不會有人發現。
        /// <summary>等待超過這個秒數才開始考慮插話。</summary>
        public static int TriggerSeconds
        { get { UCL_TavernWaitSettings.EnsureLoaded(); return UCL_TavernWaitSettings.NpcTriggerSeconds; } }
        /// <summary>兩次插話至少相隔（防一場 wait 內噴太密）。</summary>
        public static int CooldownSeconds
        { get { UCL_TavernWaitSettings.EnsureLoaded(); return UCL_TavernWaitSettings.NpcCooldownSeconds; } }
        /// <summary>達此杯數不 mute 酒保，但 agent 該自決收 turn（顯示用）。</summary>
        public static int RestHintDrinks
        { get { UCL_TavernWaitSettings.EnsureLoaded(); return UCL_TavernWaitSettings.NpcRestHintDrinks; } }

        static string LinesPath => Path.Combine(UCL_ChatTavernIO.GetTavernDir(), "bartender_lines.json");
        static string StatePath => Path.Combine(UCL_ChatTavernIO.GetTavernDir(), "_bartender_state.json");

        // 區塊職責：條件成立就插一句，回傳是否真的插了。
        // 邊界：elapsed 未達 TriggerSeconds / cooldown 未過 / 抽不到台詞 → 都是安靜回 false。
        //      targetPersona 是「被勸酒的對象」= 期待回覆的那一方（有 expect_from 時），
        //      不是發起 wait 的人 —— 對方之後 catchup 才看得懂酒保在對誰說話。
        public static bool TryInterject(string room, string waiterId, string targetPersona,
                                        double elapsedSeconds, int triggerAfterSec = 0)
        {
            int trigger = triggerAfterSec > 0 ? triggerAfterSec : TriggerSeconds;
            if (elapsedSeconds < trigger) return false;
            if (string.IsNullOrEmpty(room) || string.IsNullOrEmpty(waiterId)) return false;
            try
            {
                var state = LoadState();
                var sess = FindSession(state, room, waiterId);
                if (sess != null && sess.TryGetValue("last_drink_at", out string lastIso) && !string.IsNullOrEmpty(lastIso))
                {
                    if (DateTime.TryParse(lastIso, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var last)
                        && (DateTime.UtcNow - last).TotalSeconds < CooldownSeconds)
                        return false;
                }

                string body = PickLine();
                if (string.IsNullOrEmpty(body)) return false;

                int cup = 1;
                if (sess != null && sess.TryGetValue("consecutive_drinks", out string cs)
                    && int.TryParse(cs, out int prev)) cup = prev + 1;

                UCL_ChatTavernIO.AppendMessage(room, new UCL_ChatMessage
                {
                    sender_id = UCL_ChatTavernIO.BartenderSenderId,
                    sender_name = "酒保",
                    sender_persona = UCL_ChatTavernIO.BartenderSenderId,
                    kind = "chat",
                    body = body,
                    meta = new Dictionary<string, string>
                    {
                        // ⚠ kind=atmosphere 是「這是勸酒不是系統廣播」的唯一判準，別拿掉
                        { "kind", "atmosphere" },
                        { "tag", "bartender" },
                        { "target_agent", targetPersona ?? waiterId },
                        { "cup", cup.ToString() },
                    },
                });

                SaveSession(state, room, waiterId, cup);
                Debug.Log($"[TavernWaitNpc] 酒保插話（第 {cup} 杯，target={targetPersona ?? waiterId}）：{body}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TavernWaitNpc] 插話失敗（不影響 wait）: {e.Message}");
                return false;
            }
        }

        /// <summary>真實回覆進來時清連喝計數。</summary>
        public static void ResetDrinks(string room, string waiterId)
        {
            try
            {
                var state = LoadState();
                var sess = FindSession(state, room, waiterId);
                if (sess == null) return;
                if (sess.TryGetValue("consecutive_drinks", out string cs) && cs == "0") return;
                SaveSession(state, room, waiterId, 0);
            }
            catch { /* 計數清不掉不值得讓 wait 出錯 */ }
        }

        // 抽一條 template 並填 {slot}；缺 filler 就放棄這次（下次重抽），不硬填空字串。
        static string PickLine()
        {
            if (!File.Exists(LinesPath)) return null;
            var data = JsonData.ParseJson(File.ReadAllText(LinesPath, Encoding.UTF8));
            var templates = data?.Get("templates");
            if (templates == null || !templates.IsArray || templates.Count == 0) return null;
            string tpl = templates[Random.Range(0, templates.Count)].GetString();
            if (string.IsNullOrEmpty(tpl)) return null;

            var fillers = data.Get("fillers");
            var sb = new StringBuilder();
            for (int i = 0; i < tpl.Length; i++)
            {
                if (tpl[i] != '{') { sb.Append(tpl[i]); continue; }
                int close = tpl.IndexOf('}', i);
                if (close < 0) { sb.Append(tpl[i]); continue; }
                string slot = tpl.Substring(i + 1, close - i - 1);
                var opts = fillers?.Get(slot);
                if (opts == null || !opts.IsArray || opts.Count == 0) return null;   // 缺 filler → 放棄
                sb.Append(opts[Random.Range(0, opts.Count)].GetString());
                i = close;
            }
            return sb.ToString();
        }

        // ── _bartender_state.json：{"sessions":[{room,agent,consecutive_drinks,last_drink_at}]} ──
        // 沿用 python 版的 schema（同一份檔），不另立格式。
        /// <summary>空陣列 JsonData —— 沒有 JsonData(JsonType) 建構子，要走 Init()。</summary>
        static JsonData NewArray()
        {
            var d = new JsonData();
            d.Init(JsonType.List);   // ⚠ 是 List 不是 Array（enum 沒有 Array 這個值）
            return d;
        }

        static JsonData LoadState()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    var d = JsonData.ParseJson(File.ReadAllText(StatePath, Encoding.UTF8));
                    if (d != null && d.Get("sessions") != null) return d;
                }
            }
            catch { }
            var fresh = new JsonData();
            fresh["sessions"] = NewArray();
            return fresh;
        }

        static Dictionary<string, string> FindSession(JsonData state, string room, string agent)
        {
            var arr = state?.Get("sessions");
            if (arr == null || !arr.IsArray) return null;
            for (int i = 0; i < arr.Count; i++)
            {
                var s = arr[i];
                if (s == null) continue;
                if (s.GetString("room", "") != room || s.GetString("agent", "") != agent) continue;
                return new Dictionary<string, string>
                {
                    { "consecutive_drinks", s.GetInt("consecutive_drinks", 0).ToString() },
                    { "last_drink_at", s.GetString("last_drink_at", "") },
                };
            }
            return null;
        }

        static void SaveSession(JsonData state, string room, string agent, int drinks)
        {
            var arr = state.Get("sessions");
            if (arr == null || !arr.IsArray) { arr = NewArray(); state["sessions"] = arr; }
            string nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            for (int i = 0; i < arr.Count; i++)
            {
                var s = arr[i];
                if (s == null || s.GetString("room", "") != room || s.GetString("agent", "") != agent) continue;
                s["consecutive_drinks"] = new JsonData(drinks);
                if (drinks > 0) s["last_drink_at"] = new JsonData(nowIso);
                Persist(state);
                return;
            }
            var entry = new JsonData();
            entry["room"] = new JsonData(room);
            entry["agent"] = new JsonData(agent);
            entry["consecutive_drinks"] = new JsonData(drinks);
            entry["last_drink_at"] = new JsonData(drinks > 0 ? nowIso : "");
            arr.Add(entry);
            Persist(state);
        }

        static void Persist(JsonData state)
        {
            try
            {
                UCL_ChatTavernIO.EnsureTavernDir();
                File.WriteAllText(StatePath, state.ToJsonBeautify(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TavernWaitNpc] 狀態寫入失敗: {e.Message}");
            }
        }
    }
}
#endif
