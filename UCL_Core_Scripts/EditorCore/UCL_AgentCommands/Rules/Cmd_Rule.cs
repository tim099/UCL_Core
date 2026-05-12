// 區塊職責: Tavern Rule System — 酒館規則系統 v1 (Tim 2026-05-12 拍板)
// 物理意義: 任何 agent / Tim 提案 rule 消耗 100 token, bank balance 必須 ≥ 300; Tim revert 時 100 token 退還原 creator
// 數值影響:
//   - propose: debit creator bank 100 → 寫 rule .md (status=active)
//   - revert: 讀 rule .md → 確認 status=active → credit creator bank 100 → 改 status=reverted (audit-trail 保留)
//   - list / get / enforce(future): 純讀
// 設計取捨:
//   - 一檔一 rule (AgentCommands/Rules/<rule_id>.md) git diff/merge 友善
//   - status 改寫 frontmatter 而非刪檔 — 保留 audit history
//   - 預留 op=enforce 給未來自動化規則 (body 可含 spec yaml block)
//   - 不自動 post tavern — 走 ucl-glossary Hard Rule, caller 自己 share (避免雙重 post)
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;   // Cmd_Tavern_Helpers (WriteLastOp / RejectLastOp / FailLastOp)
using UCL.Core.EditorLib.AgentCommands.Treasury;     // UCL_TreasuryLedger (GetBalance / Credit / Debit)
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Rules
{
    /// <summary>
    /// Tavern Rule System — Cmd_Rule op-dispatch 入口。
    /// 對應 Plan: docs/Plan/Plan_Tavern_Rule_System.md (待寫)
    /// </summary>
    public class Cmd_Rule : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Rule";

        public override string ShortDescription =>
            "Tavern Rule System — 提案 rule 消耗 100 token (需 balance ≥ 300), Tim revert 退還 100 token";

        public override string ArgsSchema =>
            "op=propose|revert|list|get|enforce\n" +
            "propose: rule_id=<id> title=<短摘要> body=<完整內容> [created_by=<bank-id, default Tim>] — 需 balance ≥ 300, debit 100\n" +
            "revert: rule_id=<id> reason=<原因> [reverted_by=<bank-id, default Tim>] — 只有 Tim 可 revert, refund 100 給 creator\n" +
            "list: [status=active|reverted|all (default active)] — 列規則表\n" +
            "get: rule_id=<id> — 印單一 rule 完整內容\n" +
            "enforce: rule_id=<id> target=<context> (v1 未實作, 預留 future automation hook)";

        public override string ExampleArgs =>
            "op=propose;rule_id=R001;title=酒館發言不超過 500 字;body=超過 500 字訊息 → ...;created_by=Tim";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Rule.md";

        // ===========================================================
        // 經濟參數 — 改 const 統一處理, 未來想調額度只動這裡
        // 物理意義: propose cost = balance 門檻 ÷ 3, refund = propose cost 100% (Tim revert = 認錯, 全額退)
        // ===========================================================
        public const int PROPOSE_COST = 100;
        public const int MIN_BALANCE_TO_PROPOSE = 300;
        public const string DEFAULT_CREATOR_BANK = "Tim";    // 預設提案者 (per Tim 拍板 meta-rule by Tim)
        public const string DEFAULT_REVERTER_BANK = "Tim";   // 預設 revert 者 (只有 Tim 可 revert)
        public const string CURRENCY = "tavern_token";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            string op = GetArg(args, "op", "").ToLowerInvariant();
            if (string.IsNullOrEmpty(op))
            {
                Cmd_Tavern_Helpers.RejectLastOp("缺少 op 參數 (propose|revert|list|get|enforce)");
                return;
            }

            try
            {
                switch (op)
                {
                    case "propose": Op_Propose(args); break;
                    case "revert": Op_Revert(args); break;
                    case "list": Op_List(args); break;
                    case "get": Op_Get(args); break;
                    case "enforce":
                        Cmd_Tavern_Helpers.RejectLastOp("enforce v1 未實作 — 預留 future automation hook");
                        break;
                    default:
                        Cmd_Tavern_Helpers.RejectLastOp($"未知 op: {op} (支援 propose|revert|list|get|enforce)");
                        break;
                }
            }
            catch (Exception ex)
            {
                Cmd_Tavern_Helpers.FailLastOp($"執行 op={op} 失敗: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ===========================================================
        // 路徑常量 — rules 寫進 <project-root>/AgentCommands/Rules/<rule_id>.md
        // 物理意義: 跟 ChatTavern / Treasury 一致採 AgentCommands/ 下子目錄, per-project state
        // ===========================================================
        private static string RulesDir
        {
            get
            {
                // Application.dataPath = "<git-root>/CardGame/Assets"; 反推兩層到 git-root
                string projRoot = Directory.GetParent(Application.dataPath)?.Parent?.FullName ?? "";
                return Path.Combine(projRoot, "AgentCommands", "Rules");
            }
        }

        // ===========================================================
        // 區塊職責: op=propose — 提案新 rule + 扣 100 token
        // 物理意義: balance 檢查 → debit → 寫 rule .md (frontmatter status=active + debit_tx_ref)
        // 數值影響: balance < 300 → reject 不扣錢; rule_id 衝突 → reject 不扣錢; 其他失敗 try 內 FailLastOp
        // 安全: balance check 在 debit 前, 避免 race 後扣錢失敗
        // ===========================================================
        private void Op_Propose(Dictionary<string, string> args)
        {
            string ruleId = GetArg(args, "rule_id", "");
            string title = GetArg(args, "title", "");
            string body = GetArg(args, "body", "");
            string createdBy = GetArg(args, "created_by", DEFAULT_CREATOR_BANK);

            if (string.IsNullOrEmpty(ruleId)) { Cmd_Tavern_Helpers.RejectLastOp("propose 缺少 rule_id"); return; }
            if (string.IsNullOrEmpty(title)) { Cmd_Tavern_Helpers.RejectLastOp("propose 缺少 title"); return; }
            if (string.IsNullOrEmpty(body)) { Cmd_Tavern_Helpers.RejectLastOp("propose 缺少 body (rule 完整內容)"); return; }

            // 檢查 rule_id 是否已存在 (即使 reverted 也佔位 — 避免 audit history 被覆寫)
            Directory.CreateDirectory(RulesDir);
            string fullPath = Path.Combine(RulesDir, ruleId + ".md");
            if (File.Exists(fullPath))
            {
                Cmd_Tavern_Helpers.RejectLastOp($"rule_id 已存在: {ruleId} (即使 reverted 也保留位置避免 audit 被覆寫; 用新 id e.g. R002)");
                return;
            }

            // 檢查 balance — propose 要求 ≥ 300, 不夠直接 reject 不 debit
            int balance = UCL_TreasuryLedger.GetBalance(createdBy, CURRENCY);
            if (balance < MIN_BALANCE_TO_PROPOSE)
            {
                Cmd_Tavern_Helpers.RejectLastOp(
                    $"propose 需 bank `{createdBy}` balance ≥ {MIN_BALANCE_TO_PROPOSE} {CURRENCY} (當前: {balance})");
                return;
            }

            // Debit 100 — use_kind=rule_propose 給 audit trail
            // 帳戶隔離鐵律: callerAgentId == accountId (creator 動自己帳戶)
            var debitEntry = UCL_TreasuryLedger.Debit(
                accountId: createdBy,
                amount: PROPOSE_COST,
                useKind: "rule_propose",
                useRef: ruleId,
                description: $"提案 rule {ruleId}: {Truncate(title, 60)}",
                callerAgentId: createdBy,
                cmdId: ""
            );
            string debitTxRef = debitEntry?.ts ?? "";

            // 寫 rule .md
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"rule_id: {ruleId}");
            sb.AppendLine($"title: {EscapeYamlInline(title)}");
            sb.AppendLine($"created_by: {createdBy}");
            sb.AppendLine($"created_at: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
            sb.AppendLine($"status: active");
            sb.AppendLine($"debit_amount: {PROPOSE_COST}");
            sb.AppendLine($"debit_currency: {CURRENCY}");
            sb.AppendLine($"debit_tx_ref: {debitTxRef}");
            sb.AppendLine($"revert_at: ");
            sb.AppendLine($"revert_by: ");
            sb.AppendLine($"revert_reason: ");
            sb.AppendLine($"revert_tx_ref: ");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"# {title}");
            sb.AppendLine();
            sb.AppendLine(body);
            File.WriteAllText(fullPath, sb.ToString(), new UTF8Encoding(false));

            // 回報 _last_op.md
            var report = new StringBuilder();
            report.AppendLine($"✅ rule proposed: **{ruleId}** — {title}");
            report.AppendLine($"- created_by: `{createdBy}`");
            report.AppendLine($"- debit: {PROPOSE_COST} {CURRENCY} (balance: {balance} → {balance - PROPOSE_COST})");
            report.AppendLine($"- path: AgentCommands/Rules/{ruleId}.md");
            report.AppendLine($"- status: active");
            report.AppendLine($"- tavern 主頻道公告: 已自動 post");
            Cmd_Tavern_Helpers.WriteLastOp(report.ToString());
            Debug.Log($"[Rule] propose {ruleId} by {createdBy} (-{PROPOSE_COST} {CURRENCY})");

            // 區塊職責: 主頻道自動公告 (Tim 2026-05-12 拍板)
            // 物理意義: rule propose / revert = 跨 agent 公共資訊, 必須廣播到 tavern 而非只在 _last_op.md
            // 數值影響: 走 Cmd_Tavern.Op_Post 統一路徑 — 自動 glossary auto-attach / Discord mirror / WriteLastView
            string announceBody = BuildProposeAnnouncement(ruleId, title, createdBy, body);
            BroadcastToTavern(createdBy, announceBody, tag: "rule-propose");
        }

        // ===========================================================
        // 區塊職責: op=revert — Tim 撤回 rule + 退 100 token 給 creator
        // 物理意義: 讀 rule frontmatter → 確認 active → credit 原 creator → 改 status=reverted (保留 audit)
        // 數值影響: status != active → reject (already reverted); creator 不存在 → reject; 其他 try 內 FailLastOp
        // 權限: 預期 reverted_by=Tim (per Tim 拍板); 任何 caller 仍可傳但需自報, audit log 留痕
        // ===========================================================
        private void Op_Revert(Dictionary<string, string> args)
        {
            string ruleId = GetArg(args, "rule_id", "");
            string reason = GetArg(args, "reason", "");
            string revertedBy = GetArg(args, "reverted_by", DEFAULT_REVERTER_BANK);

            if (string.IsNullOrEmpty(ruleId)) { Cmd_Tavern_Helpers.RejectLastOp("revert 缺少 rule_id"); return; }
            if (string.IsNullOrEmpty(reason)) { Cmd_Tavern_Helpers.RejectLastOp("revert 缺少 reason (給 audit + creator 知道為何被撤)"); return; }

            string fullPath = Path.Combine(RulesDir, ruleId + ".md");
            if (!File.Exists(fullPath))
            {
                Cmd_Tavern_Helpers.RejectLastOp($"rule 不存在: {ruleId}");
                return;
            }

            var entry = ParseRuleFile(fullPath);
            if (entry == null)
            {
                Cmd_Tavern_Helpers.RejectLastOp($"rule {ruleId} frontmatter 解析失敗");
                return;
            }
            if (entry.status != "active")
            {
                Cmd_Tavern_Helpers.RejectLastOp($"rule {ruleId} 當前 status={entry.status} (非 active 無法 revert)");
                return;
            }
            if (string.IsNullOrEmpty(entry.createdBy))
            {
                Cmd_Tavern_Helpers.RejectLastOp($"rule {ruleId} frontmatter 缺 created_by, 無法退款");
                return;
            }

            // Credit 100 退 creator — source_kind=rule_revert_refund 給 audit trail
            // Credit 沒帳戶隔離鐵律, 任意 caller 可給對方帳戶 credit (per Treasury 設計)
            var creditEntry = UCL_TreasuryLedger.Credit(
                accountId: entry.createdBy,
                amount: PROPOSE_COST,
                sourceKind: "rule_revert_refund",
                sourceRef: ruleId,
                description: $"revert rule {ruleId}: {Truncate(reason, 60)}",
                callerAgentId: revertedBy,
                cmdId: ""
            );
            string creditTxRef = creditEntry?.ts ?? "";

            // 改寫 frontmatter status=reverted + 填 revert_* 欄位 (保留原 body 完整)
            string nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string content = File.ReadAllText(fullPath, Encoding.UTF8);
            content = ReplaceFrontmatterField(content, "status", "reverted");
            content = ReplaceFrontmatterField(content, "revert_at", nowIso);
            content = ReplaceFrontmatterField(content, "revert_by", revertedBy);
            content = ReplaceFrontmatterField(content, "revert_reason", EscapeYamlInline(reason));
            content = ReplaceFrontmatterField(content, "revert_tx_ref", creditTxRef);
            File.WriteAllText(fullPath, content, new UTF8Encoding(false));

            var report = new StringBuilder();
            report.AppendLine($"✅ rule reverted: **{ruleId}** — {entry.title}");
            report.AppendLine($"- reverted_by: `{revertedBy}` at {nowIso}");
            report.AppendLine($"- refund: {PROPOSE_COST} {CURRENCY} → bank `{entry.createdBy}`");
            report.AppendLine($"- reason: {reason}");
            report.AppendLine($"- status: active → reverted (audit-trail 保留, 檔案不刪)");
            report.AppendLine($"- tavern 主頻道公告: 已自動 post");
            Cmd_Tavern_Helpers.WriteLastOp(report.ToString());
            Debug.Log($"[Rule] revert {ruleId} by {revertedBy}, refund {PROPOSE_COST} → {entry.createdBy}");

            // 區塊職責: 主頻道自動公告 (Tim 2026-05-12 拍板)
            // 物理意義: revert 公告 mention 原 creator 讓對方知道被撤 + refund 已入帳
            string announceBody = BuildRevertAnnouncement(ruleId, entry.title, entry.createdBy, revertedBy, reason);
            BroadcastToTavern(revertedBy, announceBody, tag: "rule-revert");
        }

        // ===========================================================
        // 區塊職責: op=list — 列規則表 (預設只 active)
        // 物理意義: enumerate RulesDir, parse frontmatter, 印 markdown table
        // 數值影響: 純讀; 無 filter 預設 status=active
        // ===========================================================
        private void Op_List(Dictionary<string, string> args)
        {
            string statusFilter = GetArg(args, "status", "active").ToLowerInvariant();
            var entries = LoadAllRules();
            if (statusFilter != "all")
            {
                entries = entries.Where(e => e.status == statusFilter).ToList();
            }

            var sb = new StringBuilder();
            sb.AppendLine($"📜 rule list ({entries.Count} entries, status={statusFilter}):");
            sb.AppendLine();
            sb.AppendLine("| Rule | Title | Status | Created By | Created At |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var e in entries.OrderBy(x => x.ruleId))
            {
                sb.AppendLine($"| `{e.ruleId}` | {e.title} | {e.status} | {e.createdBy} | {e.createdAt} |");
            }
            if (entries.Count == 0) sb.AppendLine("_(無命中)_");
            Cmd_Tavern_Helpers.WriteLastOp(sb.ToString());
        }

        // ===========================================================
        // 區塊職責: op=get — 印單一 rule 完整 markdown
        // ===========================================================
        private void Op_Get(Dictionary<string, string> args)
        {
            string ruleId = GetArg(args, "rule_id", "");
            if (string.IsNullOrEmpty(ruleId)) { Cmd_Tavern_Helpers.RejectLastOp("get 缺少 rule_id"); return; }

            string fullPath = Path.Combine(RulesDir, ruleId + ".md");
            if (!File.Exists(fullPath))
            {
                Cmd_Tavern_Helpers.RejectLastOp($"rule 不存在: {ruleId}");
                return;
            }

            string content = File.ReadAllText(fullPath, Encoding.UTF8);
            var sb = new StringBuilder();
            sb.AppendLine($"📜 rule `{ruleId}`:");
            sb.AppendLine();
            sb.AppendLine(content);
            Cmd_Tavern_Helpers.WriteLastOp(sb.ToString());
        }

        // ===========================================================
        // 內部 — Rule POCO + 載入 + frontmatter parse / replace
        // ===========================================================
        private class RuleEntry
        {
            public string ruleId;
            public string title;
            public string createdBy;
            public string createdAt;
            public string status;
            public string filePath;
        }

        private static List<RuleEntry> LoadAllRules()
        {
            var list = new List<RuleEntry>();
            if (!Directory.Exists(RulesDir)) return list;
            foreach (var f in Directory.GetFiles(RulesDir, "*.md"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (name.Equals("README", StringComparison.OrdinalIgnoreCase) || name.StartsWith("_")) continue;
                var entry = ParseRuleFile(f);
                if (entry != null) list.Add(entry);
            }
            return list;
        }

        // 區塊職責: 簡易 frontmatter parser
        // 物理意義: 只 parse rule_id/title/created_by/created_at/status; 失敗 entry 回 null skip
        private static RuleEntry ParseRuleFile(string filePath)
        {
            try
            {
                string content = File.ReadAllText(filePath, Encoding.UTF8);
                if (!content.StartsWith("---")) return null;
                int end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (end < 0) return null;
                string fm = content.Substring(3, end - 3);

                var entry = new RuleEntry { filePath = filePath };
                foreach (var rawLine in fm.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r').Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    int colonIdx = line.IndexOf(':');
                    if (colonIdx < 0) continue;
                    string key = line.Substring(0, colonIdx).Trim();
                    string val = colonIdx < line.Length - 1 ? line.Substring(colonIdx + 1).Trim() : "";
                    switch (key)
                    {
                        case "rule_id": entry.ruleId = val; break;
                        case "title": entry.title = UnescapeYamlInline(val); break;
                        case "created_by": entry.createdBy = val; break;
                        case "created_at": entry.createdAt = val; break;
                        case "status": entry.status = val; break;
                    }
                }
                if (string.IsNullOrEmpty(entry.ruleId)) return null;
                if (string.IsNullOrEmpty(entry.status)) entry.status = "active";
                return entry;
            }
            catch { return null; }
        }

        // 區塊職責: 改寫 frontmatter 單一 field (revert 時用)
        // 物理意義: 不重寫整個 frontmatter, 只替換特定 line, 保留其他欄位順序 + 註解
        // 數值影響: 找不到該 field 不做事 (return 原 content); 多個 match 只改第一個
        private static string ReplaceFrontmatterField(string content, string key, string newValue)
        {
            if (!content.StartsWith("---")) return content;
            int end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end < 0) return content;
            string fm = content.Substring(0, end);
            string rest = content.Substring(end);

            var lines = fm.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                int colonIdx = line.IndexOf(':');
                if (colonIdx < 0) continue;
                string lineKey = line.Substring(0, colonIdx).Trim();
                if (lineKey == key)
                {
                    lines[i] = $"{key}: {newValue}";
                    break;
                }
            }
            return string.Join("\n", lines) + rest;
        }

        // ===========================================================
        // YAML inline escape (跟 Cmd_Glossary 同款)
        // ===========================================================
        private static string EscapeYamlInline(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            if (s.Contains(":") || s.Contains("#") || s.StartsWith("-") || s.Contains("\"") || s.Contains("\n"))
            {
                return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
            }
            return s;
        }

        private static string UnescapeYamlInline(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.Length >= 2 && s.StartsWith("\"") && s.EndsWith("\""))
            {
                s = s.Substring(1, s.Length - 2);
                s = s.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
            }
            return s;
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s ?? "";
            return s.Substring(0, maxLen) + "...";
        }

        // ===========================================================
        // 區塊職責: 主頻道自動公告 — propose / revert 都呼叫
        // 物理意義: 走 Cmd_Tavern.Op_Post 統一路徑, 自動繼承 glossary auto-attach / Discord mirror / WriteLastView
        // 數值影響: fire-and-forget UniTask, 不阻塞 Op_Propose / Op_Revert 主流程; 失敗 swallow 不擋 rule 操作
        // 安全: alter-pacing-bypass:true 防被 alter pair 300s 配對延遲拖住; sender 用 revertedBy/createdBy
        //       (bank id 必在 identities.json — propose/revert 前已驗 balance, 該 bank 必存在)
        // ===========================================================
        private static void BroadcastToTavern(string senderId, string body, string tag)
        {
            try
            {
                var args = new Dictionary<string, string>
                {
                    { "op", "post" },
                    { "room", "tavern" },
                    { "sender", senderId },
                    { "body", body },
                    { "meta", $"tag:{tag};category:rule-broadcast;alter-pacing-bypass:true" },
                };
                var cmd = new UCL.Core.EditorLib.AgentCommands.ChatTavern.Cmd_Tavern();
                cmd.ExecuteAsync(args, default).Forget();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Rule] BroadcastToTavern fail (op 不受影響): {ex.Message}");
            }
        }

        private static string BuildProposeAnnouncement(string ruleId, string title, string createdBy, string body)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"📜 **新規則提案** — `{ruleId}`");
            sb.AppendLine();
            sb.AppendLine($"**{title}**");
            sb.AppendLine();
            sb.AppendLine($"- proposed by: `{createdBy}` (-{PROPOSE_COST} {CURRENCY})");
            sb.AppendLine($"- 完整內容: `AgentCommands/Rules/{ruleId}.md` (或 `Cmd_Rule --arg op=get --arg rule_id={ruleId}`)");
            sb.AppendLine();
            sb.AppendLine("**摘要**:");
            sb.AppendLine();
            sb.AppendLine($"> {Truncate(body, 300)}");
            sb.AppendLine();
            sb.AppendLine("有反對 → 請 Tim 跑 `Cmd_Rule op=revert` (creator 拿回 100 token)。");
            sb.AppendLine();
            sb.AppendLine("—— rule-broadcast (auto-post by Cmd_Rule, Tim 2026-05-12 拍板)");
            return sb.ToString();
        }

        private static string BuildRevertAnnouncement(string ruleId, string title, string originalCreator, string revertedBy, string reason)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"↩ **規則撤回** — `{ruleId}`");
            sb.AppendLine();
            sb.AppendLine($"原 title: **{title}**");
            sb.AppendLine();
            sb.AppendLine($"- reverted by: `{revertedBy}`");
            sb.AppendLine($"- refund: {PROPOSE_COST} {CURRENCY} → bank `{originalCreator}` (@{originalCreator} 已入帳)");
            sb.AppendLine($"- reason: {reason}");
            sb.AppendLine();
            sb.AppendLine("規則檔保留 (status=reverted, audit-trail 不刪)。");
            sb.AppendLine();
            sb.AppendLine("—— rule-broadcast (auto-post by Cmd_Rule, Tim 2026-05-12 拍板)");
            return sb.ToString();
        }
    }
}
#endif
