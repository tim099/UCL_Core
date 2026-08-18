// 區塊職責：relationship 的 Cmd 入口 —— 寫事件 / 加看法 / 查詢 / 重建。
// 物理意義：好感度的**唯一寫入通道**。跟「錢一律走 Cmd」同一個理由：
//          重算與落檔的規則只有 C# 這一份，python 直寫會繞過它而且不會報錯。
// 數值影響：寫 letters/<persona>/relationship/ 底下的檔；不動 Treasury、不發酒館訊息。
// 設計沿革：Plan_Relationship_System.md（Tim 2026-08-18 拍板；取代舊 affinity_update.py）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Relationship
{
    public class Cmd_Relationship : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Relationship";

        public override string ShortDescription =>
            "好感度（relationship）：update 寫一筆事件 / add-opinion 加看法 / show / list / rebuild。";

        public override string ArgsSchema =>
            "op=update|add-opinion|show|list|rebuild（預設 update） | " +
            "persona=<誰的感受，required> | target=<對誰，update/add-opinion/show required> | " +
            "reason=<這件事是什麼，update required> | " +
            "trust= affection= respect= interest= irritation= dependence= admiration= loyalty=" +
            "（各軸 delta，[-1,1]，至少給一個；建議一次動 2-4 軸不是 1 個也不是全 8） | " +
            "opinion=<內心戲短句，選填；op=add-opinion 時 required>";

        public override string ExampleArgs =>
            "op=update;persona=calli;target=Tim;reason=Tim 戳穿我的盲點;trust=0.05;respect=0.03;admiration=0.02";

        public override string HelpURL => "ucl_core:Skills~/ucl-relationship/SKILL.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();
            string aOp = GetArg(args, "op", "update").Trim().ToLowerInvariant();
            string aPersona = GetArg(args, "persona", "").Trim();
            if (string.IsNullOrEmpty(aPersona))
                throw new Exception("[Relationship] 需要 --arg persona=<誰的感受>（不猜身分）");

            var aR = new StringBuilder();
            aR.AppendLine($"# Relationship op={aOp} persona={aPersona}"
                + $"  ts=`{DateTime.Now:yyyy-MM-dd HH:mm:sszzz}`（本地時間）");
            aR.AppendLine();

            switch (aOp)
            {
                case "update": OpUpdate(args, aPersona, aR); break;
                case "add-opinion": OpAddOpinion(args, aPersona, aR); break;
                case "show": OpShow(args, aPersona, aR); break;
                case "list": OpList(aPersona, aR); break;
                case "rebuild": OpRebuild(args, aPersona, aR); break;
                default:
                    throw new Exception($"[Relationship] 認不得的 op='{aOp}'"
                        + "（update|add-opinion|show|list|rebuild）");
            }

            string aPath = Path.Combine(UCL_LettersPath.PersonaDir(aPersona), $"_relationship_{aOp}.md");
            Directory.CreateDirectory(Path.GetDirectoryName(aPath));
            File.WriteAllText(aPath, aR.ToString(), new UTF8Encoding(false));
            Debug.Log($"[Relationship] op={aOp} {aPersona} → {aPath}");
        }

        // ===========================================================
        // 區塊職責：寫一筆好感事件。
        // 物理意義：**事件是事實來源**，`_current.md` 只是投影 —— 所以這裡寫完就重建投影。
        // ⚠ `at` 用當下時刻，而它同時是**檔名與身分**。同一 persona 對同一對象
        //   在同一毫秒寫兩筆的機率可以忽略；真撞上時 WriteEvent 會另存 `-b` 並 LogError，
        //   不會靜默覆蓋（見那裡的註解）。
        // 數值影響：寫一個事件檔 ＋ 重建一份 `_current.md`；不碰其他 target。
        // ===========================================================
        void OpUpdate(Dictionary<string, string> iArgs, string iPersona, StringBuilder ioR)
        {
            string aTarget = RequireTarget(iArgs);
            string aReason = GetArg(iArgs, "reason", "").Trim();
            if (string.IsNullOrEmpty(aReason))
                throw new Exception("[Relationship] update 需要 --arg reason=<這件事是什麼>"
                    + " —— 沒有理由的 delta，三個月後沒有人看得懂它為什麼發生");

            var aDeltas = new Dictionary<string, float>();
            foreach (var aAxis in UCL_RelationshipAxes.Names)
            {
                string v = GetArg(iArgs, aAxis, "").Trim();
                if (v.Length == 0) continue;
                if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float d))
                    throw new Exception($"[Relationship] 軸 {aAxis} 的值 '{v}' 不是數字");
                // 認不得的值不靜默吃掉 —— 打錯一個小數點會讓一次事件的量級差十倍
                if (d < UCL_RelationshipAxes.MIN || d > UCL_RelationshipAxes.MAX)
                    throw new Exception($"[Relationship] 軸 {aAxis} 的 delta {d} 超出 [-1,1]");
                if (Math.Abs(d) > 0.0001f) aDeltas[aAxis] = d;
            }
            if (aDeltas.Count == 0)
                throw new Exception("[Relationship] 至少要給一個軸的 delta"
                    + $"（{string.Join(" / ", UCL_RelationshipAxes.Names)}）");

            var e = new UCL_RelationshipEvent
            {
                at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                persona = iPersona,
                target = UCL_RelationshipIO.CanonicalTarget(aTarget),
                source = "live",
                axis_deltas = aDeltas,
                reason = aReason,
            };
            UCL_RelationshipIO.WriteEvent(e, false, out string aEvPath);

            string aOpinion = GetArg(iArgs, "opinion", "").Trim();
            if (aOpinion.Length > 0) WriteOpinion(iPersona, e.target, aOpinion, ioR);

            var aCur = UCL_RelationshipIO.RebuildCurrent(iPersona, e.target, null, false);
            e.surface_score_after = aCur.surface_score;

            ioR.AppendLine($"## ✅ {iPersona} → {e.target}");
            ioR.AppendLine($"- 事件：`{Path.GetFileName(aEvPath)}`");
            ioR.AppendLine($"- 動了 {aDeltas.Count} 軸："
                + string.Join("　", DeltaStrings(aDeltas)));
            ioR.AppendLine($"- 現值：**{aCur.surface_score}**（{aCur.tier}）　"
                + $"累計事件 {aCur.event_count} 筆 / 看法 {aCur.opinion_count} 則");
            ioR.AppendLine($"- reason: {aReason}");
            if (aDeltas.Count == 1)
                ioR.AppendLine("- ℹ 只動了一軸 —— 真實情緒通常多軸並存，建議一次 2~4 軸。");
        }

        static IEnumerable<string> DeltaStrings(Dictionary<string, float> d)
        {
            foreach (var kv in d)
                yield return $"{kv.Key} {(kv.Value >= 0 ? "+" : "")}{kv.Value.ToString("0.####", CultureInfo.InvariantCulture)}";
        }

        void OpAddOpinion(Dictionary<string, string> iArgs, string iPersona, StringBuilder ioR)
        {
            string aTarget = UCL_RelationshipIO.CanonicalTarget(RequireTarget(iArgs));
            string aText = GetArg(iArgs, "opinion", "").Trim();
            if (aText.Length == 0) throw new Exception("[Relationship] add-opinion 需要 --arg opinion=<短句>");
            WriteOpinion(iPersona, aTarget, aText, ioR);
            UCL_RelationshipIO.RebuildCurrent(iPersona, aTarget, null, false);
        }

        // 看法與向量**解耦**（分開的資料夾就是為了讓這件事是結構性的）——
        // 所以 add-opinion 不動任何軸，update 帶 opinion 也只是順手寫，不建立關聯。
        void WriteOpinion(string iPersona, string iTarget, string iText, StringBuilder ioR)
        {
            var o = new UCL_RelationshipOpinion
            {
                text = iText,
                at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),   // 新寫的一律有時戳
            };
            o.origin.Add("live");
            bool aNew = UCL_RelationshipIO.WriteOpinion(iPersona, iTarget, o, false, out string p);
            ioR.AppendLine(aNew
                ? $"- 看法：`{Path.GetFileName(p)}`"
                : "- 看法：**內容與既有的一則完全相同，未重複寫入**（去重靠內容雜湊）");
        }

        void OpShow(Dictionary<string, string> iArgs, string iPersona, StringBuilder ioR)
        {
            string aTarget = UCL_RelationshipIO.CanonicalTarget(RequireTarget(iArgs));
            string aCur = UCL_RelationshipIO.CurrentPath(iPersona, aTarget);
            if (!File.Exists(aCur))
            {
                ioR.AppendLine($"## （{iPersona} 對 {aTarget} 還沒有任何紀錄）");
                return;
            }
            ioR.AppendLine(File.ReadAllText(aCur, Encoding.UTF8));
            string aOpDir = UCL_RelationshipIO.OpinionsDir(iPersona, aTarget);
            if (!Directory.Exists(aOpDir)) return;
            var aFiles = Directory.GetFiles(aOpDir, "*.md");
            Array.Sort(aFiles, StringComparer.Ordinal);
            ioR.AppendLine();
            ioR.AppendLine($"## 看法（{aFiles.Length} 則）");
            foreach (var f in aFiles)
            {
                string b = ReadBodyOf(f);
                if (b.Length > 0) ioR.AppendLine("- " + b.Replace("\n", " "));
            }
        }

        void OpList(string iPersona, StringBuilder ioR)
        {
            string d = UCL_RelationshipIO.PersonaDir(iPersona);
            if (!Directory.Exists(d)) { ioR.AppendLine("（這位還沒有任何 relationship 資料）"); return; }
            ioR.AppendLine("| 對象 | 分數 | tier | 事件 | 看法 |");
            ioR.AppendLine("|---|---|---|---|---|");
            var aDirs = Directory.GetDirectories(d);
            Array.Sort(aDirs, StringComparer.Ordinal);
            foreach (var t in aDirs)
            {
                string aName = Path.GetFileName(t);
                var aCur = ReadCurrentBrief(Path.Combine(t, "_current.md"));
                ioR.AppendLine($"| {aName} | {aCur.score} | {aCur.tier} | "
                    + $"{CountMd(Path.Combine(t, UCL_RelationshipIO.EVENTS))} | "
                    + $"{CountMd(Path.Combine(t, UCL_RelationshipIO.OPINIONS))} |");
            }
        }

        // 區塊職責：投影重建 —— `_current.md` 壞了或被刪了都可以叫它。
        // 物理意義：**投影可以重建**這件事本身就是新架構的保證；
        //          舊制的存值一旦漂了就只能人工修，因為沒有東西能重算它。
        void OpRebuild(Dictionary<string, string> iArgs, string iPersona, StringBuilder ioR)
        {
            string aTarget = GetArg(iArgs, "target", "").Trim();
            var aList = new List<string>();
            if (aTarget.Length > 0) aList.Add(UCL_RelationshipIO.CanonicalTarget(aTarget));
            else
            {
                string d = UCL_RelationshipIO.PersonaDir(iPersona);
                if (Directory.Exists(d))
                    foreach (var t in Directory.GetDirectories(d)) aList.Add(Path.GetFileName(t));
            }
            foreach (var t in aList)
            {
                var c = UCL_RelationshipIO.RebuildCurrent(iPersona, t, null, false);
                ioR.AppendLine($"- {t}：{c.surface_score}（{c.tier}）事件 {c.event_count} 筆");
            }
            ioR.AppendLine();
            ioR.AppendLine($"共重建 {aList.Count} 份 `_current.md`。");
        }

        string RequireTarget(Dictionary<string, string> iArgs)
        {
            string t = GetArg(iArgs, "target", "").Trim();
            if (t.Length == 0) throw new Exception("[Relationship] 需要 --arg target=<對誰>");
            return t;
        }

        static int CountMd(string d) => Directory.Exists(d) ? Directory.GetFiles(d, "*.md").Length : 0;

        static (int score, string tier) ReadCurrentBrief(string iPath)
        {
            if (!File.Exists(iPath)) return (0, "—");
            int s = 0; string t = "—";
            foreach (var ln in File.ReadAllLines(iPath, Encoding.UTF8))
            {
                if (ln.StartsWith("surface_score:", StringComparison.Ordinal))
                    int.TryParse(ln.Substring(14).Trim(), out s);
                else if (ln.StartsWith("tier:", StringComparison.Ordinal)) t = ln.Substring(5).Trim();
                else if (ln.StartsWith("event_count:", StringComparison.Ordinal)) break;
            }
            return (s, t);
        }

        static string ReadBodyOf(string iPath)
        {
            try
            {
                var sb = new StringBuilder();
                int aDash = 0;
                foreach (var ln in File.ReadAllLines(iPath, Encoding.UTF8))
                {
                    if (aDash < 2 && ln.StartsWith("---", StringComparison.Ordinal)) { aDash++; continue; }
                    if (aDash >= 2) sb.Append(ln).Append('\n');
                }
                return sb.ToString().Trim();
            }
            catch { return ""; }
        }
    }
}
#endif
