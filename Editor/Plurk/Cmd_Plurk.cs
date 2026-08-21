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
// ⚠ 端點與參數名：Plan §5 標了「未對照官方文件」（官方頁 403）。目前實測通的是
//   `/APP/Users/me`（唯讀，200）；`/APP/Timeline/plurkAdd` 的參數以社群慣例為準，
//   ⇒ 所以 `preview` 印出**完整將送內容**，讓人在送之前用眼睛驗一次。
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
            "Plurk 共用帳號流程：resolve 查帳號 / lint 驗交付單 / preview 組 payload 不送 / post 發文（需 confirm=1）。";

        public override string ArgsSchema =>
            "op=resolve|lint|preview|post|whoami（預設 resolve） | " +
            "persona=<誰要發，lint/preview/post 建議給 —— 決定用共用還是個人帳號> | " +
            "slip_file=<交付單檔案路徑>（lint/preview/post 必填；四欄格式見 Plurk_Posting_Workflow §二） | " +
            "confirm=1（**只有 post 需要**；沒帶＝dry-run 只印不送） | " +
            "reply_to=<plurk id>（把這則發成該噗的回應 —— 長文拆則的預設形態）";

        public override string ExampleArgs =>
            "op=preview;persona=basecamp;slip_file=D:/tmp/slip.txt";

        public override string HelpURL => "ucl_core:Docs~/{lang}/Plan/Plan_Plurk_Bot.md";

        const string ApiBase = "https://www.plurk.com";
        // plurk.com 在 Cloudflare 後面：預設 .NET/urllib UA 會被 WAF 依瀏覽器簽章擋掉，
        // 回 **403 ＋ body `error code: 1010`**（Cloudflare 的碼，不是 Plurk API 的錯誤格式）。
        // 🩸 basecamp 2026-08-21：那個 403 跟「簽章錯」「端點不存在」長得一樣，而它連應用層都沒碰到。
        const string UserAgent = "UCL-PlurkBot/0.1 (+https://github.com/Persona9999)";
        const string AuditRelative = "Plurk/post_audit.jsonl";

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

            switch (aOp)
            {
                case "resolve": OpResolve(aRes, aR); break;
                case "whoami": await OpWhoAmI(aRes, aR, token); break;
                case "lint": OpLint(args, aRes, aR); break;
                case "preview": OpPreview(args, aRes, aR, out _); break;
                case "post": await OpPost(args, aRes, aR, token); break;
                default:
                    throw new Exception($"[Plurk] 認不得的 op='{aOp}'（resolve|whoami|lint|preview|post）");
            }

            string aPath = UCL_LettersPath.CmdPayload(
                string.IsNullOrEmpty(aPersona) ? "basecamp" : aPersona, "plurk", aOp);
            UCL_LettersPath.EnsurePayloadDir(aPath);
            File.WriteAllText(aPath, aR.ToString(), new UTF8Encoding(false));
            UCL_AgentCommandRunner.ReportOutputFile(args, aPath);
            Debug.Log($"[Plurk] op={aOp} → {aPath}");
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

        static string Trunc(string iText, int iLen)
            => string.IsNullOrEmpty(iText) ? "" : (iText.Length <= iLen ? iText : iText.Substring(0, iLen) + "…");
    }
}
#endif
