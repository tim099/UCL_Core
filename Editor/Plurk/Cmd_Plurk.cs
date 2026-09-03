// 區塊職責：Plurk 的 Cmd 入口 —— resolve / lint / preview / post。
// 物理意義：**發文的唯一寫入端**（Tim 2026-08-21：「這部分可以走 c# CMD」）。
//          為什麼是 C# 而不是 python：
//            ① 規則要長在必經路上 —— lint 若住在另一個語言的另一支工具裡，發文那條路繞得過它，
//               而繞過去不會報錯。這裡 `post` **強制先跑 lint**，errors 非空就不送。
//            ② 帳號解析同源 —— 直接呼叫 `UCL_PlurkAccounts.Resolve`，
//               不必再維護一份 python 鏡像（兩份遲早各說各話，且兩邊都不報錯）。
//            ③ 一筆一份 verdict —— Cmd 的 `_cmd_results/<id>.json` 與 queue 路由本來就在，
//               對「不可回復的對外動作」那是免費的審計。
// 數值影響：`resolve` / `lint` / `preview` 零副作用、不連網。
//          `post` **預設 dry-run**：沒有 `confirm=1` 一律只印 payload 不送。
//          真送時寫一筆 audit jsonl（時間／persona／帳號／source／內容雜湊／回傳 plurk id）。
//
// ⛔ 發布不可回復，而 Plurk 沒有 history ⇒ 這支永遠不自動發：`confirm=1` 是人打的。
// ⚠ 端點與參數的**驗證狀態**：事實來源在 `Docs~/{lang}/Workflows/Plurk_Maintenance.md` §5
//   （別在這裡另記一份 —— 兩份清單必漂，而漂掉的那份看起來一樣可信）。
//   ⇒ `preview` 印出**完整將送內容**，讓人在送之前用眼睛驗一次。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands;
using UnityEngine;

namespace UCL.Core.EditorLib.Plurk
{
    public class Cmd_Plurk : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Plurk";

        public override string ShortDescription =>
            "Plurk 共用帳號流程：resolve 查帳號 / lint 驗交付單 / preview 組 payload 不送 / post 發文（需 confirm=1）"
            + " / timeline·responses·friends 看別人在說什麼（唯讀）/ mentions 誰 @ 了我＋我回了沒（唯讀，優先處理）/ like·unlike 互動（需 confirm=1）"
            + " / 擴圈：profile·expand·search·alerts 唯讀，befriend·unfriend·follow·unfollow·accept·deny 需 confirm=1"
            + " / 表情：emoticons 讀表並維護本地描述表（唯讀＋寫本地），emoadd 試新增自訂表情（需 confirm=1）。";

        public override string ArgsSchema =>
            "op=resolve|lint|preview|upload|post|get|whoami|timeline|responses|mentions|friends|like|unlike"
            + "|emoticons|emoadd"
            + "|profile|expand|search|alerts|befriend|unfriend|follow|unfollow|accept|deny（預設 resolve） | "
            + "emo_desc=<編號=描述,編號=描述…>（emoticons 選填：把描述寫進本地表，merge 不覆寫） | "
            + "url=<圖檔網址>（emoadd 必填） | alias=<表情代碼>（emoadd 必填） | "
            + "user_id=<誰>（profile/befriend/unfriend/follow/unfollow/accept/deny 必填；friends 選填） | "
            + "query=<關鍵字>（search 必填） | kind=plurk|user（search 選填，預設 plurk） | "
            + "top=<列前幾名>（expand 選填，預設 15） | hops=<向外問幾位好友>（expand 選填，預設 8） | "
            + "history=1（alerts 選填：看歷史而不是待處理） | "
            + "limit=<筆數>（timeline·mentions 預設 20／friends·expand 預設 30/100；夾在 1-100） | "
            + "preview=<摘要字數>（timeline 預設 90，夾在 20-400） | "
            + "filter=only_user|only_responded|only_private|only_favorite（timeline 選填） | "
            + "user_id=<誰的好友>（friends 選填；不給就問 /APP/Users/me） | "
            + "offset=<第幾筆起>（friends 選填） | from_response=<第幾則回應起>（responses 選填） | "
            + "cache=1（**唯讀 op 才有意義**：改讀本地快取而不是現抓；回傳檔會標來源與年齡） | "
            + "plurk_id=<已發出的噗 id>（op=get 用 —— 唯讀回讀驗「它真的在那裡」） | "
            + "image=<圖片絕對路徑>（op=upload 用；⛔ 不吃相對路徑） | " +
            "persona=<誰要發，lint/preview/post 建議給 —— 決定用共用還是個人帳號> | " +
            "slip_file=<交付單檔案路徑>（lint/preview/post 必填；**五欄**格式見 Plurk_Posting_Workflow §2） | " +
            "confirm=1（**只有 post 需要**；沒帶＝dry-run 只印不送） | " +
            "reply_to=<plurk id>（把這則發成該噗的回應 —— 長文拆則的預設形態）";

        public override string ExampleArgs =>
            "op=preview;persona=basecamp;slip_file=D:/tmp/slip.txt";

        public override string HelpURL => "ucl_core:Docs~/{lang}/Workflows/Plurk_Posting_Workflow.md";

        const string ApiBase = "https://www.plurk.com";
        // plurk.com 在 Cloudflare 後面：預設 .NET/urllib UA 會被 WAF 依瀏覽器簽章擋掉，
        // 回 **403 ＋ body `error code: 1010`**（Cloudflare 的碼，不是 Plurk API 的錯誤格式）。
        // 🩸 basecamp 2026-08-21：那個 403 跟「簽章錯」「端點不存在」長得一樣，而它連應用層都沒碰到。
        const string UserAgent = "UCL-PlurkBot/0.1 (+https://github.com/Persona9999)";
        const string AuditRelative = "Plurk/post_audit.jsonl";
        // 上傳端點與欄位名取自社群慣例（官方 API 頁抓不到）——「驗證狀態」見 Plurk_Maintenance §5
        const string UploadEndpoint = "/APP/Timeline/uploadPicture";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string aOp = GetArg(args, "op", "resolve").Trim().ToLowerInvariant();
            string aPersona = GetArg(args, "persona", "").Trim();
            var aR = new StringBuilder();
            aR.AppendLine($"# Plurk op={aOp} persona={(aPersona.Length == 0 ? "(未給)" : aPersona)}"
                + $"  ts=`{DateTime.Now:yyyy-MM-dd HH:mm:sszzz}`（本地時間）");
            aR.AppendLine();

            var aRes = UCL_PlurkAccounts.Resolve(aPersona);
            aR.AppendLine($"- 帳號: **{(string.IsNullOrEmpty(aRes.SecretId) ? "(無)" : aRes.SecretId)}**"
                + $" / source: `{aRes.Source}` / 署名必填: {(aRes.RequiresSignature ? "是" : "否")}");
            aR.AppendLine($"- 說明: {aRes.Describe()}");

