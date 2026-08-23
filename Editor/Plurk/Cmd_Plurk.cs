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
            + " / timeline·responses·friends 看別人在說什麼（唯讀）/ like·unlike 互動（需 confirm=1）。";

        public override string ArgsSchema =>
            "op=resolve|lint|preview|upload|post|get|whoami|timeline|responses|friends|like|unlike（預設 resolve） | "
            + "limit=<筆數>（timeline 預設 20／friends 預設 30；夾在 1-100） | "
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
                    case "friends": await OpFriends(args, aRes, aR, token); break;
                    // ── 社交面（寫，對別人動手 ⇒ 要 confirm=1）──
                    case "like": await OpFavorite(args, aRes, aR, token, true); break;
                    case "unlike": await OpFavorite(args, aRes, aR, token, false); break;
                    default:
                        throw new Exception($"[Plurk] 認不得的 op='{aOp}'"
                            + "（resolve|whoami|lint|preview|upload|post|get|timeline|responses|friends|like|unlike）");
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
            oErrors = aErr;
            ioR.AppendLine();
            ioR.AppendLine("## lint（形式檢查）");
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
        void OpPreview(Dictionary<string, string> iArgs, UCL_PlurkAccountResolution iRes,
            StringBuilder ioR, out Dictionary<string, string> oPayload)
        {
            var aSlip = LoadSlip(iArgs);
            WriteLintSection(aSlip, iRes, ioR, out var aErrors);
            oPayload = BuildPayload(aSlip, iArgs);
            ioR.AppendLine();
            ioR.AppendLine("## 將送的 payload（**本 op 不送**）");
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
            OpPreview(iArgs, iRes, ioR, out var aPayload);
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
            ioR.AppendLine($"- content_raw（{aFull.Length} 字元）:");
            ioR.AppendLine();
            ioR.AppendLine("```");
            ioR.AppendLine(aFull.Length <= 800 ? aFull
                : aFull.Substring(0, 800) + "\n…（截斷 —— 全文比這長，別拿這段當全部）");
            ioR.AppendLine("```");
            // 附圖驗的是**渲染**不是字串：content_raw 有 URL 只證明我送進去了
            bool aHasImg = aHtml.Contains("<img");
            ioR.AppendLine($"- 渲染成 `<img>`: **{(aHasImg ? "是" : "否")}**"
                + "（附圖那則要看這格 —— `content_raw` 裡有 URL 只證明我送進去了，Plurk 認不認是另一回事）");
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
            ioR.AppendLine();

            for (int i = 0; i < aPlurks.Count; i++)
            {
                var aP = aPlurks[i];
                string aId = JsonScalar(aP, "plurk_id");
                string aOwner = JsonScalar(aP, "owner_id");
                string aRaw = UnescapeJson(JsonScalar(aP, "content_raw"));
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
            ioR.AppendLine($"- **{aList.Count}** 則回應"
                + (aRoot.Contains("responses_seen")
                    ? $"　（對方記錄的已讀數: {JsonScalar(aRoot, "responses_seen")}）" : ""));
            for (int i = 0; i < aList.Count; i++)
            {
                var aRp = aList[i];
                ioR.AppendLine($"- **{UserName(aFriends, JsonScalar(aRp, "user_id"))}**"
                    + $"　`{JsonScalar(aRp, "id")}`　{JsonScalar(aRp, "posted")}");
                ioR.AppendLine("    " + Trunc(OneLine(UnescapeJson(JsonScalar(aRp, "content_raw"))), 200));
            }
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

        // ── 讀取層：API ／ 本地快取 ────────────────────────────────
        // ⛔ 快取目錄不入 git（`AgentCommands/.gitignore` 有 `Plurk/cache/`）——
        //    那裡面是**別人的**發文內容，而且是某一刻的快照。
        //    入版控等於把別人的時間軸釘進我們的歷史，而他們沒有同意過。
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
            return UCL_PlurkLint.Parse(File.ReadAllText(aFile, Encoding.UTF8));
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
