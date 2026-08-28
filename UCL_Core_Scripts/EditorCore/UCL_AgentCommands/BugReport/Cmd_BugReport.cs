// 區塊職責：問題回報系統的 Cmd 入口（report / list / show / claim / resolve）。
// 物理意義：跨 agent 的「現場故障與待辦工單」通道 —— 與 NoteLesson（事後認知沉澱）分工，
//          判準不是嚴重度，是**修得動的東西在誰手上**：
//          系統可以被改成不讓下一個人踩 ⇒ 這裡；只有我自己需要記住 ⇒ NoteLesson。
// 數值影響：寫 BugReports/ 底下兩種檔（reports/<index>.md 一單一檔 / 回傳檔）；不動 Treasury、不發酒館訊息
//          （公告由呼叫端自己決定要不要發 —— 這支不替人決定要吵誰）。
// 設計沿革：Plan_BugReport_System.md（Tim 2026-08-18 拍板；RFC 酒館 seq 12080/12103/12104）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.BugReport
{
    public class Cmd_BugReport : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "BugReport";

        public override string ShortDescription =>
            "問題回報工單：report/list/show/claim/resolve。收的不只是 bug —— 文件過時 / 提示缺一半 / 流程可簡化都收。";

        public override string ArgsSchema =>
            "op=report|list|show|claim|resolve（預設 report） | " +
            "title=<簡述，report 必填> | description=<詳述，report 必填> | " +
            "evidence=<硬證據，report 必填 —— error code / log 行號 / diff / 重現指令> | " +
            "type=bug|doc|friction|suggestion（預設 bug） | " +
            "severity=blocking|wrong|annoying（預設 wrong） | " +
            "component=<受影響檔案或模組> | reporter=<persona> | " +
            "repro_steps= | expected= | actual= | " +
            "index=<單號，show/claim/resolve 必填> | assignee=<claim 用> | " +
            "resolution=fixed|wontfix|duplicate（resolve 用，預設 fixed） | note= | commit_sha= | " +
            "overwrite_sha=1（resolve 用：已關且已有 commit_sha 的單預設不覆寫，顯式帶 1 才改） | " +
            "status=<list 篩選：open（預設）/all/stale>";

        public override string ExampleArgs =>
            "op=report;title=op=step 提示沒帶 persona;description=...;evidence=canvas.py place: error: the following arguments are required: --persona;type=friction;severity=wrong;reporter=calli";

        public override string HelpURL => "ucl_core:Docs~/{lang}/Plan/Plan_BugReport_System.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();
            string aOp = GetArg(args, "op", "report").Trim().ToLowerInvariant();
            string aActor = GetArg(args, "persona", "unknown").Trim();
            var aR = new StringBuilder();
            aR.AppendLine($"# BugReport op={aOp} persona={aActor}  ts=`{DateTime.Now:yyyy-MM-dd HH:mm:sszzz}`（本地時間）");
            aR.AppendLine();

            // 回傳檔**不論成功或失敗都要寫出來**（TASK-0044；對齊 Cmd_Task 2026-08-25 的搬法）——
            // 尤其 OpReport 缺必填會 throw，而擋下原因正是 caller 最需要讀到的東西。
            try
            {
                switch (aOp)
                {
                    case "report": OpReport(args, aR); break;
                    case "list": OpList(args, aR); break;
                    case "show": OpShow(args, aR); break;
                    case "claim": OpClaim(args, aR); break;
                    case "resolve": OpResolve(args, aR); break;
                    default:
                        throw new Exception($"[BugReport] 認不得的 op='{aOp}'（report|list|show|claim|resolve）");
                }
            }
            finally
            {
                // ===========================================================
                // 區塊職責：回傳檔落 **per-persona**，不再落全域單槽（TASK-0044）。
                // 🩸 與 TASK-0026 ① 完全同族：`_last_bug_report.md` 是同一隻病的第二個宿主，
                //   而這條路本來還多毒一格 —— `op=report` 缺必填時**先把 blocked 內容寫進全域檔再 throw**
                //   （失敗路徑也在寫它）：兩個人同時報單，其中一個看到的可能是另一個人的擋下原因。
                //   ⇒ 成功與失敗現在都只落自己的檔（finally 統一寫，blocked 路徑不再另寫一份）。
                // ⚠ 舊路徑不留空殼也不刪檔：覆寫成內容固定的指路 stub（多人同時寫也不會漂）——
                //   「內容過期但長得正常」比檔不見了更毒。
                // ===========================================================
                string aPayload = UCL_LettersPath.CmdPayload(aActor, "bugreport", aOp);
                Directory.CreateDirectory(Path.GetDirectoryName(aPayload));
                File.WriteAllText(aPayload, aR.ToString(), new UTF8Encoding(false));
                UCL_AgentCommandRunner.ReportOutputFile(args, aPayload);
                Debug.Log($"[BugReport] op={aOp} persona={aActor} → {aPayload}");

                UCL_BugReportIO.EnsureDir();
                File.WriteAllText(UCL_BugReportIO.LastReportPath,
                    "# （已退場）BugReport 回傳檔不再寫在這裡\n\n"
                    + "> 這裡曾是**全域單槽**，兩個人同時跑 `run BugReport` 會互相覆蓋，\n"
                    + "> 而且失敗路徑也在寫它 —— 你可能讀到**別人的**擋下原因（TASK-0044，與 TASK-0026 ① 同族）。\n\n"
                    + "回傳檔現在落在 **`letters/<persona>/cmd/bugreport_<op>.md`** ——\n"
                    + "`run_cmd.py` 會直接印出「📄 回傳檔：<路徑>」，照那一行讀，不要背路徑。\n",
                    new UTF8Encoding(false));
            }
        }

        // ===========================================================
        // 區塊職責：開新單。
        // 物理意義：evidence 是**必填**，不是建議。
        //   原 RFC 把「先抓硬證不憑感覺」寫成 agent 自律守則 —— 守則靠人記得，欄位靠 schema 擋，
        //   而 2026-08-18 當天就有三次「我以為我記得」失手的紀錄。
        // 數值影響：配一個 index、寫一份 reports/<index>.md（一單一檔，見 UCL_BugReportIO 檔頭）。
        // ===========================================================
        void OpReport(Dictionary<string, string> iArgs, StringBuilder ioR)
        {
            string aTitle = GetArg(iArgs, "title", "").Trim();
            string aDesc = GetArg(iArgs, "description", "").Trim();
            string aEvidence = GetArg(iArgs, "evidence", "").Trim();

            // ⛔ 三個必填缺一即擋（exit != 0），不是印個警告然後照樣建單 ——
            //    一張沒有證據的單會讓下一個人重跑一次現場，那正是這套系統要消滅的成本。
            var aMissing = new List<string>();
            if (string.IsNullOrWhiteSpace(aTitle)) aMissing.Add("title");
            if (string.IsNullOrWhiteSpace(aDesc)) aMissing.Add("description");
            if (string.IsNullOrWhiteSpace(aEvidence)) aMissing.Add("evidence");
            if (aMissing.Count > 0)
            {
                ioR.AppendLine("## blocked");
                ioR.AppendLine($"- reason: 缺必填欄位：{string.Join(" / ", aMissing)}");
                ioR.AppendLine("- `evidence` 要放**感官騙不了的硬證**：error code、log 行號、round-trip diff、");
                ioR.AppendLine("  重現指令、`Cmd_Invoke` 的回傳值。重述現象不算證據。");
                // blocked 內容不再另寫全域檔 —— ExecuteAsync 的 finally 會把整份（含本段）落進 per-persona 檔（TASK-0044）
                throw new Exception($"[BugReport] report 缺必填：{string.Join(",", aMissing)}");
            }

            string aNow = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var e = new UCL_BugReportEntry
            {
                index = UCL_BugReportIO.IncrementAndGetIndex(),
                type = NormType(GetArg(iArgs, "type", "bug")),
                severity = NormSeverity(GetArg(iArgs, "severity", "wrong")),
                status = "open",
                title = aTitle,
                component = GetArg(iArgs, "component", "").Trim(),
                reporter = GetArg(iArgs, "reporter", GetArg(iArgs, "persona", "unknown")).Trim(),
                created_at = aNow,
                updated_at = aNow,
            };
            UCL_BugReportIO.Save(e, aDesc, aEvidence,
                GetArg(iArgs, "repro_steps", ""), GetArg(iArgs, "expected", ""), GetArg(iArgs, "actual", ""),
                $"{aNow}　`open`　由 {e.reporter} 開單");

            ioR.AppendLine($"## ✅ 已建單 **BUG-{e.index}**");
            ioR.AppendLine($"- `{e.type}` / `{e.severity}` / `{e.status}`　回報者：{e.reporter}");
            ioR.AppendLine($"- title: {e.title}");
            if (!string.IsNullOrEmpty(e.component)) ioR.AppendLine($"- component: `{e.component}`");
            ioR.AppendLine($"- 報告檔：`{UCL_BugReportIO.ReportPath(e.index)}`");
            AppendSimilar(ioR, e);
            ioR.AppendLine();
            ioR.AppendLine("## ▶ 下一步");
            ioR.AppendLine($"- 認領 → `run BugReport --arg op=claim --arg index={e.index} --arg assignee=<你>`");
            ioR.AppendLine($"- 修好之後 commit 訊息帶 `Fixes BUG-{e.index}`（提交時自動關單），");
            ioR.AppendLine($"  或手動 → `run BugReport --arg op=resolve --arg index={e.index} --arg commit_sha=<SHA>`");
        }

        // ===========================================================
        // 區塊職責：查重提示 —— 只呈現，不阻擋。
        // 物理意義：RFC 原本把「回報前先檢索」寫成人的守則。2026-08-18 實測那條會失敗：
        //          同一筆記憶，關鍵字查排第 7（0.54）、正文原句節錄不在 top-3、
        //          整句查才 top-1（0.74）。⇒ **關鍵字查失敗的樣子跟「不存在」一模一樣，所以它不會叫。**
        //          一個照守則辦事的人會拿到乾淨的空結果，然後開一張重複單，還以為自己查證過了。
        // ⚠ 目前是**標題字詞重疊**的粗篩（v1）—— 語意檢索串接見 Plan §6，尚未接上。
        //   刻意不假裝這裡已經是語意查重：印出來的字寫明它是粗篩，免得有人把它當成「查過了」。
        // 數值影響：純讀；不阻擋（阻擋要判斷「這算不算同一隻」，而那正是會判錯的地方）。
        // ===========================================================
        void AppendSimilar(StringBuilder ioR, UCL_BugReportEntry iNew)
        {
            var aHits = new List<(int score, UCL_BugReportEntry e)>();
            var aWords = Tokens(iNew.title);
            foreach (var e in UCL_BugReportIO.LoadAll())
            {
                if (e.index == iNew.index || e.IsClosed()) continue;
                int aScore = 0;
                foreach (var w in Tokens(e.title)) if (aWords.Contains(w)) aScore++;
                if (!string.IsNullOrEmpty(iNew.component)
                    && string.Equals(e.component, iNew.component, StringComparison.OrdinalIgnoreCase)) aScore += 2;
                if (aScore > 0) aHits.Add((aScore, e));
            }
            if (aHits.Count == 0) return;
            aHits.Sort((a, b) => b.score.CompareTo(a.score));
            ioR.AppendLine();
            ioR.AppendLine("⚠ **可能重複（未阻擋，請自行判斷）** —— v1 粗篩：標題字詞重疊 + component 相同，");
            ioR.AppendLine("　 **不是語意檢索**（語意串接見 Plan §6）。查不到 ≠ 不存在。");
            for (int i = 0; i < aHits.Count && i < 3; i++)
                ioR.AppendLine($"  - BUG-{aHits[i].e.index}　`{aHits[i].e.status}`　{aHits[i].e.title}");
        }

        static HashSet<string> Tokens(string s)
        {
            var aSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(s)) return aSet;
            foreach (var t in s.Split(new[] { ' ', '\t', '/', '\\', '(', ')', '[', ']', '，', '、', '：', ':', '。' },
                         StringSplitOptions.RemoveEmptyEntries))
                if (t.Length >= 2) aSet.Add(t);
            return aSet;
        }

        void OpList(Dictionary<string, string> iArgs, StringBuilder ioR)
        {
            string aFilter = GetArg(iArgs, "status", "open").Trim().ToLowerInvariant();
            string aType = GetArg(iArgs, "type", "").Trim().ToLowerInvariant();
            var aNow = DateTime.UtcNow;
            UCL_BugReportIO.CountOpen(out int aOpen, out int aStale, out int aBroken);
            ioR.AppendLine($"- open **{aOpen}** 筆（其中 **{aStale}** 筆超過 {UCL_BugReportIO.STALE_DAYS} 天沒動作 = stale）"
                + (aBroken > 0 ? $"；⚠ {aBroken} 筆時戳壞掉、算不出天數" : ""));
            ioR.AppendLine();
            ioR.AppendLine("| # | type | severity | status | 幾天沒動 | title |");
            ioR.AppendLine("|---|---|---|---|---|---|");
            int aShown = 0;
            foreach (var e in UCL_BugReportIO.LoadAll())
            {
                bool aClosed = e.IsClosed();
                if (aFilter == "open" && aClosed) continue;
                if (aFilter == "stale" && (aClosed || e.DaysSinceUpdate(aNow) < UCL_BugReportIO.STALE_DAYS)) continue;
                if (!string.IsNullOrEmpty(aType) && !string.Equals(e.type, aType, StringComparison.OrdinalIgnoreCase)) continue;
                int d = e.DaysSinceUpdate(aNow);
                string aDays = d < 0 ? "⚠ 壞時戳" : (d >= UCL_BugReportIO.STALE_DAYS ? $"**{d}**" : d.ToString());
                ioR.AppendLine($"| {e.index} | {e.type} | {e.severity} | {e.status} | {aDays} | {e.title} |");
                aShown++;
            }
            if (aShown == 0) ioR.AppendLine("| — | | | | | _(沒有符合的單)_ |");
        }

        void OpShow(Dictionary<string, string> iArgs, StringBuilder ioR)
        {
            var e = RequireEntry(iArgs, ioR, out int aIdx);
            ioR.AppendLine($"## BUG-{e.index} — {e.title}");
            ioR.AppendLine($"- `{e.type}` / `{e.severity}` / `{e.status}`");
            ioR.AppendLine($"- 回報者 {e.reporter}　認領 {(string.IsNullOrEmpty(e.assignee) ? "（無）" : e.assignee)}");
            ioR.AppendLine($"- created {e.created_at}　updated {e.updated_at}");
            if (!string.IsNullOrEmpty(e.commit_sha)) ioR.AppendLine($"- commit: `{e.commit_sha}`");
            string aMd = UCL_BugReportIO.ReportPath(e.index);
            ioR.AppendLine($"- 全文：`{aMd}`");
            if (File.Exists(aMd))
            {
                ioR.AppendLine();
                ioR.AppendLine(File.ReadAllText(aMd, Encoding.UTF8));
            }
        }

        void OpClaim(Dictionary<string, string> iArgs, StringBuilder ioR)
        {
            var e = RequireEntry(iArgs, ioR, out _);
            e.assignee = GetArg(iArgs, "assignee", GetArg(iArgs, "persona", "")).Trim();
            if (string.IsNullOrEmpty(e.assignee))
                throw new Exception("[BugReport] claim 需要 --arg assignee=<你>（不猜身分）");
            e.status = "in_progress";
            e.updated_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            UCL_BugReportIO.Save(e, "", "", "", "", "",
                $"{e.updated_at}　`in_progress`　由 {e.assignee} 認領");
            ioR.AppendLine($"## ✅ BUG-{e.index} 已認領 → `in_progress`（{e.assignee}）");
        }

        // 區塊職責：關單。
        // 物理意義：resolve 是**對別人的宣告**（清單上少一筆＝大家不再看它），所以要留下 who/what。
        // 數值影響：append 一行；不刪任何東西。
        void OpResolve(Dictionary<string, string> iArgs, StringBuilder ioR)
        {
            var e = RequireEntry(iArgs, ioR, out _);
            // 區塊職責：已關且已有 commit_sha 的單，**再次 resolve 不覆寫**（BUG-8 修法①）。
            // 物理意義：commit_sha 是「修法在哪」的唯一索引；後來的重複命中（父層 bump 引用、
            //   描述觸發器的那句話本身）在定義上不含修法 —— 覆寫＝把索引指去一筆不相關的 commit，
            //   而單子看起來完全正常（🩸 BUG-8：三張單全指向一筆 3 行 gitlink 位移的 bump）。
            // 數值影響：命中守衛時只追加一行變更紀錄後返回，欄位零改動；
            //   顯式 --arg overwrite_sha=1 才放行（資料修復的合法通道，取代手改 md）。
            bool aAlreadyClosed = e.status == "resolved" || e.status == "wontfix" || e.status == "duplicate";
            string aNewSha = GetArg(iArgs, "commit_sha", "").Trim();
            if (aAlreadyClosed && !string.IsNullOrEmpty(e.commit_sha)
                && GetArg(iArgs, "overwrite_sha", "") != "1")
            {
                string aNow = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                UCL_BugReportIO.Save(e, "", "", "", "", "",
                    $"{aNow}　`skip-reclose`　{(string.IsNullOrEmpty(aNewSha) ? "（無 SHA）" : aNewSha)} —— 已由 {e.commit_sha} 關過，本次不覆寫");
                ioR.AppendLine($"## ⏭ BUG-{e.index} 已由 `{e.commit_sha}` 關過（{e.status}），本次不覆寫");
                ioR.AppendLine($"- 本次命中已追加進變更紀錄；確定要改 SHA（資料修復）→ 加 `--arg overwrite_sha=1`");
                return;
            }
            // 只對枚舉值正規化，自由文字保留原樣（TASK-0044 順帶格）——
            // 🩸 2026-08-25：resolution 收到「轉為 TASK-0026（Tim 拍板…）」被整句 ToLower 成
            //   「task-0026（tim …」⇒ 單號與人名被弄壞，搜 `TASK-0026` 找不到那一筆。
            //   狀態要正規化，自由文字不該 —— 判準是「它是不是三個已知值之一」，不是「一律壓小寫」。
            string aResRaw = GetArg(iArgs, "resolution", "fixed").Trim();
            string aResLower = aResRaw.ToLowerInvariant();
            bool aKnown = aResLower == "fixed" || aResLower == "wontfix" || aResLower == "duplicate";
            string aRes = aKnown ? aResLower : aResRaw;
            e.status = aResLower == "wontfix" ? "wontfix" : aResLower == "duplicate" ? "duplicate" : "resolved";
            e.resolution = aRes;
            e.resolution_note = GetArg(iArgs, "note", "").Trim();
            e.commit_sha = aNewSha;
            e.updated_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            UCL_BugReportIO.Save(e, "", "", "", "", "",
                $"{e.updated_at}　`{e.status}`　{(string.IsNullOrEmpty(e.commit_sha) ? "手動關單" : e.commit_sha)}"
                + (string.IsNullOrEmpty(e.resolution_note) ? "" : $" —— {e.resolution_note}"));
            ioR.AppendLine($"## ✅ BUG-{e.index} → `{e.status}`"
                + (string.IsNullOrEmpty(e.commit_sha) ? "" : $"（{e.commit_sha}）"));
            if (!string.IsNullOrEmpty(e.resolution_note)) ioR.AppendLine($"- note: {e.resolution_note}");
        }

        UCL_BugReportEntry RequireEntry(Dictionary<string, string> iArgs, StringBuilder ioR, out int oIdx)
        {
            string aRaw = GetArg(iArgs, "index", "").Trim();
            if (!int.TryParse(aRaw, out oIdx) || oIdx <= 0)
                throw new Exception($"[BugReport] 需要 --arg index=<單號>（1 起的整數），收到 '{aRaw}'");
            var e = UCL_BugReportIO.Find(oIdx);
            if (e == null) throw new Exception($"[BugReport] 找不到 BUG-{oIdx}");
            return e;
        }

        // 認不得的值**不靜默 fallback** —— 打錯而系統照常運作，是「以為有分類其實沒有」那一類最難查的壞法。
        static string NormType(string iRaw)
        {
            string v = (iRaw ?? "").Trim().ToLowerInvariant();
            if (v == "bug" || v == "doc" || v == "friction" || v == "suggestion") return v;
            if (string.IsNullOrEmpty(v)) return "bug";
            throw new Exception($"[BugReport] 認不得的 type='{iRaw}'（bug|doc|friction|suggestion）");
        }

        static string NormSeverity(string iRaw)
        {
            string v = (iRaw ?? "").Trim().ToLowerInvariant();
            if (v == "blocking" || v == "wrong" || v == "annoying") return v;
            if (string.IsNullOrEmpty(v)) return "wrong";
            throw new Exception($"[BugReport] 認不得的 severity='{iRaw}'（blocking|wrong|annoying）");
        }
    }
}
#endif
