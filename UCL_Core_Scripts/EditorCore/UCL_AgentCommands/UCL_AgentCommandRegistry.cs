
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/04 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
// Registry of agent command handlers — auto-discovers all UCL_AgentCommandHandlerBase subclasses.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 註冊所有可被 agent command 觸發的 handler。
    ///
    /// 自動發現：static ctor 內透過 <see cref="AssemblyExtensions.GetAllSubclass(System.Type)"/>
    /// 掃描所有 <see cref="UCL_AgentCommandHandlerBase"/> 子類，反射建立後註冊。
    /// 專案新增指令只要寫一個 class 繼承基底即可，無需手動 Register。
    /// </summary>
    public static class UCL_AgentCommandRegistry
    {
        // CommandType (case-insensitive) → handler 實例
        static readonly Dictionary<string, UCL_AgentCommandHandlerBase> s_Handlers
            = new(StringComparer.OrdinalIgnoreCase);

        static UCL_AgentCommandRegistry()
        {
            // 區塊職責：自動發現並註冊所有 Handler 子類
            // 物理意義：以反射列出全部 assembly 內 UCL_AgentCommandHandlerBase 的非抽象子類
            // 數值影響：Editor-only，啟動 domain reload 時跑一次，後續為 O(1) 查表
            var baseType = typeof(UCL_AgentCommandHandlerBase);
            foreach (var t in baseType.GetAllSubclass())
            {
                if (t.IsAbstract) continue;
                try
                {
                    var inst = (UCL_AgentCommandHandlerBase)Activator.CreateInstance(t);
                    if (string.IsNullOrEmpty(inst.CommandType))
                    {
                        Debug.LogError($"[UCL_AgentCmd] Handler '{t.FullName}' returned empty CommandType — ignored.");
                        continue;
                    }
                    if (s_Handlers.TryGetValue(inst.CommandType, out var existing))
                    {
                        Debug.LogError($"[UCL_AgentCmd] Duplicate CommandType '{inst.CommandType}' — '{t.FullName}' overrides '{existing.GetType().FullName}'.");
                    }
                    s_Handlers[inst.CommandType] = inst;
                }
                catch (Exception e)
                {
                    Debug.LogError($"[UCL_AgentCmd] Failed to instantiate handler '{t.FullName}': {e}");
                }
            }
        }

        // 區塊職責：cmd type 別名表 — 把常見打錯名稱自動映射到正確 cmd
        // 物理意義：跟 run_cmd.py TYPE_ALIASES 對齊；Python 端 submit-time rewrite 會擋掉
        //          直走 run_cmd.py 的 caller，但 stuck cmd in queue.json / 別 daemon 直寫
        //          queue 的 case 仍可能含舊名 → Editor 端再防一道
        // 數值影響：別名命中 → 印 warning + 用 canonical handler 跑；找不到才回 null
        // 安全：case-insensitive；新 alias 必須對映到既有 registered type
        private static readonly Dictionary<string, string> s_TypeAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ChatTavern", "Tavern" },
                { "chat_tavern", "Tavern" },
                { "chat-tavern", "Tavern" },
                { "TavernChat", "Tavern" },
                { "Lessons", "NoteLesson" },
                { "Lesson", "NoteLesson" },
                { "note_lesson", "NoteLesson" },
            };

        /// <summary>取得 handler 實例（找不到回 null）。支援 TYPE_ALIASES 自動 rewrite 與 Cmd_ 前綴剝除。</summary>
        public static UCL_AgentCommandHandlerBase Get(string type)
        {
            if (string.IsNullOrEmpty(type)) return null;
            // Phase 1: 直接查
            if (s_Handlers.TryGetValue(type, out var h)) return h;
            // Phase 2: 套 alias 重查
            if (s_TypeAliases.TryGetValue(type, out var canonical)
                && s_Handlers.TryGetValue(canonical, out var aliasHandler))
            {
                Debug.LogWarning($"[UCL_AgentCmd] cmd type '{type}' → '{canonical}' (auto-aliased — see UCL_AgentCommandRegistry.s_TypeAliases)");
                return aliasHandler;
            }
            // Phase 3: 剝除 Cmd_ 前綴後重走 Phase 1+2
            // 物理意義：handler class 命名慣例是 Cmd_<Name>，但 registry key 是去前綴的 CommandType —
            //          文件與程式碼到處以 class 名稱呼指令，人與 agent 自然會送 class 名（summit 血證
            //          2026-07-31：Cmd_Tavern 連吃兩發 Unknown type）。這不是 typo 是介面誘導，
            //          與其逐一補 alias，不如把整族前綴誤用在查表層一次吸收。
            // 安全：僅剝一次固定前綴再查既有表，不做模糊比對；查無仍回 null 交給呼叫端報錯。
            if (type.StartsWith("Cmd_", StringComparison.OrdinalIgnoreCase))
            {
                string stripped = type.Substring(4);
                UCL_AgentCommandHandlerBase strippedHandler = null;
                if (s_Handlers.TryGetValue(stripped, out var direct)) strippedHandler = direct;
                else if (s_TypeAliases.TryGetValue(stripped, out var strippedCanonical))
                    s_Handlers.TryGetValue(strippedCanonical, out strippedHandler);
                if (strippedHandler != null)
                {
                    Debug.LogWarning($"[UCL_AgentCmd] cmd type '{type}' → '{strippedHandler.CommandType}' (Cmd_ prefix stripped — registry key 是去前綴的 CommandType)");
                    return strippedHandler;
                }
            }
            return null;
        }

        // 區塊職責：對查無的 cmd type 給出最近似的已註冊名稱（did-you-mean）。
        // 物理意義：Unknown type 的完整註冊清單過去只印在 Editor console，CLI 端只收到一句錯誤 —
        //          「知識存在但留在對面樓層」。把建議塞進 LastRunError 讓 run_cmd 呼叫端直接看到出路。
        // 數值影響：Levenshtein 距離排序取前 max 個；距離 > max(3, 名稱長度/2) 視為不相干不列入。
        //          只在錯誤路徑呼叫（每次 unknown type 一次 O(N×L²)，N=32 可忽略）。
        public static IReadOnlyList<string> SuggestTypes(string type, int max = 3)
        {
            if (string.IsNullOrEmpty(type)) return Array.Empty<string>();
            string probe = type.StartsWith("Cmd_", StringComparison.OrdinalIgnoreCase) ? type.Substring(4) : type;
            int cutoff = Math.Max(3, probe.Length / 2);
            return s_Handlers.Keys
                .Select(k => (Name: k, Dist: LevenshteinDistance(probe.ToLowerInvariant(), k.ToLowerInvariant())))
                .Where(x => x.Dist <= cutoff)
                .OrderBy(x => x.Dist).ThenBy(x => x.Name)
                .Take(max)
                .Select(x => x.Name)
                .ToList();
        }

        // 區塊職責：標準 Levenshtein 編輯距離（滾動單列版）。
        // 物理意義：did-you-mean 的相似度量尺；不引第三方套件、不做加權變形。
        // 數值影響：O(|a|×|b|) 時間、O(|b|) 空間；僅 SuggestTypes 錯誤路徑使用。
        static int LevenshteinDistance(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            var row = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) row[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                int prev = row[0];
                row[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cur = row[j];
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), prev + cost);
                    prev = cur;
                }
            }
            return row[b.Length];
        }

        // 區塊職責：把別名表公開給 schema 匯出器 — 讓 Python 端不必再手抄第二份。
        // 物理意義：本表與 run_cmd.py 的 TYPE_ALIASES 原本是同一張表的兩份手抄鏡像（第四處鏡像）。
        //          匯出進 commands_schema.json 後，Python 端改讀產物，這一族漂移就結構性消失。
        // 數值影響：回唯讀視圖，呼叫端不能改動內部狀態。
        /// <summary>cmd type 別名對照（alias → canonical），供 <c>UCL_CmdSchemaExporter</c> 匯出。</summary>
        public static IReadOnlyDictionary<string, string> ListTypeAliases() => s_TypeAliases;

        /// <summary>列出所有已註冊的指令類型名稱（按字母排序）。</summary>
        public static IReadOnlyList<string> ListTypes()
        {
            return s_Handlers.Keys.OrderBy(s => s).ToList();
        }

        /// <summary>列出所有 handler 實例（按 CommandType 排序）。</summary>
        public static IReadOnlyList<UCL_AgentCommandHandlerBase> ListHandlers()
        {
            return s_Handlers.Values.OrderBy(h => h.CommandType).ToList();
        }
    }
}
#endif
