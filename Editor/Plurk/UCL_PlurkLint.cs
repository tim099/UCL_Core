// 區塊職責：Plurk 交付單的**形式**檢查 —— 把血證變成機器會擋的東西。
// 物理意義：每條規則底下都有一次真的出事的紀錄（見各規則的 🩸）。規則放這裡而不是放 skill／文件，
//          理由是 summit 自己寫的那句：**對我有效的修法只有一種 —— 把交付格式搬到發文那條路上。**
// 數值影響：純函式，零 IO、零網路。回 (errors, warns)；errors 非空 ⇒ 呼叫端必須擋下。
//
// ⛔ **本檢查只驗形式，不驗「能不能公開」。** 公開判準是
//   「這段被轉述出去，問題是我會不好意思，還是有人被傷到」—— **機器判不了後半句**。
//   ⇒ 所以本類別**不提供任何「可以發」的綠燈**，呼叫端輸出必須附上那句免責，
//   否則「過了 lint」會被讀成「過了審查」（某一層的回報只涵蓋它自己那一層，卻講得像涵蓋全部）。
//
// 📄 加一條規則 / 改判準之前先讀 `Docs~/{lang}/Workflows/Plurk_Maintenance.md` §2
//   （errors 與 warns 的分野、血證的寫法、以及「驗收要看是哪一條規則報的」那條）。
//
// 為什麼規則住 C# 而不是 python：`post` 在 C#（唯一寫入端），而**規則要長在必經路上** ——
// 規則在另一個語言的另一支工具裡，發文那條路就繞得過它，而繞過去不會報錯。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace UCL.Core.EditorLib.Plurk
{
    /// <summary>一份交付單（`Plurk_Posting_Workflow.md` §二 的四欄 ＋ 公開度）。</summary>
    public class UCL_PlurkSlip
    {
        public string Persona = "";
        public string Qualifier = "";
        public string Body = "";
        public string Image = "";
        public string Privacy = "";

        /// <summary>`@persona` 自動轉換做了什麼（由 `LoadSlip` 填）。
        /// ⚠ **一定要印出來** —— 自動改動了使用者的文案而不說，就是靜默代筆。</summary>
        public List<string> MentionNotes = new List<string>();
        /// <summary>轉換不掉的 `@persona`（沒帳號／nick 未登記）。非空 ⇒ 呼叫端必須擋下。</summary>
        public List<string> MentionProblems = new List<string>();

        /// <summary>有沒有附圖（「無」與空字串都算沒有）。</summary>
        public bool HasImage =>
            !string.IsNullOrWhiteSpace(Image) && Image.Trim() != "無" && Image.Trim() != "none";
    }

    public static class UCL_PlurkLint
    {
        /// <summary>字元預算上限（Tim 2026-08-21：300，不是原本記的 360）。</summary>
        public const int Limit = 300;
        // 區塊職責：附圖時要替圖片 URL 保留多少字元
        // 物理意義：附圖是**兩段式** —— 上傳完拿到的 URL 會被併進 content，所以它吃 content 的預算。
        // 🩸 首版寫 30 是**估的**；2026-08-21 實測 `https://images.plurk.com/<21 碼>.png` = **50 字元**
        //   ⇒ 估值比實際少 20，那讓「lint 過了、併入 URL 後超長」變成可能 ——
        //   而那個失敗發生在**圖片已經上傳到 CDN 之後**（清不掉的無主圖片）。
        // 數值影響：50（實測）＋ 換行 1 ＋ 餘裕 9 ＝ 60。要改小之前先自己傳一張量一次。
        public const int ImageReserve = 60;

        static readonly Regex EmoRe = new Regex(@"\[emo(\d+)\]", RegexOptions.Compiled);
        static readonly Regex NoteLineRe = new Regex(@"^[（(][^）)]{2,40}[）)]$", RegexOptions.Compiled);
        static readonly Regex NoteInlineRe = new Regex(@"[（(][^）)]{2,20}[）)]", RegexOptions.Compiled);
        static readonly Regex NoteAllowRe = new Regex(@"^[（(](\d+/\d+|emo\d+)[）)]$", RegexOptions.Compiled);
        // 句末判定：標點之後允許**收尾符號**（粗體標記、引號、括號）——
        // 🩸 2026-08-21 誤報：`…要被檢查的訊號。**` 被判成「句內手動斷行」，
        //   因為 `**` 卡在句號與行尾之間。那是**規則自己的邊界沒畫對**，不是文案的錯；
        //   而誤報的代價跟漏報一樣真：它會讓人去改一個沒問題的地方，然後開始不信這條規則。
        static readonly Regex SentenceEndRe =
            new Regex(@"[。！？!?][\*」』）\)】\s]*$", RegexOptions.Compiled);
        static readonly Regex SignRe = new Regex(@"(——|—|--)\s*\S", RegexOptions.Compiled);
        static readonly Regex MentionRe = new Regex(@"@([A-Za-z0-9_\-]{2,20})", RegexOptions.Compiled);

        // ===========================================================
        // 區塊職責：解析四欄交付單
        // 物理意義：欄名全形／半形冒號都收（人手打的東西兩種都有）；
        //          `文案本體：` 之後直到下一個欄名之前的**整段**都是文案。
        // 數值影響：解析不出文案 ⇒ Body 為空，由 Check 報錯（不猜一份文案出來檢查）。
        // ===========================================================
        public static UCL_PlurkSlip Parse(string iText)
        {
            var aSlip = new UCL_PlurkSlip();
            var aBody = new List<string>();
            string aCur = null;
            foreach (var aRaw in (iText ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string aKey = FieldKey(aRaw, out string aRest);
                if (aKey != null)
                {
                    aCur = aKey;
                    switch (aKey)
                    {
                        case "persona": aSlip.Persona = aRest.Trim(); break;
                        case "qualifier": aSlip.Qualifier = aRest.Trim(); break;
                        case "image": aSlip.Image = aRest.Trim(); break;
                        case "privacy": aSlip.Privacy = aRest.Trim(); break;
                        case "body":
                            aBody.Clear();
                            if (!string.IsNullOrWhiteSpace(aRest)) aBody.Add(aRest);
                            break;
                    }
                    continue;
                }
                if (aCur == "body") aBody.Add(aRaw);
            }
            aSlip.Body = string.Join("\n", aBody).Trim('\n');
            if (string.IsNullOrWhiteSpace(aSlip.Privacy)) aSlip.Privacy = "所有人";
            return aSlip;
        }

        static string FieldKey(string iLine, out string oRest)
        {
            oRest = "";
            foreach (char aSep in new[] { '：', ':' })
            {
                int i = iLine.IndexOf(aSep);
                if (i <= 0 || i > 12) continue;      // 欄名很短；限長度避免把文案裡的冒號當欄名
                string aHead = iLine.Substring(0, i).Trim().ToLowerInvariant();
                oRest = iLine.Substring(i + 1);
                switch (aHead)
                {
                    case "persona": return "persona";
                    case "心情詞": case "qualifier": return "qualifier";
                    case "文案本體": case "body": return "body";
                    case "圖片路徑": case "image": return "image";
                    case "公開度": case "privacy": return "privacy";
                }
            }
            oRest = "";
            return null;
        }

        /// <summary>字元預算：總長／`[emoN]` 佔用字元／emo 個數。**不是中文字數** —— 換行、`**`、全形標點都算。</summary>
        public static (int total, int emoLen, int emoCount) Budget(string iBody)
        {
            var aMatches = EmoRe.Matches(iBody ?? "");
            int aEmoLen = aMatches.Cast<Match>().Sum(m => m.Value.Length);
            return ((iBody ?? "").Length, aEmoLen, aMatches.Count);
        }

        public static int Allowed(UCL_PlurkSlip iSlip) => Limit - (iSlip.HasImage ? ImageReserve : 0);

        // ===========================================================
        // 區塊職責：把文案裡的 `@persona` 改寫成真的會送達的 `@nick`（多人帳號再加 `→persona`）。
        // 物理意義：Tim 2026-09-03 拍板「發文時 @gura 自動轉換」。這**不是猜** ——
        //          persona→帳號→nick 是三段查表，查不到就回 problem 讓呼叫端擋下。
        // 數值影響：**在 lint 與字元預算之前跑** —— 改寫會改變長度
        //          （`@gura` 5 字 → `@hololive_myth→gura` 20 字），
        //          先 lint 再改寫的話那份預算是假的。
        // ⚠ 回傳的 oNotes 一定要印出來：**自動改動了使用者的文案，不印就是靜默代筆。**
        // ===========================================================
        /// <summary>改寫 `@persona` → `@nick[→persona]`。<paramref name="oProblems"/> 非空 ⇒ 呼叫端必須擋下。</summary>
        public static string RewriteMentions(string iBody, out List<string> oNotes, out List<string> oProblems)
        {
            oNotes = new List<string>();
            oProblems = new List<string>();
            if (string.IsNullOrEmpty(iBody)) return iBody ?? "";

            var aSeen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var aName in MentionRe.Matches(iBody).Cast<Match>()
                     .Select(m => m.Groups[1].Value).Distinct())
            {
                var aFix = UCL_PlurkAccounts.ResolveMention(aName);
                if (!string.IsNullOrEmpty(aFix.Problem)) { oProblems.Add(aFix.Problem); continue; }
                if (!aFix.NeedsRewrite) continue;
                aSeen[aName] = aFix.Replacement;
                oNotes.Add($"`@{aName}` → `{aFix.Replacement}`"
                    + (aFix.PersonaCount > 1
                        ? $"（`{aFix.Nick}` 有 {aFix.PersonaCount} 位 persona 在用 ⇒ 帶 `{UCL_PlurkAccounts.PersonaTagSep}{aFix.Persona}` 指名）"
                        : $"（`{aFix.Nick}` 只有 {aFix.Persona} 一個人 ⇒ 不加標記）"));
            }
            if (aSeen.Count == 0) return iBody;

            // ⚠ 逐一 Regex.Replace 而不是字串 Replace：`@calli` 不可以命中 `@calliope` 的前半。
            //   `(?![A-Za-z0-9_\-])` 就是那個邊界；沒有它會把別人的 nick 切一半。
            string aOut = iBody;
            foreach (var aPair in aSeen)
            {
                aOut = Regex.Replace(aOut,
                    "@" + Regex.Escape(aPair.Key) + @"(?![A-Za-z0-9_\-])",
                    aPair.Value.Replace("$", "$$"));
            }
            return aOut;
        }

        // ===========================================================
        // 區塊職責：形式檢查本體
        // 數值影響：errors 非空 ⇒ 擋下。⚠ 通過**不代表可以發**（見檔頭）。
        // ===========================================================
        public static (List<string> errors, List<string> warns) Check(
            UCL_PlurkSlip iSlip, bool iRequiresSignature)
        {
            var aErr = new List<string>();
            var aWarn = new List<string>();
            string aBody = iSlip?.Body ?? "";
            if (string.IsNullOrWhiteSpace(aBody))
            {
                aErr.Add("交付單沒有『文案本體：』欄，或該欄是空的 —— 沒有文案就沒有東西可檢查");
                return (aErr, aWarn);
            }

            var aLines = aBody.Split('\n').Select(s => s.Trim()).ToList();

            // ① 括號編輯註記
            // 🩸 2026-08-07：草稿裡的「（短、好笑、純自嘲）」被代發的人原樣貼上，**成了噗文標題**。
            // 🩸 而 python 首版這條規則**在驗收時漏掉了它要防的那一篇**：我把「含標點的括號」
            //   當正文補述而跳過，`、` 也算標點 ⇒ 剛好放行。那篇仍被擋下，但擋它的是規則②。
            //   ⇒ **「有擋下」與「被該擋它的規則擋下」是兩件事**，而前者會讓我以為規則有效。
            //   判準因此改成兩層：(a) 整行就是括號 ⇒ 一律當註記；(b) 行內括號只跳過含**句末**標點的。
            var aNoteLines = new HashSet<string>();
            foreach (var aLn in aLines)
            {
                if (NoteLineRe.IsMatch(aLn) && !NoteAllowRe.IsMatch(aLn))
                {
                    aNoteLines.Add(aLn);
                    aErr.Add($"整行都是括號註記：{aLn} —— 代發的人只會照貼（🩸 2026-08-07 它上了標題）");
                }
            }
            foreach (Match m in NoteInlineRe.Matches(aBody))
            {
                if (aNoteLines.Contains(m.Value)) continue;           // 整行規則報過了，不重複
                if (Regex.IsMatch(m.Value, @"[。！？!?]")) continue;   // 含句末標點多半是正文補述
                if (NoteAllowRe.IsMatch(m.Value)) continue;
                aWarn.Add($"疑似編輯註記留在文案裡：{m.Value} —— 代發的人只會照貼");
            }

            // ② 句內手動斷行
            // 🩸 2026-08-11：手動斷的行**疊上** Plurk 自己的軟斷行 ⇒ 雙重換行、句子被劈兩半。
            //   根因是照了自己編輯器的欄寬，而那個欄寬不存在於讀者螢幕上。
            foreach (var aPara in aBody.Split(new[] { "\n\n" }, StringSplitOptions.None))
            {
                var aInner = aPara.Split('\n')
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0 && !aNoteLines.Contains(s))   // 註記行已報過，不重複計
                    .ToList();
                if (aInner.Count <= 1) continue;
                foreach (var aLn in aInner.Take(aInner.Count - 1))
                {
                    if (SentenceEndRe.IsMatch(aLn)) continue;
                    string aTail = aLn.Length > 18 ? aLn.Substring(aLn.Length - 18) : aLn;
                    aErr.Add($"句內手動斷行（段落中間換行且該行沒有句末標點）：…{aTail}⏎ —— 只在段落之間空行");
                    break;
                }
            }

            // ③ 預算（🩸 2026-08-16：我的稿 365 字元、超線 5 個 —— 是 Tim 先量出來的，我沒量）
            var (aTotal, aEmoLen, aEmoCount) = Budget(aBody);
            int aAllowed = Allowed(iSlip);
            bool aOver = aTotal > aAllowed;
            if (aOver)
            {
                aErr.Add($"預算超出：{aTotal} 字元 > {aAllowed}"
                    + $"（上限 {Limit}{(iSlip.HasImage ? "，附圖保留 " + ImageReserve : "")}）"
                    + $"；其中 [emoN] 佔 {aEmoLen} 字元 × {aEmoCount} 個");
            }

            // ④ 第一行要能單獨站著（轉 Paste／走回應時，時間軸上只看得到它）
            string aFirst = aLines.First(s => s.Length > 0);
            if (aFirst.Length < 6) aWarn.Add($"第一行太短、站不住：「{aFirst}」");
            if (EmoRe.IsMatch(aFirst) && EmoRe.Replace(aFirst, "").Trim().Length < 6)
                aWarn.Add("第一行幾乎只有表情 —— 轉 Paste 時表情不渲染，那一行會變空的");

            // ⑤ 共用帳號末行署名（Tim 2026-08-16 硬規則 —— 只有 shared-default 才必填）
            string aLast = aLines.Last(s => s.Length > 0);
            bool aSigned = SignRe.IsMatch(aLast);
            if (iRequiresSignature && !aSigned)
                aErr.Add("共用帳號但末行沒有署名 —— 時間軸上讀者只看得到帳號，看不到是誰寫的");
            else if (!aSigned)
                aWarn.Add("末行沒有署名（個人帳號非必填，帳號本身就是身分）");

            // ⑥ 表情要人確認：編號是位置性的（面板重排就漂），而**表情表是 per-persona 的品味**
            //   （summit 2026-08-21：共用帳號上別人的語氣不同，別用一個人的尺量另一個人的文案）
            if (aEmoCount > 0)
            {
                aWarn.Add($"文案含 {aEmoCount} 個 [emoN] —— **編號是快取、特徵才是事實**："
                    + $"請對照**目標帳號**的面板逐一確認（{(string.IsNullOrEmpty(iSlip.Persona) ? "未填 persona" : iSlip.Persona)} 的表情表與共用帳號面板可能不同號）");
            }

            // ⑦ 點名：先確認那個 @ 會連到誰，再談禮節
            // 🩸 血證（summit 2026-09-03）：我們一直寫 `@summit` / `@basecamp` 以為在點名同事。
            //   Plurk 只認 nick，而我的 nick 是 `zeta_summit`、她的是 `cc_basecamp`
            //   ⇒ 那些 @ **對內從沒送達**，對外 linkify 成 `plurk.com/summit`（id 3905812，真實帳號）。
            //   `@calli` 更糟：連到 `Calli`（id 3369366，karma 94.97 的活人）。
            //   而本規則當時只印一句禮節提醒 —— **它看見了那個 @，卻沒有問它會連到哪裡。**
            foreach (var aName in MentionRe.Matches(aBody).Cast<Match>()
                     .Select(m => m.Groups[1].Value).Distinct())
            {
                var aFix = UCL_PlurkAccounts.ResolveMention(aName);
                if (!string.IsNullOrEmpty(aFix.Problem)) { aErr.Add(aFix.Problem); continue; }
                if (aFix.NeedsRewrite)
                {
                    // 走到這裡代表呼叫端沒有先跑 RewriteMentions ⇒ 擋下，不要靜默放行
                    aErr.Add($"`@{aName}` 是 persona 名不是 Plurk nick —— 它會連到 `plurk.com/{aName}`。"
                        + $"應寫成 `{aFix.Replacement}`（發文路徑會自動轉換；這裡看到它代表沒轉到）");
                    continue;
                }
                aWarn.Add($"文案點名 @{aName} —— 發前親自去講一聲（mention 會通知，但『已通知 ≠ 已讀』）");
            }

            // ⑧ 附圖路徑必須是**絕對路徑**且檔案存在（Tim 2026-08-21：「圖片需要完整路徑」）
            // 物理意義：相對路徑會相對於 Editor 的工作目錄（repo 根），不是交付單所在的位置
            //          ⇒ 同一份交付單換個地方跑就指到別的檔，或指到不存在的檔。
            // 數值影響：擋在 lint —— 不是等到上傳那一刻才炸（那時噗還沒發，但已浪費一次往返）。
            if (iSlip.HasImage)
            {
                string aImg = iSlip.Image.Trim();
                if (!System.IO.Path.IsPathRooted(aImg))
                    aErr.Add($"圖片路徑不是絕對路徑：`{aImg}` —— 相對路徑會相對於 Editor 的工作目錄，"
                        + "同一份交付單換個地方跑就指到別的檔");
                else if (!System.IO.File.Exists(aImg))
                    aErr.Add($"圖片檔不存在：`{aImg}`");
            }

            // ⑨ 逐篇公開度（Tim 2026-08-21 拍板：預設為所有人，方便認識更多朋友）
            if (!string.IsNullOrWhiteSpace(iSlip.Privacy) &&
                iSlip.Privacy != "所有人" && iSlip.Privacy != "只限朋友" && iSlip.Privacy != "本人" &&
                !iSlip.Privacy.Equals("public", StringComparison.OrdinalIgnoreCase) &&
                !iSlip.Privacy.Equals("friends", StringComparison.OrdinalIgnoreCase) &&
                !iSlip.Privacy.Equals("self", StringComparison.OrdinalIgnoreCase))
            {
                aErr.Add($"公開度『{iSlip.Privacy}』不合法。選項：所有人 / 只限朋友 / 本人（未填則預設為「所有人」）");
            }

            // ⑨ 超限時的拆則形態（Tim 2026-08-21：自主判斷、**預設走回應**）
            if (aOver)
            {
                aErr.Add("⚠ 需要拆則。形態判準：**這是一篇被切成兩半，還是兩篇？**"
                    + "\n      · 兩半（後半離開前半讀不完整）⇒ **第二則走回應（預設）**"
                    + "\n      · 兩篇（每則各有一個主題、能獨立被讀）⇒ 兩則獨立噗"
                    + "\n      · 分不出來 ⇒ 走預設（回應）。⚠ 署名兩則都要；只切段落邊界，不切句內");
            }
            return (aErr, aWarn);
        }

        /// <summary>固定免責 —— 每個輸出都要附。理由見檔頭。</summary>
        public const string Disclaimer =
            "⛔ **本檢查不含公開度審查** —— 「這段被轉述出去，是我不好意思還是有人被傷到」機器判不了。\n"
            + "   形式通過只代表形式通過，**不代表可以發**。那一格必須由人看。";
    }
}
#endif
