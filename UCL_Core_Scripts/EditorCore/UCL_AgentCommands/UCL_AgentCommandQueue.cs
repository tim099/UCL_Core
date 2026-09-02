
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/04 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
// Queue persistence — read/write AgentCommands/queue.json at repository root.
// 與 RCG 版相容（相同檔案路徑 + 相同 JSON 格式），可平行運作。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// queue.json 的讀寫管理。
    /// 路徑：&lt;repoRoot&gt;/AgentCommands/queues/&lt;persona&gt;/queue[-&lt;lane&gt;].json
    /// repoRoot 由 <see cref="UCL_RepoPath.RepoRoot"/> 解析（git-walk，與 Python run_cmd.py 對齊）。
    /// ⚠ 路徑樣板**兩邊各有一份**（本檔與 run_cmd.py），改動必須同時進行 ——
    ///   任一邊落後，trigger 就寫在對方沒在看的地方，而那種斷線是**靜默**的
    ///   （cmd 永遠 pending 到 timeout，沒有任何錯誤訊息指向真因）。
    /// </summary>
    public static class UCL_AgentCommandQueue
    {
        public const string QueueDirRelative = "AgentCommands";
        public const string QueueFileName = "queue.json";
        // 區塊職責：multi-queue 子資料夾名稱 (agent-command-pipeline-parallelize T02)
        // 物理意義：**persona 資料夾制**（Tim 2026-08-01 拍板，取代原本的平鋪檔名制）——
        //          <AgentCommandsDir>/queues/<persona>/queue.json
        //          <AgentCommandsDir>/queues/<persona>/queue-<lane>.json
        //          舊制是 queues/queue-<persona>.json 與 queues/queue-<persona>-<lane>.json 平鋪，
        //          「這筆是誰派的」得從檔名字串反推，而 queue-ame-design 無法判定
        //          「-design 是用途還是名字的一部分」。改成資料夾之後身分與通道
        //          **在檔案系統層就分開了**，不必解析任何字串。
        // 數值影響：切換式改版，**無相容層**（切換時 36 個舊 queue 全為空、0 筆在途 cmd，
        //          點清後直接刪除；沒有需要搬運的狀態，因此不寫遷移碼也不雙讀）。
        //          最外層共用 <AgentCommandsDir>/queue.json 一併廢除。
        public const string QueuesSubdir = "queues";

        // 區塊職責：未宣告身分者的落點（Tim 2026-08-01）。
        // 物理意義：不帶身分的派遣不再落「最外層共用 queue.json」這個特例，而是落一個
        //          **名字就說明狀態**的資料夾。好處是掃描規則變成一條沒有例外的
        //          「資料夾名 = 身分」，而且 queues/anonymous/ 的流量自己就是
        //          「還有多少未署名派遣」的儀表 —— 不需要有人記得去統計。
        // ⚠ 這是**保留字，不是 persona**：身分解析讀到它必須回「本層沒有答案」，
        //   不可回字串 "anonymous"。否則它會流進記帳層，而 bank_resolver 的命名慣例
        //   fallback（{canonical}-da-xiaojie）會為一個不存在的人隱含開帳戶。
        public const string AnonymousQueueId = "anonymous";

        // 區塊職責：系統自動產生的派遣落點（Tim 2026-08-18）。
        // 物理意義：commit 領薪公告、daemon 之類**不是人派的**指令，過去跟「忘了帶 --persona」
        //          的人擠同一個 anonymous 資料夾 ⇒ anonymous 的流量同時混了兩種東西：
        //          「系統本來就該匿名」與「有人漏帶旗標」。混在一起，那個資料夾就不再是儀表 ——
        //          數字降不下來，而且看不出哪些是該修的。
        //          分出 system/ 之後，**anonymous 剩下的每一筆都是待修的漏帶**。
        // ⚠ 同樣是**保留字不是 persona**：身分解析讀到它一律回「本層沒有答案」
        //   （理由同 AnonymousQueueId —— 否則會為不存在的人隱含開帳戶）。
        //   系統訊息的真實身分仍走 `--arg persona=<P>`（那是「這筆代表誰」，跟走哪條 lane 無關）。
        public const string SystemQueueId = "system";

        // 區塊職責：queueId 的形狀 —— "<persona>" 或 "<persona>/<lane>"。
        // 物理意義：呼叫端（Watcher / Page / Runner）仍然只傳**一個不透明字串**，簽名不變；
        //          但這個字串現在是**路徑形狀**而不是要猜的名字 —— '/' 是呼叫端自己組出來的
        //          結構分隔符，不是我們從 "ame-design" 這種字串裡猜出來的邊界。
        public const char LaneSeparator = '/';

        /// <summary>把 queueId 拆成 (資料夾, lane)。lane 可為 null。空值 → anonymous。</summary>
        /// <remarks>
        /// 只切**第一個** '/'：persona 名不含 '/'，其餘一律歸 lane。
        /// 含 ".." 或反斜線的段落視為不合法 → 落 anonymous 並警告（這些值來自 CLI，
        /// 不做防護的話是一條寫出 queues/ 之外的路徑穿越）。
        /// </remarks>
        public static void SplitQueueId(string queueId, out string folder, out string lane)
        {
            folder = AnonymousQueueId;
            lane = null;
            if (string.IsNullOrEmpty(queueId)) return;
            string id = queueId.Replace('\\', LaneSeparator).Trim();
            int i = id.IndexOf(LaneSeparator);
            string f = i < 0 ? id : id.Substring(0, i);
            string l = i < 0 ? null : id.Substring(i + 1).Replace(LaneSeparator.ToString(), "-");
            if (!IsSafeSegment(f) || (l != null && !IsSafeSegment(l)))
            {
                Debug.LogWarning($"[UCL_AgentCommandQueue] 不合法的 queueId '{queueId}' → 落 {AnonymousQueueId}。");
                return;
            }
            folder = f;
            lane = string.IsNullOrEmpty(l) ? null : l;
        }

        static bool IsSafeSegment(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "." || s == "..") return false;
            if (s.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            return s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        // 區塊職責：lock-file 機制使用的兩個 trigger 檔名
        // 物理意義：
        //   TriggerFileName        — Python / 外部寫入後留下的「請執行」訊號
        //   RunningTriggerFileName — Watcher 偵測到 Trigger 後 File.Move 為此名，代表「Editor 已接手」
        // 數值影響：兩檔皆為跨 process 同步用的 marker；Runner finally 結束時刪除 .running 檔，外部可監聽該事件作為「執行完成」訊號。
        public const string TriggerFileName = "pending.trigger";
        public const string RunningTriggerFileName = "pending.trigger.running";

        /// <summary>
        /// 取得 queue 檔絕對路徑。
        /// agentId=null → <c>AgentCommands/queues/anonymous/queue.json</c>。
        /// "&lt;persona&gt;" → <c>queues/&lt;persona&gt;/queue.json</c>；
        /// "&lt;persona&gt;/&lt;lane&gt;" → <c>queues/&lt;persona&gt;/queue-&lt;lane&gt;.json</c>。
        /// </summary>
        public static string GetQueuePath(string agentId = null)
        {
            SplitQueueId(agentId, out string folder, out string lane);
            string file = string.IsNullOrEmpty(lane) ? QueueFileName : $"queue-{lane}.json";
            return Path.Combine(UCL_RepoPath.AgentCommandsDir, QueuesSubdir, folder, file);
        }

        /// <summary>取得該 queue 所屬的 persona 資料夾絕對路徑（queues/&lt;persona&gt;/）。</summary>
        public static string GetQueueDir(string agentId = null)
        {
            SplitQueueId(agentId, out string folder, out _);
            return Path.Combine(UCL_RepoPath.AgentCommandsDir, QueuesSubdir, folder);
        }

        /// <summary>取得 pending trigger 路徑：queues/&lt;persona&gt;/pending[-&lt;lane&gt;].trigger。</summary>
        public static string GetTriggerPath(string agentId = null)
        {
            SplitQueueId(agentId, out string folder, out string lane);
            string file = string.IsNullOrEmpty(lane) ? TriggerFileName : $"pending-{lane}.trigger";
            return Path.Combine(UCL_RepoPath.AgentCommandsDir, QueuesSubdir, folder, file);
        }

        /// <summary>取得 running trigger 路徑。對應 GetTriggerPath()。</summary>
        public static string GetRunningTriggerPath(string agentId = null)
        {
            return GetTriggerPath(agentId) + ".running";
        }

        /// <summary>確保該 queue 的 persona 資料夾存在。</summary>
        public static void EnsureDir(string agentId = null)
        {
            string dir = GetQueueDir(agentId);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        /// <summary>
        /// 列舉現存 queue 的 id 清單（掃 queues/&lt;persona&gt;/queue*.json）。Watcher 用。
        /// 回傳形狀："&lt;persona&gt;"（本命 queue）或 "&lt;persona&gt;/&lt;lane&gt;"（子通道）。
        /// </summary>
        public static System.Collections.Generic.List<string> ListAgentIds()
        {
            var list = new System.Collections.Generic.List<string>();
            string queuesDir = Path.Combine(UCL_RepoPath.AgentCommandsDir, QueuesSubdir);
            if (!Directory.Exists(queuesDir)) return list;
            foreach (var dir in Directory.GetDirectories(queuesDir))
            {
                string persona = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(persona)) continue;
                foreach (var f in Directory.GetFiles(dir, "queue*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (name == "queue") list.Add(persona);                       // 本命
                    else if (name.StartsWith("queue-"))                           // 子通道
                        list.Add(persona + LaneSeparator + name.Substring("queue-".Length));
                }
            }
            return list;
        }

        /// <summary>
        /// 從 queueId 取得宣告的 persona —— 身分解析階梯 tier 2「queue 反推」。
        /// 查不到 / 匿名一律回 null。
        /// </summary>
        /// <remarks>
        /// ⚠ 回 null 的語意是「**本層沒有答案**」，不是「查無此人」——
        /// 呼叫端不可把它當否定證據，該往解析階梯的下一層走。
        /// anonymous / system 回 null 是刻意的：它們是狀態不是人
        /// （見 AnonymousQueueId / SystemQueueId 註解）。
        /// </remarks>
        public static string GetDeclaredPersona(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return null;
            SplitQueueId(agentId, out string folder, out _);
            return IsReservedQueueId(folder) ? null : folder;
        }

        // 區塊職責：判斷一個 queue 資料夾名是不是保留字（狀態，不是人）。
        // 物理意義：保留字有兩個且會再長 —— 逐處寫 `== AnonymousQueueId` 的話，
        //          新增第二個保留字就得去找出所有比對點，而**漏掉的那一處不會報錯**：
        //          它會把 "system" 當成 persona 回出去，下游 bank_resolver 的
        //          fallback 命名就替一個不存在的人開了帳戶。收成一處＝只有一種讀法。
        // 數值影響：純比對，無 IO。
        public static bool IsReservedQueueId(string folder)
            => folder == AnonymousQueueId || folder == SystemQueueId;

        /// <summary>讀取 queue.json — 不存在或解析失敗時回傳空 queue。</summary>
        public static UCL_AgentCommandQueueData Load(string agentId = null)
        {
            string path = GetQueuePath(agentId);
            if (!File.Exists(path))
            {
                return new UCL_AgentCommandQueueData();
            }
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                // Unity JsonUtility 不支援 Dictionary，因此採手寫 JSON parse（極簡）
                return ParseJson(json);
            }
            catch (Exception e)
            {
                Debug.LogError($"[UCL_AgentCommandQueue] Failed to load queue: {e}");
                return new UCL_AgentCommandQueueData();
            }
        }

        /// <summary>寫入 queue.json（會覆寫整個檔案）。</summary>
        public static void Save(UCL_AgentCommandQueueData data, string agentId = null)
        {
            EnsureDir(agentId);
            string path = GetQueuePath(agentId);
            try
            {
                string json = SerializeJson(data);
                File.WriteAllText(path, json, new UTF8Encoding(false)); // no BOM
                Debug.Log($"[UCL_AgentCommandQueue] Saved queue → {path}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[UCL_AgentCommandQueue] Failed to save queue: {e}");
            }
        }

        // ===========================================================
        // 簡易 JSON 序列化（不依賴 Unity JsonUtility，因為要支援 Dictionary）
        // ===========================================================

        static string SerializeJson(UCL_AgentCommandQueueData data)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"Commands\": [");
            bool firstCmd = true;
            foreach (var c in data.Commands ?? new List<UCL_AgentCommand>())
            {
                if (!firstCmd) sb.Append(",");
                firstCmd = false;
                sb.Append("\n    {");
                AppendField(sb, "Id", c.Id, true);
                AppendField(sb, "Type", c.Type, false);
                AppendField(sb, "Mode", c.Mode.ToString(), false);
                AppendInt(sb, "RunCount", c.RunCount);
                sb.Append(",\n      \"Args\": {");
                if (c.Args != null && c.Args.Count > 0)
                {
                    bool firstArg = true;
                    foreach (var kv in c.Args)
                    {
                        if (!firstArg) sb.Append(",");
                        firstArg = false;
                        sb.Append("\n        \"").Append(EscapeStr(kv.Key)).Append("\": \"").Append(EscapeStr(kv.Value)).Append("\"");
                    }
                    sb.Append("\n      ");
                }
                sb.Append("}");
                AppendField(sb, "CreatedAt", c.CreatedAt, false);
                AppendField(sb, "LastRunAt", c.LastRunAt, false);
                AppendField(sb, "LastRunResult", c.LastRunResult, false);
                AppendField(sb, "LastRunError", c.LastRunError, false);
                AppendField(sb, "Description", c.Description, false);
                sb.Append("\n    }");
            }
            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        static void AppendField(StringBuilder sb, string key, string value, bool first)
        {
            if (!first) sb.Append(",");
            sb.Append("\n      \"").Append(key).Append("\": ");
            if (value == null) sb.Append("null");
            else sb.Append("\"").Append(EscapeStr(value)).Append("\"");
        }
        static void AppendBool(StringBuilder sb, string key, bool value)
        {
            sb.Append(",\n      \"").Append(key).Append("\": ").Append(value ? "true" : "false");
        }
        static void AppendInt(StringBuilder sb, string key, int value)
        {
            sb.Append(",\n      \"").Append(key).Append("\": ").Append(value);
        }
        static string EscapeStr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        // ===========================================================
        // 簡易 JSON 解析（支援我們自寫的格式 + agent 手寫的標準 JSON）
        // ===========================================================

        static UCL_AgentCommandQueueData ParseJson(string json)
        {
            int pos = 0;
            SkipWS(json, ref pos);
            ExpectChar(json, ref pos, '{');
            var result = new UCL_AgentCommandQueueData();
            while (true)
            {
                SkipWS(json, ref pos);
                if (json[pos] == '}') { pos++; break; }
                string key = ParseString(json, ref pos);
                SkipWS(json, ref pos);
                ExpectChar(json, ref pos, ':');
                SkipWS(json, ref pos);
                if (key == "Commands")
                {
                    result.Commands = ParseCommandArray(json, ref pos);
                }
                else
                {
                    SkipValue(json, ref pos);
                }
                SkipWS(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
            }
            return result;
        }

        static List<UCL_AgentCommand> ParseCommandArray(string json, ref int pos)
        {
            var list = new List<UCL_AgentCommand>();
            ExpectChar(json, ref pos, '[');
            while (true)
            {
                SkipWS(json, ref pos);
                if (json[pos] == ']') { pos++; break; }
                list.Add(ParseCommand(json, ref pos));
                SkipWS(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
            }
            return list;
        }

        static UCL_AgentCommand ParseCommand(string json, ref int pos)
        {
            var c = new UCL_AgentCommand();
            ExpectChar(json, ref pos, '{');
            while (true)
            {
                SkipWS(json, ref pos);
                if (json[pos] == '}') { pos++; break; }
                string key = ParseString(json, ref pos);
                SkipWS(json, ref pos);
                ExpectChar(json, ref pos, ':');
                SkipWS(json, ref pos);

                switch (key)
                {
                    case "Id":            c.Id = ParseStringOrNull(json, ref pos); break;
                    case "Type":          c.Type = ParseStringOrNull(json, ref pos); break;
                    case "Mode":
                        {
                            string s = ParseStringOrNull(json, ref pos);
                            if (Enum.TryParse<UCL_AgentCommandMode>(s, out var m)) c.Mode = m;
                            break;
                        }
                    case "RunCount":      c.RunCount = ParseInt(json, ref pos); break;
                    case "Executed":      // 向後相容：舊版 bool true 視為 RunCount=1
                        c.RunCount = ParseBool(json, ref pos) ? 1 : 0;
                        break;
                    case "Args":          c.Args = ParseStringDict(json, ref pos); break;
                    case "CreatedAt":     c.CreatedAt = ParseStringOrNull(json, ref pos); break;
                    case "LastRunAt":     c.LastRunAt = ParseStringOrNull(json, ref pos); break;
                    case "LastRunResult": c.LastRunResult = ParseStringOrNull(json, ref pos); break;
                    case "LastRunError":  c.LastRunError = ParseStringOrNull(json, ref pos); break;
                    case "Description":   c.Description = ParseStringOrNull(json, ref pos); break;
                    default:              SkipValue(json, ref pos); break;
                }
                SkipWS(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
            }
            return c;
        }

        static Dictionary<string, string> ParseStringDict(string json, ref int pos)
        {
            var d = new Dictionary<string, string>();
            ExpectChar(json, ref pos, '{');
            while (true)
            {
                SkipWS(json, ref pos);
                if (json[pos] == '}') { pos++; break; }
                string k = ParseString(json, ref pos);
                SkipWS(json, ref pos);
                ExpectChar(json, ref pos, ':');
                SkipWS(json, ref pos);
                string v = ParseStringOrNull(json, ref pos) ?? "";
                d[k] = v;
                SkipWS(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
            }
            return d;
        }

        /// <summary>
        /// 從 <paramref name="aStart"/> 起讀 4 位 hex（JSON \uXXXX 的那四位），成功回 true 並輸出 code unit。
        /// ⚠ 不移動 pos —— 移動由呼叫端決定，因為代理對要先看完低位才知道要吃幾個字元。
        /// </summary>
        static bool TryReadHex4(string json, int aStart, out int aValue)
        {
            aValue = 0;
            if (aStart + 4 > json.Length) return false;
            for (int i = 0; i < 4; i++)
            {
                char c = json[aStart + i];
                int v;
                if (c >= '0' && c <= '9') v = c - '0';
                else if (c >= 'a' && c <= 'f') v = c - 'a' + 10;
                else if (c >= 'A' && c <= 'F') v = c - 'A' + 10;
                else { aValue = 0; return false; }
                aValue = (aValue << 4) | v;
            }
            return true;
        }

        static string ParseString(string json, ref int pos)
        {
            ExpectChar(json, ref pos, '"');
            var sb = new StringBuilder();
            while (pos < json.Length)
            {
                char ch = json[pos++];
                if (ch == '"') break;
                if (ch == '\\' && pos < json.Length)
                {
                    char esc = json[pos++];
                    switch (esc)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        // \uXXXX —— 缺這一格時會落到 default 把反斜線吃掉，`🩸` 變成裸字 `uD83EuDE78`。
                        // 🩸 2026-08-29 basecamp 開 TASK-0093；2026-09-02 summit 在開遷移單時撞到活體：
                        //    同一份 criteria 走 senate CLI 落檔後非 BMP emoji 全損毀，走 run_cmd.py 完好（對照組在單上）。
                        // 物理意義：兩個 client 對同一份內容的逃逸寫法不同（python json 預設 ensure_ascii 出 \uXXXX），
                        //          而**每一層都回綠** —— Cmd Success、回傳檔正常、單子建得出來，只有內容是壞的。
                        case 'u':
                            if (TryReadHex4(json, pos, out int aHi))
                            {
                                pos += 4;
                                // 代理對：C# string 本身就是 UTF-16，high+low 兩個 char 直接接上即是一個 code point。
                                // ⚠ 單獨的 high surrogate 不合併就吐半個字 —— 所以低位不成對時只吐高位，交給上層顯示層決定怎麼畫。
                                if (aHi >= 0xD800 && aHi <= 0xDBFF
                                    && pos + 6 <= json.Length && json[pos] == '\\' && json[pos + 1] == 'u'
                                    && TryReadHex4(json, pos + 2, out int aLo)
                                    && aLo >= 0xDC00 && aLo <= 0xDFFF)
                                {
                                    pos += 6;
                                    sb.Append((char)aHi);
                                    sb.Append((char)aLo);
                                }
                                else sb.Append((char)aHi);
                            }
                            else
                            {
                                // 非法逃逸（hex 不足 4 位／非 hex）⇒ **原樣保留並喊出來**，不靜默吃掉反斜線。
                                // ⛔ 不 throw：一筆壞 JSON 讓整條 queue 讀不了會把所有 persona 一起卡住，
                                //    而「內容有一段沒解開」比「大家都動不了」輕。喊聲留給人判。
                                Debug.LogWarning($"[AgentCommandQueue] 非法 \\u 逃逸（pos={pos}），原樣保留未解碼。");
                                sb.Append('\\');
                                sb.Append(esc);
                            }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        static string ParseStringOrNull(string json, ref int pos)
        {
            SkipWS(json, ref pos);
            if (pos < json.Length && json[pos] == 'n')
            {
                if (pos + 4 <= json.Length && json.Substring(pos, 4) == "null")
                {
                    pos += 4;
                    return null;
                }
            }
            return ParseString(json, ref pos);
        }

        static bool ParseBool(string json, ref int pos)
        {
            SkipWS(json, ref pos);
            if (pos + 4 <= json.Length && json.Substring(pos, 4) == "true")  { pos += 4; return true; }
            if (pos + 5 <= json.Length && json.Substring(pos, 5) == "false") { pos += 5; return false; }
            return false;
        }

        static int ParseInt(string json, ref int pos)
        {
            SkipWS(json, ref pos);
            int start = pos;
            if (pos < json.Length && (json[pos] == '-' || json[pos] == '+')) pos++;
            while (pos < json.Length && json[pos] >= '0' && json[pos] <= '9') pos++;
            if (pos == start) return 0;
            return int.TryParse(json.Substring(start, pos - start), out var v) ? v : 0;
        }

        static void SkipValue(string json, ref int pos)
        {
            SkipWS(json, ref pos);
            if (pos >= json.Length) return;
            char ch = json[pos];
            if (ch == '"') { ParseString(json, ref pos); return; }
            if (ch == '{' || ch == '[')
            {
                char open = ch, close = (ch == '{') ? '}' : ']';
                int depth = 0;
                while (pos < json.Length)
                {
                    char c = json[pos];
                    if (c == '"') { ParseString(json, ref pos); continue; }
                    if (c == open) depth++;
                    else if (c == close) { depth--; pos++; if (depth == 0) return; continue; }
                    pos++;
                }
                return;
            }
            while (pos < json.Length)
            {
                char c = json[pos];
                if (c == ',' || c == '}' || c == ']' || char.IsWhiteSpace(c)) return;
                pos++;
            }
        }

        static void SkipWS(string json, ref int pos)
        {
            while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
        }
        static void ExpectChar(string json, ref int pos, char ch)
        {
            SkipWS(json, ref pos);
            if (pos >= json.Length || json[pos] != ch)
                throw new Exception($"Expected '{ch}' at pos {pos}");
            pos++;
        }
    }
}
#endif