            // ===========================================================
            // 區塊職責：不論成功或失敗，**回傳檔都要寫出來**
            // 🩸 2026-08-21：`op=lint` 擋下時我直接 throw，而寫檔在 switch 之後 ⇒
            //   錯誤訊息說「詳見回傳檔」，**而那個回傳檔從來沒被寫出來**。
            //   指路牌指向一個不存在的東西 —— 而 Cmd 本身「正確地失敗了」，所以沒有任何一層會喊。
            // ⇒ 判準：報告是**診斷**，失敗的時候比成功的時候更需要它。
            //   所以 try/finally：先落檔，再把例外丟出去。
            // ===========================================================
            string aPath = UCL_LettersPath.CmdPayload(
                string.IsNullOrEmpty(aPersona) ? "basecamp" : aPersona, "plurk", aOp);
            try
            {
                switch (aOp)
                {
                    case "resolve": OpResolve(aRes, aR); break;
                    case "whoami": await OpWhoAmI(aRes, aR, token); break;
                    case "lint": OpLint(args, aRes, aR); break;
                    case "preview": OpPreview(args, aRes, aR, out _); break;
                    case "upload": await OpUpload(args, aRes, aR, token); break;
                    case "get": await OpGet(args, aRes, aR, token); break;
                    case "post": await OpPost(args, aRes, aR, token); break;
                    // ── 社交面（讀）──
                    case "timeline": await OpTimeline(args, aRes, aR, token); break;
                    case "responses": await OpResponses(args, aRes, aR, token); break;
                    case "mentions": await OpMentions(args, aRes, aR, token); break;
                    case "friends": await OpFriends(args, aRes, aR, token); break;
                    // ── 社交面（寫，對別人動手 ⇒ 要 confirm=1）──
                    case "like": await OpFavorite(args, aRes, aR, token, true); break;
                    case "unlike": await OpFavorite(args, aRes, aR, token, false); break;
                    // ── 擴圈（讀）──
                    case "emoticons": await OpEmoticons(args, aRes, aR, token); break;
                    case "emoadd": await OpEmoAdd(args, aRes, aR, token); break;
                    case "profile": await OpProfile(args, aRes, aR, token); break;
                    case "expand": await OpExpand(args, aRes, aR, token); break;
                    case "search": await OpSearch(args, aRes, aR, token); break;
                    case "alerts": await OpAlerts(args, aRes, aR, token); break;
                    // ── 擴圈（寫，改的是關係 ⇒ 要 confirm=1）──
                    case "befriend": await OpRelation(args, aRes, aR, token, "befriend"); break;
                    case "unfriend": await OpRelation(args, aRes, aR, token, "unfriend"); break;
                    case "follow": await OpRelation(args, aRes, aR, token, "follow"); break;
                    case "unfollow": await OpRelation(args, aRes, aR, token, "unfollow"); break;
                    case "accept": await OpRelation(args, aRes, aR, token, "accept"); break;
                    case "deny": await OpRelation(args, aRes, aR, token, "deny"); break;
                    default:
                        throw new Exception($"[Plurk] 認不得的 op='{aOp}'"
                            + "（resolve|whoami|lint|preview|upload|post|get|timeline|responses|friends|like|unlike"
                            + "|profile|expand|search|alerts|befriend|unfriend|follow|unfollow|accept|deny）");
                }
            }
            finally
            {
                UCL_LettersPath.EnsurePayloadDir(aPath);
                File.WriteAllText(aPath, aR.ToString(), new UTF8Encoding(false));
                UCL_AgentCommandRunner.ReportOutputFile(args, aPath);
                Debug.Log($"[Plurk] op={aOp} → {aPath}");
            }
        }

        // ===========================================================
        // 區塊職責：帳號與憑證狀態
        // 數值影響：只讀存在性與欄位到齊；⛔ 憑證值一律不印（外洩沒有錯誤訊息）。
        // ===========================================================
        void OpResolve(UCL_PlurkAccountResolution iRes, StringBuilder ioR)
        {
            if (string.IsNullOrEmpty(iRes.SecretId))
            {
                ioR.AppendLine();
                ioR.AppendLine("⛔ 帳號未設定 ⇒ 不能發文。先在 UCL_PlurkAdminPage 設共用帳號或個人 override。");
                return;
            }
            string aEnc = SecretPath(iRes.SecretId, ".enc");
            string aTxt = SecretPath(iRes.SecretId, ".txt");
            ioR.AppendLine();
            // `.enc 有` 與 `明文已安裝` 分開報 —— 合成一個綠燈的話，只有密文的機器看起來也像好了
            ioR.AppendLine($"- `.enc`: {(File.Exists(aEnc) ? "有" : "無")}　`{aEnc}`");
            ioR.AppendLine($"- 明文已安裝: {(File.Exists(aTxt) ? "**有**" : "無")}　`{aTxt}`");
            if (!File.Exists(aTxt)) return;
            var aCred = LoadCredentials(iRes.SecretId, out string aWhy);
            ioR.AppendLine(aCred == null
                ? $"- ⚠ 憑證不完整：{aWhy}"
                : "- 憑證: 四欄到齊（長度 "
                  + string.Join(" / ", CredFields.Select(k => $"{k}={aCred[k].Length}"))
                  + "）　⛔ 值不印");
        }

        async UniTask OpWhoAmI(UCL_PlurkAccountResolution iRes, StringBuilder ioR, CancellationToken token)
        {
            var aCred = RequireCredentials(iRes);
            var (aStatus, aBody) = await CallAsync("/APP/Users/me", aCred, null, token);
            ioR.AppendLine();
            ioR.AppendLine($"## whoami（唯讀，不寫入任何 Plurk 資料）");
            ioR.AppendLine($"- http: **{aStatus}**");
            // 只挑身分欄位；整包回應含 email／生日等個資，不無條件倒進回傳檔
            foreach (var aKey in new[] { "id", "nick_name", "display_name", "full_name", "karma" })
            {
                string v = PickJsonValue(aBody, aKey);
                if (v != null) ioR.AppendLine($"- {aKey}: {v}");
            }

            // 區塊職責：把這個帳號的 nick 寫回 registry —— 發文端 `@persona` 轉換的唯一對照來源。
            // 物理意義：**nick 只有帳號自己問得到**（`/APP/Users/me` 走的是這份憑證）。
            //          ⇒ 每個帳號的持有者跑一次 whoami，表就自己長出來；沒人手打，就不會漂。
            // 數值影響：只在 http 200 且真的讀到 nick 時寫。⚠ 寫入要出聲 ——
            //          「唯讀 op 偷偷寫了本地檔」是我們自己抓過的那一族。
            if (aStatus == 200)
            {
                string aNick = (PickJsonValue(aBody, "nick_name") ?? "").Trim().Trim('"');
                if (!string.IsNullOrEmpty(aNick) && !string.IsNullOrEmpty(iRes.SecretId))
                {
                    string aOld = UCL_PlurkAccounts.NickOf(iRes.SecretId);
                    UCL_PlurkAccounts.SetNick(iRes.SecretId, aNick);
                    ioR.AppendLine(aOld == aNick
                        ? $"- 📝 nick 登記表：`{iRes.SecretId}` = `{aNick}`（已是這個值，重新蓋時間戳）"
                        : $"- 📝 **已寫入 nick 登記表**：`{iRes.SecretId}` = `{aNick}`"
                          + (string.IsNullOrEmpty(aOld) ? "（原本沒登記）" : $"（原本是 `{aOld}`）")
                          + " —— 發文端 `@persona` 轉換讀的就是這張表");
                }
            }
            if (aStatus != 200)
            {
                ioR.AppendLine($"- ✗ body（前 300 字）: {Trunc(aBody, 300)}");
                throw new Exception($"[Plurk] whoami 失敗 http={aStatus} —— "
                    + "判準：先確認端點存在，再懷疑簽章，最後才是 WAF。三者的失敗都是 4xx。");
            }
        }

        // ===========================================================
        // 區塊職責：lint（形式檢查）—— 規則本體在 UCL_PlurkLint
        // 數值影響：errors 非空 ⇒ **throw**（Cmd 失敗），因為 lint 的存在意義就是擋下。
        // ===========================================================
        void OpLint(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes, StringBuilder ioR)
        {
            var aSlip = LoadSlip(iArgs);
            WriteLintSection(aSlip, iRes, ioR, out var aErrors);
            if (aErrors.Count > 0)
                throw new Exception($"[Plurk] lint 擋下 {aErrors.Count} 個錯誤（詳見回傳檔）");
        }

        void WriteLintSection(UCL_PlurkSlip iSlip, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, out List<string> oErrors)
        {
            var (aTotal, aEmoLen, aEmoCount) = UCL_PlurkLint.Budget(iSlip.Body);
            var (aErr, aWarn) = UCL_PlurkLint.Check(iSlip, iRes.RequiresSignature);
            // 🩸 轉換不掉的**不在這裡補** —— `Check` 的 ⑦ 已經對同一段文案問過一次
            //   （轉換不掉的 `@gura` 還留在 body 裡，所以它一定會被 ⑦ 命中）。
            //   首版兩邊都加 ⇒ 同一條錯誤印兩次。實跑抓到的，不是讀 code 想到的。
            oErrors = aErr;
            ioR.AppendLine();
            ioR.AppendLine("## lint（形式檢查）");
            if (iSlip.MentionNotes != null && iSlip.MentionNotes.Count > 0)
            {
                ioR.AppendLine($"- ✍ **`@persona` 已自動轉換 {iSlip.MentionNotes.Count} 處**"
                    + "（Plurk 的 @ 只認 nick；persona 名會連到同名的第三方帳號）：");
                foreach (var n in iSlip.MentionNotes) ioR.AppendLine($"    · {n}");
                ioR.AppendLine("  ⚠ 下面的字元預算算的是**轉換後**的文案 —— 轉換會變長。");
            }
            ioR.AppendLine($"- 預算: **{aTotal}** 字元　上限 {UCL_PlurkLint.Allowed(iSlip)}"
                + $"（{UCL_PlurkLint.Limit}{(iSlip.HasImage ? " − 附圖保留 " + UCL_PlurkLint.ImageReserve : "")}）"
                + $"；[emoN] {aEmoLen} 字元 × {aEmoCount} 個");
            ioR.AppendLine($"- 公開度: {(string.IsNullOrWhiteSpace(iSlip.Privacy) ? "**(未指定)**" : iSlip.Privacy)}"
                + $"　心情詞: {(string.IsNullOrWhiteSpace(iSlip.Qualifier) ? "(未指定→ says)" : iSlip.Qualifier)}");
            foreach (var w in aWarn) ioR.AppendLine($"- ⚠ {w}");
            foreach (var e in aErr) ioR.AppendLine($"- ✗ {e}");
            ioR.AppendLine();
            ioR.AppendLine(UCL_PlurkLint.Disclaimer);
        }

        // ===========================================================
        // 區塊職責：preview —— 組出**完整將送內容**但不送
        // 物理意義：端點參數名未對照官方文件 ⇒ 讓人在送之前用眼睛驗一次它到底要送什麼。
        // 數值影響：零副作用。回傳 payload 供 post 重用（同一份，不重組 —— 重組就會漂）。
        // ===========================================================
        /// <param name="iWillSend">
        /// 這一趟**接下來會不會真的送**（`op=post` ＋ `confirm=1`）。
        /// 🩸 BUG-28：本段被 `post` 重用，而標題硬寫「本 op 不送」⇒
        /// 真發出去的那一份回傳檔，開頭寫「不送」、下面寫「已送出」。
        /// 兩句都在同一個檔裡自相矛盾，而**先讀到的是錯的那句**。
        /// ⇒ 共用渲染段不可以宣告呼叫端的行為，那件事只有呼叫端知道。
        /// </param>
        void OpPreview(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, out Dictionary<string, string> oPayload, bool iWillSend = false)
        {
            var aSlip = LoadSlip(iArgs);
            WriteLintSection(aSlip, iRes, ioR, out var aErrors);
            oPayload = BuildPayload(aSlip, iArgs);
            ioR.AppendLine();
            ioR.AppendLine(iWillSend
                ? "## 將送的 payload（**帶了 `confirm=1` ⇒ lint 過就會送出**）"
                : "## 將送的 payload（**本 op 不送**）");
            ioR.AppendLine($"- endpoint: `POST {(string.IsNullOrEmpty(GetArg(iArgs, "reply_to", "")) ? "/APP/Timeline/plurkAdd" : "/APP/Responses/responseAdd")}`");
            foreach (var kv in oPayload.OrderBy(k => k.Key))
            {
                ioR.AppendLine(kv.Key == "content"
                    ? $"- `content`（{kv.Value.Length} 字元）:\n\n```\n{kv.Value}\n```"
                    : $"- `{kv.Key}`: `{kv.Value}`");
            }
            if (aSlip.HasImage)
            {
                ioR.AppendLine();
                ioR.AppendLine($"⚠ **本則有附圖**：`{aSlip.Image}`");
                ioR.AppendLine($"　post 會**先上傳**（`{UploadEndpoint}`）再把回傳的 URL 併進 content 末行"
                    + $" —— 實測 URL 50 字元，lint 已為此保留 {UCL_PlurkLint.ImageReserve} 字元。"
                    + "　⛔ 本 op **不上傳**。");
            }
            if (aErrors.Count > 0)
                ioR.AppendLine($"\n⛔ lint 有 {aErrors.Count} 個錯誤 ⇒ **post 會拒絕**（先修那些）。");
        }

        // ===========================================================
        // 區塊職責：post —— 真的送出（預設 dry-run）
        // 物理意義：發布不可回復。所以三道閘：① lint errors 非空一律拒絕；
        //          ② 沒有 `confirm=1` 只印不送；③ 送成功後寫 audit jsonl。
        // 數值影響：真送時對外新增一則噗（或一則回應）。audit 落 `<data_root>/Plurk/post_audit.jsonl`。
        // ===========================================================
        async UniTask OpPost(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, CancellationToken token)
        {
            OpPreview(iArgs, iRes, ioR, out var aPayload,
                GetArg(iArgs, "confirm", "").Trim() == "1");   // Fixes BUG-28
            var aSlip = LoadSlip(iArgs);
            var (aErr, _) = UCL_PlurkLint.Check(aSlip, iRes.RequiresSignature);
            if (aErr.Count > 0)
                throw new Exception($"[Plurk] post 拒絕：lint 有 {aErr.Count} 個錯誤 —— 規則長在這條路上，繞不過");

            bool aConfirm = GetArg(iArgs, "confirm", "").Trim() == "1";
            ioR.AppendLine();
            if (!aConfirm)
            {
                ioR.AppendLine("## dry-run（沒帶 `confirm=1` ⇒ **什麼都沒送出**）");
                ioR.AppendLine("要真的發，重跑同一道指令並加 `--arg confirm=1`。"
                    + "⚠ 發布不可回復，Plurk 沒有 history —— 這一步刻意要人打一個字。");
                return;
            }

            var aCred = RequireCredentials(iRes);

            // ===========================================================
            // 區塊職責：附圖 —— 兩段式的接合處
            // 物理意義：先上傳拿到圖片 URL，再把那個 URL 併進 content（Plurk 自己渲染成圖）。
            //          ⇒ 順序不能顛倒：URL 是上傳的**回傳值**，不是可以先算出來的東西。
            // 數值影響：content 變長（實測 URL 50 字元）⇒ 送出前用**最終長度**再驗一次預算。
            // ⚠ 上傳成功之後才發現超長 ⇒ 圖片已經在 CDN 上（無主圖片，無害但清不掉），
            //   所以 lint 的附圖保留額度要夠（見 UCL_PlurkLint.ImageReserve 的實測值）。
            // ===========================================================
            if (aSlip.HasImage)
            {
                RequireAbsoluteExistingImage(aSlip.Image);
                var (aUpStatus, aUpBody) = await UploadImageAsync(aSlip.Image, aCred, token);
                if (aUpStatus != 200)
                {
                    ioR.AppendLine($"- ✗ 圖片上傳失敗 http={aUpStatus}：{Trunc(aUpBody, 300)}");
                    throw new Exception($"[Plurk] 圖片上傳失敗 http={aUpStatus} —— **噗沒有發出去**");
                }
                string aImgUrl = PickJsonValue(aUpBody, "full");
                if (string.IsNullOrEmpty(aImgUrl))
                    throw new Exception("[Plurk] 圖片上傳回 200 但拿不到 `full` URL —— 噗沒有發出去");
                ioR.AppendLine($"- 圖片已上傳: `{aImgUrl}`（{aImgUrl.Length} 字元）");
                aPayload["content"] = aPayload["content"] + "\n" + aImgUrl;
                int aFinal = aPayload["content"].Length;
                ioR.AppendLine($"- content 併入圖片後: **{aFinal}** 字元（上限 {UCL_PlurkLint.Limit}）");
                if (aFinal > UCL_PlurkLint.Limit)
                    throw new Exception($"[Plurk] 併入圖片 URL 後超出上限（{aFinal} > {UCL_PlurkLint.Limit}）"
                        + " —— 噗沒有發出去；⚠ 圖片已上傳到 CDN（無主圖片）。請縮短文案再跑一次");
            }

            string aReplyTo = GetArg(iArgs, "reply_to", "").Trim();
            string aEndpoint = aReplyTo.Length > 0 ? "/APP/Responses/responseAdd" : "/APP/Timeline/plurkAdd";
            var (aStatus, aBody) = await CallAsync(aEndpoint, aCred, aPayload, token);
            ioR.AppendLine("## post（已送出）");
            ioR.AppendLine($"- http: **{aStatus}**　endpoint: `{aEndpoint}`");
            if (aStatus != 200)
            {
                ioR.AppendLine($"- ✗ body（前 400 字）: {Trunc(aBody, 400)}");
                throw new Exception($"[Plurk] post 失敗 http={aStatus}（內容未發出；詳見回傳檔）");
            }
            string aPlurkId = PickJsonValue(aBody, "plurk_id") ?? PickJsonValue(aBody, "id") ?? "?";
            ioR.AppendLine($"- plurk_id: **{aPlurkId}**");
            WriteAudit(iRes, aSlip, aPayload, aPlurkId, aReplyTo);
            ioR.AppendLine($"- audit: `{AuditPath()}`（append-only）");
        }


        // ===========================================================
        // 區塊職責：圖片上傳（兩段式的第一段）—— multipart/form-data
        // 物理意義：Plurk 的附圖是**兩段式**：先把檔案傳上去拿一個圖片 URL，
        //          再把那個 URL 併進 `content`（時間軸上由 Plurk 自己渲染成圖）。
        //          ⇒ 所以「附圖」不是一個 payload 參數，是**兩次請求 ＋ 一段文字**。
        // ⚠ 與現有請求**不同形**：其餘全部是 form-urlencoded，這支是 multipart。
        //   OAuth 1.0a 對 multipart **只簽 `oauth_*` 參數**（檔案內容不進簽章基底）——
        //   把 body 塞進基底會簽出一個看起來正常的簽章，然後回 4xx，
        //   而那個 4xx 跟「端點不存在」「被 WAF 擋」長得一模一樣。
        // 數值影響：**這是對外寫入** —— 會在 Plurk 的 CDN 上留下一張圖（即使沒有建立噗）。
        //          所以 `op=upload` 也要 `confirm=1`。
        // ===========================================================
        async UniTask<(int status, string body)> UploadImageAsync(
            string iPath, Dictionary<string, string> iCred, CancellationToken token)
        {
            string aUrl = ApiBase + UploadEndpoint;
            byte[] aBytes = File.ReadAllBytes(iPath);
            string aName = Path.GetFileName(iPath);

            using (var aClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
            using (var aForm = new MultipartFormDataContent("UCLPlurk" + Guid.NewGuid().ToString("N")))
            {
                var aFile = new ByteArrayContent(aBytes);
                aFile.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(GuessMime(iPath));
                // 欄位名 `image` 取自社群慣例（官方頁抓不到）—— 驗證狀態見 Plurk_Maintenance §5
                aForm.Add(aFile, "image", aName);

                var aReq = new HttpRequestMessage(HttpMethod.Post, aUrl) { Content = aForm };
                // ⚠ 第四參 null：multipart **不把 body 參數放進簽章基底**
                aReq.Headers.TryAddWithoutValidation("Authorization",
                    OAuthHeader("POST", aUrl, iCred, null));
                aReq.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                var aResp = await aClient.SendAsync(aReq, token);
                string aBody = await aResp.Content.ReadAsStringAsync();
                return ((int)aResp.StatusCode, aBody);
            }
        }

        // 887 bytes / 1024 == 0 ⇒ 印成「0 KB」會被讀成空檔案。小檔印 bytes。
        static string FormatSize(long iBytes)
            => iBytes < 1024 ? $"{iBytes} bytes" : $"{iBytes / 1024} KB";

        static string GuessMime(string iPath)
        {
            switch (Path.GetExtension(iPath).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                default: return "application/octet-stream";
            }
        }

        // ===========================================================
        // 區塊職責：op=upload —— 單獨驗上傳端點（不建立噗）
        // 物理意義：先驗端點再接流程。這一步拿到的是**真實 URL 長度**，
        //          而那個長度決定 lint 該替附圖保留多少字元預算（不是憑估）。
        // 數值影響：對外寫入（CDN 上多一張無主圖片）⇒ 要 `confirm=1`。
        // ===========================================================
        // ===========================================================
        // 區塊職責：op=get —— **唯讀回讀**一則已發出的噗（驗「它真的在那裡」）
        // 物理意義：`post` 回的 200 ＋ `plurk_id` 只證明**送出被接受**。
        //          「公開度真的生效了嗎」「用哪個帳號發的」「附圖真的被渲染成 `<img>` 嗎」
        //          —— 這三件都**不是從 plurk_id 推得出來的**，要去問對方。
        // 🩸 2026-08-21：唯讀診斷 `plurk.py` 移除之後，這條線只剩「送出」那一半有工具，
        //          當天最後一則（第一次走「所有人」）因此只驗到 200，沒驗到它真的公開。
        //          ⇒ 把歸路搬進 Cmd（Tim 2026-08-21：「CMD 流程應該可以跑驗證」）。
        // 數值影響：純唯讀（`/APP/Timeline/getPlurk`），不改任何 Plurk 資料。
        // ===========================================================
        async UniTask OpGet(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, CancellationToken token)
        {
            string aId = GetArg(iArgs, "plurk_id", "").Trim();
            if (aId.Length == 0)
                throw new Exception("[Plurk] op=get 需要 --arg plurk_id=<發文回傳的 id>");
            var aCred = RequireCredentials(iRes);
            var (aStatus, aBody) = await CallAsync("/APP/Timeline/getPlurk", aCred,
                new Dictionary<string, string> { { "plurk_id", aId } }, token);

            ioR.AppendLine();
            ioR.AppendLine($"## get（唯讀回讀 —— 驗「它真的在那裡」）");
            ioR.AppendLine($"- http: **{aStatus}**　plurk_id: `{aId}`");
            if (aStatus != 200)
            {
                ioR.AppendLine($"- ✗ body（前 300 字）: {Trunc(aBody, 300)}");
                throw new Exception($"[Plurk] getPlurk 失敗 http={aStatus}");
            }
            // 回應是 { "plurk": {...}, "user": {...} } —— 取內層那顆
            string aPlurk = ExtractObject(aBody, "plurk");
            string aOwner = PickJsonValue(aPlurk, "owner_id");
            string aLimited = PickJsonValue(aPlurk, "limited_to");
            string aRaw = PickJsonValue(aPlurk, "content_raw") ?? "";
            string aHtml = PickJsonValue(aPlurk, "content") ?? "";
            ioR.AppendLine($"- owner_id: **{aOwner ?? "?"}**"
                + "（比對 `op=whoami` 的 `id` ⇒ 這則到底是哪個帳號發的）");
            ioR.AppendLine($"- limited_to: **{(string.IsNullOrEmpty(aLimited) ? "(無 ⇒ 公開)" : aLimited)}**"
                + "　⚠ 存回來的格式與送出的**不同形**（送 `[0]`、存 `|0|`）⇒ 別拿送出的值比對");
            ioR.AppendLine($"- qualifier: {PickJsonValue(aPlurk, "qualifier") ?? "?"}"
                + $"　posted: {PickJsonValue(aPlurk, "posted") ?? "?"}");
            // 🩸 2026-08-23：本 op 原本只印首行 —— 那對「驗它在不在」夠用，
            //   但要**回應別人**時，只讀首行等於對著一句話的開頭講話。
            //   ⇒ 全文印出來（截 800 字，而且截了會說）。
            string aFull = UnescapeJson(aRaw);
            // 表情反解析標在全文上；⚠ 標註會**加長字串**，所以字元數用標註前的算
            int aFullLen = aFull.Length;
            var aEmoCtx = EmoBegin(iRes);
            aFull = EmoAnnotatePaired(aFull, UnescapeJson(aHtml), aEmoCtx, aOwner);
            ioR.AppendLine($"- content_raw（{aFullLen} 字元"
                + (aFull.Length == aFullLen ? "" : "；下面的 `⟨…⟩` 是表情標註，不在原文裡") + "）:");
            ioR.AppendLine();
            ioR.AppendLine("```");
            ioR.AppendLine(aFull.Length <= 800 ? aFull
                : aFull.Substring(0, 800) + "\n…（截斷 —— 全文比這長，別拿這段當全部）");
            ioR.AppendLine("```");
            // 附圖驗的是**渲染**不是字串：content_raw 有 URL 只證明我送進去了
            bool aHasImg = aHtml.Contains("<img");
            ioR.AppendLine($"- 渲染成 `<img>`: **{(aHasImg ? "是" : "否")}**"
                + "（附圖那則要看這格 —— `content_raw` 裡有 URL 只證明我送進去了，Plurk 認不認是另一回事）");
            EmoEnd(aEmoCtx, ioR);
        }


        // ===========================================================
        // 區塊職責：**唯讀的社交面** —— 看好友在說什麼、看一則噗底下的回應、看好友清單。
        // 物理意義：在這之前這支 Cmd 只有「送出」與「回讀自己那則」——
        //          也就是說它能發文，但**不能參與**。而 Plurk 是雙向的：
        //          別人回了什麼、誰在講話，沒有入口就等於不存在。
        // 數值影響：純唯讀，不改任何 Plurk 資料。可選寫一份本地快取（見 CacheDir，⛔ 不入 git）。
        // ⚠ 預設**一律打 API**，`--arg cache=1` 才吃快取 —— 反過來的話，
        //   「現況」與「三小時前的快照」在回傳檔上會長得一樣，而那正是這個 repo 最貴的錯誤形狀。
        // ===========================================================
        async UniTask OpTimeline(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, CancellationToken token)
        {
            var aCred = RequireCredentials(iRes);
            int aLimit = ParseIntArg(iArgs, "limit", 20, 1, 100);
            int aPreview = ParseIntArg(iArgs, "preview", 90, 20, 400);
            string aFilter = GetArg(iArgs, "filter", "").Trim();

            var aParams = new Dictionary<string, string>
                { { "limit", aLimit.ToString(CultureInfo.InvariantCulture) } };
            // filter 是 Plurk 端的既有語彙（only_user / only_responded / only_private / only_favorite）；
            // 認不得的值我不猜、原樣送出 —— 讓對方回錯，而不是我這裡靜默丟掉它
            if (aFilter.Length > 0) aParams["filter"] = aFilter;

            string aBody = await FetchAsync(iArgs, "timeline_" + (aFilter.Length == 0 ? "all" : aFilter),
                "/APP/Timeline/getPlurks", aParams, aCred, iRes, ioR, token);

            // 「哪一則是我自己發的」問一次就好 —— 不猜，也不寫死 id
            string aMeId = "";
            var (aMeSt, aMeBody) = await CallAsync("/APP/Users/me", aCred, null, token);
            if (aMeSt == 200) aMeId = PickJsonValue(aMeBody, "id") ?? "";

            ioR.AppendLine();
            ioR.AppendLine("## 河道（好友＋自己的噗）");
            var aRoot = SafeParse(aBody);
            var aPlurks = (aRoot != null && aRoot.Contains("plurks")) ? aRoot["plurks"] : null;
            var aUsers = (aRoot != null && aRoot.Contains("plurk_users")) ? aRoot["plurk_users"] : null;
            if (aPlurks == null || !aPlurks.IsArray)
            {
                ioR.AppendLine("- ⚠ 回應裡沒有 `plurks` 陣列 —— 這不是「沒有噗」，是**格式跟我預期的不一樣**。");
                ioR.AppendLine("- body（前 300 字）: " + Trunc(aBody, 300));
                return;
            }
            ioR.AppendLine($"- **{aPlurks.Count}** 則（limit={aLimit}"
                + (aFilter.Length == 0 ? "" : $"　filter=`{aFilter}`")
                + $"　摘要 {aPreview} 字）"
                + (aMeId.Length == 0 ? "　⚠ 問不到自己的 id ⇒ 🪞 標記這一輪不可信" : ""));
            ioR.AppendLine();
            ioR.AppendLine("> 形狀取自酒館 catchup：**先短摘要掃一遍，再挑要細看的那幾則**。");
            ioR.AppendLine("> 摘要是「開頭 N 字」不是「首行」—— 首行可能只有兩個字，那掃不出東西。");
            var aEmoCtx = EmoBegin(iRes);
            ioR.AppendLine($"> 表情標註 `[emoN]⟨…⟩`：描述來自**共用表**"
                + $"（現有 {aEmoCtx.Table.Values.Count(r => r.Desc.Length > 0)}/{aEmoCtx.Table.Count} 張已描述）——"
                + "**描述一次、之後純文字查表，不必再抓圖**；沒描述的會被登記進待描述清單，"
                + "配不上時印 `?配不上` —— 不猜。");
            ioR.AppendLine();

            for (int i = 0; i < aPlurks.Count; i++)
            {
                var aP = aPlurks[i];
                string aId = JsonScalar(aP, "plurk_id");
                string aOwner = JsonScalar(aP, "owner_id");
                string aRaw = UnescapeJson(JsonScalar(aP, "content_raw"));
                // 表情反解析：`[emoN]` 是 per-account 別名 ⇒ 用同一筆的 HTML 按序配對出 URL
                aRaw = EmoAnnotatePaired(aRaw, UnescapeJson(JsonScalar(aP, "content")), aEmoCtx, aOwner);
                string aFlat = OneLine(aRaw).Trim();

                var aTags = new List<string>();
                if (aMeId.Length > 0 && aOwner == aMeId) aTags.Add("🪞我");
                if (aRaw.Contains("images.plurk.com") || UnescapeJson(JsonScalar(aP, "content")).Contains("<img"))
                    aTags.Add("🖼");
                if (aFlat.StartsWith("http", StringComparison.OrdinalIgnoreCase)) aTags.Add("🔗");
                string aRc = JsonScalar(aP, "response_count");
                string aFc = JsonScalar(aP, "favorite_count");

                ioR.AppendLine($"- **[{aId}]** {ShortTime(JsonScalar(aP, "posted"))} "
                    + $"**{UserName(aUsers, aOwner)}** «{JsonScalar(aP, "qualifier")}»"
                    + (aRc == "0" || aRc.Length == 0 ? "" : $" 💬{aRc}")
                    + (aFc == "0" || aFc.Length == 0 ? "" : $" ❤{aFc}")
                    + (aTags.Count == 0 ? "" : "　" + string.Join(" ", aTags)));
                ioR.AppendLine("    " + (aFlat.Length == 0 ? "(沒有文字內容)" : Trunc(aFlat, aPreview)));
            }

            ioR.AppendLine();
            ioR.AppendLine("### ▶ 挑一則細看／互動（id 抄上面那個）");
            ioR.AppendLine("```bash");
            ioR.AppendLine("--arg op=get       --arg plurk_id=<id>                    # 全文（不是首行）");
            ioR.AppendLine("--arg op=responses --arg plurk_id=<id>                    # 底下的回應");
            ioR.AppendLine("--arg op=like      --arg plurk_id=<id> --arg confirm=1    # 按讚");
            ioR.AppendLine("--arg op=post --arg slip_file=<交付單> --arg reply_to=<id> --arg confirm=1   # 回應");
            ioR.AppendLine("```");
            ioR.AppendLine("⚠ 回應**走既有發文路**，刻意不另開一條短回應路 ——");
            ioR.AppendLine("　 兩條發文路就是兩套規則，而字數 lint 與末行署名只會套用在其中一條。");
            ioR.AppendLine("⚠ 摘要是**截斷過的**：要回應誰之前先 `op=get` 讀全文。");
            ioR.AppendLine("　 對著一段開頭講話，跟讀完再講，在對方那邊看起來完全不一樣。");
            EmoEnd(aEmoCtx, ioR);
        }

        /// <summary>
        /// `Sun, 23 Aug 2026 09:03:42 GMT` → `08-23 17:03`（本地）。
        /// 解析不了就**原樣回**（不吞掉，也不假裝知道時間）。
        /// </summary>
        static string ShortTime(string iRfc)
        {
            if (string.IsNullOrEmpty(iRfc)) return "(無時間)";
            if (!DateTime.TryParse(iRfc, CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime aUtc))
                return iRfc;
            return aUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        async UniTask OpResponses(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, CancellationToken token)
        {
            string aId = GetArg(iArgs, "plurk_id", "").Trim();
            if (aId.Length == 0) throw new Exception("[Plurk] op=responses 需要 --arg plurk_id=<噗 id>");
            var aCred = RequireCredentials(iRes);
            int aFrom = ParseIntArg(iArgs, "from_response", 0, 0, 100000);

            var aParams = new Dictionary<string, string>
            {
                { "plurk_id", aId },
                { "from_response", aFrom.ToString(CultureInfo.InvariantCulture) },
            };
            string aBody = await FetchAsync(iArgs, "responses_" + aId, "/APP/Responses/get",
                aParams, aCred, iRes, ioR, token);

            ioR.AppendLine();
            ioR.AppendLine($"## responses（`{aId}` 底下的回應）");
            var aRoot = SafeParse(aBody);
            var aList = (aRoot != null && aRoot.Contains("responses")) ? aRoot["responses"] : null;
            var aFriends = (aRoot != null && aRoot.Contains("friends")) ? aRoot["friends"] : null;
            if (aList == null || !aList.IsArray)
            {
                ioR.AppendLine("- ⚠ 回應裡沒有 `responses` 陣列 —— 不是「沒人回」，是格式跟我預期的不一樣。");
                ioR.AppendLine("- body（前 300 字）: " + Trunc(aBody, 300));
                return;
            }
            // 🩸 2026-08-24：陣列 4 筆而 id 只有 3 個 —— **Plurk 自己回了同一則兩次**。
            //   首版直接印 `aList.Count` 當「幾則回應」⇒ 那個數字是陣列長度，不是回應數。
            //   ⇒ 兩個數都印（陣列筆數／相異 id 數）＋ 跟 root 的 `response_count` 對帳，
            //     不一致就說出來。同一個量有三個來源時，挑一個印等於替其他兩個背書。
            var aRpEmoCtx = EmoBegin(iRes);
            var aSeen = new HashSet<string>();
            for (int i = 0; i < aList.Count; i++) aSeen.Add(JsonScalar(aList[i], "id"));
            string aDeclared = aRoot.Contains("response_count") ? JsonScalar(aRoot, "response_count") : null;
            ioR.AppendLine($"- **{aSeen.Count}** 則回應（相異 id）"
                + (aList.Count != aSeen.Count
                    ? $"　⚠ 而陣列有 **{aList.Count}** 筆 ⇒ **Plurk 回了重複的**（不是我印兩次）" : "")
                + (aDeclared != null && aDeclared != aSeen.Count.ToString(CultureInfo.InvariantCulture)
                    ? $"　⚠ 而它自己宣告 `response_count`={aDeclared} ⇒ 三個數不一致，我不挑一個當真" : "")
                + (aRoot.Contains("responses_seen")
                    ? $"　（對方記錄的已讀數: {JsonScalar(aRoot, "responses_seen")}）" : ""));
            var aDone = new HashSet<string>();
            for (int i = 0; i < aList.Count; i++)
            {
                var aRp = aList[i];
                string aRid = JsonScalar(aRp, "id");
                bool aDup = !aDone.Add(aRid);
                ioR.AppendLine($"- **{UserName(aFriends, JsonScalar(aRp, "user_id"))}**"
                    + $"　`{aRid}`　{JsonScalar(aRp, "posted")}"
                    + (aDup ? "　⚠ **重複的同一則**（上面出現過）" : ""));
                if (aDup) continue;   // 內容不重印，但**那一行要留著** —— 靜默去重會讓筆數對不上
                string aRpRaw = EmoAnnotatePaired(UnescapeJson(JsonScalar(aRp, "content_raw")),
                    UnescapeJson(JsonScalar(aRp, "content")), aRpEmoCtx, JsonScalar(aRp, "user_id"));
                ioR.AppendLine("    " + Trunc(OneLine(aRpRaw), 200));
            }
            EmoEnd(aRpEmoCtx, ioR);
        }

        // ===========================================================
        // 區塊職責：**誰 @ 了我、在哪一則、我回了沒** —— 被點名的訊息要優先回（Tim 2026-09-03）。
        // 物理意義：在這之前「被 @」這件事沒有入口：河道摘要只列噗不列回應，而 @ 幾乎都發生在
        //          回應裡；alerts 有 «mentioned» 型別但 getActive 讀了就清（不可重跑）、且不帶噗 id。
        //          🩸 現場：海苔 09-01 13:18 在一則噗底下 @ 我問「怎麼決定回哪些噗」，
        //          兩天後 Tim 從截圖上看到，我這邊的工具沒有任何一格讓它浮上來。
        // 數值影響：三步都是唯讀 ——
        //          ① /APP/Users/me 拿我的 id 與 nick（@ 的目標是 nick，不是顯示名）
        //          ② 候選噗＝兩條路徑聯集（TASK-0110，summit 2026-09-03 量出來的）：
        //             `filter=mentioned`（噗本體提到我）∪ `filter=only_responded`（我回過的串）。
        //             🩸 首版只有前者：海苔 08-27 在她自己的噗底下回 @summit，那則噗不在 mentioned 集合裡
        //               ⇒ 工具印「真的 0」，summit 隔七天靠 alerts 才發現。第二條路徑蓋住最大宗來源
        //               —— 別人在我參與過的串裡回我 —— 而它是實測過會列出那則的。
        //          ③ 每則噗拉 Responses/get，挑出內文含 `@<nick>` 的回應；
        //             「我回了沒」＝那則 @ 之後有沒有**我自己 id** 的回應（時間序，不比對內容）。
        //          ④ 通知層對帳：讀 `Alerts/getHistory`（不是 getActive —— 那支讀了就清）的 «mentioned»，
        //             拿（誰、何時）跟 ③ 的命中對；對不上的印「通知層有、兩條路徑找不到」。
        //             alerts 不帶噗 id，所以它只能證明「有」，證不了「在哪」—— 但那正是要印出來的那一格。
        // ⚠ 判準是**位置與 id**，不是「看起來像不像回了」：
        //   我在該噗有回應但都在 @ 之前 ⇒ 仍算未回。噗本體 @ 我而底下沒有我 ⇒ 未回。
        // ⚠ 兩條路徑都回 0 時**不印「真的 0」**：這兩條的射程是「噗本體提到我」＋「我參與過的串」，
        //   @ 若在我沒參與的別人噗裡，這裡看不到 —— 把射程外講成量過了，讀的人就不會再去別處看。
        // ===========================================================
        async UniTask OpMentions(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, CancellationToken token)
        {
            var aCred = RequireCredentials(iRes);
            int aLimit = ParseIntArg(iArgs, "limit", 20, 1, 100);
            int aPreview = ParseIntArg(iArgs, "preview", 160, 20, 400);

            var (aMeSt, aMeBody) = await CallAsync("/APP/Users/me", aCred, null, token);
            if (aMeSt != 200) throw new Exception($"[Plurk] mentions 問不到自己是誰（/APP/Users/me http={aMeSt}）—— 沒有 nick 就判不了誰 @ 我");
            string aMeId = PickJsonValue(aMeBody, "id") ?? "";
            string aNick = PickJsonValue(aMeBody, "nick_name") ?? "";
            if (aMeId.Length == 0 || aNick.Length == 0)
                throw new Exception("[Plurk] /APP/Users/me 缺 id 或 nick_name ⇒ 判不了 @，不猜");
            string aNeedle = "@" + aNick;
            string aMyPersona = GetArg(iArgs, "persona", "").Trim();
            var aOtherTagged = new List<string>();
            var aRoommates = UCL_PlurkAccounts.PersonasOn(iRes.SecretId);
            bool aMulti = aRoommates.Count > 1 || iRes.IsShared;

            ioR.AppendLine();
            ioR.AppendLine("## mentions（誰 @ 了我、我回了沒）");
            ioR.AppendLine($"- 我：id `{aMeId}`　nick `{aNick}`（@ 的比對字串是 `{aNeedle}`，不分大小寫）");
            // 區塊職責：共用帳號的 persona 路由 —— 通知是帳號層的，而帳號有多個人。
            if (aMulti)
            {
                ioR.AppendLine($"- 👥 **這是多人帳號**（`{iRes.SecretId}`：{string.Join(" / ", aRoommates)}）"
                    + $"　我＝`{(aMyPersona.Length > 0 ? aMyPersona : "(未指定 persona)")}`");
                ioR.AppendLine($"- 🧭 路由判準（Tim 2026-09-03）：`@{aNick}{UCL_PlurkAccounts.PersonaTagSep}<我>` ⇒ 指名我；"
                    + $"`@{aNick}{UCL_PlurkAccounts.PersonaTagSep}<別人>` ⇒ 指名別人（列在文末，不算我未回）；"
                    + $"**`@{aNick}` 沒帶標記 ⇒ 視為 @ 這個帳號內所有人，算我**"
                    + "　—— 誰收到是機械的，誰回才是人的決定。");
                if (aMyPersona.Length == 0)
                    ioR.AppendLine("- ⚠ **沒帶 `--persona`** ⇒ 帶標記的那些一律算「指名別人」，"
                        + "而那可能包含指名你的。要正確路由請顯式帶 persona。");
            }

            // 候選集：兩條路徑各拉一次，依 plurk_id 去重（同一則兩邊都有時只拉一次回應）
            var aCandidates = new List<UCL.Core.JsonLib.JsonData>();
            var aUsersAll = new UCL.Core.JsonLib.JsonData();
            var aSeenPid = new HashSet<string>();
            var aPathCounts = new List<string>();
            foreach (string aFilter in new[] { "mentioned", "only_responded" })
            {
                var aParams = new Dictionary<string, string>
                {
                    { "limit", aLimit.ToString(CultureInfo.InvariantCulture) },
                    { "filter", aFilter },
                };
                string aBody = await FetchAsync(iArgs, "timeline_" + aFilter, "/APP/Timeline/getPlurks",
                    aParams, aCred, iRes, ioR, token);
                var aRoot = SafeParse(aBody);
                var aPlurks = (aRoot != null && aRoot.Contains("plurks")) ? aRoot["plurks"] : null;
                var aPathUsers = (aRoot != null && aRoot.Contains("plurk_users")) ? aRoot["plurk_users"] : null;
                if (aPlurks == null || !aPlurks.IsArray)
                {
                    ioR.AppendLine($"- ⚠ filter={aFilter} 的回應裡沒有 `plurks` 陣列 —— 這不是「沒人 @ 我」，是**格式跟我預期的不一樣**。");
                    ioR.AppendLine("- body（前 300 字）: " + Trunc(aBody, 300));
                    continue;
                }
                int aNew = 0;
                for (int i = 0; i < aPlurks.Count; i++)
                {
                    if (!aSeenPid.Add(JsonScalar(aPlurks[i], "plurk_id"))) continue;
                    aCandidates.Add(aPlurks[i]); aNew++;
                }
                if (aPathUsers != null) foreach (string k in aPathUsers.Keys) aUsersAll[k] = aPathUsers[k];
                aPathCounts.Add($"`{aFilter}` {aPlurks.Count} 則（新增 {aNew}）");
            }
            ioR.AppendLine($"- 候選噗 **{aCandidates.Count}** 則（limit={aLimit}／路徑）：{string.Join("、", aPathCounts)}");
            if (aCandidates.Count == 0)
                ioR.AppendLine("- ⚠ 兩條路徑都回 0 ⇒ **不是「沒人 @ 我」**：射程是「噗本體提到我」＋「我回過的串」，"
                    + "@ 若在我沒參與的別人噗裡，這裡看不到。下面的通知層對帳會說有沒有那種。");

            int aPending = 0, aAnswered = 0;
            var aEmoCtx = EmoBegin(iRes);
            var aHitLog = new List<(string uid, string when)>();      // 給通知層對帳用
            var aUsers = aUsersAll;
            for (int i = 0; i < aCandidates.Count; i++)
            {
                var aP = aCandidates[i];
                string aPid = JsonScalar(aP, "plurk_id");
                string aOwner = JsonScalar(aP, "owner_id");
                string aPRaw = UnescapeJson(JsonScalar(aP, "content_raw"));
                // 噗本體 @ 我 ＝ 第 0 則
                var aHits = new List<(int idx, string who, string uid, string when, string text)>();
                var aBodyHit = UCL_PlurkAccounts.ClassifyMention(aPRaw, aNick, aMyPersona);
                if (aBodyHit.HitsMe)
                    aHits.Add((0, UserName(aUsers, aOwner), aOwner, JsonScalar(aP, "posted"), aPRaw));
                else if (aBodyHit.Found)
                    aOtherTagged.Add($"[{aPid}] 噗本體 @ 了帳號但指名 {string.Join(" / ", aBodyHit.Tags)}");
                var aHeader = new StringBuilder();
                aHeader.AppendLine();
                aHeader.AppendLine($"### [{aPid}] {ShortTime(JsonScalar(aP, "posted"))} **{UserName(aUsers, aOwner)}** «{JsonScalar(aP, "qualifier")}»"
                    + $" 💬{JsonScalar(aP, "response_count")}");
                aHeader.AppendLine("    " + Trunc(OneLine(aPRaw), aPreview));

                // 回應：一次拉全（from_response=0）；量到的是 Plurk 回的那一頁，超過的會印出來
                var (aRSt, aRBody) = await CallAsync("/APP/Responses/get", aCred,
                    new Dictionary<string, string> { { "plurk_id", aPid }, { "from_response", "0" } }, token);
                if (aRSt != 200)
                {
                    ioR.Append(aHeader);
                    ioR.AppendLine($"    ⚠ 拉不到回應（http={aRSt}）⇒ 這則**判不了**回了沒（不是「沒回」）");
                    continue;
                }
                var aRRoot = SafeParse(aRBody);
                var aList = (aRRoot != null && aRRoot.Contains("responses")) ? aRRoot["responses"] : null;
                var aFriends = (aRRoot != null && aRRoot.Contains("friends")) ? aRRoot["friends"] : null;
                int aLastMineIdx = -1;      // 我最後一則回應在陣列裡的位置（陣列＝時間序）
                int aCount = aList != null && aList.IsArray ? aList.Count : 0;
                var aSeen = new HashSet<string>();
                for (int r = 0; r < aCount; r++)
                {
                    var aRp = aList[r];
                    if (!aSeen.Add(JsonScalar(aRp, "id"))) continue;    // Plurk 會重複回同一則
                    string aUid = JsonScalar(aRp, "user_id");
                    if (aUid == aMeId) { aLastMineIdx = r; continue; }
                    string aRaw = UnescapeJson(JsonScalar(aRp, "content_raw"));
                    var aRHit = UCL_PlurkAccounts.ClassifyMention(aRaw, aNick, aMyPersona);
                    if (!aRHit.HitsMe)
                    {
                        // 有 @ 帳號但指名別人 ⇒ 不算我未回，但**要看得見**（否則它會從所有人的視野消失）
                        if (aRHit.Found)
                            aOtherTagged.Add($"[{aPid}] 第 {r + 1} 則 @ 了帳號但指名 {string.Join(" / ", aRHit.Tags)}");
                        continue;
                    }
                    aHits.Add((r + 1, UserName(aFriends, aUid), aUid, JsonScalar(aRp, "posted"),
                        EmoAnnotatePaired(aRaw, UnescapeJson(JsonScalar(aRp, "content")), aEmoCtx, aUid)));
                }
                string aDeclared = aRRoot != null && aRRoot.Contains("response_count") ? JsonScalar(aRRoot, "response_count") : "";
                bool aPartial = aDeclared.Length > 0 && aDeclared != aSeen.Count.ToString(CultureInfo.InvariantCulture);

                // only_responded 的候選大多**沒有** @ 我（我回過的串裡別人在講別的）—— 那不是判不了，
                // 是正常的「這串沒人點名我」；沒命中且回應讀滿的就不印，免得把河道整份重印一次。
                if (aHits.Count == 0 && !aPartial) continue;
                ioR.Append(aHeader);
                if (aPartial)
                    ioR.AppendLine($"    ⚠ 只讀到 {aSeen.Count} 則回應而它宣告 response_count={aDeclared} ⇒ 沒讀到的那些裡有沒有 @ 我，這裡**不知道**");
                foreach (var h in aHits)
                {
                    // 「回了沒」＝ 那則 @ 之後有沒有我的回應（位置比較；第 0 則＝噗本體 ⇒ 我有任何回應即算）
                    bool aReplied = aLastMineIdx >= 0 && (h.idx == 0 || aLastMineIdx > h.idx - 1);
                    if (aReplied) aAnswered++; else aPending++;
                    aHitLog.Add((h.uid, h.when));
                    ioR.AppendLine($"    - {(aReplied ? "✅ 已回" : "🔔 **未回**")}　@ 在{(h.idx == 0 ? "噗本體" : $"第 {h.idx} 則回應")}"
                        + $"　**{h.who}**　{ShortTime(h.when)}");
                    ioR.AppendLine("        " + Trunc(OneLine(h.text), aPreview));
                }
            }

            // ④ 通知層對帳 —— getHistory 不清通知（getActive 會）。alerts 沒有噗 id，只能用（誰、何時）配。
            ioR.AppendLine();
            ioR.AppendLine("## 通知層對帳（`Alerts/getHistory` 的 «mentioned»，唯讀）");
            var (aAlSt, aAlBody) = await CallAsync("/APP/Alerts/getHistory", aCred, null, token);
            if (aAlSt != 200)
            {
                ioR.AppendLine($"- ⚠ 讀不到通知歷史（http={aAlSt}）⇒ 這一格**沒有讀數**，上面的清單只代表兩條時間軸路徑");
            }
            else
            {
                var aAl = SafeParse(aAlBody);
                int aMentionAlerts = 0, aUnmatched = 0;
                if (aAl != null && aAl.IsArray)
                {
                    for (int i = 0; i < aAl.Count; i++)
                    {
                        var aIt = aAl[i];
                        if (JsonScalar(aIt, "type") != "mentioned") continue;
                        aMentionAlerts++;
                        var aFrom = aIt.Contains("from_user") ? aIt["from_user"] : null;
                        string aFid = aFrom != null ? JsonScalar(aFrom, "id") : "";
                        string aFname = aFrom != null ? UnescapeJson(JsonScalar(aFrom, "display_name")) : "(查無名稱)";
                        string aWhen = JsonScalar(aIt, "posted");
                        bool aMatched = false;
                        if (DateTime.TryParse(aWhen, CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime aT))
                        {
                            foreach (var h in aHitLog)
                            {
                                if (h.uid != aFid) continue;
                                if (DateTime.TryParse(h.when, CultureInfo.InvariantCulture,
                                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime aHt)
                                    && Math.Abs((aHt - aT).TotalMinutes) <= 3) { aMatched = true; break; }
                            }
                        }
                        if (!aMatched)
                        {
                            aUnmatched++;
                            ioR.AppendLine($"- ⚠ **通知層有、兩條路徑找不到**：{ShortTime(aWhen)}　**{aFname}**（`{aFid}`）"
                                + " ⇒ 多半在我沒參與的噗裡；alerts 不帶噗 id，去 `op=profile --arg user_id=" + aFid + "` 看他近期的噗再拉回應");
                        }
                    }
                }
                ioR.AppendLine($"- 通知歷史裡 «mentioned» **{aMentionAlerts}** 筆，其中對不上路徑命中的 **{aUnmatched}** 筆"
                    + "（配法：同一個人 ＋ 時間差 ≤3 分；歷史只有最近 30 筆通知，更舊的這裡也看不到）");
            }

            ioR.AppendLine();
            ioR.AppendLine($"## 讀數：🔔 未回 **{aPending}**　✅ 已回 **{aAnswered}**"
                + "　（「已回」＝ @ 之後有我的回應，只看位置與 id，不看內容有沒有答到；射程＝噗本體提到我＋我回過的串＋通知層對帳）");
            // 指名別人的那些：**不算我未回，但一定要印** —— 不印的話它會從所有人的視野消失，
            // 而「被過濾掉」跟「不存在」在回傳檔上長得一模一樣。
            if (aOtherTagged.Count > 0)
            {
                ioR.AppendLine($"- 👥 另有 **{aOtherTagged.Count}** 筆 @ 了這個帳號但**指名別人**（不算我未回，列出來讓它不消失）：");
                foreach (var s in aOtherTagged) ioR.AppendLine($"    · {s}");
                ioR.AppendLine("  ⇒ 那幾位跑自己的 `op=mentions` 時會看到它們是自己的 🔔。");
            }
            ioR.AppendLine("### ▶ 回（走既有發文路，@ 的先回）");
            ioR.AppendLine("```bash");
            ioR.AppendLine("--arg op=get       --arg plurk_id=<id>                    # 先讀全文與脈絡");
            ioR.AppendLine("--arg op=responses --arg plurk_id=<id>                    # 讀完整串再回，別對著摘要講話");
            ioR.AppendLine("--arg op=post --arg slip_file=<交付單> --arg reply_to=<id> --arg confirm=1");
            ioR.AppendLine("```");
            EmoEnd(aEmoCtx, ioR);
        }

        async UniTask OpFriends(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, CancellationToken token)
        {
            var aCred = RequireCredentials(iRes);
            string aUserId = GetArg(iArgs, "user_id", "").Trim();
            if (aUserId.Length == 0)
            {
                // 不猜「我是誰」—— 去問一次 /APP/Users/me，並且把那個讀數印出來
                var (aSt, aMe) = await CallAsync("/APP/Users/me", aCred, null, token);
                if (aSt != 200)
                    throw new Exception($"[Plurk] 問不到自己的 user_id（http={aSt}）⇒ 請顯式帶 --arg user_id=");
                aUserId = PickJsonValue(aMe, "id") ?? "";
                if (aUserId.Length == 0)
                    throw new Exception("[Plurk] /APP/Users/me 沒有 id 欄位 ⇒ 請顯式帶 --arg user_id=");
                ioR.AppendLine($"- user_id 未給 ⇒ 由 `/APP/Users/me` 讀回 **{aUserId}**（讀的，不是推的）");
            }
            int aLimit = ParseIntArg(iArgs, "limit", 30, 1, 100);
            int aOffset = ParseIntArg(iArgs, "offset", 0, 0, 100000);

            var aParams = new Dictionary<string, string>
            {
                { "user_id", aUserId },
                { "offset", aOffset.ToString(CultureInfo.InvariantCulture) },
                { "limit", aLimit.ToString(CultureInfo.InvariantCulture) },
            };
            string aBody = await FetchAsync(iArgs, "friends_" + aUserId,
                "/APP/FriendsFans/getFriendsByOffset", aParams, aCred, iRes, ioR, token);

            ioR.AppendLine();
            ioR.AppendLine($"## friends（`{aUserId}` 的好友，offset={aOffset} limit={aLimit}）");
            var aRoot = SafeParse(aBody);
            if (aRoot == null || !aRoot.IsArray)
            {
                ioR.AppendLine("- ⚠ 回應不是陣列 —— 格式跟我預期的不一樣（不是「沒有好友」）。");
                ioR.AppendLine("- body（前 300 字）: " + Trunc(aBody, 300));
                return;
            }
            ioR.AppendLine($"- **{aRoot.Count}** 位");
            for (int i = 0; i < aRoot.Count; i++)
            {
                var aU = aRoot[i];
                ioR.AppendLine($"- `{JsonScalar(aU, "id")}`"
                    + $"　**{UnescapeJson(JsonScalar(aU, "display_name"))}**"
                    + $"（{JsonScalar(aU, "nick_name")}）");
            }
            if (aRoot.Count == aLimit)
                ioR.AppendLine($"- ⚠ 剛好取滿 {aLimit} 筆 ⇒ **後面可能還有**"
                    + $"（`--arg offset={aOffset + aLimit}` 續取）。取滿與取完在這裡同形，所以這行一定要印。");
        }

        // ===========================================================
        // 區塊職責：表情表 —— 讀 Plurk 的表情清單，並維護一份**帶描述**的本地表。
        // 物理意義：`[emoN]` 在文案裡是**不透明的**。lint 只數得出「有幾個」，數不出「那是什麼」，
        //          所以既有規則只能是一句「請對照面板逐一確認」—— 把那一格整個丟給人，
        //          而 agent 讀別人的噗時看到 `[emo17399]` 也只能當它是一段亂碼。
        //          ⇒ 這支把面板搬進 repo：代碼／圖檔 URL **從 API 讀**，描述**由人或 agent 寫**。
        // 數值影響：`emoticons` 對 Plurk 純唯讀（`/APP/Emoticons/get`），但**會寫本地表**：
        //          `Plurk/emoticons/<account>.json` ＋ 人可讀投影 `.md`。
        //          刷新是 **merge 不是覆寫** —— API 那邊沒有「描述」這個欄位，
        //          覆寫等於每次刷新都把人寫的擦掉，而擦掉之後跟「還沒寫」長得一模一樣。
        //          消失的條目**不刪**，標 `missing` 留著 —— 「被下架」與「我沒讀到」不可以同形。
        // ⚠ 官方 API 頁（2026-08-24 以顯式 UA 讀回 200）**只有** `/APP/Emoticons/get`：
        //   新增自訂表情沒有任何文件化端點。emoadd 是**未驗證的嘗試**，讀數見它自己的回傳檔。
        // ===========================================================
        const string EmoTableRelative = "Plurk/emoticons";

        // ⚠ **一份共用表，不是 per-account 表**（Tim 2026-08-24 拍板）：
        //   表要存的是「這張圖是什麼」——那個事實跟哪個帳號在看它無關。
        //   per-account 分檔會讓同一張圖被每個帳號各自看圖描述一次（那是最貴的一步），
        //   而共用表只要**任何人描述過一次**，之後所有帳號都是純文字查表、不必再抓圖。
        //   ⇒ 鍵用圖檔 URL（跨帳號穩定），別名（`emoN`）只是某帳號怎麼叫它，存在同一列裡。
        static string EmoTableJson()
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, EmoTableRelative, "shared.json");

        static string EmoTableMd()
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, EmoTableRelative, "shared.md");

        /// <summary>舊的 per-account 檔（2026-08-24 首版）—— 只讀來搬一次，不再寫。</summary>
        static string EmoTableLegacyJson(string iAccount)
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, EmoTableRelative, SafeName(iAccount) + ".json");

        /// <summary>
        /// 表情表的一列。
        /// <para>⚠ `Code` 是 **per-account 的別名**（`(bigeyes)` 是全站碼，但自訂表情是 `emo1`/`emo4`
        /// 這種帳號內編號）—— 🩸 2026-08-24 讀 `/APP/Emoticons/get` 才知道：
        /// 別人噗裡的 `[emo17399]` 跟我的 `[emo4]` **不在同一個命名空間**，
        /// 拿我的表去查他的編號會查到一個長得很像答案的錯答案。</para>
        /// <para>⇒ 所以 `Key` 用 **圖檔 URL**（跨帳號唯一穩定），別名只是這個帳號怎麼叫它。</para>
        /// </summary>
        class EmoRow
        {
            public string Code = "";
            public string Id = "";
            public string Url = "";
            public string Tier = "";
            public string Desc = "";
            public string State = "present";
            /// <summary>誰怎麼叫它：`plurk_summit:emo4` / `18166697:emo17399`（讀別人的噗時登記的）。</summary>
            public List<string> Aliases = new List<string>();
            public string FirstSeen = "";
            public string Key => Url.Length > 0 ? "url:" + Url : "code:" + Code;

            public void AddAlias(string iAlias)
            {
                if (iAlias.Length > 0 && !Aliases.Contains(iAlias)) Aliases.Add(iAlias);
            }
        }

        /// <summary>
        /// 讀取端的表情上下文：**一次載入、整趟共用**，並把「這趟新看到的圖」登記起來。
        /// <para>物理意義：描述一張圖要**看圖**（那一步貴且要人／要視覺模型）；
        /// 查一段文字不用。所以流程是「看一次 → 寫進共用表 → 之後永遠查表」。
        /// 而要知道「還有哪些沒看過」，讀取端就得**把沒見過的登記下來** ——
        /// 否則待描述清單只存在於某一次的畫面上，關掉就沒了。</para>
        /// </summary>
        class EmoCtx
        {
            public Dictionary<string, EmoRow> Table;
            public int NewSeen;
            public int Hit;
            public int Miss;
            public bool Dirty;
        }

        async UniTask OpEmoticons(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, CancellationToken token)
        {
            var aCred = RequireCredentials(iRes);
            string aBody = await FetchAsync(iArgs, "emoticons", "/APP/Emoticons/get",
                null, aCred, iRes, ioR, token);

            var aRoot = SafeParse(aBody);
            ioR.AppendLine();
            ioR.AppendLine("## emoticons（表情表）");
            if (aRoot == null || !aRoot.IsObject)
            {
                ioR.AppendLine("- ⚠ 回應不是物件 —— **格式跟我預期的不一樣**（不是「沒有表情」）。");
                ioR.AppendLine("- body（前 300 字）: " + Trunc(aBody, 300));
                return;
            }

            // ① 結構讀數：先把「這份回應長什麼樣」印出來，再談內容。
            //    🩸 判準取自這支既有的血證：格式不同與沒有資料必須分得開。
            var aGroups = aRoot.Keys.ToList();
            ioR.AppendLine($"- 頂層分組 **{aGroups.Count}** 組: "
                + string.Join(" / ", aGroups.Select(k => $"`{k}`"
                    + (aRoot[k] != null && aRoot[k].IsArray ? $"[{aRoot[k].Count}]"
                        : aRoot[k] != null && aRoot[k].IsObject ? $"{{{aRoot[k].Count}}}" : "(純量)"))));

            var aRows = new List<EmoRow>();
            foreach (string aGroup in aGroups)
            {
                var aNode = aRoot[aGroup];
                if (aNode == null) continue;
                if (aNode.IsObject)
                {
                    // karma / recruited：{ "0": [[code,url],...], "25": [...] }
                    foreach (string aTier in aNode.Keys.ToList())
                        CollectEmoList(aNode[aTier], aGroup + "/" + aTier, aRows);
                }
                else if (aNode.IsArray)
                {
                    CollectEmoList(aNode, aGroup, aRows);   // custom 走這條
                }
            }

            ioR.AppendLine($"- 讀到 **{aRows.Count}** 個表情"
                + $"（其中有數字編號的 **{aRows.Count(r => r.Id.Length > 0)}** 個）");
            foreach (var aTierGroup in aRows.GroupBy(r => r.Tier).OrderBy(g => g.Key))
                ioR.AppendLine($"    · `{aTierGroup.Key}`　{aTierGroup.Count()} 個"
                    + $"　例: {string.Join(" ", aTierGroup.Take(4).Select(r => r.Code))}");

            // ② 反解析能不能做，用**讀數**回答，不用推論
            var aOwn = aRows.Where(r => EmoAliasRe.IsMatch(r.Code)).ToList();
            ioR.AppendLine();
            ioR.AppendLine("### ▶ `[emoN]` 反解析可行性（用讀數回答，不是用推論）");
            ioR.AppendLine($"- 本帳號自己的 `[emoN]` 別名: **{aOwn.Count}** 個"
                + (aOwn.Count == 0 ? "" : "（" + string.Join(" ", aOwn.Select(r => "[" + r.Code + "]")) + "）")
                + " ⇒ **我自己文案裡的 `[emoN]` 這張表查得到**。");
            ioR.AppendLine("- ⛔ 而**別人噗裡的 `[emoN]` 這張表查不到**："
                + "`emoN` 是 per-account 別名，他的 `[emo17399]` 與我的 `[emo4]` 不同命名空間。"
                + "拿我的表去查他的編號，會查到一個長得很像答案的**錯**答案。");
            ioR.AppendLine("- ✅ 跨帳號真正對得上的鍵是**圖檔 URL**："
                + "`getPlurks` 同一筆裡的 `content`（HTML）帶著每個表情的 `<img src>`，"
                + "跟 `content_raw` 的 `[emoN]` **同序**⇒ 讀取端按序配對就拿得到 URL，"
                + "再用 URL 查描述。timeline／responses／get 已接上這條（配不上時標 `⟨?配不上⟩`，不猜）。");

            // ③ merge 進共用表（描述**只增不減**）
            var aOld = LoadEmoTable(iRes.SecretId ?? "_");
            string aAccountTag = iRes.SecretId ?? "_";
            foreach (var aRow in aRows) aRow.AddAlias(aAccountTag + ":" + aRow.Code);
            int aKept = 0, aNew = 0;
            var aByKey = aRows.ToDictionary(r => r.Key, r => r);
            foreach (var aRow in aRows)
            {
                if (aOld.TryGetValue(aRow.Key, out EmoRow aPrev))
                {
                    if (aPrev.Desc.Length > 0)
                    {
                        aRow.Desc = aPrev.Desc;   // 人寫的描述活下來
                        aKept++;
                    }
                    foreach (string aAlias in aPrev.Aliases) aRow.AddAlias(aAlias);
                    if (aPrev.FirstSeen.Length > 0) aRow.FirstSeen = aPrev.FirstSeen;
                }
                else aNew++;
            }
            // 舊表裡這次沒讀到的：**不刪**，留著。
            // ⚠ 但只有「上次是 API 給的」那些才標 missing ——
            //   `state=seen` 是讀別人的噗登記進來的圖，它**本來就不會**出現在我這個帳號的 API 表裡。
            //   把它標成 missing 等於說「它下架了」，而那是假的。
            int aMissing = 0;
            foreach (var aPrev in aOld.Values)
            {
                if (aByKey.ContainsKey(aPrev.Key)) continue;
                if (aPrev.State != "seen") { aPrev.State = "missing"; aMissing++; }
                aRows.Add(aPrev);
            }

            // ④ 手動描述（`--arg emo_desc=17399=紅心眼,590=攤手`）
            string aDescArg = GetArg(iArgs, "emo_desc", "").Trim();
            int aWrote = 0;
            var aUnmatched = new List<string>();
            if (aDescArg.Length > 0)
            {
                foreach (string aPair in aDescArg.Split(','))
                {
                    int aEq = aPair.IndexOf('=');
                    if (aEq <= 0) continue;
                    string aKey = aPair.Substring(0, aEq).Trim();
                    string aDesc = aPair.Substring(aEq + 1).Trim();
                    if (aKey.Length == 0 || aDesc.Length == 0) continue;
                    // 三種鍵都收：別名（emo4）／全站碼（(bigeyes)）／圖檔 URL 片段
                    // —— 因為跨帳號唯一穩定的是 URL，而人手上最常有的是別名
                    var aHit = aRows.FirstOrDefault(r => r.Id == aKey || r.Code == aKey
                        || (aKey.Length >= 6 && r.Url.Contains(aKey)));
                    if (aHit == null)
                    {
                        // 表裡沒有這個編號 ⇒ **新增一列**（那正是好友噗裡撈到的編號的家），
                        // 並且標明它不是 API 給的
                        aHit = new EmoRow { Id = aKey, Code = "[emo" + aKey + "]", Tier = "manual", State = "manual" };
                        aRows.Add(aHit);
                        aUnmatched.Add(aKey);
                    }
                    aHit.Desc = aDesc;
                    aWrote++;
                }
            }

            SaveEmoTable(aRows, ioR);
            ioR.AppendLine($"- merge: 新增 **{aNew}**／保留既有描述 **{aKept}**"
                + $"／這次沒讀到但留著 **{aMissing}**（標 `missing`，不刪）"
                + (aWrote > 0 ? $"／本次寫入描述 **{aWrote}**" : ""));
            if (aUnmatched.Count > 0)
                ioR.AppendLine($"    · ⚠ 其中 {aUnmatched.Count} 筆編號**不在 API 表裡**（標 `manual`）: "
                    + string.Join(" ", aUnmatched.Select(s => "[emo" + s + "]")));

            int aDescribed = aRows.Count(r => r.Desc.Length > 0);
            ioR.AppendLine($"- 描述覆蓋率: **{aDescribed}/{aRows.Count}**"
                + "　（描述是人寫的，API 沒有這個欄位 ⇒ 覆蓋率只會靠人推進）");

            ioR.AppendLine();
            ioR.AppendLine("### ▶ 下一步");
            ioR.AppendLine("```bash");
            ioR.AppendLine("--arg op=emoticons --arg emo_desc=17399=紅心眼,590=攤手   # 補描述（merge，不覆寫）");
            ioR.AppendLine("--arg op=emoadd --arg url=<圖檔網址> --arg alias=<代碼> --arg confirm=1  # 試新增（未驗證）");
            ioR.AppendLine("```");
        }

        /// <summary>把 `[[code,url],...]` 或 `[{...},...]` 收成列。認不得的元素**跳過但不假裝沒有**。</summary>
        static void CollectEmoList(UCL.Core.JsonLib.JsonData iNode, string iTier, List<EmoRow> ioRows)
        {
            if (iNode == null || !iNode.IsArray) return;
            for (int i = 0; i < iNode.Count; i++)
            {
                var aItem = iNode[i];
                if (aItem == null) continue;
                var aRow = new EmoRow { Tier = iTier };
                if (aItem.IsArray && aItem.Count >= 2)
                {
                    aRow.Code = UnescapeJson(StripQuote(aItem[0].ToJson()));
                    aRow.Url = UnescapeJson(StripQuote(aItem[1].ToJson()));
                }
                else if (aItem.IsObject)
                {
                    aRow.Code = UnescapeJson(JsonScalar(aItem, "alias"));
                    if (aRow.Code.Length == 0) aRow.Code = UnescapeJson(JsonScalar(aItem, "name"));
                    aRow.Url = UnescapeJson(JsonScalar(aItem, "url"));
                    aRow.Id = JsonScalar(aItem, "id");
                }
                else continue;
                // 自訂表情的別名本身就是 `emoN` ⇒ 那個 N 就是文案裡 `[emoN]` 的編號。
                // 🩸 首版把 Id 留空，於是表格的「編號」欄印 `—`，
                //    看起來像「這個表情沒有編號可用」—— 而它其實是**唯一**能打進文案的那格。
                if (aRow.Id.Length == 0 && EmoAliasRe.IsMatch(aRow.Code))
                    aRow.Id = aRow.Code.Substring(3);
                if (aRow.Id.Length == 0) aRow.Id = IdFromUrl(aRow.Url);
                if (aRow.Code.Length == 0 && aRow.Url.Length == 0) continue;
                ioRows.Add(aRow);
            }
        }

        /// <summary>URL 檔名**純數字**時當它是編號；其餘回空 —— 不從雜湊檔名硬擠一個編號出來。</summary>
        static string IdFromUrl(string iUrl)
        {
            if (string.IsNullOrEmpty(iUrl)) return "";
            int aSlash = iUrl.LastIndexOf('/');
            string aName = aSlash >= 0 ? iUrl.Substring(aSlash + 1) : iUrl;
            int aDot = aName.IndexOf('.');
            if (aDot > 0) aName = aName.Substring(0, aDot);
            return aName.Length > 0 && aName.All(char.IsDigit) ? aName : "";
        }

        static string StripQuote(string iRaw)
        {
            string aText = (iRaw ?? "").Trim();
            if (aText.Length >= 2 && aText[0] == '"' && aText[aText.Length - 1] == '"')
                aText = aText.Substring(1, aText.Length - 2);
            return aText == "null" ? "" : aText;
        }

        static Dictionary<string, EmoRow> LoadEmoTable(string iLegacyAccount = null)
        {
            var aMap = new Dictionary<string, EmoRow>();
            string aFile = EmoTableJson();
            // 共用表還不存在時，把舊的 per-account 檔搬進來一次（含它累積的描述）
            if (!File.Exists(aFile) && !string.IsNullOrEmpty(iLegacyAccount)
                && File.Exists(EmoTableLegacyJson(iLegacyAccount)))
                aFile = EmoTableLegacyJson(iLegacyAccount);
            if (!File.Exists(aFile)) return aMap;
            var aRoot = SafeParse(File.ReadAllText(aFile, Encoding.UTF8));
            if (aRoot == null || !aRoot.Contains("entries") || !aRoot["entries"].IsArray) return aMap;
            var aArr = aRoot["entries"];
            for (int i = 0; i < aArr.Count; i++)
            {
                var aRow = new EmoRow
                {
                    Code = UnescapeJson(JsonScalar(aArr[i], "code")),
                    Id = JsonScalar(aArr[i], "id"),
                    Url = UnescapeJson(JsonScalar(aArr[i], "url")),
                    Tier = JsonScalar(aArr[i], "tier"),
                    Desc = UnescapeJson(JsonScalar(aArr[i], "desc")),
                    State = JsonScalar(aArr[i], "state"),
                    FirstSeen = JsonScalar(aArr[i], "first_seen"),
                };
                if (aArr[i].Contains("aliases") && aArr[i]["aliases"].IsArray)
                {
                    var aAl = aArr[i]["aliases"];
                    for (int j = 0; j < aAl.Count; j++)
                        aRow.AddAlias(UnescapeJson(StripQuote(aAl[j].ToJson())));
                }
                if (aRow.Code.Length == 0 && aRow.Id.Length == 0 && aRow.Url.Length == 0) continue;
                aMap[aRow.Key] = aRow;
            }
            return aMap;
        }

        static void SaveEmoTable(List<EmoRow> iRows, StringBuilder ioR)
        {
            string aFile = EmoTableJson();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(aFile));
                var aRoot = new UCL.Core.JsonLib.JsonData();
                aRoot["refreshed_at"] = new UCL.Core.JsonLib.JsonData(DateTime.UtcNow.ToString("o"));
                var aArr = new UCL.Core.JsonLib.JsonData();
                foreach (var aRow in iRows.OrderBy(r => r.Tier).ThenBy(r => r.Code))
                {
                    var aItem = new UCL.Core.JsonLib.JsonData();
                    aItem["code"] = new UCL.Core.JsonLib.JsonData(aRow.Code);
                    aItem["id"] = new UCL.Core.JsonLib.JsonData(aRow.Id);
                    aItem["url"] = new UCL.Core.JsonLib.JsonData(aRow.Url);
                    aItem["tier"] = new UCL.Core.JsonLib.JsonData(aRow.Tier);
                    aItem["desc"] = new UCL.Core.JsonLib.JsonData(aRow.Desc);
                    aItem["state"] = new UCL.Core.JsonLib.JsonData(aRow.State);
                    aItem["first_seen"] = new UCL.Core.JsonLib.JsonData(aRow.FirstSeen);
                    var aAl = new UCL.Core.JsonLib.JsonData();
                    foreach (string aAlias in aRow.Aliases) aAl.Add(new UCL.Core.JsonLib.JsonData(aAlias));
                    aItem["aliases"] = aAl;
                    aArr.Add(aItem);
                }
                aRoot["entries"] = aArr;
                File.WriteAllText(aFile, aRoot.ToJson(), new UTF8Encoding(false));
                ioR.AppendLine($"- 📋 共用表: `{aFile}`（**merge 寫入**，描述不被刷新擦掉）");

                var aMd = new StringBuilder();
                aMd.AppendLine("# Plurk 表情共用表（描述一次，之後純文字查表）");
                aMd.AppendLine();
                aMd.AppendLine("> 機械投影：`" + Path.GetFileName(aFile) + "` 是真相源"
                    + "（改描述走 `--arg op=emoticons --arg emo_desc=<別名或URL片段>=<描述>`；本檔每次寫入重生成）。");
                aMd.AppendLine("> **鍵是圖檔 URL 不是編號**：`[emoN]` 是 per-account 別名，"
                    + "同一個編號在不同帳號是不同張圖 ⇒ 別名記在 `aliases` 欄，查表查 URL。");
                aMd.AppendLine("> `state=seen` ＝ 讀別人的噗時撞見的圖，**還沒有人看過它** ⇒ 那就是待描述清單。");
                aMd.AppendLine();
                int aDesc = iRows.Count(r => r.Desc.Length > 0);
                aMd.AppendLine($"- 共 **{iRows.Count}** 張／已描述 **{aDesc}**"
                    + $"／待描述 **{iRows.Count - aDesc}**");
                aMd.AppendLine();
                aMd.AppendLine("| 別名 | 全站碼 | 分層 | 描述 | 狀態 | 圖檔 |");
                aMd.AppendLine("|---|---|---|---|---|---|");
                foreach (var aRow in iRows.OrderBy(r => r.Tier).ThenBy(r => r.Code))
                    aMd.AppendLine($"| {(aRow.Aliases.Count == 0 ? "—" : "`" + string.Join("` `", aRow.Aliases) + "`")}"
                        + $" | `{(aRow.Code.Length == 0 ? "—" : aRow.Code)}` | {aRow.Tier}"
                        + $" | {(aRow.Desc.Length == 0 ? "*(未描述)*" : aRow.Desc)} | {aRow.State}"
                        + $" | [{EmoShort(aRow.Url)}]({aRow.Url}) |");
                File.WriteAllText(EmoTableMd(), aMd.ToString(), new UTF8Encoding(false));
                ioR.AppendLine($"- 📋 人可讀投影: `{EmoTableMd()}`");
            }
            catch (Exception ex)
            {
                // 寫不進去不影響這次的讀數，但要說 —— 不然下次讀不到會變成沒人解釋得了的謎
                ioR.AppendLine($"- ⚠ 本地表寫入失敗（不影響本次讀數）：{ex.Message}");
            }
        }

        static readonly System.Text.RegularExpressions.Regex EmoAliasRe =
            new System.Text.RegularExpressions.Regex(@"^emo\d+$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        static readonly System.Text.RegularExpressions.Regex EmoTokenRe =
            new System.Text.RegularExpressions.Regex(@"\[emo\d+\]",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // 表情圖只從這兩個 host 來（`s.plurk.com/emoticons/...` 是全站表情，
        // `emos.plurk.com/...` 是自訂表情）。⚠ 一定要濾 host：
        // 同一段 HTML 裡還有**使用者上傳的圖片**（images.plurk.com），
        // 把它們算進來會讓配對整排錯開一格 —— 而錯開一格的結果每一個都看起來像答案。
        static readonly System.Text.RegularExpressions.Regex EmoImgRe =
            new System.Text.RegularExpressions.Regex(
                @"<img[^>]+src=[""'](?<u>https?://(?:emos\.plurk\.com|s\.plurk\.com/emoticons)/[^""']+)[""']",
                System.Text.RegularExpressions.RegexOptions.Compiled
                | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// 讀取端的反解析：拿同一筆噗的 `content`（HTML）與 `content_raw` **按序配對**，
        /// 把 `[emoN]` 標成 `[emoN]⟨描述⟩`（或 `⟨🖼 短碼⟩`／`⟨?配不上⟩`）。
        /// <para>為什麼不查本地表的別名：`emoN` 是 per-account 的，
        /// 別人的 `[emo17399]` 用我的表查會查到**錯**的那一個 —— 而錯的那個看起來一樣像答案。
        /// URL 才是跨帳號穩定的鍵。</para>
        /// <para>數量對不上時**每一個都標 `⟨?配不上⟩`**，不做「前 N 個先配」：
        /// 錯開一格的結果比沒有結果更貴。</para>
        /// </summary>
        static string EmoAnnotatePaired(string iRaw, string iHtml, EmoCtx iCtx, string iOwnerId)
        {
            if (string.IsNullOrEmpty(iRaw) || !EmoTokenRe.IsMatch(iRaw)) return iRaw;
            var aTokens = EmoTokenRe.Matches(iRaw);
            var aUrls = EmoImgRe.Matches(iHtml ?? "").Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Groups["u"].Value).ToList();
            bool aAligned = aUrls.Count == aTokens.Count;
            int aIdx = 0;
            return EmoTokenRe.Replace(iRaw, m =>
            {
                string aNote;
                if (!aAligned) aNote = "?配不上";
                else
                {
                    string aUrl = aUrls[aIdx];
                    string aKey = "url:" + aUrl;
                    if (!iCtx.Table.TryGetValue(aKey, out EmoRow aRow))
                    {
                        // 沒見過這張圖 ⇒ **登記**（描述留空）。
                        // 不登記的話「還有哪些沒看過」只存在於這一次的畫面上，關掉就沒了。
                        aRow = new EmoRow
                        {
                            Url = aUrl,
                            Tier = "seen",
                            State = "seen",
                            FirstSeen = DateTime.UtcNow.ToString("o"),
                        };
                        iCtx.Table[aKey] = aRow;
                        iCtx.NewSeen++;
                        iCtx.Dirty = true;
                    }
                    string aAlias = (string.IsNullOrEmpty(iOwnerId) ? "?" : iOwnerId)
                        + ":" + m.Value.Trim('[', ']');
                    if (!aRow.Aliases.Contains(aAlias)) { aRow.AddAlias(aAlias); iCtx.Dirty = true; }

                    if (aRow.Desc.Length > 0) { aNote = aRow.Desc; iCtx.Hit++; }
                    else { aNote = "未描述:" + EmoShort(aUrl); iCtx.Miss++; }
                }
                aIdx++;
                return m.Value + "⟨" + aNote + "⟩";
            });
        }

        /// <summary>目前 `custom` 有幾個。讀不到回 **-1**（不回 0 —— 「讀不到」與「沒有」不可以同形）。</summary>
        async UniTask<int> EmoCustomCountAsync(Dictionary<string, string> iCred, CancellationToken token)
        {
            var (aSt, aBody) = await CallAsync("/APP/Emoticons/get", iCred, null, token);
            if (aSt != 200) return -1;
            var aRoot = SafeParse(aBody);
            if (aRoot == null || !aRoot.IsObject || !aRoot.Contains("custom")) return -1;
            return aRoot["custom"] != null && aRoot["custom"].IsArray ? aRoot["custom"].Count : -1;
        }

        /// <summary>讀取端起手：載共用表（必要時從舊 per-account 檔搬一次）。</summary>
        static EmoCtx EmoBegin(UCL_PlurkAccountResolution iRes)
            => new EmoCtx { Table = LoadEmoTable(iRes.SecretId ?? "_") };

        /// <summary>
        /// 讀取端收尾：**有新登記就落盤**，並把三個數印出來（命中／待描述／新登記）。
        /// <para>⚠ 這讓唯讀 op 產生一個本地寫入 —— 所以它一定要印出來。
        /// 靜默寫檔會讓「唯讀」這個標籤比事實大。</para>
        /// </summary>
        static void EmoEnd(EmoCtx iCtx, StringBuilder ioR)
        {
            if (iCtx == null) return;
            if (iCtx.Hit + iCtx.Miss + iCtx.NewSeen == 0) return;
            ioR.AppendLine();
            ioR.AppendLine($"### 🙂 表情查表：命中 **{iCtx.Hit}**／待描述 **{iCtx.Miss}**"
                + $"／本次新登記 **{iCtx.NewSeen}** 張");
            if (iCtx.Dirty)
            {
                SaveEmoTable(iCtx.Table.Values.ToList(), ioR);
                ioR.AppendLine("- ⚠ 本 op 對 Plurk 是唯讀，但**寫了本地共用表**（新圖登記／別名補齊）——"
                    + "這一行就是那個寫入的讀數。");
            }
            var aTodo = iCtx.Table.Values.Where(r => r.Desc.Length == 0 && r.State == "seen")
                .OrderBy(r => r.FirstSeen).Take(8).ToList();
            if (aTodo.Count > 0)
            {
                ioR.AppendLine("- 待描述（看一次圖，之後永遠查表）:");
                foreach (var aRow in aTodo)
                    ioR.AppendLine($"    · {aRow.Url}"
                        + (aRow.Aliases.Count == 0 ? "" : "　（" + string.Join(" ", aRow.Aliases) + "）"));
                ioR.AppendLine("    ⇒ 描述寫回: `--arg op=emoticons --arg emo_desc=<URL片段>=<描述>`");
            }
        }

        /// <summary>圖檔 URL 的短碼（檔名前 8 碼）—— 給人眼比對「這兩個 `[emoN]` 是不是同一張圖」。</summary>
        static string EmoShort(string iUrl)
        {
            if (string.IsNullOrEmpty(iUrl)) return "?";
            int aSlash = iUrl.LastIndexOf('/');
            string aName = aSlash >= 0 ? iUrl.Substring(aSlash + 1) : iUrl;
            return aName.Length <= 8 ? aName : aName.Substring(0, 8);
        }


        // ===========================================================
        // 區塊職責：試著新增一個自訂表情。
        // ⚠ 這支是**未驗證的嘗試**，不是已知可用的功能：
        //   官方 API 頁（2026-08-24，200）的 Emoticons 章節**只有** `get`，
        //   一個新增用的端點都沒有。所以這裡送的路徑取自社群慣例，
        //   而它的三種失敗（端點不存在／簽章錯／WAF 擋）**全都是 4xx，長得一樣**。
        // ⇒ 判準：不論成功或失敗，把 http 碼與 body 原樣印出來，**不翻譯成「成功／不支援」**。
        // 數值影響：若真的成立，這會在帳號上新增一個自訂表情（對外、可見）⇒ 要 `confirm=1`。
        // ===========================================================
        async UniTask OpEmoAdd(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, CancellationToken token)
        {
            string aUrl = GetArg(iArgs, "url", "").Trim();
            string aAlias = GetArg(iArgs, "alias", "").Trim();
            if (aUrl.Length == 0 || aAlias.Length == 0)
                throw new Exception("[Plurk] op=emoadd 需要 --arg url=<圖檔網址> 與 --arg alias=<表情代碼>");
            var aCred = RequireCredentials(iRes);

            ioR.AppendLine();
            ioR.AppendLine("## emoadd（**未驗證的嘗試** —— 官方 API 頁沒有這個端點）");
            ioR.AppendLine($"- alias: `{aAlias}`　url: `{aUrl}`");
            ioR.AppendLine("- ⚠ 官方 `/APP/API` 的 Emoticons 章節只有 `get`（2026-08-24 顯式 UA 讀回 200 確認）。");
            ioR.AppendLine("  ⇒ 下面每一個候選端點的 4xx **不能**當成「Plurk 不支援」——"
                + "端點不存在／簽章錯／WAF 擋在這裡長得一樣。");

            if (GetArg(iArgs, "confirm", "") != "1")
            {
                ioR.AppendLine("- 🛑 dry-run（沒帶 `confirm=1`）⇒ 一個請求都沒送。");
                return;
            }

            // ① 動手**之前**先數一次 —— before/after 才是「這一次加成功了」的證據。
            //    🩸 2026-08-24 首版只驗「送出的 alias 有沒有出現在回讀裡」，而 Plurk **不吃我給的
            //    alias**（它自己回 `{"success_text":"ok","keyword":"emo7"}` 自動編號）⇒
            //    那一行印「否 ← 沒生效」，而事實是 custom 從 6 變成 7，**加成功了**。
            //    ⇒ 判準：驗收要問「這個動作有沒有發生」，不是「我猜的那個副作用有沒有出現」。
            int aBefore = await EmoCustomCountAsync(aCred, token);
            ioR.AppendLine($"- 動手前 `custom` 數量: **{(aBefore < 0 ? "讀不到" : aBefore.ToString())}**");

            // 兩個候選端點都試：先 addFromURL（社群慣例），再 add。
            // 兩個都印讀數 —— 只試一個然後說「不支援」，那是拿一條路徑的結果替整個世界作答。
            string aKeyword = "";
            string[] aCandidates = { "/APP/Emoticons/addFromURL", "/APP/Emoticons/add" };
            foreach (string aEndpoint in aCandidates)
            {
                var aParams = new Dictionary<string, string> { { "url", aUrl }, { "alias", aAlias } };
                var (aSt, aBody) = await CallAsync(aEndpoint, aCred, aParams, token);
                ioR.AppendLine($"- `POST {aEndpoint}`　http: **{aSt}**　body（前 200 字）: {Trunc(aBody, 200)}");
                if (aSt == 200 && aKeyword.Length == 0)
                    aKeyword = PickJsonValue(aBody, "keyword") ?? "";
            }
            if (aKeyword.Length > 0)
                ioR.AppendLine($"- ⚠ Plurk **自己命名**成 `{aKeyword}`（我送的 alias `{aAlias}` 被忽略）"
                    + " ⇒ 文案裡要打的是 `[" + aKeyword + "]`，不是我取的那個名字。");

            ioR.AppendLine("- ▶ 回讀（`/APP/Emoticons/get` 的 `custom` 分組才是憑據，200 不是）:");
            var (aGetSt, aGetBody) = await CallAsync("/APP/Emoticons/get", aCred, null, token);
            var aRoot = SafeParse(aGetBody);
            bool aHasCustom = aRoot != null && aRoot.IsObject && aRoot.Contains("custom");
            int aAfter = aHasCustom && aRoot["custom"] != null && aRoot["custom"].IsArray
                ? aRoot["custom"].Count : -1;
            ioR.AppendLine($"    · http {aGetSt}　`custom` 分組: "
                + (aHasCustom ? $"有，{(aAfter < 0 ? "不是陣列（格式與預期不同）" : aAfter + " 個")}"
                    : "**沒有這個分組**"));
            ioR.AppendLine($"    · 數量 **{(aBefore < 0 ? "?" : aBefore.ToString())} → "
                + $"{(aAfter < 0 ? "?" : aAfter.ToString())}**　"
                + (aBefore >= 0 && aAfter == aBefore + 1 ? "✅ 加了一個（這是直接證據）"
                    : aBefore >= 0 && aAfter == aBefore ? "⛔ **沒變 ⇒ 沒生效**"
                    : "⚠ 兩個讀數之一沒拿到 ⇒ 這一格沒有證據，不當成成功"));
            if (aKeyword.Length > 0)
                ioR.AppendLine($"    · 回傳的 `{aKeyword}` 出現在 custom 清單裡: "
                    + (aHasCustom && (aGetBody ?? "").Contains("\"" + aKeyword + "\"") ? "**是**" : "**否**"));
            ioR.AppendLine("- ⚠ API **沒有刪除端點** ⇒ 加錯了只能上網頁 UI 收拾。");
            ioR.AppendLine("- ▶ 下一步：跑 `--arg op=emoticons` 把它併進共用表（順手補描述）。");
        }

        // ===========================================================
        // 區塊職責：按讚／取消讚 —— **對別人的東西動手**，所以守衛比讀取那幾個嚴。
        // 物理意義：這是一個對外、別人看得到、而且掛在我們帳號名下的動作。
        // 數值影響：改 Plurk 上的 favorite 狀態。三道守衛：
        //   ① `confirm=1` 才真的送（跟 op=post 同一條規矩）
        //   ② 送之前先 **getPlurk 把那則印出來** —— 「我要按的是這則」要看得見，防 id 打錯
        //      （數字打錯不會有任何一層喊，而它會按到一個陌生人的噗）
        //   ③ 送之後 **回讀** —— 印 ✓ 不算數，讀回來才算
        // ===========================================================
        async UniTask OpFavorite(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, CancellationToken token, bool iOn)
        {
            string aVerb = iOn ? "like" : "unlike";
            string aId = GetArg(iArgs, "plurk_id", "").Trim();
            if (aId.Length == 0) throw new Exception($"[Plurk] op={aVerb} 需要 --arg plurk_id=<噗 id>");
            var aCred = RequireCredentials(iRes);

            ioR.AppendLine();
            ioR.AppendLine($"## {aVerb}（對外動作 —— 別人看得到，而且掛在這個帳號名下）");

            // ① 先看清楚要動的是哪一則
            var (aSt0, aBefore) = await CallAsync("/APP/Timeline/getPlurk", aCred,
                new Dictionary<string, string> { { "plurk_id", aId } }, token);
            if (aSt0 != 200)
            {
                ioR.AppendLine($"- ✗ 讀不到 `{aId}`（http={aSt0}）⇒ **不動作**。" + Trunc(aBefore, 200));
                throw new Exception($"[Plurk] {aVerb} 前置讀取失敗 http={aSt0}");
            }
            string aObj = ExtractObject(aBefore, "plurk");
            string aFavBefore = PickJsonValue(aObj, "favorite_count") ?? "?";
            ioR.AppendLine($"- 目標: `{aId}`　owner_id: {PickJsonValue(aObj, "owner_id") ?? "?"}"
                + $"　目前 favorite_count: **{aFavBefore}**");
            ioR.AppendLine("- 內容首行: "
                + Trunc(FirstLine(UnescapeJson(PickJsonValue(aObj, "content_raw") ?? "")), 60));

            // ② confirm 守衛
            if (GetArg(iArgs, "confirm", "") != "1")
            {
                ioR.AppendLine();
                ioR.AppendLine("- 🛑 **dry-run（沒有送出）** —— 這是對別人的東西動手，跟 `op=post` 同一條規矩。");
                ioR.AppendLine("  要真的做請加 `--arg confirm=1`。");
                return;
            }

            string aEndpoint = iOn ? "/APP/Timeline/favoritePlurks" : "/APP/Timeline/unfavoritePlurks";
            var (aSt, aBody) = await CallAsync(aEndpoint, aCred,
                new Dictionary<string, string> { { "ids", "[" + aId + "]" } }, token);
            ioR.AppendLine($"- endpoint: `POST {aEndpoint}`　http: **{aSt}**");
            if (aSt != 200)
            {
                ioR.AppendLine("- ✗ body（前 300 字）: " + Trunc(aBody, 300));
                throw new Exception($"[Plurk] {aVerb} 失敗 http={aSt}");
            }

            // ③ 回讀 —— 200 只證明對方收到請求
            var (aSt2, aAfter) = await CallAsync("/APP/Timeline/getPlurk", aCred,
                new Dictionary<string, string> { { "plurk_id", aId } }, token);
            string aObj2 = aSt2 == 200 ? ExtractObject(aAfter, "plurk") : null;
            string aFavAfter = aObj2 == null ? "(回讀失敗)" : (PickJsonValue(aObj2, "favorite_count") ?? "?");
            ioR.AppendLine($"- 回讀 favorite_count: **{aFavBefore} → {aFavAfter}**"
                + "　⚠ 這是**總數**不是「我按了沒」—— 同時有別人按或收回時它不是乾淨的證據");
            string aFavFlag = aObj2 == null ? null : PickJsonValue(aObj2, "favorite");
            ioR.AppendLine(aFavFlag == null
                ? "- ⚠ 回應裡沒有 `favorite` 這個欄位 ⇒ **「我按了沒」這一格沒有讀數**（不是「沒按到」）"
                : $"- `favorite`（就這個帳號而言）: **{aFavFlag}**　← 這才是直接證據");
        }

        // ===========================================================
        // 區塊職責：**擴圈**（Tim 2026-08-24）—— 找到有興趣的陌生人、看清楚他是誰、送出關係請求。
        // 物理意義：在這之前這支 Cmd 的社交面只到「好友之間」。而好友清單是個封閉集合：
        //          它能告訴妳誰已經在裡面，說不出**誰可能該進來**。
        // 數值影響：`profile` / `expand` / `search` / `alerts` 純唯讀；
        //          `befriend` / `unfriend` / `follow` / `unfollow` / `accept` / `deny` **改關係**，
        //          一律要 `confirm=1`，且送出前把「那個人是誰」印成人看得懂的東西。
        //
        // ⚠ 端點名的驗證狀態：本區塊那幾支是 **2026-08-24 首次接上**，
        //   事實來源仍在 `Docs~/{lang}/Workflows/Plurk_Maintenance.md` §5（別在這裡另記一份）。
        //   ⇒ 所以每一支的非 200 都**把 body 印出來**：
        //     「端點不存在」「簽章錯」「被 WAF 擋」三種失敗都是 4xx，長得一樣。
        // ⛔ 這裡不做「全部同意」「批次加好友」：
        //   「該不該加這個人」機器判不了，而批次動作會讓那一格沒有人看過。
        // ===========================================================

        /// <summary>
        /// 印一張「這個人是誰」的卡：顯示名／帳號／自介／近期一則噗／關係現況。
        /// <para>物理意義：對外動作的第一道守衛。id 錯一位不會有任何一層喊，
        /// 而它會把請求送給一個陌生人 —— 所以送出前印的是**人**，不是 user_id。</para>
        /// <para>回傳 profile 的原始 body（沒讀到回 null），讓呼叫端能再撈關係欄位。</para>
        /// </summary>
        async UniTask<string> PersonCardAsync(string iUserId, Dictionary<string, string> iCred,
            StringBuilder ioR, CancellationToken token)
        {
            var (aSt, aBody) = await CallAsync("/APP/Profile/getPublicProfile", iCred,
                new Dictionary<string, string> { { "user_id", iUserId } }, token);
            if (aSt != 200)
            {
                ioR.AppendLine($"- ⚠ 讀不到 `{iUserId}` 的公開檔（http={aSt}）"
                    + "⇒ **這一格沒有讀數**，不是「這個人不存在」。");
                ioR.AppendLine("  body（前 300 字）: " + Trunc(aBody, 300));
                return null;
            }
            var aRoot = SafeParse(aBody);
            var aInfo = (aRoot != null && aRoot.Contains("user_info")) ? aRoot["user_info"] : null;
            if (aInfo == null)
            {
                ioR.AppendLine("- ⚠ 回應裡沒有 `user_info` —— 格式跟我預期的不一樣（不是「查無此人」）。");
                ioR.AppendLine("  body（前 300 字）: " + Trunc(aBody, 300));
                return aBody;
            }
            ioR.AppendLine($"- 👤 **{UnescapeJson(JsonScalar(aInfo, "display_name"))}**"
                + $"（`{JsonScalar(aInfo, "nick_name")}` / id `{JsonScalar(aInfo, "id")}`）"
                + $"　karma {JsonScalar(aInfo, "karma")}");
            string aAbout = OneLine(StripTags(UnescapeJson(JsonScalar(aInfo, "about")))).Trim();
            ioR.AppendLine("- 自介: " + (aAbout.Length == 0 ? "(空)" : Trunc(aAbout, 160)));
            ioR.AppendLine($"- 好友 {JsonScalar(aRoot, "friends_count")}"
                + $" / 粉絲 {JsonScalar(aRoot, "fans_count")}"
                + $"　關係現況: {RelationText(aRoot)}");
            // 近期噗 —— 「這個人在寫什麼」比「他有幾個好友」重要得多
            var aPlurks = aRoot.Contains("plurks") ? aRoot["plurks"] : null;
            if (aPlurks != null && aPlurks.IsArray && aPlurks.Count > 0)
            {
                int aShow = Math.Min(3, aPlurks.Count);
                ioR.AppendLine($"- 近期噗（{aShow}/{aPlurks.Count} 則，各截 100 字）:");
                for (int i = 0; i < aShow; i++)
                {
                    string aTxt = OneLine(UnescapeJson(JsonScalar(aPlurks[i], "content_raw"))).Trim();
                    // 🩸 2026-08-24：首版沒印 plurk_id ⇒ 這張卡「看得到、回不了」——
                    //   要回應誰得先有 id，而卡上沒有 id 就得再繞一趟 timeline／search 去湊。
                    ioR.AppendLine($"    · `{JsonScalar(aPlurks[i], "plurk_id")}`"
                        + $" [{ShortTime(JsonScalar(aPlurks[i], "posted"))}] "
                        + (aTxt.Length == 0 ? "(沒有文字內容)" : Trunc(aTxt, 100)));
                }
            }
            else
            {
                ioR.AppendLine("- 近期噗: **沒有讀到**（可能是不公開，也可能是格式不同 —— 這兩件事我分不出來）");
            }
            return aBody;
        }

        /// <summary>
        /// 關係現況：把 profile 回應裡幾個布林欄位收成一句人話。
        /// <para>⚠ 欄位都不在時回「沒有讀數」而**不是**「不是好友」——
        /// 那兩件事的處置完全不同（一個是要去查，一個是可以送請求）。</para>
        /// </summary>
        static string RelationText(UCL.Core.JsonLib.JsonData iRoot)
        {
            if (iRoot == null) return "(沒有讀數)";
            var aBits = new List<string>();
            foreach (string aKey in new[] { "are_friends", "is_fan", "is_following", "has_read_permission" })
                if (iRoot.Contains(aKey)) aBits.Add($"{aKey}={JsonScalar(iRoot, aKey)}");
            return aBits.Count == 0 ? "(沒有讀數 —— 不等於「不是好友」)" : string.Join(" / ", aBits);
        }

        /// <summary>自介欄位是 HTML ⇒ 粗暴剝標籤。只求能讀，不求正確渲染。</summary>
        static string StripTags(string iHtml)
        {
            if (string.IsNullOrEmpty(iHtml)) return "";
            var sb = new StringBuilder(iHtml.Length);
            bool aIn = false;
            foreach (char c in iHtml)
            {
                if (c == '<') { aIn = true; continue; }
                if (c == '>') { aIn = false; sb.Append(' '); continue; }
                if (!aIn) sb.Append(c);
            }
            return sb.ToString();
        }

        // ── 唯讀：看一個人 ────────────────────────────────────────
        async UniTask OpProfile(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, CancellationToken token)
        {
            string aUserId = GetArg(iArgs, "user_id", "").Trim();
            if (aUserId.Length == 0)
                throw new Exception("[Plurk] op=profile 需要 --arg user_id=<誰>"
                    + "（從 op=friends / op=expand / op=search 的清單抄）");
            var aCred = RequireCredentials(iRes);
            ioR.AppendLine();
            ioR.AppendLine($"## profile（`{aUserId}` 的公開檔 —— 唯讀）");
            await PersonCardAsync(aUserId, aCred, ioR, token);
            ioR.AppendLine();
            ioR.AppendLine("### ▶ 下一步");
            ioR.AppendLine("```bash");
            ioR.AppendLine("--arg op=follow   --arg user_id=<id> --arg confirm=1   # 單向追蹤，不需對方同意");
            ioR.AppendLine("--arg op=befriend --arg user_id=<id> --arg confirm=1   # 送好友請求（對方會收到通知）");
            ioR.AppendLine("```");
        }

        // ── 唯讀：擴圈（好友的好友，按共同好友數排序）────────────
        // 🩸 判準：這裡**只算共同好友數**、只讀公開發文，不做別的資料拼合、不建檔。
        //    快取照現行規矩不入 git —— 那些是陌生人的東西，他們沒有同意過被釘進我們的歷史。
        async UniTask OpExpand(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, CancellationToken token)
        {
            var aCred = RequireCredentials(iRes);
            int aTop = ParseIntArg(iArgs, "top", 15, 1, 100);
            int aPerFriend = ParseIntArg(iArgs, "limit", 100, 1, 100);
            int aHops = ParseIntArg(iArgs, "hops", 8, 1, 50);   // 最多向外問幾位好友（省 API 呼叫）

            // ① 我是誰、我的好友有誰 —— 兩個都讀，不推
            var (aMeSt, aMe) = await CallAsync("/APP/Users/me", aCred, null, token);
            if (aMeSt != 200) throw new Exception($"[Plurk] 問不到自己的 user_id（http={aMeSt}）");
            string aMeId = PickJsonValue(aMe, "id") ?? "";
            if (aMeId.Length == 0) throw new Exception("[Plurk] /APP/Users/me 沒有 id 欄位");

            var (aFrSt, aFrBody) = await CallAsync("/APP/FriendsFans/getFriendsByOffset", aCred,
                new Dictionary<string, string>
                {
                    { "user_id", aMeId }, { "offset", "0" },
                    { "limit", aPerFriend.ToString(CultureInfo.InvariantCulture) },
                }, token);
            var aMine = SafeParse(aFrBody);
            ioR.AppendLine();
            ioR.AppendLine("## expand（好友的好友 —— 唯讀，按共同好友數排序）");
            if (aFrSt != 200 || aMine == null || !aMine.IsArray)
            {
                ioR.AppendLine($"- ⚠ 拿不到自己的好友清單（http={aFrSt}）⇒ 這一格沒有讀數。");
                ioR.AppendLine("- body（前 300 字）: " + Trunc(aFrBody, 300));
                return;
            }

            var aKnown = new HashSet<string> { aMeId };      // 已經是好友的＋我自己 ⇒ 不列
            var aSeed = new List<(string id, string name)>();
            for (int i = 0; i < aMine.Count; i++)
            {
                string aId = JsonScalar(aMine[i], "id");
                if (aId.Length == 0) continue;
                aKnown.Add(aId);
                aSeed.Add((aId, UnescapeJson(JsonScalar(aMine[i], "display_name"))));
            }
            ioR.AppendLine($"- 我（`{aMeId}`）的好友 **{aSeed.Count}** 位（讀的，不是推的）");

            // ② 向外一跳。⚠ 只問前 aHops 位 —— 而且**把沒問的那幾位說出來**：
            //    靜默截斷會讓「掃過全部」與「掃了一半」在回傳檔上同形。
            int aAsk = Math.Min(aHops, aSeed.Count);
            if (aAsk < aSeed.Count)
                ioR.AppendLine($"- ⚠ 只向外問了前 **{aAsk}/{aSeed.Count}** 位好友"
                    + $"（`--arg hops={aSeed.Count}` 問完）—— 這不是全圖，是取樣。");

            var aCount = new Dictionary<string, int>();
            var aVia = new Dictionary<string, List<string>>();
            var aName = new Dictionary<string, string>();
            var aNick = new Dictionary<string, string>();
            int aFailed = 0;
            for (int i = 0; i < aAsk; i++)
            {
                var (aSt, aBody) = await CallAsync("/APP/FriendsFans/getFriendsByOffset", aCred,
                    new Dictionary<string, string>
                    {
                        { "user_id", aSeed[i].id }, { "offset", "0" },
                        { "limit", aPerFriend.ToString(CultureInfo.InvariantCulture) },
                    }, token);
                var aList = SafeParse(aBody);
                if (aSt != 200 || aList == null || !aList.IsArray)
                {
                    aFailed++;
                    ioR.AppendLine($"- ⚠ `{aSeed[i].name}`（{aSeed[i].id}）的好友清單讀不到（http={aSt}）"
                        + " ⇒ 他那一票沒進統計");
                    continue;
                }
                for (int j = 0; j < aList.Count; j++)
                {
                    string aId = JsonScalar(aList[j], "id");
                    if (aId.Length == 0 || aKnown.Contains(aId)) continue;   // 已是好友／我自己 ⇒ 不是候選
                    aCount[aId] = (aCount.TryGetValue(aId, out int c) ? c : 0) + 1;
                    if (!aVia.TryGetValue(aId, out var aL)) { aL = new List<string>(); aVia[aId] = aL; }
                    aL.Add(aSeed[i].name);
                    aName[aId] = UnescapeJson(JsonScalar(aList[j], "display_name"));
                    aNick[aId] = JsonScalar(aList[j], "nick_name");
                }
            }

            ioR.AppendLine($"- 候選陌生人 **{aCount.Count}** 位"
                + $"（來自 {aAsk - aFailed} 份好友清單{(aFailed > 0 ? $"，{aFailed} 份讀不到" : "")}）");
            if (aCount.Count == 0)
            {
                ioR.AppendLine("- 沒有候選 —— 而這**可能**是「好友的好友都已經是我的好友」，"
                    + "也可能是上面那幾份清單讀不到。兩者我分不出來，所以不下結論。");
                return;
            }
            ioR.AppendLine();
            ioR.AppendLine($"### 前 {Math.Min(aTop, aCount.Count)} 名（共同好友數 ↓）");
            ioR.AppendLine("> ⚠ 共同好友數是**排序訊號**，不是「該加」的判準。");
            ioR.AppendLine("> 要不要加，得先 `op=profile` 讀他在寫什麼 —— 那一格機器判不了。");
            ioR.AppendLine();
            var aRank = aCount.OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(aTop).ToList();
            // 🩸 2026-08-24 首跑：最高分 3，而前 15 名**全部都是 3** ⇒ 名次其實由 tie-break（id 字串序）決定，
            //   也就是「帳號註冊得早」被印成了「比較推薦」。分數看起來像排名，而它不是。
            //   ⇒ 把平手狀況印出來：同分幾位、這一頁切在哪。**排序的解析度要自己講清楚。**
            int aTopScore = aRank[0].Value;
            int aTie = aCount.Count(kv => kv.Value == aTopScore);
            ioR.AppendLine($"- 最高共同好友數 **{aTopScore}**，同分 **{aTie}** 位"
                + (aTie > aTop
                    ? $"　⚠ 而我只列 {aTop} 位 ⇒ **這一頁的名次是 tie-break（id 序）決定的，不是推薦度**。"
                      + "　同分的人之間這個分數區分不了他們，要挑得靠 `op=profile` 讀內容。"
                    : ""));
            ioR.AppendLine("- ⚠ 本表**不知道誰已經被送過請求** —— pending 的人會照樣出現在這裡"
                + "（`op=profile` 的 `are_friends=false` 也分不出「沒送過」與「送了他沒理」）。");
            ioR.AppendLine();
            foreach (var kv in aRank)
            {
                var aList = aVia[kv.Key];
                ioR.AppendLine($"- `{kv.Key}`　**{(aName[kv.Key].Length == 0 ? "(查無名稱)" : aName[kv.Key])}**"
                    + $"（{aNick[kv.Key]}）　共同好友 **{kv.Value}**"
                    + $"　← 經由 {string.Join("／", aList.Take(4))}"
                    + (aList.Count > 4 ? $" 等 {aList.Count} 位" : ""));
            }
            ioR.AppendLine();
            ioR.AppendLine("### ▶ 下一步（id 抄上面那個）");
            ioR.AppendLine("```bash");
            ioR.AppendLine("--arg op=profile  --arg user_id=<id>                    # 先讀他在寫什麼（唯讀）");
            ioR.AppendLine("--arg op=follow   --arg user_id=<id> --arg confirm=1    # 單向追蹤，不打擾對方");
            ioR.AppendLine("--arg op=befriend --arg user_id=<id> --arg confirm=1    # 送好友請求");
            ioR.AppendLine("```");
        }

        // ── 唯讀：搜尋（找主題，而不是找人）────────────────────────
        async UniTask OpSearch(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, CancellationToken token)
        {
            string aQuery = GetArg(iArgs, "query", "").Trim();
            if (aQuery.Length == 0)
                throw new Exception("[Plurk] op=search 需要 --arg query=<關鍵字>");
            string aKind = GetArg(iArgs, "kind", "plurk").Trim().ToLowerInvariant();
            if (aKind != "plurk" && aKind != "user")
                throw new Exception($"[Plurk] --arg kind={aKind} 只吃 plurk|user（不靜默取預設值）");
            var aCred = RequireCredentials(iRes);
            int aOffset = ParseIntArg(iArgs, "offset", 0, 0, 100000);
            int aPreview = ParseIntArg(iArgs, "preview", 90, 20, 400);

            string aEndpoint = aKind == "user" ? "/APP/UserSearch/search" : "/APP/PlurkSearch/search";
            var aParams = new Dictionary<string, string> { { "query", aQuery } };
            if (aOffset > 0) aParams["offset"] = aOffset.ToString(CultureInfo.InvariantCulture);

            var (aSt, aBody) = await CallAsync(aEndpoint, aCred, aParams, token);
            ioR.AppendLine();
            ioR.AppendLine($"## search kind={aKind}（唯讀）　query: `{aQuery}`");
            ioR.AppendLine($"- endpoint: `POST {aEndpoint}`　http: **{aSt}**");
            if (aSt != 200)
            {
                ioR.AppendLine("- ✗ body（前 400 字）: " + Trunc(aBody, 400));
                ioR.AppendLine("- ⚠ 排查順序：**先確認端點存在 → 再懷疑簽章 → 最後才是 WAF**"
                    + "（三種失敗都是 4xx，而 WAF 那格看 body 不看 status）。");
                throw new Exception($"[Plurk] search 失敗 http={aSt}");
            }
            var aRoot = SafeParse(aBody);
            var aList = (aRoot != null && aRoot.Contains(aKind == "user" ? "users" : "plurks"))
                ? aRoot[aKind == "user" ? "users" : "plurks"] : null;
            if (aList == null || !aList.IsArray)
            {
                ioR.AppendLine($"- ⚠ 回應裡沒有 `{(aKind == "user" ? "users" : "plurks")}` 陣列 ——"
                    + " 這是**格式跟我預期的不一樣**，不是「搜不到」。");
                ioR.AppendLine("- body（前 400 字）: " + Trunc(aBody, 400));
                return;
            }
            ioR.AppendLine($"- **{aList.Count}** 筆（offset={aOffset}）");
            ioR.AppendLine();
            // 🩸 2026-08-24：河道那支的 user 字典叫 `plurk_users`，而**搜尋這支不叫那個** ⇒
            //   首跑 30 筆作者全印「查無名稱」。UserName 那格已經守住了（不回空字串），
            //   所以我看得出是「我沒查到作者」而不是「這則沒有作者」—— 兩個候選鍵都試。
            var aUsers = (aRoot != null && aRoot.Contains("plurk_users")) ? aRoot["plurk_users"]
                : ((aRoot != null && aRoot.Contains("users")) ? aRoot["users"] : null);
            if (aUsers == null && aKind == "plurk")
                ioR.AppendLine("- ⚠ 回應裡沒有 `plurk_users` 也沒有 `users` ⇒ 作者名這一欄**沒有讀數**"
                    + "（下面印的 id 仍可直接餵 `op=profile`）");
            for (int i = 0; i < aList.Count; i++)
            {
                var aIt = aList[i];
                if (aKind == "user")
                {
                    ioR.AppendLine($"- `{JsonScalar(aIt, "id")}`"
                        + $"　**{UnescapeJson(JsonScalar(aIt, "display_name"))}**"
                        + $"（{JsonScalar(aIt, "nick_name")}）"
                        + $"　karma {JsonScalar(aIt, "karma")}");
                    continue;
                }
                string aTxt = OneLine(UnescapeJson(JsonScalar(aIt, "content_raw"))).Trim();
                ioR.AppendLine($"- **[{JsonScalar(aIt, "plurk_id")}]** {ShortTime(JsonScalar(aIt, "posted"))}"
                    + $" **{UserName(aUsers, JsonScalar(aIt, "owner_id"))}**"
                    + $"　（owner_id `{JsonScalar(aIt, "owner_id")}`）");
                ioR.AppendLine("    " + (aTxt.Length == 0 ? "(沒有文字內容)" : Trunc(aTxt, aPreview)));
            }
            ioR.AppendLine();
            ioR.AppendLine("⚠ 搜到的是**噗**不是人 ⇒ 覺得對盤先 `op=profile --arg user_id=<owner_id>` 讀他這個人，");
            ioR.AppendLine("　 再決定追蹤或送請求。**摘要是截斷過的，別對著開頭下判斷。**");
        }

        // ── 唯讀：誰在等我回應 ────────────────────────────────────
        async UniTask OpAlerts(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, CancellationToken token)
        {
            var aCred = RequireCredentials(iRes);
            bool aHistory = GetArg(iArgs, "history", "") == "1";
            string aEndpoint = aHistory ? "/APP/Alerts/getHistory" : "/APP/Alerts/getActive";
            var (aSt, aBody) = await CallAsync(aEndpoint, aCred, null, token);

            ioR.AppendLine();
            ioR.AppendLine($"## alerts（{(aHistory ? "歷史" : "待處理")}）");
            // 🩸 2026-08-24 實測：我把 getActive 標成「唯讀」，而**它不是** ——
            //   第一次讀回 4 筆（2 pending ＋ plurk_liked ＋ my_responded），
            //   第二次同一支指令只剩 2 筆。⇒ 讀這一支會把通知清掉（friendship_pending 留著，其餘消失）。
            //   ⇒ 這格是「讀取有副作用」，而副作用不可逆（清掉的通知不會回來）。
            ioR.AppendLine("- ⚠ **這一支不是唯讀** —— 實測第二次呼叫少了兩筆：");
            ioR.AppendLine("  `getActive` 會把讀到的通知**清掉**（friendship_pending 會留，按讚／回應類不會）。");
            ioR.AppendLine("  ⇒ 不要當成可重跑的查詢用；要看歷史走 `--arg history=1`。");
            ioR.AppendLine($"- endpoint: `POST {aEndpoint}`　http: **{aSt}**");
            if (aSt != 200)
            {
                ioR.AppendLine("- ✗ body（前 400 字）: " + Trunc(aBody, 400));
                throw new Exception($"[Plurk] alerts 失敗 http={aSt}");
            }
            var aRoot = SafeParse(aBody);
            if (aRoot == null || !aRoot.IsArray)
            {
                ioR.AppendLine("- ⚠ 回應不是陣列 —— 格式跟我預期的不一樣（**不是「沒有通知」**）。");
                ioR.AppendLine("- body（前 400 字）: " + Trunc(aBody, 400));
                return;
            }
            ioR.AppendLine($"- **{aRoot.Count}** 筆"
                + (aRoot.Count == 0 ? "（真的空 —— 這是讀回來的 0，不是讀不到）" : ""));
            for (int i = 0; i < aRoot.Count; i++)
            {
                var aIt = aRoot[i];
                string aType = JsonScalar(aIt, "type");
                // ⚠ 方向在欄位名裡，不在 type 裡（🩸 2026-08-24 我一開始只看 `from_user`）：
                //   `from_user` ＝ 別人對我做了什麼（要我處置）
                //   `to_user`   ＝ **我對別人做的還在等他** —— 對它跑 accept 是沒有意義的
                var aFrom = aIt.Contains("from_user") ? aIt["from_user"] : null;
                var aTo = aIt.Contains("to_user") ? aIt["to_user"] : null;
                var aWho = aFrom ?? aTo;
                string aDir = aFrom != null ? "⬅ 對方 → 我（要我處置）"
                    : (aTo != null ? "➡ 我 → 對方（**等他回應，我這邊沒事可做**）" : "(方向不明)");
                string aFromId = aWho != null ? JsonScalar(aWho, "id") : JsonScalar(aIt, "user_id");
                string aFromName = aWho != null ? UnescapeJson(JsonScalar(aWho, "display_name")) : "";
                ioR.AppendLine($"- «{aType}»　{aDir}　`{aFromId}`"
                    + $"　**{(aFromName.Length == 0 ? "(查無名稱)" : aFromName)}**"
                    + $"（{(aWho != null ? JsonScalar(aWho, "nick_name") : "")}）"
                    + $"　{ShortTime(JsonScalar(aIt, "posted"))}");
                // 🩸 2026-08-24：`friendship_pending` 的人不在 `from_user` 裡 ⇒ 上面那行印成
                //   「(查無名稱)」＋空 id。而「我不知道他是誰」跟「這筆沒有人」長得一樣，
                //   於是待處理的請求看起來像壞資料。⇒ 認不出人就把原始物件攤開，讓下一個人看得到真欄位名。
                if (aFromId.Length == 0)
                    ioR.AppendLine("    ⚠ 撈不到 user id ⇒ 原始欄位攤開（**不是這筆沒有人**）: "
                        + Trunc(OneLine(aIt.ToJson()), 400));
            }
            ioR.AppendLine();
            ioR.AppendLine("### ▶ 逐筆處理（⛔ 刻意沒有「全部同意」）");
            ioR.AppendLine("```bash");
            ioR.AppendLine("--arg op=profile --arg user_id=<id>                   # 先看他是誰");
            ioR.AppendLine("--arg op=accept  --arg user_id=<id> --arg confirm=1   # 同意");
            ioR.AppendLine("--arg op=deny    --arg user_id=<id> --arg confirm=1   # 拒絕");
            ioR.AppendLine("```");
        }

        // ── 對外：關係動作（befriend / unfriend / follow / unfollow / accept / deny）──
        // 守衛三道，跟 like 同一族：
        //   ① 送出前 `PersonCardAsync` 把那個人印成人看得懂的東西（防 id 打錯）
        //   ② `confirm=1` 才真的送（「我只是想看看」與「我要按下去」不得同形）
        //   ③ 送出後**回讀** profile 的關係欄位 —— 200 只證明對方收到請求
        async UniTask OpRelation(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, CancellationToken token, string iVerb)
        {
            string aUserId = GetArg(iArgs, "user_id", "").Trim();
            if (aUserId.Length == 0)
                throw new Exception($"[Plurk] op={iVerb} 需要 --arg user_id=<誰>");
            var aCred = RequireCredentials(iRes);

            // 端點與參數名一張表 —— 免得六個動作各自散在六段裡漂
            string aEndpoint, aParamKey, aWhat;
            switch (iVerb)
            {
                case "befriend": aEndpoint = "/APP/FriendsFans/becomeFriend"; aParamKey = "friend_id";
                    aWhat = "送出好友請求（對方會收到通知，要他同意才成立）"; break;
                case "unfriend": aEndpoint = "/APP/FriendsFans/removeAsFriend"; aParamKey = "friend_id";
                    aWhat = "解除好友"; break;
                case "follow": aEndpoint = "/APP/FriendsFans/becomeFan"; aParamKey = "fan_id";
                    aWhat = "**單向**追蹤（不需對方同意）"; break;
                // 🩸 2026-08-24：首版把 unfollow 也接到 becomeFan＋`follow=false` ——
                //   回 **200 ＋ `{"success_text":"ok"}`，而回讀 `is_following` 沒動**。
                //   多餘的參數被無聲吃掉，成功字串照樣印。⇒ 換成 setFollowing（它才吃 follow 旗標）。
                case "unfollow": aEndpoint = "/APP/FriendsFans/setFollowing"; aParamKey = "user_id";
                    aWhat = "取消追蹤"; break;
                case "accept": aEndpoint = "/APP/Alerts/addAsFriend"; aParamKey = "user_id";
                    aWhat = "同意對方的好友請求"; break;
                case "deny": aEndpoint = "/APP/Alerts/denyFriendship"; aParamKey = "user_id";
                    aWhat = "拒絕對方的好友請求"; break;
                default: throw new Exception($"[Plurk] 認不得的關係動作 '{iVerb}'");
            }

            ioR.AppendLine();
            ioR.AppendLine($"## {iVerb}（**對外動作 —— 改的是關係，而對方會知道**）");
            ioR.AppendLine($"- 動作: {aWhat}");
            ioR.AppendLine($"- 目標 user_id: `{aUserId}`");
            ioR.AppendLine();

            // ① 這個人是誰
            string aBefore = await PersonCardAsync(aUserId, aCred, ioR, token);

            // ② confirm 守衛
            if (GetArg(iArgs, "confirm", "") != "1")
            {
                ioR.AppendLine();
                ioR.AppendLine("- 🛑 **dry-run（沒有送出）** —— 上面那張卡就是這一步的用途：");
                ioR.AppendLine("  **確認我要動的是這個人**。id 錯一位不會有任何一層喊。");
                ioR.AppendLine("  要真的做請加 `--arg confirm=1`。");
                return;
            }

            var aParams = new Dictionary<string, string> { { aParamKey, aUserId } };
            // unfollow 走同一支 becomeFan 但 follow=false —— ⚠ 這一格未驗，回 4xx 就是它不對
            if (iVerb == "unfollow") aParams["follow"] = "false";
            var (aSt, aBody) = await CallAsync(aEndpoint, aCred, aParams, token);
            ioR.AppendLine();
            ioR.AppendLine($"- endpoint: `POST {aEndpoint}`　`{aParamKey}={aUserId}`　http: **{aSt}**");
            if (aSt != 200)
            {
                ioR.AppendLine("- ✗ body（前 400 字）: " + Trunc(aBody, 400));
                ioR.AppendLine("- ⚠ 端點名這一族是 2026-08-24 首次接上 ⇒ 4xx 先懷疑端點名／參數名，"
                    + "再懷疑簽章，最後才是 WAF（看 body 不看 status）。");
                throw new Exception($"[Plurk] {iVerb} 失敗 http={aSt}");
            }
            ioR.AppendLine("- body（前 200 字）: " + Trunc(aBody, 200));

            // ③ 回讀 —— 200 只證明對方收到請求
            var (aSt2, aAfter) = await CallAsync("/APP/Profile/getPublicProfile", aCred,
                new Dictionary<string, string> { { "user_id", aUserId } }, token);
            if (aSt2 != 200)
            {
                ioR.AppendLine($"- ⚠ 回讀失敗（http={aSt2}）⇒ **結果那本帳沒有讀數**"
                    + "（已送出 ≠ 已生效，這兩件事要分開報）。");
                return;
            }
            var aAfterJd = SafeParse(aAfter);
            string aRelBefore = aBefore == null ? "(沒有讀數)" : RelationText(SafeParse(aBefore));
            ioR.AppendLine($"- 回讀關係: `{aRelBefore}` → `{RelationText(aAfterJd)}`");

            // ── 結果那本帳 ──────────────────────────────────────
            // 🩸 2026-08-24：`unfollow` 回 200 ＋ `{"success_text":"ok"}`，而 `is_following` 沒動 ——
            //   多餘的參數被無聲吃掉，成功字串照樣印。**200 是「對方收到請求」，不是「事情發生了」。**
            //   ⇒ 每個動作宣告它該讓哪個欄位變成什麼；沒變就大聲說未生效，不准讓 200 代表結果。
            //   ⚠ befriend 是唯一「現在本來就不會變」的動作（要等對方同意）—— 它的證人在 op=alerts。
            string aField = null, aWant = null;
            switch (iVerb)
            {
                case "follow": aField = "is_following"; aWant = "true"; break;
                case "unfollow": aField = "is_following"; aWant = "false"; break;
                case "unfriend": aField = "are_friends"; aWant = "false"; break;
                case "accept": aField = "are_friends"; aWant = "true"; break;
            }
            if (aField == null)
            {
                ioR.AppendLine($"- ⚠ `{iVerb}` **現在不該有變化** —— 請求送到了與對方同意了是兩件事。"
                    + "前者的證人是 `op=alerts` 裡多一筆 `friendship_pending`（去看那個，別看這裡的 200）。");
                return;
            }
            string aGot = (aAfterJd != null && aAfterJd.Contains(aField)) ? JsonScalar(aAfterJd, aField) : null;
            if (aGot == null)
                ioR.AppendLine($"- ⚠ 回讀沒有 `{aField}` 欄位 ⇒ **結果那本帳沒有讀數**（不是「沒生效」）。");
            else if (aGot.Equals(aWant, StringComparison.OrdinalIgnoreCase))
                ioR.AppendLine($"- ✅ 結果: `{aField}` = **{aGot}**（期待 {aWant}）← 這才是生效的直接證據");
            else
                ioR.AppendLine($"- ⛔ **回 200 但沒生效**：`{aField}` = **{aGot}**，期待 `{aWant}`。"
                    + "　成功字串與實際狀態脫鉤 ⇒ 先懷疑參數名被無聲吃掉，再懷疑端點。");
        }

        // ── 讀取層：API ／ 本地快取 ────────────────────────────────
        // ⛔ 快取目錄不入 git —— 那裡面是**別人的**發文內容，而且是某一刻的快照。
        //    入版控等於把別人的時間軸釘進我們的歷史，而他們沒有同意過。
        // 🩸 2026-08-24：這行註解原本寫「`AgentCommands/.gitignore` 有 `Plurk/cache/`」——
        //    **而寫的當下它沒有**。ignore 規則是 @summit 當天才補上的（`.gitignore:32`）。
        //    我不只引用了一條不存在的規則，還在程式碼裡替它作證，而那行讀起來像讀數。
        //    ⇒ 判準：註解裡宣告「別處有一道防護」之前，去讀那一處；
        //      而**寫進註解不會讓那道防護存在**（那正是它最容易被誤讀成已完成的地方）。
        const string CacheRelative = "Plurk/cache";

        static string CacheDir() => Path.Combine(UCL_AgentCommandsPath.DataRoot, CacheRelative);

        static string CacheFile(string iAccount, string iKey)
            => Path.Combine(CacheDir(), SafeName(iAccount) + "__" + SafeName(iKey) + ".json");

        static string SafeName(string iText)
        {
            if (string.IsNullOrEmpty(iText)) return "_";
            var sb = new StringBuilder(iText.Length);
            foreach (char c in iText)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        /// <summary>
        /// 取資料：**預設打 API 並落快取**；`--arg cache=1` 才改讀快取。
        /// <para>⚠ 不論走哪一條，回傳檔一定印**資料來源與年齡** ——
        /// 「現況」與「快照」不可以同形，而它們天生就同形。</para>
        /// </summary>
        async UniTask<string> FetchAsync(Dictionary<string, string> iArgs, string iCacheKey,
            string iEndpoint, Dictionary<string, string> iParams, Dictionary<string, string> iCred,
            UCL_PlurkAccountResolution iRes, StringBuilder ioR, CancellationToken token)
        {
            string aAccount = iRes.SecretId ?? "_";
            string aFile = CacheFile(aAccount, iCacheKey);

            if (GetArg(iArgs, "cache", "") == "1")
            {
                if (File.Exists(aFile))
                {
                    var aCached = SafeParse(File.ReadAllText(aFile, Encoding.UTF8));
                    string aAt = aCached == null ? null : JsonScalar(aCached, "fetched_at");
                    ioR.AppendLine($"- 📦 **資料來源：本地快取**（`{(string.IsNullOrEmpty(aAt) ? "?" : aAt)}`，"
                        + $"**{AgeText(aAt)}前**）—— 這不是現況");
                    ioR.AppendLine($"  · 檔案 `{aFile}`　要現抓就拿掉 `--arg cache=1`");
                    if (aCached != null && aCached.Contains("body")) return aCached["body"].GetString();
                    ioR.AppendLine("  · ⚠ 快取檔在但沒有 `body` 欄位 ⇒ 當成沒有，改打 API");
                }
                else
                {
                    ioR.AppendLine($"- 📦 要求讀快取但**檔案不存在**（`{aFile}`）⇒ 改打 API"
                        + " —— 沒有靜默降級，這一行就是那個降級的讀數");
                }
            }

            var (aStatus, aBody) = await CallAsync(iEndpoint, iCred, iParams, token);
            ioR.AppendLine($"- 🌐 **資料來源：API 現抓**　`POST {iEndpoint}`　http: **{aStatus}**");
            if (aStatus != 200)
            {
                ioR.AppendLine("- ✗ body（前 300 字）: " + Trunc(aBody, 300));
                ioR.AppendLine("  · ⚠ 403 ＋ `error code: 1010` ＝ Cloudflare 依 UA 擋，"
                    + "不是簽章錯也不是端點不存在（三種失敗都是 4xx，長得一樣）");
                throw new Exception($"[Plurk] {iEndpoint} 失敗 http={aStatus}");
            }
            TryWriteCache(aFile, aAccount, iEndpoint, aBody, ioR);
            return aBody;
        }

        static void TryWriteCache(string iFile, string iAccount, string iEndpoint,
            string iBody, StringBuilder ioR)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iFile));
                var aJd = new UCL.Core.JsonLib.JsonData();
                aJd["fetched_at"] = new UCL.Core.JsonLib.JsonData(DateTime.UtcNow.ToString("o"));
                aJd["account"] = new UCL.Core.JsonLib.JsonData(iAccount);
                aJd["endpoint"] = new UCL.Core.JsonLib.JsonData(iEndpoint);
                aJd["body"] = new UCL.Core.JsonLib.JsonData(iBody);
                File.WriteAllText(iFile, aJd.ToJson(), new UTF8Encoding(false));
                ioR.AppendLine($"  · 已落快取 `{iFile}`（⛔ 不入 git）");
            }
            catch (Exception ex)
            {
                // 快取寫不進去不影響這一次的讀數 —— 但要說出來，
                // 不然下次 `cache=1` 讀不到會變成一個沒有人解釋得了的謎
                ioR.AppendLine($"  · ⚠ 快取寫入失敗（不影響本次讀數）：{ex.Message}");
            }
        }

        static string AgeText(string iIso)
        {
            if (string.IsNullOrEmpty(iIso)) return "年齡不明";
            if (!DateTime.TryParse(iIso, CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime aAt))
                return "年齡不明";
            var aSpan = DateTime.UtcNow - aAt.ToUniversalTime();
            if (aSpan.TotalMinutes < 1) return "不到 1 分鐘";
            if (aSpan.TotalHours < 1) return $"約 {(int)aSpan.TotalMinutes} 分鐘";
            if (aSpan.TotalDays < 1) return $"約 {aSpan.TotalHours:0.#} 小時";
            return $"約 {aSpan.TotalDays:0.#} 天";
        }

        // ── 小工具 ────────────────────────────────────────────────
        static UCL.Core.JsonLib.JsonData SafeParse(string iJson)
        {
            try { return UCL.Core.JsonLib.JsonData.ParseJson(iJson); }
            catch { return null; }
        }

        /// <summary>取純量欄位；字串或數字都拿得到（同 <see cref="PickJsonValue"/> 那條血證）。</summary>
        static string JsonScalar(UCL.Core.JsonLib.JsonData iNode, string iKey)
        {
            if (iNode == null || !iNode.Contains(iKey)) return "";
            string aRaw = (iNode[iKey].ToJson() ?? "").Trim();
            if (aRaw.Length >= 2 && aRaw[0] == '"' && aRaw[aRaw.Length - 1] == '"')
                aRaw = aRaw.Substring(1, aRaw.Length - 2);
            return aRaw == "null" ? "" : aRaw;
        }

        /// <summary>
        /// user id → 顯示名。查不到就回 id 本身並標記 —— **不回空字串**：
        /// 空的那格會讓人以為「這則沒有作者」，而事實是「我沒查到作者」。
        /// </summary>
        static string UserName(UCL.Core.JsonLib.JsonData iUsers, string iId)
        {
            if (string.IsNullOrEmpty(iId)) return "(無 id)";
            if (iUsers == null || !iUsers.Contains(iId)) return iId + "(查無名稱)";
            var aU = iUsers[iId];
            string aName = UnescapeJson(JsonScalar(aU, "display_name"));
            if (aName.Length == 0) aName = JsonScalar(aU, "nick_name");
            return aName.Length == 0 ? iId + "(查無名稱)" : aName;
        }

        static string FirstLine(string iText)
        {
            if (string.IsNullOrEmpty(iText)) return "";
            int i = iText.IndexOf('\n');
            return i < 0 ? iText : iText.Substring(0, i);
        }

        static string OneLine(string iText)
            => string.IsNullOrEmpty(iText) ? "" : iText.Replace("\r", " ").Replace("\n", " ");

        static int ParseIntArg(Dictionary<string, string> iArgs, string iKey,
            int iDefault, int iMin, int iMax)
        {
            string aRaw = GetArg(iArgs, iKey, "").Trim();
            if (aRaw.Length == 0) return iDefault;
            if (!int.TryParse(aRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aVal))
                throw new Exception($"[Plurk] --arg {iKey}={aRaw} 不是整數"
                    + "（不靜默取預設值 —— 打錯字要當場知道）");
            return aVal < iMin ? iMin : (aVal > iMax ? iMax : aVal);
        }

        // 從 `{"plurk":{...},"user":{...}}` 取出內層物件的原始 JSON，讓 PickJsonValue 能繼續用。
        // 極簡實作：只服務本檔這一個回應形狀，不做通用 JSON 導覽。
        static string ExtractObject(string iJson, string iKey)
        {
            try
            {
                var aJd = UCL.Core.JsonLib.JsonData.ParseJson(iJson);
                if (aJd == null || !aJd.Contains(iKey)) return iJson;
                return aJd[iKey].ToJson();
            }
            catch { return iJson; }
        }

        async UniTask OpUpload(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, CancellationToken token)
        {
            string aPath = GetArg(iArgs, "image", "").Trim();
            if (aPath.Length == 0)
                throw new Exception("[Plurk] op=upload 需要 --arg image=<圖片的絕對路徑>");
            RequireAbsoluteExistingImage(aPath);
            var aCred = RequireCredentials(iRes);

            ioR.AppendLine();
            ioR.AppendLine("## upload（**對外寫入** —— CDN 上會留下一張圖）");
            ioR.AppendLine($"- 檔案: `{aPath}`（{FormatSize(new FileInfo(aPath).Length)}, {GuessMime(aPath)}）");
            if (GetArg(iArgs, "confirm", "").Trim() != "1")
            {
                ioR.AppendLine("- **dry-run**：沒帶 `confirm=1` ⇒ 什麼都沒上傳。");
                return;
            }
            var (aStatus, aBody) = await UploadImageAsync(aPath, aCred, token);
            ioR.AppendLine($"- http: **{aStatus}**　endpoint: `{UploadEndpoint}`");
            if (aStatus != 200)
            {
                ioR.AppendLine($"- ✗ body（前 400 字）: {Trunc(aBody, 400)}");
                throw new Exception($"[Plurk] 上傳失敗 http={aStatus}"
                    + "（判準：先確認端點與欄位名，再懷疑 multipart 的簽章 —— 兩者都是 4xx）");
            }
            string aFull = PickJsonValue(aBody, "full");
            string aThumb = PickJsonValue(aBody, "thumbnail");
            ioR.AppendLine($"- full: `{aFull}`（**{(aFull ?? "").Length} 字元** —— 這個長度會吃掉 content 預算）");
            if (aThumb != null) ioR.AppendLine($"- thumbnail: `{aThumb}`");
            if (string.IsNullOrEmpty(aFull))
            {
                ioR.AppendLine($"- ⚠ 回應裡沒有 `full` 欄位。body（前 300 字）: {Trunc(aBody, 300)}");
                throw new Exception("[Plurk] 上傳回 200 但拿不到圖片 URL —— 欄位名可能不是 `full`");
            }
        }

        // 區塊職責：圖片路徑的硬性檢查（Tim 2026-08-21：**要完整路徑，不吃相對路徑**）
        // 物理意義：相對路徑會相對於 Editor 的工作目錄（那是 repo 根，而不是交付單所在的位置）
        //          ⇒ 同一份交付單在不同地方跑會指到不同檔，而**檔案不存在的失敗發生在上傳那一刻**，
        //          不是在 lint。所以這條擋在前面。
        static void RequireAbsoluteExistingImage(string iPath)
        {
            if (!Path.IsPathRooted(iPath))
                throw new Exception($"[Plurk] 圖片要**絕對路徑**：'{iPath}' 是相對路徑"
                    + "（相對路徑會相對於 Editor 的工作目錄，同一份交付單換個地方跑就指到別的檔）");
            if (!File.Exists(iPath))
                throw new Exception($"[Plurk] 圖片不存在：{iPath}");
        }

        // ===========================================================
        // 區塊職責：payload 組裝
        // 物理意義：`qualifier` 是心情詞（Plurk 的固定詞彙表，非自由字串 ⇒ 認不得就退 `says`）；
        //          公開度靠 `limited_to`：**沒有這個參數就是公開**（所以「沒指定」必須在 lint 就擋下，
        //          不能讓它一路走到這裡變成預設公開 —— summit 2026-08-21 指出的漏洞）。
        // ⚠ 未對照官方文件：`limited_to` 的值格式（`[0]`＝僅朋友）取自社群慣例，preview 會印出來讓人看。
        // ===========================================================
        static readonly Dictionary<string, string> QualifierMap = new Dictionary<string, string>
        {
            { "覺得", "feels" }, { "說", "says" }, { "想", "thinks" }, { "哭", "cries" },
            { "正在", "is" }, { "分享", "shares" }, { "問", "asks" }, { "希望", "hopes" },
            { "愛", "loves" }, { "討厭", "hates" }, { "需要", "needs" }, { "有", "has" },
        };

        Dictionary<string, string> BuildPayload(UCL_PlurkSlip iSlip, Dictionary<string, string> iArgs)
        {
            var aOut = new Dictionary<string, string>();
            string aReplyTo = GetArg(iArgs, "reply_to", "").Trim();
            if (aReplyTo.Length > 0)
            {
                aOut["plurk_id"] = aReplyTo;
                aOut["content"] = iSlip.Body;
                aOut["qualifier"] = MapQualifier(iSlip.Qualifier);
                return aOut;                        // 回應沒有公開度參數（跟著母噗）
            }
            aOut["content"] = iSlip.Body;
            aOut["qualifier"] = MapQualifier(iSlip.Qualifier);
            string aPrivacy = (iSlip.Privacy ?? "").Trim();
            if (aPrivacy == "只限朋友" || aPrivacy.Equals("friends", StringComparison.OrdinalIgnoreCase))
                aOut["limited_to"] = "[0]";         // 社群慣例：[0] = 僅好友可見
            else if (aPrivacy == "本人" || aPrivacy.Equals("self", StringComparison.OrdinalIgnoreCase))
                aOut["limited_to"] = "[]";          // ⚠ 未驗證：空清單是否等於只有自己
            // 「所有人」⇒ 不帶 limited_to（公開）
            return aOut;
        }

        static string MapQualifier(string iWord)
        {
            string w = (iWord ?? "").Trim();
            if (w.Length == 0) return "says";
            return QualifierMap.TryGetValue(w, out string v) ? v : "says";
        }

        // ===========================================================
        // 區塊職責：OAuth 1.0a 簽章（HMAC-SHA1）
        // 物理意義：base = METHOD & percent(url) & percent(排序後參數)；key = percent(cs) & percent(ts)。
        //          三處都要 RFC 3986 percent-encoding（`-._~` 之外全編碼）——
        //          少編一個字元就只會回 4xx，而它不會說是哪一格錯。
        // 數值影響：純計算。nonce 走 RandomNumberGenerator（簽章材料，不用 System.Random）。
        // ===========================================================
        static string Q(string iValue)
        {
            const string aUnreserved = "-._~";
            var sb = new StringBuilder();
            foreach (byte b in Encoding.UTF8.GetBytes(iValue ?? ""))
            {
                char c = (char)b;
                if (char.IsLetterOrDigit(c) && b < 128 || aUnreserved.IndexOf(c) >= 0) sb.Append(c);
                else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        static string OAuthHeader(string iMethod, string iUrl, Dictionary<string, string> iCred,
            Dictionary<string, string> iParams)
        {
            var aNonce = new byte[16];
            using (var aRng = RandomNumberGenerator.Create()) aRng.GetBytes(aNonce);
            var aOAuth = new Dictionary<string, string>
            {
                { "oauth_consumer_key", iCred["consumer_key"] },
                { "oauth_token", iCred["access_token"] },
                { "oauth_signature_method", "HMAC-SHA1" },
                { "oauth_timestamp", ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds).ToString(CultureInfo.InvariantCulture) },
                { "oauth_nonce", BitConverter.ToString(aNonce).Replace("-", "").ToLowerInvariant() },
                { "oauth_version", "1.0" },
            };
            var aAll = new Dictionary<string, string>(aOAuth);
            if (iParams != null) foreach (var kv in iParams) aAll[kv.Key] = kv.Value;
            string aNorm = string.Join("&", aAll.OrderBy(k => k.Key, StringComparer.Ordinal)
                .Select(kv => $"{Q(kv.Key)}={Q(kv.Value)}"));
            string aBase = $"{iMethod.ToUpperInvariant()}&{Q(iUrl)}&{Q(aNorm)}";
            string aKey = $"{Q(iCred["consumer_secret"])}&{Q(iCred["access_token_secret"])}";
            using (var aMac = new HMACSHA1(Encoding.UTF8.GetBytes(aKey)))
            {
                aOAuth["oauth_signature"] = Convert.ToBase64String(
                    aMac.ComputeHash(Encoding.UTF8.GetBytes(aBase)));
            }
            return "OAuth " + string.Join(", ", aOAuth.OrderBy(k => k.Key, StringComparer.Ordinal)
                .Select(kv => $"{Q(kv.Key)}=\"{Q(kv.Value)}\""));
        }

        async UniTask<(int status, string body)> CallAsync(string iPath, Dictionary<string, string> iCred,
            Dictionary<string, string> iParams, CancellationToken token)
        {
            string aUrl = ApiBase + iPath;
            using (var aClient = new HttpClient { Timeout = TimeSpan.FromSeconds(25) })
            {
                var aContent = new FormUrlEncodedContent(iParams ?? new Dictionary<string, string>());
                var aReq = new HttpRequestMessage(HttpMethod.Post, aUrl) { Content = aContent };
                aReq.Headers.TryAddWithoutValidation("Authorization",
                    OAuthHeader("POST", aUrl, iCred, iParams));
                aReq.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                var aResp = await aClient.SendAsync(aReq, token);
                string aBody = await aResp.Content.ReadAsStringAsync();
                return ((int)aResp.StatusCode, aBody);
            }
        }

        // ===========================================================
        // 區塊職責：憑證讀取（只讀已解密的明文）
        // ⛔ 不做加解密、不碰 passphrase（那是 Secret Manager 的事）；值一律不印。
        // ===========================================================
        static readonly string[] CredFields =
            { "consumer_key", "consumer_secret", "access_token", "access_token_secret" };

        static string SecretPath(string iAccount, string iExt)
            => Path.Combine(UCL_SecretsPath.AbsoluteDir, iAccount + iExt).Replace('\\', '/');

        static Dictionary<string, string> LoadCredentials(string iAccount, out string oWhy)
        {
            oWhy = "";
            string aTxt = SecretPath(iAccount, ".txt");
            if (!File.Exists(aTxt))
            {
                oWhy = File.Exists(SecretPath(iAccount, ".enc"))
                    ? "`.enc` 有但明文沒安裝 ⇒ 到 Secret Manager 做一次解密安裝"
                    : "連 `.enc` 都沒有 ⇒ 先在 UCL_PlurkAdminPage 產出憑證";
                return null;
            }
            var aJson = UCL.Core.JsonLib.JsonData.ParseJson(File.ReadAllText(aTxt, Encoding.UTF8));
            var aOut = new Dictionary<string, string>();
            var aMissing = new List<string>();
            foreach (var aKey in CredFields)
            {
                string v = aJson.GetString(aKey, "").Trim();
                if (v.Length == 0) aMissing.Add(aKey); else aOut[aKey] = v;
            }
            if (aMissing.Count > 0)
            {
                oWhy = "缺欄位 " + string.Join(", ", aMissing)
                    + "（OAuth 1.0a 一定四個值：consumer 認 app、access token 認帳號）";
                return null;
            }
            return aOut;
        }

        Dictionary<string, string> RequireCredentials(UCL_PlurkAccountResolution iRes)
        {
            if (string.IsNullOrEmpty(iRes.SecretId))
                throw new Exception("[Plurk] 帳號未設定 ⇒ 不能發文（UCL_PlurkAdminPage 設共用帳號或個人 override）");
            var aCred = LoadCredentials(iRes.SecretId, out string aWhy);
            if (aCred == null) throw new Exception($"[Plurk] 憑證不可用：{aWhy}");
            return aCred;
        }

        UCL_PlurkSlip LoadSlip(Dictionary<string, string> iArgs)
        {
            string aFile = GetArg(iArgs, "slip_file", "").Trim();
            if (aFile.Length == 0)
                throw new Exception("[Plurk] 需要 --arg slip_file=<交付單檔案>"
                    + "（四欄格式見 Plurk_Posting_Workflow §二；長文一律走檔案不走參數）");
            if (!File.Exists(aFile)) throw new Exception($"[Plurk] 找不到交付單：{aFile}");
            var aSlip = UCL_PlurkLint.Parse(File.ReadAllText(aFile, Encoding.UTF8));

            // 區塊職責：`@persona` → `@nick[→persona]` 自動轉換（Tim 2026-09-03 拍板）。
            // 物理意義：**放這裡而不是放各 op** —— lint／preview／post 走同一個 LoadSlip，
            //          分三處寫就會漂，而漂掉的那一處剛好是真的送出去的那一條路。
            // 數值影響：改寫**在 Budget/Check 之前** —— 它會改變長度
            //          （`@gura` 5 字 → `@hololive_myth→gura` 20 字），
            //          先算預算再改寫的話那個數字是假的。
            aSlip.Body = UCL_PlurkLint.RewriteMentions(aSlip.Body,
                out var aNotes, out var aProblems);
            aSlip.MentionNotes = aNotes;
            aSlip.MentionProblems = aProblems;
            return aSlip;
        }

        // ===========================================================
        // 區塊職責：audit —— 發出去的東西留一筆不可回復動作的帳
        // 物理意義：Plurk 沒有 history ⇒ 對帳只能靠自己留。內容存 **SHA256 前 16 位**不存全文
        //          （全文在 Plurk 上；這裡要的是「這則是不是我發的」而不是再存一份）。
        // 數值影響：append 一行 jsonl；寫失敗只 LogError 不影響已發出的事實。
        // ===========================================================
        static string AuditPath()
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, AuditRelative).Replace('\\', '/');

        void WriteAudit(UCL_PlurkAccountResolution iRes, UCL_PlurkSlip iSlip,
            Dictionary<string, string> iPayload, string iPlurkId, string iReplyTo)
        {
            try
            {
                string aPath = AuditPath();
                Directory.CreateDirectory(Path.GetDirectoryName(aPath));
                string aHash;
                using (var aSha = SHA256.Create())
                {
                    aHash = BitConverter.ToString(aSha.ComputeHash(Encoding.UTF8.GetBytes(iSlip.Body)))
                        .Replace("-", "").ToLowerInvariant().Substring(0, 16);
                }
                var aJd = new UCL.Core.JsonLib.JsonData();
                aJd["at"] = new UCL.Core.JsonLib.JsonData(DateTime.UtcNow.ToString("o"));
                aJd["persona"] = new UCL.Core.JsonLib.JsonData(iSlip.Persona ?? "");
                aJd["account"] = new UCL.Core.JsonLib.JsonData(iRes.SecretId ?? "");
                aJd["source"] = new UCL.Core.JsonLib.JsonData(iRes.Source ?? "");
                aJd["privacy"] = new UCL.Core.JsonLib.JsonData(iSlip.Privacy ?? "");
                aJd["limited_to"] = new UCL.Core.JsonLib.JsonData(
                    iPayload.TryGetValue("limited_to", out string lt) ? lt : "(public)");
                aJd["reply_to"] = new UCL.Core.JsonLib.JsonData(iReplyTo ?? "");
                aJd["body_sha256_16"] = new UCL.Core.JsonLib.JsonData(aHash);
                aJd["body_len"] = new UCL.Core.JsonLib.JsonData(iSlip.Body.Length);
                aJd["plurk_id"] = new UCL.Core.JsonLib.JsonData(iPlurkId ?? "");
                File.AppendAllText(aPath, aJd.ToJson() + "\n", new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Plurk] audit 寫入失敗（噗已經發出去了，這筆帳要手動補）：{ex.Message}");
            }
        }

        // 極簡取值：只為了從回應撈幾個純量欄位，不值得為它引入完整反序列化。
        // 🩸 首版用 `GetString()`，而 Plurk 的 `id` / `karma` / `plurk_id` 是**數字** ⇒ 一律回空字串。
        //   whoami 印成 `id:` 空白時只是難看，但同一支函式也負責撈 **post 回傳的 plurk_id** ——
        //   那會讓 audit 記成 `?`：**噗發出去了，而帳上找不到它是哪一則**（不可回復動作最不該的失敗）。
        //   ⇒ 改成走 `ToJson()` 再剝引號：型別不論字串或數字都拿得到值。
        static string PickJsonValue(string iJson, string iKey)
        {
            if (string.IsNullOrEmpty(iJson)) return null;
            try
            {
                var aJd = UCL.Core.JsonLib.JsonData.ParseJson(iJson);
                if (aJd == null || !aJd.Contains(iKey)) return null;
                string aRaw = (aJd[iKey].ToJson() ?? "").Trim();
                if (aRaw.Length >= 2 && aRaw[0] == '"' && aRaw[aRaw.Length - 1] == '"')
                    aRaw = aRaw.Substring(1, aRaw.Length - 2);
                return aRaw.Length == 0 || aRaw == "null" ? null : aRaw;
            }
            catch { return null; }
        }

        // 區塊職責：把 JSON 的 \uXXXX 轉義還原成字元
        // 物理意義：`JsonData.ToJson()` 會轉義非 ASCII ⇒ 回讀中文內容印出來是一串 引用…，
        //          人看不出那是不是自己發的那段。
        // 🩸 2026-08-21 op=get 首跑就撞到：**驗證輸出讀不懂，等於只驗了一半** ——
        //   我能證明「有東西在那裡」，但不能證明「在那裡的是我那段」。
        // 數值影響：只影響顯示；解析不出來就原樣留著（不吞掉）。
        // 🩸 2026-08-23 同族第二隻：本函式原本**只**處理 \uXXXX，於是 `content_raw` 裡的換行
        //   回來是**字面兩個字元**（反斜線 ＋ n）。timeline 那張表的欄位叫「內容首行」，
        //   而 FirstLine 找的是真正的換行字元 ⇒ 切不到 ⇒ 那一格印的其實是整段的前 40 字。
        //   **欄位名說「首行」而內容不是首行** —— 名字比事實大，而且不會報錯。
        //   ⇒ 修在這一層而不是修 timeline：`op=get` 的「content_raw 首行」是同一個病，
        //     只修看得見的那半邊就是又留一隻同族的下一個。
        static string UnescapeJson(string iText)
        {
            if (string.IsNullOrEmpty(iText)) return iText;
            var sb = new StringBuilder(iText.Length);
            for (int i = 0; i < iText.Length; i++)
            {
                if (iText[i] != '\\' || i + 1 >= iText.Length) { sb.Append(iText[i]); continue; }
                char aNext = iText[i + 1];
                if (aNext == 'u' && i + 5 < iText.Length
                    && int.TryParse(iText.Substring(i + 2, 4),
                        System.Globalization.NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out int aCode))
                {
                    sb.Append((char)aCode);
                    i += 5;
                    continue;
                }
                switch (aNext)
                {
                    case 'n': sb.Append('\n'); i++; break;
                    case 'r': sb.Append('\r'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    case '"': sb.Append('"'); i++; break;
                    case '/': sb.Append('/'); i++; break;
                    case '\\': sb.Append('\\'); i++; break;
                    // 認不得的轉義**原樣留著**（含那個反斜線）—— 吞掉的話它會變成一個
                    // 「看起來正常但少了一個字元」的字串，而那比看得見的怪符號難查十倍
                    default: sb.Append(iText[i]); break;
                }
            }
            return sb.ToString();
        }

        static string Trunc(string iText, int iLen)
            => string.IsNullOrEmpty(iText) ? "" : (iText.Length <= iLen ? iText : iText.Substring(0, iLen) + "…");
    }
}
#endif
