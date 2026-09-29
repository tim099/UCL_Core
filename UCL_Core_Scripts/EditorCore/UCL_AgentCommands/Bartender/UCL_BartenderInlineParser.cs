// 區塊職責：解析 tavern 訊息 body 內的 inline bartender 指令 (跟 Cmd_Bartender 共用底層邏輯)
// 物理意義：使用者不必跑 Cmd_Bartender RPC, 直接在酒館發言寫 [進行時間規則] / [help] / [查詢餘額] block
//          → daemon 在 tick 內 parse + register time rule／回應, 對齊 CMD 行為.
//          [進行留言]（關鍵字 trigger）已於 2026-09-29 廢棄：只留偵測，讓 daemon 回一則明確的拒絕。
// 設計取捨：
//   - 用 marker prefix (中括號) 明確區分 registration vs 一般 chat
//   - key:value 解析寬鬆 — 支援 `=` / `:` / ` ` 三種 delimiter (對齊 Tim 自然語感)
//   - parse fail 不 throw — fail-safe log warning, 不擋 daemon 其他工作
// 對齊 Cmd_Bartender 的 args 命名: id / time / msg / room
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UCL.Core.EditorLib.AgentCommands.Bartender
{
    /// <summary>
    /// Tavern 訊息 body 內的 inline bartender 指令 parser.
    /// 支援的 marker (case-insensitive, 任一命中即視為 registration):
    ///   [進行留言] / [留言] / [leave message] / [bartender add]   → 已廢棄（只偵測、回拒絕）
    ///   [進行時間規則] / [時間規則] / [time rule] / [bartender time]  → time rule
    /// </summary>
    public static class UCL_BartenderInlineParser
    {
        // 區塊職責：marker 偵測 — case-insensitive substring 比對
        // 物理意義：marker 出現在 body 任何位置都算 (不必開頭), 寬鬆設計避免使用者抓不到
        static readonly string[] TriggerMarkers =
        {
            "[進行留言]", "[留言]", "[leave message]", "[bartender add]",
        };
        static readonly string[] TimeRuleMarkers =
        {
            "[進行時間規則]", "[時間規則]", "[time rule]", "[bartender time]",
        };
        // 區塊職責：餘額查詢 marker — 任一命中即視為「請酒保查帳」
        // 物理意義：daemon 偵測後走 C# 原生查詢（UCL_BartenderDaemon.RunBalanceQuery → UCL_TreasuryLedger）
        //          並把 markdown post 回 tavern；2026-08-17 起不再 spawn python
        // 數值影響：純 read-only 查詢，無 ledger 變動
        static readonly string[] BalanceQueryMarkers =
        {
            "[查詢餘額]", "[餘額]", "[查詢帳戶]", "[balance]", "[bartender balance]",
        };
        // 區塊職責：help marker — 任一命中即視為「請酒保列服務清單」
        // 物理意義：daemon 偵測後直接 post 一份 hardcoded markdown cheatsheet, 不 spawn 任何外部進程
        // 數值影響：純對 tavern 多一條酒保訊息, 無 state mutation
        static readonly string[] HelpMarkers =
        {
            "[help]", "[幫助]", "[酒保幫助]", "[酒館指令]", "[酒保服務]", "[?]", "[？]",
        };

        public enum InlineCommandKind { None, RetiredTrigger, AddTimeRule, BalanceQuery, Help }

        // 區塊職責：剝 markdown code span (單 backtick) 與 fenced code block (triple backtick)
        // 物理意義：QA bug fix (Zeta 報, 2026-05-13) — 使用者在 tavern 發說明文 / 教學 / share 時,
        //          常在 backtick 內 quote marker (e.g. ``[查詢餘額]``) 作為「範例」, 不該觸發 daemon.
        //          沒此 strip → 任何 task share / help 引用 marker 都假觸發新一輪 inline.
        // 設計取捨：只剝 backtick code (純文字 marker), 不剝 link / heading / quote — 那些不會誤觸發
        //          替換成 same-length 空白以維持 IndexOf 位置語意 (給未來「marker 出現在哪一行」分析用)
        // 數值影響：純字串轉換, 無 IO; O(n) regex pass
        static string StripCodeForMarkerScan(string body)
        {
            if (string.IsNullOrEmpty(body)) return body;
            // 先剝 triple-backtick fenced block (greedy 跨行)
            body = Regex.Replace(body, @"```[\s\S]*?```", m => new string(' ', m.Length));
            // 再剝 single-backtick inline code (不允許跨行)
            body = Regex.Replace(body, @"`[^`\r\n]*`", m => new string(' ', m.Length));
            return body;
        }

        /// <summary>偵測 body 內是否含 bartender 控制 marker, 回 kind 列舉 (None = 無).
        /// QA fix: 先剝 markdown code spans, 防止 quoted-example 假觸發.</summary>
        public static InlineCommandKind DetectKind(string body)
        {
            if (string.IsNullOrEmpty(body)) return InlineCommandKind.None;
            // QA bug fix (Zeta 2026-05-13): 剝 code spans 後再 scan, 避免說明文 quote marker 假觸發
            string scanBody = StripCodeForMarkerScan(body);
            foreach (var m in TriggerMarkers)
                if (scanBody.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)
                    return InlineCommandKind.RetiredTrigger;
            foreach (var m in TimeRuleMarkers)
                if (scanBody.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)
                    return InlineCommandKind.AddTimeRule;
            foreach (var m in BalanceQueryMarkers)
                if (scanBody.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)
                    return InlineCommandKind.BalanceQuery;
            foreach (var m in HelpMarkers)
                if (scanBody.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)
                    return InlineCommandKind.Help;
            return InlineCommandKind.None;
        }

        /// <summary>BalanceQuery 解析結果 — daemon spawn python 用.</summary>
        public class BalanceQuerySpec
        {
            // account 空 → daemon 端 fallback 用 msg.sender_id (查自己)
            public string account;
            public int limit = 10;
            public bool valid = true;     // marker 命中即視為合法，account 可空 (daemon 補)
            public string error;
        }

        /// <summary>從 body 解析 BalanceQuery spec. body 該含 [查詢餘額] 或同義 marker.</summary>
        public static BalanceQuerySpec ParseBalanceQuery(string body)
        {
            var spec = new BalanceQuerySpec();
            if (string.IsNullOrEmpty(body)) { spec.valid = false; spec.error = "empty body"; return spec; }

            // 用既有 StripMarker / ExtractValue helper — 對齊 trigger / time rule 解析慣例
            string content = StripMarker(body, BalanceQueryMarkers);

            string accountRaw = ExtractValue(content, new[] { "account", "帳戶", "帳號", "acct" });
            if (!string.IsNullOrWhiteSpace(accountRaw)) spec.account = accountRaw.Trim();

            string limitRaw = ExtractValue(content, new[] { "limit", "筆數", "近期" });
            if (!string.IsNullOrWhiteSpace(limitRaw) && int.TryParse(limitRaw.Trim(), out int lim))
            {
                if (lim < 0) lim = 0;
                if (lim > 100) lim = 100;
                spec.limit = lim;
            }
            return spec;
        }

        // ===========================================================
        // 解析 — key:value pair
        // ===========================================================

        // 區塊職責：寬鬆 key-value 解析 — 對齊 Cmd_Bartender args 命名
        // 物理意義：每個 known key 有對應 regex, capture value (到 next known key 或 EOL)
        //          msg 特例: 不止到 next key, 而是整個剩餘 (用戶可能 msg 內含逗號 / 換行)
        // 數值影響: 純 string parse, 無 IO 副作用

        /// <summary>TimeRule 解析結果.</summary>
        public class TimeRuleSpec
        {
            public string id;
            public string time_hhmm;
            public string msg;
            public string room = "tavern";
            public bool valid;
            public string error;
        }

        // ===========================================================
        // Public API — Parse time rule body
        // ===========================================================

        /// <summary>從 body 解析 time rule spec. body 該含 [進行時間規則] 或同義 marker.</summary>
        public static TimeRuleSpec ParseTimeRule(string body)
        {
            var spec = new TimeRuleSpec();
            if (string.IsNullOrEmpty(body)) { spec.error = "empty body"; return spec; }

            string content = StripMarker(body, TimeRuleMarkers);

            string msg = ExtractValue(content, new[] { "msg", "message", "提醒", "內容" }, greedy: true);
            spec.msg = StripAutoAttachedBlocks((msg ?? "").Trim());
            string remaining = string.IsNullOrEmpty(msg) ? content : RemoveValueSegment(content, new[] { "msg", "message", "提醒", "內容" });

            spec.id = (ExtractValue(remaining, new[] { "id", "規則id" }) ?? "").Trim();
            spec.time_hhmm = (ExtractValue(remaining, new[] { "time", "時間" }) ?? "").Trim();
            spec.room = (ExtractValue(remaining, new[] { "room", "房間" }) ?? "tavern").Trim();




            if (string.IsNullOrEmpty(spec.id)) { spec.error = "缺 id (規則 id)"; return spec; }
            if (string.IsNullOrEmpty(spec.time_hhmm)) { spec.error = "缺 time (HH:mm)"; return spec; }
            if (string.IsNullOrEmpty(spec.msg)) { spec.error = "缺 msg (提醒內容)"; return spec; }
            spec.valid = true;
            return spec;
        }

        // ===========================================================
        // 區塊：剝掉 tavern post 末段 auto-attached 區塊 (Cmd_Glossary 等自動拼接)
        // 物理意義：tavern AppendMessage 後 Cmd_Glossary 會 append 新詞區塊 (--- + 📖 本回提到的新詞 + cite list)
        //          這段不該被當 inline register 的 msg 值, 必須剝掉
        // 數值影響: 同步 trim trailing whitespace; 找不到 marker 不動原字串
        // ===========================================================
        static readonly string[] AutoAttachedBlockMarkers =
        {
            "📖 **本回提到的新詞**",  // Cmd_Glossary auto-attach 開頭
            "📖 本回提到的新詞",      // 無粗體
            "\n---\n\n📖",            // 加上分隔線的形式
            "\r\n---\r\n\r\n📖",
        };

        // ⚠ 2026-08-19 改為 public：第二個消費端是酒館 CLI（UCL_BartenderCliService）。
        //   🩸 為什麼需要：`cmd msg kiara <訊息>` 這句話因為提到了 persona 名，
        //   Cmd_Glossary 在發文後把整段新詞區塊附到訊息尾端 —— 於是那段變成指令的一部分，
        //   而群發會把**整本詞典打進對方的輸入框並按 Enter**。
        //   剝除器只留一份（本函式），不在 CLI 端再造一套 —— 兩套 marker 清單必漂，
        //   而漂掉的那天是「有人收到一段莫名的詞典」。
        public static string StripAutoAttachedBlocks(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            foreach (var marker in AutoAttachedBlockMarkers)
            {
                int idx = s.IndexOf(marker, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    s = s.Substring(0, idx);
                    break;  // 第一個命中就停
                }
            }
            // 順便剝末尾的 separator line "---" + 多餘空白
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[\s\r\n]*---[\s\r\n]*$", "");
            return s.Trim();
        }

        // ===========================================================
        // 解析 helper
        // ===========================================================

        static string StripMarker(string body, string[] markers)
        {
            foreach (var m in markers)
            {
                int idx = body.IndexOf(m, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                    return body.Substring(idx + m.Length);
            }
            return body;
        }

        // 區塊職責：對單一 key 抓 value — 支援 `key=val` / `key: val` / `key val` 三種 delimiter
        //          greedy=true 時抓到 EOL 整段 (給 msg 用); greedy=false 抓到 next known key marker 或 EOL
        // 物理意義: regex 編出 key 後面允許 0+ 空白 / : / =, 然後 capture 直到 next-key-or-EOL
        // 數值影響: case-insensitive multiline; 抓不到回 null
        static readonly string[] AllKeyAliases =
        {
            "msg", "message", "訊息", "內容", "提醒",
            "key", "keyword", "關鍵字", "key word",
            "targets", "target", "目標", "對象",
            "tokens", "token",
            "room", "房間",
            "id", "規則id",
            "time", "時間",
            "account", "帳戶", "帳號", "acct",
            "limit", "筆數", "近期",
        };

        static string ExtractValue(string content, string[] keyAliases, bool greedy = false)
        {
            if (string.IsNullOrEmpty(content)) return null;
            foreach (var key in keyAliases)
            {
                string pattern;
                if (greedy)
                {
                    // greedy: 抓 key 後到 string 結尾的所有內容 (msg 用, 可含逗號 / 換行)
                    pattern = $@"(?:^|[\s,;])\s*{Regex.Escape(key)}\s*[:=]?\s*(.+)$";
                }
                else
                {
                    // non-greedy: 抓 key 後到 next known key 或 string 結尾
                    // 用 lookahead 偵測 next key boundary
                    string nextKeyAlt = string.Join("|", System.Linq.Enumerable.Select(AllKeyAliases, k => Regex.Escape(k)));
                    pattern = $@"(?:^|[\s,;])\s*{Regex.Escape(key)}\s*[:=]?\s*([^,;\n\r]*?)(?=(?:[\s,;]+(?:{nextKeyAlt})\s*[:=\s])|[\n\r]|$)";
                }
                var m = Regex.Match(content, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (m.Success && m.Groups.Count > 1)
                {
                    string val = m.Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
            }
            return null;
        }

        // 區塊職責：從 content 摳掉指定 key 的 (key, value) 整段, 給後續解析其他 key 用
        // 物理意義: 避免 msg 內的「逗號 + 看起來像 key 的字」干擾其他 key 解析
        static string RemoveValueSegment(string content, string[] keyAliases)
        {
            foreach (var key in keyAliases)
            {
                string pattern = $@"(?:^|[\s,;])\s*{Regex.Escape(key)}\s*[:=]?\s*.+$";
                content = Regex.Replace(content, pattern, "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }
            return content;
        }
    }
}
#endif
