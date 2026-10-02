// 區塊職責：Editor 這側**唯一**的酒館發文入口 —— 轉交 `senate cmd tavern-post`／`tavern-post-system`（TASK-0366）。
// 物理意義：`Cmd_Tavern op=post` 退場後，組訊息（身分／顯示名／CLI 判定／詞典）與寫入（配號／發薪／@mention）都在 Senate：
//          · 有 persona 的發言 ⇒ `tavern-post`（計酬記在 persona 上）
//          · 系統元件／後台頁打字（沒有 persona）⇒ `tavern-post-system`（點名 sender，不計酬）
//          兩支都帶 `target_data_root=<本 Editor 的資料根>` ⇒ Senate 以資料根選專案，比不到就擋（⛔ 不會落進別的專案的酒館）。
//          spawn 走既有的 `UCL_PersonaProfileSenateBridge.RunCmd`（⛔ 不另寫第二支 process 呼叫器）。
// 數值影響：一次 process（＋酒館 Server round-trip），秒級 ⇒ ⚠ **一律在背景執行緒呼叫**（`PostAsync`／`PostSystemAsync`）。
//          🩸 舊規矩「發文一定要在主執行緒」是配號還在 Editor 時的不變式（兩條 lane 同時 post 會撞號）；
//            配號早已只在酒館 Server 一處（TASK-0341）⇒ 那條禁令不再成立，背景呼叫是安全的。
// 結果三態（照 tavern-post）：0 已發（或 `scheduled` 延後）／6 確定沒發（重跑安全）／7 **不知道**（⛔ 別補發，先回讀）。
//          叫不到 senate ＝ -1（確定沒發）；外層逾時 ＝ -2（不知道）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    public static class UCL_TavernSenatePost
    {
        /// <summary>外層逾時：要比 tavern-post 自己等酒館 Server 的 30 秒長（Server 沒開時它會先拉一顆）。</summary>
        const double OUTER_TIMEOUT_SEC = 90.0;

        public struct Result
        {
            public int ExitCode;
            /// <summary>配到的 seq；0 ＝ 沒有讀數（沒發、不知道、或延後排程還沒配號）。</summary>
            public int Seq;
            /// <summary>延後排程（alter 配對）—— 還沒配號，⛔ 不是失敗也不是已發。</summary>
            public bool Scheduled;
            /// <summary>已排隊（TASK-0372）：酒館 Server 不在，排進它的 queue、起來後送出 —— 還沒配號，⛔ 不是失敗、⛔ 不要補發。</summary>
            public bool Queued;
            /// <summary>senate 的原文輸出（失敗時貼進回報）。</summary>
            public string Output;

            public bool Posted => ExitCode == 0;
            /// <summary>不知道有沒有發 ⇒ ⛔ 別補發（同一則發兩次就是付兩次薪水）。</summary>
            public bool Unknown => ExitCode == 7 || ExitCode == -2;

            /// <summary>給回報用的一句話（三態分得出來）。</summary>
            public string Describe()
            {
                if (Posted && Queued) return "已排隊（酒館 Server 不在，起來後送出；還沒有 seq）—— ⛔ 不要補發";
                if (Posted) return Scheduled ? "已排程（alter 延後，到點由酒館 Server 發）" : $"已發（seq {Seq}）";
                if (Unknown) return $"**不知道有沒有發**（exit {ExitCode}）—— ⛔ 別補發，先 `senate cmd tavern-query --arg kind=tail` 回讀";
                return $"確定沒發（exit {ExitCode}）：{FirstLine(Output)}";
            }
        }

        /// <summary>有 persona 的發言（`tavern-post`）。⚠ 同步等 —— 主緒請用 <see cref="PostAsync"/>。</summary>
        public static Result Post(string iPersona, string iRoom, string iBody, string iMeta = null, string iRefs = null,
                                  int? iReplyTo = null)
        {
            if (string.IsNullOrWhiteSpace(iPersona))
                return Fail("沒有 persona —— 系統發言請走 PostSystem（⛔ 不把「忘了帶」當匿名）");
            var a = Common(iRoom, iBody, iMeta, iRefs, iReplyTo);
            a["persona"] = iPersona.Trim();
            return Run("tavern-post", a);
        }

        /// <summary>沒有 persona 的系統發言（`tavern-post-system`）。<paramref name="iSenderName"/> 空 ⇒ Senate 用帳戶顯示名。</summary>
        public static Result PostSystem(string iSender, string iSenderName, string iRoom, string iBody, string iMeta = null,
                                        string iRefs = null, int? iReplyTo = null)
        {
            if (string.IsNullOrWhiteSpace(iSender)) return Fail("系統發言要點名 sender（⛔ 不猜身分）");
            var a = Common(iRoom, iBody, iMeta, iRefs, iReplyTo);
            a["sender"] = iSender.Trim();
            if (!string.IsNullOrWhiteSpace(iSenderName)) a["sender_name"] = iSenderName.Trim();
            return Run("tavern-post-system", a);
        }

        public static UniTask<Result> PostAsync(string iPersona, string iRoom, string iBody, string iMeta = null,
                                                string iRefs = null, int? iReplyTo = null)
            => UniTask.RunOnThreadPool(() => Post(iPersona, iRoom, iBody, iMeta, iRefs, iReplyTo));

        public static UniTask<Result> PostSystemAsync(string iSender, string iSenderName, string iRoom, string iBody,
                                                      string iMeta = null, string iRefs = null, int? iReplyTo = null)
            => UniTask.RunOnThreadPool(() => PostSystem(iSender, iSenderName, iRoom, iBody, iMeta, iRefs, iReplyTo));

        static Dictionary<string, string> Common(string iRoom, string iBody, string iMeta, string iRefs, int? iReplyTo)
        {
            var a = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["room"] = string.IsNullOrWhiteSpace(iRoom) ? "tavern" : iRoom.Trim(),
                ["body"] = iBody ?? "",
            };
            if (!string.IsNullOrWhiteSpace(iMeta)) a["meta"] = iMeta;
            if (!string.IsNullOrWhiteSpace(iRefs)) a["refs"] = iRefs;
            if (iReplyTo.HasValue && iReplyTo.Value > 0) a["reply_to"] = iReplyTo.Value.ToString();
            return a;
        }

        static readonly Regex s_Seq = new Regex(@"🔢 post_seq = (\d+)");
        static readonly Regex s_Scheduled = new Regex(@"🔢 scheduled = 1");
        static readonly Regex s_Queued = new Regex(@"🔢 queued = 1");

        static Result Run(string iCmd, Dictionary<string, string> iArgs)
        {
            var (aExit, aOut) = UCL_PersonaProfileSenateBridge.RunCmd(iCmd, iArgs, OUTER_TIMEOUT_SEC, "target_data_root");
            var r = new Result { ExitCode = aExit, Output = aOut ?? "" };
            Match m = s_Seq.Match(r.Output);
            if (m.Success) int.TryParse(m.Groups[1].Value, out r.Seq);
            r.Scheduled = s_Scheduled.IsMatch(r.Output);
            r.Queued = s_Queued.IsMatch(r.Output);
            // ⚠ exit 0 卻沒有 seq、也不是延後排程或已排隊 ⇒ 沒有讀數，⛔ 不印成「已發」。
            if (r.ExitCode == 0 && r.Seq <= 0 && !r.Scheduled && !r.Queued) r.ExitCode = 7;
            return r;
        }

        static Result Fail(string iWhy) => new Result { ExitCode = 2, Output = iWhy };

        static string FirstLine(string iText)
        {
            foreach (string l in (iText ?? "").Split('\n'))
                if (l.Trim().StartsWith("✗") || l.Trim().StartsWith("⛔")) return l.Trim();
            foreach (string l in (iText ?? "").Split('\n'))
                if (l.Trim().Length > 0) return l.Trim();
            return "（沒有輸出）";
        }
    }
}
#endif
