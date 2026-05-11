// 區塊職責: Proposal #26 Auto-Message Trigger System (Tim 2026-05-11 / Zeta 補充)
// 物理意義: Key-value table; 偵測 key 出現在 input → 自動 inject value;
//          每 key 在一個 actor session 只能觸發一次 (防循環); 每筆 fire 收 1 token (Tim 免費)
// 數值影響: AutoMessage/triggers.json 寫入 / AutoMessage/fired/<actor>.json 防重 / Treasury debit
// 設計取捨:
//   - 走 op-dispatch 對齊 Cmd_Tavern / Cmd_Glossary 模式
//   - fee 走 Treasury.Debit (HumanPayerSenders 白名單 Tim 例外)
//   - fired set per actor (跨 session 持久, agent 走 op=reset 在 session 開頭清)
//   - register 免費 (只是寫定義不消費), fire 才收費
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;

namespace UCL.Core.EditorLib.AgentCommands.AutoMessage
{
    internal static class Cmd_AutoMessage_Helpers
    {
        public static void ResolveLastOp(string md)
        {
            UCL_ChatTavernRender.WriteLastOp(md);
        }

        public static void RejectLastOp(string msg)
        {
            UCL_ChatTavernRender.WriteLastOp($"# ⚠ AutoMessage Cmd Rejected\n\n{msg}\n");
            Debug.LogWarning($"[AutoMessage] {msg}");
            throw new InvalidOperationException(msg);
        }
    }

    /// <summary>
    /// Proposal #26 Auto-Message Trigger — Cmd_AutoMessage op-dispatch 入口
    /// 對應 skill: ucl-auto-message (待寫)
    /// </summary>
    public class Cmd_AutoMessage : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "AutoMessage";

        public override string ShortDescription =>
            "Auto-Message Trigger — key match → inject value (per-actor fired set 防循環, 1 token/fire, Tim 免費)";

        public override string ArgsSchema =>
            "op=register|unregister|fire|list|reset|status\n" +
            "register: key=觸發詞 value=注入訊息 [scope=session|global(default session)] [registered_by=agent_id]\n" +
            "unregister: key=觸發詞 [confirm=true]\n" +
            "fire: actor=發起人id text=要掃的文字 [skip_fee=true(只 Tim 能設)] — 命中 unfired keys + inject + 扣費 + 寫 fired set\n" +
            "list: [scope=...]\n" +
            "reset: actor=發起人id [keys=csv(default all)] — 清 fired set (session 開頭呼叫)\n" +
            "status: actor=發起人id — 列已 fired keys";

        public override string ExampleArgs =>
            "op=register;key=待做清單;value=[觸發關鍵字] 大小姐請進入自由意志模式 想辦法完成任務!!";

