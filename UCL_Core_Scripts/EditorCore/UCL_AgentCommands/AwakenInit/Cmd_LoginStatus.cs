// 區塊職責：T04 — 對外曝光 persona pool + active lock 狀態的 read-only Cmd
// 物理意義：scan AgentCommands/_session/_persona_*.json (active locks) +
//          AgentCommands/AwakenInit/personas/*.json (registry), serialize JSON + markdown 給 caller
// 數值影響：純讀檔, 不寫 lock 不動 registry; 輸出 _login_status.md + _login_status_latest.json
//
// 設計理由 (multi-persona-per-base T04, 2026-05-14):
//   awakening.py status 是 CLI-only 文字輸出, agent 想 programmatic 拿 persona pool / 上線狀態
//   只能 parse stdout 或自己重 scan files. 本 Cmd 補上 Cmd-level interface, 跨 agent 共用 read API.
//   對應的視覺化 UI 是 UCL_LoginStatusPage (純 IMGUI), 本 Cmd 鏡像同一份資料給 RPC caller.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.AwakenInit
{
    /// <summary>
    /// T04 — Read-only persona pool + lock status 查詢指令。鏡像 UCL_LoginStatusPage 的資料。
    ///
    /// <para>典型用法：</para>
    /// <code>
    /// # 全部 personas + locks (預設)
    /// senate ucmd run LoginStatus
    ///
    /// # 只看 online
    /// senate ucmd run LoginStatus --arg filter_status=online
    ///
    /// # 篩 agent
    /// senate ucmd run LoginStatus --arg filter_agent=claude-code
    ///
    /// # 只要 JSON (給 agent programmatic 用)
    /// senate ucmd run LoginStatus --arg format=json
    /// </code>
    ///
    /// <para>輸出檔：</para>
    /// <code>
    /// AgentCommands/AwakenInit/
    ///   ├── _login_status.md            (human-readable table)
    ///   └── _login_status_latest.json   (raw JSON dump; format=json/both)
    /// </code>
    /// </summary>
    public class Cmd_LoginStatus : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "LoginStatus";

        public override string ShortDescription =>
            "Read-only persona pool + active lock 查詢 (鏡像 UCL_LoginStatusPage 資料, 給 agent RPC 用).";

        public override string ArgsSchema =>
            "filter_status=online|offline|all (default: all) | " +
            "filter_agent=<agent name> 篩 persona/lock 的 agent 欄 (default: '' 不篩) | " +
            "format=md|json|both (default: both) — md 走 _login_status.md, json 走 _login_status_latest.json";

        public override string ExampleArgs =>
            "filter_status=online;filter_agent=claude-code;format=both";

        // 2026-08-17：舊值指向的 Plan 整個 UCL_Core 都不存在（死連結）。改指本 Cmd 的說明文件。
        // 另補 {lang}：舊值寫死 zh-Hant，其他語系拿不到回退。
        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_LoginStatus.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            string filterStatus = GetArg(args, "filter_status", "all").Trim().ToLowerInvariant();
            string filterAgent = GetArg(args, "filter_agent", "").Trim();
            string format = GetArg(args, "format", "both").Trim().ToLowerInvariant();

            if (filterStatus != "online" && filterStatus != "offline" && filterStatus != "all")
            {
                throw new Exception($"[LoginStatus] filter_status 必為 online|offline|all (got '{filterStatus}')");
            }
            if (format != "md" && format != "json" && format != "both")
            {
                throw new Exception($"[LoginStatus] format 必為 md|json|both (got '{format}')");
            }

            // 區塊職責: 路徑解析
            // 物理意義: 走可 override 資料根撈 _session 跟 AwakenInit/personas (預設 = RepoRoot/AgentCommands)
            string agentCmdDir = UCL.Core.EditorLib.UCL_AgentCommandsPath.DataRoot;
            string outDir = Path.Combine(agentCmdDir, "AwakenInit");
            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex) { throw new Exception($"[LoginStatus] 建立輸出目錄失敗: {ex.Message}"); }

            // 區塊職責: scan locks —— 走 UCL_ActivePersonaLocks 唯一掃描實作（含過期視圖），不自己掃
            string nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var locks = new List<LockEntry>();
            foreach (var l in UCL_ActivePersonaLocks.ListLocks())
            {
                locks.Add(new LockEntry
                {
                    Persona = l.Persona,
                    Agent = l.Agent,
                    Model = l.Model,
                    BankAccount = l.BankAccount,
                    LockedAt = l.LockedAt,
                    SessionKey = l.SessionKey,
                    Pid = l.Pid,
                });
            }

            // 區塊職責: scan persona registry —— 走 UCL_PersonaProfile 唯一讀取入口（Phase 0 接縫）
            var personas = new List<PersonaEntry>();
            var lockedSet = new HashSet<string>(locks.Select(l => l.Persona));
            foreach (var name in UCL_PersonaProfile.PoolNamesSorted())
            {
                var jd = UCL_PersonaProfile.GetRaw(name);
                if (jd == null) continue;   // 壞檔接縫已警告
                personas.Add(new PersonaEntry
                {
                    Name = name,
                    Agent = jd.GetString("agent", ""),
                    Status = jd.GetString("status", ""),
                    WakeCount = jd.GetInt("wake_count", 0),
                    LayerRole = jd.GetString("layer_role", ""),
                    LastActive = jd.GetString("last_active", ""),
                    HasLock = lockedSet.Contains(name),
                });
            }
            personas.Sort((a, b) => b.WakeCount.CompareTo(a.WakeCount));

            // 區塊職責: filter
            // 物理意義: filter_status 比對 persona.HasLock (online = 有 lock; offline = 無)
            //          filter_agent 比對 persona.Agent / lock.Agent (case-insensitive)
            var filteredPersonas = personas.Where(p =>
            {
                if (filterStatus == "online" && !p.HasLock) return false;
                if (filterStatus == "offline" && p.HasLock) return false;
                if (!string.IsNullOrEmpty(filterAgent)
                    && !string.Equals(p.Agent, filterAgent, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }).ToList();

            var filteredLocks = locks.Where(l =>
            {
                if (!string.IsNullOrEmpty(filterAgent)
                    && !string.Equals(l.Agent, filterAgent, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }).ToList();

            // 區塊職責: collision 偵測 (same full_key 重複 lock)
            // 物理意義: full_key = session_key 完整字串; 兩 lock 同 full_key = 多 Claude IDE 同 cwd 同 persona
            var keyGroups = new Dictionary<string, int>();
            foreach (var l in filteredLocks)
            {
                if (string.IsNullOrEmpty(l.SessionKey)) continue;
                keyGroups[l.SessionKey] = keyGroups.GetValueOrDefault(l.SessionKey, 0) + 1;
            }
            int collisionCount = keyGroups.Count(kv => kv.Value >= 2);

            // ============ Output: JSON ============
            string jsonPath = Path.Combine(outDir, "_login_status_latest.json");
            if (format == "json" || format == "both")
            {
                var sb = new StringBuilder();
                sb.Append('{');
                sb.Append("\"ts\":").Append(ToJsonString(nowIso)).Append(',');
                sb.Append("\"filter_status\":").Append(ToJsonString(filterStatus)).Append(',');
                sb.Append("\"filter_agent\":").Append(ToJsonString(filterAgent)).Append(',');
                sb.Append("\"collision_count\":").Append(collisionCount).Append(',');
                sb.Append("\"locks\":[");
                for (int i = 0; i < filteredLocks.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var l = filteredLocks[i];
                    sb.Append('{');
                    sb.Append("\"persona\":").Append(ToJsonString(l.Persona)).Append(',');
                    sb.Append("\"agent\":").Append(ToJsonString(l.Agent)).Append(',');
                    sb.Append("\"model\":").Append(ToJsonString(l.Model)).Append(',');
                    sb.Append("\"bank_account\":").Append(ToJsonString(l.BankAccount)).Append(',');
                    sb.Append("\"locked_at\":").Append(ToJsonString(l.LockedAt)).Append(',');
                    sb.Append("\"session_key\":").Append(ToJsonString(l.SessionKey)).Append(',');
                    sb.Append("\"pid\":").Append(l.Pid);
                    sb.Append('}');
                }
                sb.Append("],");
                sb.Append("\"personas\":[");
                for (int i = 0; i < filteredPersonas.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var p = filteredPersonas[i];
                    sb.Append('{');
                    sb.Append("\"name\":").Append(ToJsonString(p.Name)).Append(',');
                    sb.Append("\"agent\":").Append(ToJsonString(p.Agent)).Append(',');
                    sb.Append("\"status\":").Append(ToJsonString(p.Status)).Append(',');
                    sb.Append("\"wake_count\":").Append(p.WakeCount).Append(',');
                    sb.Append("\"layer_role\":").Append(ToJsonString(p.LayerRole)).Append(',');
                    sb.Append("\"last_active\":").Append(ToJsonString(p.LastActive)).Append(',');
                    sb.Append("\"has_lock\":").Append(p.HasLock ? "true" : "false");
                    sb.Append('}');
                }
                sb.Append("]}");
                try { File.WriteAllText(jsonPath, sb.ToString(), new UTF8Encoding(false)); }
                catch (Exception ex) { throw new Exception($"[LoginStatus] 寫 JSON 失敗: {ex.Message}"); }
            }

            // ============ Output: Markdown ============
            string mdPath = Path.Combine(outDir, "_login_status.md");
            if (format == "md" || format == "both")
            {
                var sb = new StringBuilder();
                sb.AppendLine($"# 🌅 Login Status (ts: `{nowIso}`)");
                sb.AppendLine();
                sb.AppendLine($"- filter_status: `{filterStatus}` / filter_agent: `{(string.IsNullOrEmpty(filterAgent) ? "(none)" : filterAgent)}`");
                sb.AppendLine($"- active locks: **{filteredLocks.Count}** / personas (篩後): **{filteredPersonas.Count}**");
                if (collisionCount > 0)
                {
                    sb.AppendLine($"- ⚠ **collision_count: {collisionCount}** (同 full_key 多 lock — 多 Claude IDE 同 persona 場景)");
                }
                sb.AppendLine();

                sb.AppendLine("## Active Locks");
                if (filteredLocks.Count == 0)
                {
                    sb.AppendLine("_(無 active lock)_");
                }
                else
                {
                    sb.AppendLine("| Persona | Agent | Model | Locked@ | PID | Session Key |");
                    sb.AppendLine("|---|---|---|---|---|---|");
                    foreach (var l in filteredLocks.OrderBy(x => x.LockedAt))
                    {
                        string skShort = string.IsNullOrEmpty(l.SessionKey) ? "" : l.SessionKey;
                        sb.AppendLine($"| `{l.Persona}` | {l.Agent} | {l.Model} | {l.LockedAt} | {l.Pid} | `{skShort}` |");
                    }
                }
                sb.AppendLine();

                sb.AppendLine("## Persona Pool");
                if (filteredPersonas.Count == 0)
                {
                    sb.AppendLine("_(篩後無 persona)_");
                }
                else
                {
                    sb.AppendLine("| Persona | Agent | Wake# | Status | HasLock | Layer Role |");
                    sb.AppendLine("|---|---|---|---|---|---|");
                    foreach (var p in filteredPersonas)
                    {
                        string lockMark = p.HasLock ? "🔒" : "";
                        string roleShort = p.LayerRole.Length > 50 ? p.LayerRole.Substring(0, 50) + "…" : p.LayerRole;
                        sb.AppendLine($"| `{p.Name}` | {p.Agent} | {p.WakeCount} | {p.Status} | {lockMark} | {roleShort} |");
                    }
                }
                sb.AppendLine();
                sb.AppendLine($"---");
                sb.AppendLine($"JSON dump: `{Rel(jsonPath)}` (若 format=json/both)");

                try { File.WriteAllText(mdPath, sb.ToString(), new UTF8Encoding(false)); }
                catch (Exception ex) { throw new Exception($"[LoginStatus] 寫 MD 失敗: {ex.Message}"); }
            }

            Debug.Log($"[LoginStatus] {filteredLocks.Count} active locks / {filteredPersonas.Count} personas (filter: status={filterStatus}, agent={filterAgent})");
        }

        // ===========================================================
        // Schema (對齊 UCL_LoginStatusPage)
        // ===========================================================
        class LockEntry
        {
            public string Persona = "";
            public string Agent = "";
            public string Model = "";
            public string BankAccount = "";
            public string LockedAt = "";
            public string SessionKey = "";
            public int Pid = 0;
        }

        class PersonaEntry
        {
            public string Name = "";
            public string Agent = "";
            public string Status = "";
            public int WakeCount = 0;
            public string LayerRole = "";
            public string LastActive = "";
            public bool HasLock = false;
        }

        // ===========================================================
        // Helpers (對齊 Cmd_NoteLesson — 手寫 JSON escape 避免 Newtonsoft 依賴)
        // ===========================================================
        static string ToJsonString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        static string Rel(string absPath)
        {
            try
            {
                string root = UCL_RepoPath.RepoRoot.Replace('\\', '/');
                string p = absPath.Replace('\\', '/');
                if (p.StartsWith(root)) return p.Substring(root.Length).TrimStart('/');
                return p;
            }
            catch { return absPath; }
        }
    }
}
#endif
