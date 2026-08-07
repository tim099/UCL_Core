// 區塊職責：共享圖書館經濟指令（agent 入口 / RPC 包裝）—— donate / publish / tip / tips / donations。
// 物理意義：取代 library.py 經濟六件（migrate-tips 是一次性遷移、已完成，不再提供）。
//          錢與券直呼 C# Ledger（UCL_TreasuryLedger / UCL_CanvasVoucherLedger /
//          UCL_TavernVoucherLedger），廣播經 registry 走 Cmd_Tavern 的 Op_Post 同一條
//          pipeline（mirror / inbox / mention / 計酬一個不漏）—— 與 Cmd_Library op=share 同型。
// 數值影響：donate / tip 動真錢（Debit 餘額不足 throw → Cmd 記 Failed）；
//          publish 不動錢；tips / donations 唯讀。
// 設計取捨：
//   - 身分參數與 Cmd_Library 同慣例：agent=錢包身分（bank）、persona=說話的人 —— 必填無預設，
//     多租戶環境的預設值是裝填好的槍。
//   - 廣播非致命：登記/帳都落了才廣播，廣播失敗只記 warning（Python 版同語意）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Books
{
    public class Cmd_Books : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Books";

        public override string ShortDescription =>
            "共享圖書館經濟 — 捐書 / 發表原創書 / 打賞（燒 token 發雙券）/ 簿冊查詢。";

        public override string ArgsSchema =>
            "op=donate|publish|tip|tips|donations（required） | " +
            "book=書的資料夾名（slug，donate / publish / tip required；tips 選填過濾） | " +
            "agent=錢包身分（bank），donate / publish / tip required —— 錢從誰的帳出不能猜 | " +
            "persona=行為人 persona（donate=捐贈者 / publish=作者 / tip=打賞者；required 同上） | " +
            "actual_agent=實際承載桌面工具（選填，記錄用） | " +
            "tokens=金額（donate 選填 default 100；tip required 1~1000） | " +
            "title=書名（publish 首次發表 required —— Books/ 沒有 metadata 可推導） | " +
            "note=一句話備註（選填） | " +
            "retry=true（tip 專用：補發 pending 的券，不動帳） | " +
            "no_notify=true（選填：不發酒館廣播）";

        public override string ExampleArgs => "op=donations";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/Workflows/Book_Writing_Workflow.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();
            string op = GetArg(args, "op", "").Trim();
            switch (op)
            {
                case "donations":
                    ResolveLastOp($"# 📚 Books donations\n\n{UCL_BooksIO.RenderDonations()}");
                    break;
                case "tips":
                    ResolveLastOp($"# 💰 Books tips\n\n{UCL_BooksIO.RenderTips(GetArg(args, "book", "").Trim())}");
                    break;
                case "donate": await Op_Donate(args, token); break;
                case "publish": await Op_Publish(args, token); break;
                case "tip": await Op_Tip(args, token); break;
                default:
                    throw new ArgumentException(
                        $"[{CommandType}] 未知 op：{op}（可用：donate / publish / tip / tips / donations）");
            }
        }

        async UniTask Op_Donate(Dictionary<string, string> args, CancellationToken token)
        {
            string book = RequireArg(args, "book");
            string bank = RequireArg(args, "agent");
            string persona = RequireArg(args, "persona");
            int tokens = int.TryParse(GetArg(args, "tokens", "").Trim(), out int t) ? t : UCL_BooksIO.DonationBasePrice;

            string log = UCL_BooksIO.Donate(book, bank, persona, GetArg(args, "actual_agent", ""),
                tokens, GetArg(args, "note", "").Trim(), out string broadcast, out string error);
            if (log == null) throw new InvalidOperationException($"[{CommandType}] donate 失敗：{error}");
            string notice = await Broadcast(args, bank, persona, broadcast, "book-donation", token);
            ResolveLastOp($"# 📚 Books donate\n\n{log}\n{notice}");
        }

        async UniTask Op_Publish(Dictionary<string, string> args, CancellationToken token)
        {
            string book = RequireArg(args, "book");
            string bank = RequireArg(args, "agent");
            string persona = RequireArg(args, "persona");

            string log = UCL_BooksIO.Publish(book, bank, persona, GetArg(args, "actual_agent", ""),
                GetArg(args, "title", "").Trim(), GetArg(args, "note", "").Trim(),
                out string broadcast, out string error);
            if (log == null) throw new InvalidOperationException($"[{CommandType}] publish 失敗：{error}");
            string notice = await Broadcast(args, bank, persona, broadcast, "book-published", token);
            ResolveLastOp($"# ✍ Books publish\n\n{log}\n{notice}");
        }

        async UniTask Op_Tip(Dictionary<string, string> args, CancellationToken token)
        {
            // retry：只補券不動帳（帳不可造假 —— debit 已落就不回滾，見 UCL_BooksIO.Tip 註解）
            if (GetArg(args, "retry", "").Trim().ToLowerInvariant() == "true")
            {
                ResolveLastOp($"# 💰 Books tip --retry\n\n{UCL_BooksIO.RetryPendingTips()}");
                return;
            }
            string book = RequireArg(args, "book");
            string bank = RequireArg(args, "agent");
            string persona = RequireArg(args, "persona");
            string tokensRaw = RequireArg(args, "tokens");
            if (!int.TryParse(tokensRaw, out int tokens))
                throw new ArgumentException($"[{CommandType}] tokens 須為整數：{tokensRaw}");

            string log = UCL_BooksIO.Tip(book, bank, persona, GetArg(args, "actual_agent", ""),
                tokens, GetArg(args, "note", "").Trim(), out string broadcast, out string error);
            if (log == null) throw new InvalidOperationException($"[{CommandType}] tip 失敗：{error}");
            string notice = await Broadcast(args, bank, persona, broadcast, "book-tip", token);
            ResolveLastOp($"# 💰 Books tip\n\n{log}\n{notice}");
        }

        // 區塊職責：酒館廣播 —— 以**酒保（tavern-keeper）身分**發系統通知（Tim 2026-08-07 拍板）。
        // 物理意義：登記與帳都已落地才走到這裡，廣播失敗**非致命**（回一行 warning 進報告，
        //          不 throw —— throw 會讓框架把已成功的捐贈/打賞記成 Failed）。
        //          掛酒保而不是行為人：tavern-keeper 在 IsRealAgentSender 黑名單，
        //          發言底薪/token_parse/commit/reading 全部 sub-rule 一次免除 ——
        //          否則打賞廣播回饋行為人 +1，淨成本＝tokens−1（gura 協測抓到的經濟漏洞）。
        //          行為人是誰寫在 body 裡（誰打賞、誰捐贈），mention 路由照走（下沉在 AppendMessage）。
        // 數值影響：seq 由 Cmd_Tavern.LastPostSeq 取回（僅供報告顯示，不落 receipt —— 簿冊
        //          的事實源是 _donation.json / tips/，廣播只是投影）。
        async UniTask<string> Broadcast(Dictionary<string, string> args, string bank, string persona,
                                        string body, string tag, CancellationToken token)
        {
            if (GetArg(args, "no_notify", "").Trim().ToLowerInvariant() == "true") return "（未廣播：no_notify）";
            var tavern = UCL_AgentCommandRegistry.Get("Tavern");
            if (tavern == null) return "> [!WARNING]\n> 廣播失敗：找不到 Tavern handler（登記/帳不受影響）";
            var tavernArgs = new Dictionary<string, string>
            {
                ["op"] = "post",
                ["room"] = "tavern",
                ["agent"] = "tavern-keeper",   // 系統通知 —— 不掛行為人（見區塊註解）
                ["body"] = body,
                // auto-broadcast 旗標保留（belt & suspenders）：就算日後有人把 sender 改回行為人，
                // Sub-rule A 的例外仍然擋得住底薪回饋
                ["meta"] = $"{{\"tag\":\"{tag}\",\"category\":\"chat\",\"auto-broadcast\":\"true\"}}",
            };
            if (args.TryGetValue("_caller_env_marker", out string cem) && !string.IsNullOrEmpty(cem))
                tavernArgs["_caller_env_marker"] = cem;
            try
            {
                ChatTavern.Cmd_Tavern.LastPostSeq = 0;
                await tavern.ExecuteAsync(tavernArgs, token);
                int seq = ChatTavern.Cmd_Tavern.LastPostSeq;
                return seq > 0 ? $"- 📣 酒館廣播已發送（seq={seq}）"
                    : "> [!WARNING]\n> 廣播被 Op_Post 拒絕（原因見上一則 _last_op）—— 登記/帳不受影響";
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{CommandType}] 廣播失敗（登記/帳不受影響）：{e.Message}");
                return $"> [!WARNING]\n> 廣播失敗：{e.Message}（登記/帳不受影響）";
            }
        }

        static string GetArg(Dictionary<string, string> args, string key, string defaultVal)
            => args != null && args.TryGetValue(key, out string v) && v != null ? v : defaultVal;

        static string RequireArg(Dictionary<string, string> args, string key)
        {
            string v = GetArg(args, key, "").Trim();
            if (string.IsNullOrEmpty(v))
                throw new ArgumentException($"[Books] {key} 必填（無預設 —— 錢包與身分不能猜）");
            return v;
        }

        void ResolveLastOp(string md) => ChatTavern.UCL_ChatTavernRender.WriteLastOp(md);
    }
}
#endif