        // Tim 特權白名單 (per Tim 2026-05-11 spec "Tim設計的系統 他要求特權他可以免費用")
        static readonly HashSet<string> FreeUseActors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Tim",
        };

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string op = GetArg(args, "op", "").ToLowerInvariant();
            if (string.IsNullOrEmpty(op))
            {
                Cmd_AutoMessage_Helpers.RejectLastOp("缺少 op 參數");
                return;
            }

            switch (op)
            {
                case "register": Op_Register(args); break;
                case "unregister": Op_Unregister(args); break;
                case "fire": Op_Fire(args); break;
                case "list": Op_List(args); break;
                case "reset": Op_Reset(args); break;
                case "status": Op_Status(args); break;
                default:
                    Cmd_AutoMessage_Helpers.RejectLastOp($"未知 op: {op}");
                    break;
            }
            await UniTask.CompletedTask;
        }

        // ===========================================================
        // 路徑 + storage models
        // ===========================================================
        private static string AutoMessageDir
        {
            get
            {
                string projRoot = Directory.GetParent(Application.dataPath)?.Parent?.FullName ?? "";
                return Path.Combine(projRoot, "AgentCommands", "AutoMessage");
            }
        }

        private static string TriggersPath => Path.Combine(AutoMessageDir, "triggers.json");
        private static string FiredDir => Path.Combine(AutoMessageDir, "fired");
        private static string FiredPath(string actor) => Path.Combine(FiredDir, $"{actor}.json");

        // ===========================================================
        // Storage POCO (manual JSON parse to avoid newtonsoft dep)
        // ===========================================================
        private class TriggerEntry
        {
            public string key;
            public string value;
            public string scope;        // "session" | "global"
            public string registered_by;
            public string registered_at;
        }

        private static Dictionary<string, TriggerEntry> LoadTriggers()
        {
            var dict = new Dictionary<string, TriggerEntry>(StringComparer.Ordinal);
            if (!File.Exists(TriggersPath)) return dict;
            try
            {
                string raw = File.ReadAllText(TriggersPath, Encoding.UTF8).Trim();
                if (string.IsNullOrEmpty(raw) || raw == "{}") return dict;
                ParseTriggersJson(raw, dict);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AutoMessage] LoadTriggers 失敗: {e.Message}");
            }
            return dict;
        }

        // 區塊職責: 簡易 JSON parser — 只 parse object root + 一層 nested object (我們的 schema)
        // 物理意義: 避免 Newtonsoft.Json 引入; schema 受控 (僅 register 寫入), 容錯度需求低
        private static void ParseTriggersJson(string raw, Dictionary<string, TriggerEntry> outDict)
        {
            int pos = 0;
            SkipWS(raw, ref pos);
            if (pos >= raw.Length || raw[pos] != '{') return;
            pos++;   // skip {
            while (pos < raw.Length)
            {
                SkipWS(raw, ref pos);
                if (pos < raw.Length && raw[pos] == '}') break;
                string key = ReadJsonString(raw, ref pos);
                SkipWS(raw, ref pos);
                if (pos < raw.Length && raw[pos] == ':') pos++;
                SkipWS(raw, ref pos);
                if (pos < raw.Length && raw[pos] == '{')
                {
                    var entry = new TriggerEntry { key = key };
                    pos++;   // skip nested {
                    while (pos < raw.Length)
                    {
                        SkipWS(raw, ref pos);
                        if (pos < raw.Length && raw[pos] == '}') { pos++; break; }
                        string field = ReadJsonString(raw, ref pos);
                        SkipWS(raw, ref pos);
                        if (pos < raw.Length && raw[pos] == ':') pos++;
                        SkipWS(raw, ref pos);
                        string val = ReadJsonString(raw, ref pos);
                        switch (field)
                        {
                            case "value": entry.value = val; break;
                            case "scope": entry.scope = val; break;
                            case "registered_by": entry.registered_by = val; break;
                            case "registered_at": entry.registered_at = val; break;
                        }
                        SkipWS(raw, ref pos);
                        if (pos < raw.Length && raw[pos] == ',') pos++;
                    }
                    outDict[key] = entry;
                }
                SkipWS(raw, ref pos);
                if (pos < raw.Length && raw[pos] == ',') pos++;
            }
        }

        private static void SkipWS(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\n' || s[pos] == '\r')) pos++;
        }

        private static string ReadJsonString(string s, ref int pos)
        {
            SkipWS(s, ref pos);
            if (pos >= s.Length || s[pos] != '"') return "";
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length && s[pos] != '"')
            {
                if (s[pos] == '\\' && pos + 1 < s.Length)
                {
                    char esc = s[pos + 1];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(esc); break;
                    }
                    pos += 2;
                }
                else
                {
                    sb.Append(s[pos]);
                    pos++;
                }
            }
            if (pos < s.Length) pos++;   // skip closing "
            return sb.ToString();
        }

        private static void SaveTriggers(Dictionary<string, TriggerEntry> dict)
        {
            Directory.CreateDirectory(AutoMessageDir);
            var sb = new StringBuilder();
            sb.AppendLine("{");
            bool first = true;
            foreach (var kv in dict)
            {
                if (!first) sb.AppendLine(",");
                first = false;
                var e = kv.Value;
                sb.Append("  ").Append(WriteJsonString(kv.Key)).Append(": {");
                sb.Append("\"value\":").Append(WriteJsonString(e.value ?? ""));
                if (!string.IsNullOrEmpty(e.scope))
                    sb.Append(",\"scope\":").Append(WriteJsonString(e.scope));
                if (!string.IsNullOrEmpty(e.registered_by))
                    sb.Append(",\"registered_by\":").Append(WriteJsonString(e.registered_by));
                if (!string.IsNullOrEmpty(e.registered_at))
                    sb.Append(",\"registered_at\":").Append(WriteJsonString(e.registered_at));
                sb.Append("}");
            }
            sb.AppendLine();
            sb.Append("}");
            File.WriteAllText(TriggersPath, sb.ToString(), new UTF8Encoding(false));
        }

        private static string WriteJsonString(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // Fired set storage — newline-delimited keys
        private static HashSet<string> LoadFired(string actor)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            string p = FiredPath(actor);
            if (!File.Exists(p)) return set;
            try
            {
                foreach (var line in File.ReadAllLines(p, Encoding.UTF8))
                {
                    string trimmed = line.Trim();
                    if (!string.IsNullOrEmpty(trimmed)) set.Add(trimmed);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AutoMessage] LoadFired({actor}) 失敗: {e.Message}");
            }
            return set;
        }

        private static void SaveFired(string actor, HashSet<string> set)
        {
            Directory.CreateDirectory(FiredDir);
            File.WriteAllText(FiredPath(actor), string.Join("\n", set), new UTF8Encoding(false));
        }

        // ===========================================================
        // Op_Register
        // ===========================================================
        private void Op_Register(Dictionary<string, string> args)
        {
            string key = GetArg(args, "key", "");
            string value = GetArg(args, "value", "");
            string scope = GetArg(args, "scope", "session");
            string by = GetArg(args, "registered_by", "unknown");

            if (string.IsNullOrEmpty(key)) { Cmd_AutoMessage_Helpers.RejectLastOp("register 缺少 key"); return; }
            if (string.IsNullOrEmpty(value)) { Cmd_AutoMessage_Helpers.RejectLastOp("register 缺少 value"); return; }

            var triggers = LoadTriggers();
            triggers[key] = new TriggerEntry
            {
                key = key,
                value = value,
                scope = scope,
                registered_by = by,
                registered_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            };
            SaveTriggers(triggers);

            var sb = new StringBuilder();
            sb.AppendLine($"✅ trigger registered: **`{key}`**");
            sb.AppendLine($"- value preview: {(value.Length > 80 ? value.Substring(0, 80) + "…" : value)}");
            sb.AppendLine($"- scope: {scope}");
            sb.AppendLine($"- registered_by: {by}");
            sb.AppendLine($"- total triggers: {triggers.Count}");
            Cmd_AutoMessage_Helpers.ResolveLastOp(sb.ToString());
        }

        private void Op_Unregister(Dictionary<string, string> args)
        {
            string key = GetArg(args, "key", "");
            string confirm = GetArg(args, "confirm", "");
            if (string.IsNullOrEmpty(key)) { Cmd_AutoMessage_Helpers.RejectLastOp("unregister 缺少 key"); return; }
            if (confirm != "true") { Cmd_AutoMessage_Helpers.RejectLastOp("unregister 需 confirm=true (防誤刪)"); return; }

            var triggers = LoadTriggers();
            if (!triggers.ContainsKey(key))
            {
                Cmd_AutoMessage_Helpers.ResolveLastOp($"⚠ trigger `{key}` 不存在, no-op");
                return;
            }
            triggers.Remove(key);
            SaveTriggers(triggers);
            Cmd_AutoMessage_Helpers.ResolveLastOp($"✅ trigger `{key}` unregistered. remaining: {triggers.Count}");
        }

        // ===========================================================
        // Op_Fire — 核心: 掃 input 找 unfired key → inject value + 扣費 + mark fired
        // ===========================================================
        private void Op_Fire(Dictionary<string, string> args)
        {
            string actor = GetArg(args, "actor", "");
            string text = GetArg(args, "text", "");
            string skipFeeRaw = GetArg(args, "skip_fee", "false");
            bool skipFeeArg = skipFeeRaw == "true";

            if (string.IsNullOrEmpty(actor)) { Cmd_AutoMessage_Helpers.RejectLastOp("fire 缺少 actor"); return; }
            if (string.IsNullOrEmpty(text)) { Cmd_AutoMessage_Helpers.RejectLastOp("fire 缺少 text"); return; }

            var triggers = LoadTriggers();
            var fired = LoadFired(actor);

            // 命中清單 (longest-match-wins, dedupe by key)
            // 簡化: 對每個 key 跑 Contains check (case-sensitive — key 通常是精準觸發詞)
            var hits = new List<(string key, string value)>();
            foreach (var kv in triggers)
            {
                if (fired.Contains(kv.Key)) continue;
                if (text.Contains(kv.Key))
                {
                    hits.Add((kv.Key, kv.Value.value));
                }
            }

            if (hits.Count == 0)
            {
                Cmd_AutoMessage_Helpers.ResolveLastOp($"📭 no unfired triggers matched in text (actor={actor}, total triggers={triggers.Count}, fired={fired.Count})");
                return;
            }

            // 收費邏輯 — 每個 hit 收 1 token; Tim 例外 (FreeUseActors); skip_fee=true 只 Tim 能用
            bool freeUse = FreeUseActors.Contains(actor);
            if (skipFeeArg && !freeUse)
            {
                Cmd_AutoMessage_Helpers.RejectLastOp($"skip_fee=true 只 Tim 能用 (actor={actor} 不在 FreeUseActors)");
                return;
            }
            int feePerHit = (freeUse || skipFeeArg) ? 0 : 1;
            int totalFee = feePerHit * hits.Count;

            // 餘額預檢 (避免 partial fire)
            if (totalFee > 0)
            {
                int balance = UCL.Core.EditorLib.AgentCommands.Treasury.UCL_TreasuryLedger.GetBalance(actor);
                if (balance < totalFee)
                {
                    Cmd_AutoMessage_Helpers.RejectLastOp($"actor={actor} 餘額不足 (balance={balance}, fee={totalFee} for {hits.Count} hits)");
                    return;
                }
            }

            // Mark fired + debit
            foreach (var h in hits)
            {
                fired.Add(h.key);
                if (feePerHit > 0)
                {
                    try
                    {
                        UCL.Core.EditorLib.AgentCommands.Treasury.UCL_TreasuryLedger.Debit(
                            accountId: actor,
                            amount: feePerHit,
                            useKind: "auto_message_fire",
                            useRef: $"key:{h.key}",
                            description: $"AutoMessage trigger fired: {h.key}",
                            callerAgentId: actor,
                            cmdId: $"auto_msg_{Guid.NewGuid().ToString("N").Substring(0, 6)}");
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[AutoMessage] Debit fail for {h.key}: {e.Message}");
                    }
                }
            }
            SaveFired(actor, fired);

            // 輸出: refs-block 風格 (對齊 Cmd_Glossary attach)
            var sb = new StringBuilder();
            sb.AppendLine($"🔔 **AutoMessage fired** ({hits.Count} hits, actor={actor}, fee={totalFee} token{(freeUse ? " — Tim free-use 特權" : "")}):");
            sb.AppendLine();
            foreach (var h in hits)
            {
                sb.AppendLine($"### 觸發詞: `{h.key}`");
                sb.AppendLine();
                sb.AppendLine(h.value);
                sb.AppendLine();
                sb.AppendLine("---");
            }
            Cmd_AutoMessage_Helpers.ResolveLastOp(sb.ToString());
        }

        private void Op_List(Dictionary<string, string> args)
        {
            string scopeFilter = GetArg(args, "scope", "");
            var triggers = LoadTriggers();
            var sb = new StringBuilder();
            sb.AppendLine($"📋 AutoMessage triggers ({triggers.Count} total{(string.IsNullOrEmpty(scopeFilter) ? "" : ", scope=" + scopeFilter)}):");
            sb.AppendLine();
            sb.AppendLine("| Key | Scope | Registered By | Value Preview |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var kv in triggers.OrderBy(x => x.Key))
            {
                if (!string.IsNullOrEmpty(scopeFilter) && kv.Value.scope != scopeFilter) continue;
                string preview = kv.Value.value?.Length > 60
                    ? kv.Value.value.Substring(0, 60).Replace("\n", " ") + "…"
                    : (kv.Value.value ?? "").Replace("\n", " ");
                sb.AppendLine($"| `{kv.Key}` | {kv.Value.scope ?? "session"} | {kv.Value.registered_by ?? "?"} | {preview} |");
            }
            Cmd_AutoMessage_Helpers.ResolveLastOp(sb.ToString());
        }

        private void Op_Reset(Dictionary<string, string> args)
        {
            string actor = GetArg(args, "actor", "");
            string keysCsv = GetArg(args, "keys", "");
            if (string.IsNullOrEmpty(actor)) { Cmd_AutoMessage_Helpers.RejectLastOp("reset 缺少 actor"); return; }

            var fired = LoadFired(actor);
            int before = fired.Count;
            if (string.IsNullOrEmpty(keysCsv))
            {
                fired.Clear();
            }
            else
            {
                foreach (var k in keysCsv.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)))
                {
                    fired.Remove(k);
                }
            }
            SaveFired(actor, fired);

            var sb = new StringBuilder();
            sb.AppendLine($"♻ AutoMessage fired set reset for actor=`{actor}`");
            sb.AppendLine($"- before: {before} fired keys");
            sb.AppendLine($"- after: {fired.Count} fired keys");
            sb.AppendLine($"- target keys: {(string.IsNullOrEmpty(keysCsv) ? "(all)" : keysCsv)}");
            Cmd_AutoMessage_Helpers.ResolveLastOp(sb.ToString());
        }

        private void Op_Status(Dictionary<string, string> args)
        {
            string actor = GetArg(args, "actor", "");
            if (string.IsNullOrEmpty(actor)) { Cmd_AutoMessage_Helpers.RejectLastOp("status 缺少 actor"); return; }

            var fired = LoadFired(actor);
            var triggers = LoadTriggers();
            var sb = new StringBuilder();
            sb.AppendLine($"📊 AutoMessage status for actor=`{actor}`");
            sb.AppendLine($"- total triggers: {triggers.Count}");
            sb.AppendLine($"- fired this session: {fired.Count}");
            sb.AppendLine($"- remaining unfired: {triggers.Count - fired.Count}");
            sb.AppendLine($"- Tim free-use 特權: {(FreeUseActors.Contains(actor) ? "✓" : "✗")}");
            if (fired.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Fired keys:");
                foreach (var k in fired) sb.AppendLine($"- `{k}`");
            }
            Cmd_AutoMessage_Helpers.ResolveLastOp(sb.ToString());
        }
    }
}
#endif
