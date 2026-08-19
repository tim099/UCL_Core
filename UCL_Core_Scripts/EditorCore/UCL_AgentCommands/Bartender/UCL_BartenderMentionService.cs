// 區塊職責：`@酒保` 被點名時的回話 —— 偵測、節流、生成（LLM）或罐頭、發回酒館。
// 物理意義：酒保原本只在「keyword trigger 命中」或「時間到」才說話，兩者都是**別人事先註冊**的。
//          本檔加的是第三條：**當場被點名**。它的形狀跟前兩者不同 ——
//          沒有預算上限（trigger 有 remaining_triggers）、沒有每日一次（time rule 有 fired_today），
//          所以節流必須自己長出來，否則兩個人互相 @ 一晚就能洗掉整個酒館。
//
// 數值影響：每次回話 = 一則 tavern 訊息（會 mirror 到 Discord）。
//          LLM 路徑會佔顯存（keep_alive 秒數後自動卸）；罐頭路徑零成本。
//
// 設計取捨（每一條都是「這種失敗不會叫」）：
//   · **罐頭是 fallback 不是替代**：服務沒開／逾時／輸出空 → 一律退罐頭。
//     ⇒ 通道上永遠有話可講。靜默不回的症狀跟「沒偵測到 @」一模一樣，最難查。
//   · **已回過的 seq 記在磁碟**，不只記在記憶體 —— domain reload 會清掉 static 欄位，
//     而清掉之後同一則訊息會被再回一次（tick 是 poll 模型，訊息還在那裡）。
//   · **全域冷卻 ＋ 每日上限**：擋的不是單一使用者，是**互 ping**
//     （A @酒保 → 酒保回覆裡出現 A 的名字 → A 的 agent 又回…）。
//   · **酒保自己的訊息永遠不參與**（沿用既有 IsBartenderOwnMessage 的防回音判準）。
//   · **生成走 async，絕不在 tick 裡等**：tick 是 5s 一次的 Editor update，
//     同步等模型（實測 4b 要 50 秒）會把 Editor 凍住。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;
using UCL.Core.EditorLib.AgentCommands.LLMAdmin;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Bartender
{
    /// <summary>`@酒保` 回話的節流狀態（落磁碟；domain reload 不該讓它重來）。</summary>
    public class UCL_BartenderMentionState : UnityJsonSerializable
    {
        /// <summary>今天的日期（YYYY-MM-DD，本地）—— 跨日自動歸零。</summary>
        public string day = "";
        /// <summary>今天已回幾則。</summary>
        public int replied_today = 0;
        /// <summary>最後一次回話的 unix 秒（全域冷卻用）。</summary>
        public long last_reply_unix = 0;
        /// <summary>已回過的訊息 seq（防重複回；只保留最近 MAX_SEEN 筆）。</summary>
        public List<int> replied_seqs = new List<int>();
    }

    /// <summary>`@酒保` 偵測與回話。</summary>
    public static class UCL_BartenderMentionService
    {
        public const string StateFile = "mention_state.json";
        const int MAX_SEEN = 200;          // 已回 seq 的保留量（夠擋重複，又不讓檔案無限長）

        // 點名寫法。⚠ 刻意包含酒保自己的 sender_id —— 有人會寫 @tavern-keeper。
        //   ⚠ 不收「酒保」兩個裸字：訊息裡提到酒保（例如「酒保剛剛說…」）不是點名，
        //     那種寬鬆比對會讓酒保插進每一段談論它的對話。**要有 @ 才算叫它。**
        static readonly string[] MentionForms = { "@酒保", "@tavern-keeper", "@bartender", "＠酒保" };

        /// <summary>這則訊息有沒有在叫酒保？</summary>
        public static bool IsMention(string iBody)
        {
            if (string.IsNullOrEmpty(iBody)) return false;
            foreach (var aForm in MentionForms)
            {
                if (iBody.IndexOf(aForm, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // ===========================================================
        // 狀態
        // ===========================================================
        public static string GetStatePath()
            => Path.Combine(UCL_BartenderIO.GetBartenderDir(), StateFile);

        public static UCL_BartenderMentionState LoadState()
        {
            try
            {
                string aPath = GetStatePath();
                if (!File.Exists(aPath)) return new UCL_BartenderMentionState();
                var aState = new UCL_BartenderMentionState();
                aState.DeserializeFromJson(JsonData.ParseJson(File.ReadAllText(aPath)));
                return aState;
            }
            catch (Exception e)
            {
                // 壞檔退預設會讓節流歸零 ⇒ 出聲，別讓它安靜地放行
                Debug.LogWarning($"[Bartender] mention 狀態讀取失敗（節流從零算起）：{e.Message}");
                return new UCL_BartenderMentionState();
            }
        }

        public static void SaveState(UCL_BartenderMentionState iState)
        {
            try
            {
                UCL_BartenderIO.EnsureBartenderDir();
                string aPath = GetStatePath();
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, iState.SerializeToJson().ToJsonBeautify(), new UTF8Encoding(false));
                if (File.Exists(aPath)) File.Delete(aPath);
                File.Move(aTmp, aPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bartender] mention 狀態寫入失敗：{e.Message}");
            }
        }

        // ===========================================================
        // 節流判定
        // ===========================================================
        /// <summary>可以回這一則嗎？不行的話 <paramref name="oReason"/> 說為什麼（給 log，不對外發言）。</summary>
        public static bool CanReply(UCL_BartenderMentionState iState, UCL_BartenderLLMSettings iSettings,
            int iSeq, out string oReason)
        {
            oReason = "";
            string aToday = DateTime.Now.ToString("yyyy-MM-dd");
            if (iState.day != aToday)
            {
                iState.day = aToday;
                iState.replied_today = 0;      // 跨日歸零（呼叫端會存回去）
            }
            if (iState.replied_seqs.Contains(iSeq))
            {
                oReason = $"seq {iSeq} 已經回過（poll 模型會重看到同一則）";
                return false;
            }
            if (iSettings.mention_daily_cap > 0 && iState.replied_today >= iSettings.mention_daily_cap)
            {
                oReason = $"今日已達上限 {iSettings.mention_daily_cap} 則";
                return false;
            }
            long aNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long aElapsed = aNow - iState.last_reply_unix;
            if (iSettings.mention_cooldown_seconds > 0 && aElapsed < iSettings.mention_cooldown_seconds)
            {
                oReason = $"冷卻中（還要 {iSettings.mention_cooldown_seconds - aElapsed}s）";
                return false;
            }
            return true;
        }

        static void MarkReplied(UCL_BartenderMentionState iState, int iSeq)
        {
            iState.replied_seqs.Add(iSeq);
            if (iState.replied_seqs.Count > MAX_SEEN)
            {
                iState.replied_seqs.RemoveRange(0, iState.replied_seqs.Count - MAX_SEEN);
            }
            iState.replied_today += 1;
            iState.last_reply_unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SaveState(iState);
        }

        // ===========================================================
        // 回話
        // ===========================================================
        // ⚠ 同一時間只跑一件 —— tick 每 5s 一次，而生成可能要幾十秒。
        //   沒有這道閘的話同一則訊息會被連續 tick 各起一次生成（顯存與訊息都會爆）。
        static bool s_Running = false;

        /// <summary>正在生成中？（daemon 用來跳過本輪，避免疊起來）</summary>
        public static bool IsBusy => s_Running;

        /// <summary>
        /// 處理一則點名（fire-and-forget；**絕不在 tick 裡 await**）。
        /// 先寫節流帳再生成 —— 反過來的話，生成期間的下一個 tick 會再進來一次。
        /// </summary>
        public static void HandleMentionAsync(UCL_ChatMessage iMsg, string iRoomId)
        {
            if (iMsg == null || s_Running) return;
            var aSettings = UCL_BartenderLLMSettingsIO.Load();
            if (!aSettings.mention_enabled) return;

            var aState = LoadState();
            if (!CanReply(aState, aSettings, iMsg.seq, out string aReason))
            {
                // 節流是預期行為，不是錯誤 —— 但要留下讀數，否則「為什麼不回」無法查
                if (!aReason.StartsWith("seq")) Debug.Log($"[Bartender] @酒保 略過：{aReason}");
                return;
            }
            MarkReplied(aState, iMsg.seq);       // 先記帳：生成很慢，這期間 tick 會再來
            s_Running = true;
            ReplyAsync(iMsg, iRoomId, aSettings).Forget();
        }

        static async UniTask ReplyAsync(UCL_ChatMessage iMsg, string iRoomId,
            UCL_BartenderLLMSettings iSettings)
        {
            string aBody = "";
            string aSource = "canned";
            try
            {
                if (!iSettings.IsCannedOnly)
                {
                    var aGen = await GenerateAsync(iMsg, iSettings);
                    if (!string.IsNullOrEmpty(aGen))
                    {
                        aBody = aGen;
                        aSource = iSettings.model_id;
                    }
                }
                if (string.IsNullOrEmpty(aBody))
                {
                    aBody = PickCanned(iSettings, iMsg.seq);      // fallback：永遠有話可講
                }
                PostReply(aBody, iMsg, iRoomId, aSource);
            }
            catch (Exception e)
            {
                // 生成炸掉也要把罐頭發出去 —— 沉默是最難查的失敗
                Debug.LogWarning($"[Bartender] @酒保 生成失敗，退罐頭：{e.Message}");
                try { PostReply(PickCanned(iSettings, iMsg.seq), iMsg, iRoomId, "canned-after-error"); }
                catch (Exception e2) { Debug.LogWarning($"[Bartender] 罐頭也發不出去：{e2.Message}"); }
            }
            finally { s_Running = false; }
        }

        /// <summary>走 llm_admin.py 生成一句。失敗／逾時／空輸出一律回空字串（呼叫端退罐頭）。</summary>
        static async UniTask<string> GenerateAsync(UCL_ChatMessage iMsg, UCL_BartenderLLMSettings iSettings)
        {
            string aWho = !string.IsNullOrEmpty(iMsg.sender_persona) ? iMsg.sender_persona
                : (!string.IsNullOrEmpty(iMsg.sender_name) ? iMsg.sender_name : iMsg.sender_id);
            // 把點名字樣去掉再餵給模型 —— 留著它會誘導模型在回覆裡也寫 @酒保（自我點名＝下一輪自觸發）
            string aAsk = iMsg.body ?? "";
            foreach (var aForm in MentionForms) aAsk = aAsk.Replace(aForm, "");
            aAsk = aAsk.Trim();
            if (string.IsNullOrEmpty(aAsk)) aAsk = "有人叫了你一聲，回應一下。";

            string aPrompt = $"{aWho} 對你說：{aAsk}";
            string aSystem = string.IsNullOrEmpty(iSettings.persona_prompt)
                ? "你是酒館的酒保，講話簡短、親切、帶點幽默，一律使用繁體中文（台灣用語），只輸出要說的那一句話。"
                : iSettings.persona_prompt;

            // ⚠ `--think` 一定要帶：thinking 模型（qwen3 全家）不帶它時**會把推理寫進 content**,
            //   帶了才把推理分到 thinking 欄、content 只剩要講的那句。
            //   🩸 2026-08-19 實測 qwen3:4b／同一組 prompt：
            //     不帶 → output = 「首先，用户要求我作为傲娇的女仆…關鍵點：」（簡體推理, 而且 ok=True）
            //     帶了 → output = 乾淨的一句台詞（thinking 910 字另外收）
            //   daemon 只讀 output ⇒ 不帶的話酒保會把自言自語發到酒館, 而沒有任何一層報錯。
            string aArgs = $"reply --model {iSettings.model_id} --think" +
                $" --prompt \"{Sanitize(aPrompt)}\"" +
                $" --system \"{Sanitize(aSystem)}\"" +
                $" --num-predict {Mathf.Max(16, iSettings.max_tokens)}" +
                $" --keep-alive {Mathf.Max(0, iSettings.keep_alive_seconds)}" +
                $" --timeout {Mathf.Max(5, iSettings.timeout_seconds)} --format json";
            var aResult = await UCL_LLMAdminRunner.RunAsync(aArgs,
                (Mathf.Max(5, iSettings.timeout_seconds) + 30) * 1000);
            if (!aResult.Ok)
            {
                Debug.Log($"[Bartender] @酒保 生成未成功（退罐頭）：{aResult.DisplayText}");
                return "";
            }
            try
            {
                var aJson = JsonData.ParseJson(aResult.Stdout);
                string aOut = aJson != null ? aJson.GetString("output", "") : "";
                return (aOut ?? "").Trim();
            }
            catch (Exception) { return ""; }
        }

        /// <summary>把會弄壞命令列的字元換掉（長文走檔案是另一件事；這裡是短句）。</summary>
        static string Sanitize(string iText)
            => (iText ?? "").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");

        /// <summary>罐頭挑一句 —— **用 seq 當種子**，同一則訊息永遠得到同一句（可複驗）。</summary>
        static string PickCanned(UCL_BartenderLLMSettings iSettings, int iSeq)
        {
            var aPool = (iSettings.canned_replies != null && iSettings.canned_replies.Count > 0)
                ? iSettings.canned_replies : UCL_BartenderLLMSettings.DefaultCanned;
            return aPool[Math.Abs(iSeq) % aPool.Count];
        }

        static void PostReply(string iBody, UCL_ChatMessage iSrc, string iRoomId, string iSource)
        {
            var aMsg = new UCL_ChatMessage
            {
                sender_id = UCL_BartenderDaemon.TavernKeeperId,
                sender_name = "酒保",
                kind = "chat",
                body = iBody,
                meta = new Dictionary<string, string>
                {
                    { "tag", UCL_BartenderDaemon.BartenderRelayTag },   // 防回音的既有判準
                    { "reply_kind", "mention" },
                    { "reply_source", iSource },                        // canned / <model tag>
                    { "triggered_by_seq", iSrc.seq.ToString() },
                    { "triggered_by_sender", iSrc.sender_id ?? "" },
                },
            };
            UCL_ChatTavernIO.AppendMessage(iRoomId, aMsg);
        }
    }
}
#endif
