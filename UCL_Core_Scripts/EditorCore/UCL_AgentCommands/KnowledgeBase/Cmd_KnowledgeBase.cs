// 區塊職責：Cmd_KnowledgeBase — 知識庫管理層的 agent RPC 入口 (op 分派式)。
// 物理意義：agent 透過 queue.json 呼叫 → 轉呼 knowledge_base.py 對應 op → 結果寫 _last_op.md 供 caller 讀。
//          管理類 ops (status/install/prefetch/reindex/search) 走這裡，人 (AdminPage) 與 agent 共用同一 code path。
// 設計取捨：檢索熱路徑 (每次 query) agent 建議直接呼 knowledge_base.py，不必繞 Cmd/Editor round-trip；
//          本 Cmd 提供的 search 是「Editor 內驗證 / 偶發查詢」便利入口，非高頻主路徑。
// 2026-07-23 (Zeta/summit, per Tim 拍板)：對齊 Cmd_Bartender op-dispatch 慣例。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.AgentCommands.KnowledgeBase
{
    /// <summary>
    /// 知識庫管理指令 — op 分派式。真正的向量計算在 knowledge_base.py；本 Cmd 只做橋接。
    /// </summary>
    public class Cmd_KnowledgeBase : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "KnowledgeBase";
        public override string ShortDescription => "Agent 知識庫 — 向量索引 / 檢索管理 (嵌入後端可換)";

        public override string ArgsSchema =>
@"op=<sub-op> 派遣式. 真正計算在 AgentCommands/Tools/knowledge_base.py.
熱路徑檢索 (高頻 query) 建議 agent 直接呼 python，不必走本 Cmd.

[status]                       環境 / 模型 / 索引狀態 (含可用 target 清單)
[install]   [full=true]        pip 安裝依賴 (FlagEmbedding；full=true 顯式加 torch)
[prefetch]                     下載並預熱 bge-m3 權重 (~1.2GB)
[reindex]   target=<語料庫>    掃描目標語料庫、切塊、建向量索引 (增量：文字沒變的 chunk 沿用舊向量)
[search]    query=<文字> [target=docs] [topk=5]   向量檢索 top-k
            ⚠ 索引缺 / 過期時**會就地建或增量更新**再查 (Tim 2026-08-16)，故單次可能數十秒~數分鐘
[stale]     [target=all]       索引是否該更新 (唯讀、秒級、不載模型)
[embed]     text=<文字>        單句嵌入測試 (維度 + 延遲)

target 參數（要索引 / 檢索哪個語料庫）— 合法值由 kb_targets.json 定義 (config-driven)，
可逗號多選或 all；填未知值會回完整合法清單。跑 op=status 看目前有哪些。";

        public override string ExampleArgs => "op=status";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_KnowledgeBase.md";

        // install (FlagEmbedding+torch，可能數百 MB~GB) / prefetch (bge-m3 ~1.2GB) 很久 → 放寬 timeout。
        // 須 > runner 的 timeoutMs (下方 install/prefetch 用 1800000ms=30min)，否則框架會先 timeout kill。
        public override int TimeoutSeconds => 2100;

        // ⚠ 框架 UCL_AgentCommandRunner 對 handler 的 UniTask 會 await 兩次 (WhenAny + 再 await handlerTask)。
        //   一般 async UniTask 的 pooled source 是「單次消費」— 被 WhenAny 消費後回收，第二次 await 觸發
        //   "Token version is not matched"。用 .Preserve() 包成可多次 await 的 source，繞開此框架 double-await。
        //   (本 handler 走 thread pool 完成，比其他停在 main thread 的 handler 更易踩到此 race。)
        public override UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
            => ExecuteInner(args, token).Preserve();

        async UniTask ExecuteInner(Dictionary<string, string> args, CancellationToken token)
        {
            string op = GetArg(args, "op", "").ToLowerInvariant();
            if (string.IsNullOrEmpty(op))
            {
                WriteLastOp("❌ 缺少 op 參數。支援: status / install / prefetch / reindex / search / stale / embed");
                return;
            }

            try
            {
                string argLine = BuildArgLine(op, args, out string argErr);
                if (argLine == null)
                {
                    WriteLastOp(argErr);
                    return;
                }

                // ⚠ search 也要吃長 timeout：自 2026-08-16 起「缺索引就地建 / 過期就增量更新」，
                //   一次 search 有可能真的在建索引。沿用 120s 會在 agent 端把「正在做對的事」
                //   砍成「失敗」，而且砍掉的那次已經寫了一半的算力 —— 這正是舊 STT 守護程式
                //   看 worker 90s 沒產出就重啟、把下載中的模型連砍三次的同一個形狀。
                int timeoutMs = (op == "install" || op == "prefetch" || op == "reindex" || op == "search")
                                ? 1800000 : 120000;
                var r = await UCL_KnowledgeBaseRunner.RunAsync(argLine, token, timeoutMs);
                WriteLastOp(r.DisplayText);
            }
            catch (Exception e)
            {
                WriteLastOp($"❌ Cmd_KnowledgeBase op={op} 例外: {e.Message}");
                Debug.LogWarning($"[Cmd_KnowledgeBase] op={op} fail: {e}");
            }
        }

        // 區塊職責：把 Cmd args 轉成 knowledge_base.py 的命令列 (text 格式，人類可讀)
        // 物理意義：帶空白的值 (query/text) 走 QuoteArg 降級雙引號，避免 shell 破引號。
        // 回 null 表示參數錯誤，argErr 帶訊息。
        static string BuildArgLine(string op, Dictionary<string, string> args, out string argErr)
        {
            argErr = "";
            switch (op)
            {
                case "status":
                    // probe=true → 現場跑真煙霧探針 (載權重 + forward) 而非讀指紋快取。
                    // 物理意義：import 過只證明檔案能讀，權重壞 / CUDA 不合要到 forward 才炸 (反向假 OK)；
                    // 數值影響：--probe 會載 ~1.2GB 權重 (十秒級)，故預設 off，需要硬證據時才開。
                    return "status --format text"
                           + (GetArg(args, "probe", "false").ToLowerInvariant() == "true" ? " --probe" : "");
                case "install":
                    return "install" + (GetArg(args, "full", "false").ToLowerInvariant() == "true" ? " --full" : "");
                case "prefetch":
                    return "prefetch";
                case "reindex":
                {
                    string target = GetArg(args, "target", "");
                    if (string.IsNullOrEmpty(target)) { argErr = "❌ reindex 缺 target (docs|lessons)"; return null; }
                    return $"reindex --target {target}";
                }
                case "search":
                {
                    string query = GetArg(args, "query", "");
                    if (string.IsNullOrEmpty(query)) { argErr = "❌ search 缺 query"; return null; }
                    string target = GetArg(args, "target", "docs");
                    string topk = GetArg(args, "topk", "5");
                    return $"search --query {UCL_KnowledgeBaseRunner.QuoteArg(query)} --target {target} --topk {topk}";
                }
                case "stale":
                    // 唯讀新鮮度檢查（純 stat，不載模型）— 想知道「該不該更新」而**不想付更新代價**時走這裡。
                    return $"stale --target {GetArg(args, "target", "all")}";
                case "embed":
                {
                    string text = GetArg(args, "text", "");
                    if (string.IsNullOrEmpty(text)) { argErr = "❌ embed 缺 text"; return null; }
                    return $"embed --text {UCL_KnowledgeBaseRunner.QuoteArg(text)}";
                }
                default:
                    argErr = $"❌ 未知 op='{op}'. 支援: status / install / prefetch / reindex / search / stale / embed";
                    return null;
            }
        }

        // 區塊職責：結果寫 _last_op — 收編進 UCL_ChatTavernRender.WriteLastOp 這個唯一 choke point
        //   （TASK-0059）：舊版自己 File.WriteAllText ⇒ **沒有 cmd_id 章也沒有 per-persona 鏡寫**，
        //   是第 16 個消費端裡唯一繞過章的那支 —— 多 session 並發時它寫的內容會被誤認成別人的結果。
        static void WriteLastOp(string content)
        {
            try
            {
                UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_ChatTavernRender.WriteLastOp(content);
            }
            catch { /* fail-safe */ }
            Debug.Log($"[Cmd_KnowledgeBase] {content.Substring(0, Math.Min(200, content.Length))}");
        }
    }
}
#endif
