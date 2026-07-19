// 區塊職責：Discord Mirror smoke test 觸發器 — 發一則到 git-ignored 測試 webhook，走 native poll 送出路徑
// 物理意義：驗 basecamp 點名的最大風險「UnityWebRequest 在純 editor(非 play)下 poll isDone 會不會如期完成 + 真送達」。
//          webhook URL 由本 Cmd 從 git-ignored 檔讀（不經 arg → 避免 secret 進 run_cmd log / _last_op.md）。
// 數值影響：走 UCL_DiscordMirrorDaemon.SmokeTest → StartPost 掛 in-flight → daemon tick DrainInFlight 判讀 + log；
//          recordOnSuccess=false → 只 log 不動 canonical state（smoke 不污染真實 cursor）。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Discord Mirror smoke test。從 <c>AgentCommands/PromptQueue/_smoke_test_webhook.txt</c>（git-ignored）
    /// 讀一行 webhook URL，發一則測試訊息走 native poll 送出路徑。結果看 Console 的 <c>[DiscordMirror]</c> log。
    /// </summary>
    public class Cmd_MirrorSmoke : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "MirrorSmoke";

        public override string ShortDescription =>
            "Discord Mirror smoke test — send one message to the git-ignored test webhook via the native poll-send path.";

        public override string ArgsSchema =>
            "content=message body to send (default a timestamped smoke marker)";

        public override string ExampleArgs => "content=mirror smoke hello";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string path = Path.Combine(UCL_RepoPath.AgentCommandsDir, "PromptQueue", "_smoke_test_webhook.txt");
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[AgentCmd:MirrorSmoke] 測試 webhook 檔不存在: {path}（放一行 webhook URL，git-ignored）");
                await UniTask.CompletedTask;
                return;
            }

            // mode=statetest：不送 Discord，只驗 state 寫入層（basecamp 條件 b：Save 保留未知欄位 + JsonLib schema）
            // 對 _mirror_selftest 房 RecordSent + Save，之後由外部 Python inspect _tavern_state.json 驗 schema/保欄位
            string mode = (args != null && args.TryGetValue("mode", out var m)) ? m : "send";
            if (mode == "statetest")
            {
                UCL_DiscordMirrorState.RecordSent("_mirror_selftest", "999selftest", "aaa111", "2026-07-19T21:00:00.000Z");
                UCL_DiscordMirrorState.RecordSent("_mirror_selftest", "999selftest", "bbb222", "2026-07-19T21:00:05.000Z");
                UCL_DiscordMirrorState.Save();
                Debug.Log("[AgentCmd:MirrorSmoke] statetest: RecordSent x2 + Save 完成 — 外部 inspect _tavern_state.json 驗 schema/保欄位");
                await UniTask.CompletedTask;
                return;
            }

            string url = File.ReadAllText(path).Trim();
            string content = (args != null && args.TryGetValue("content", out var c) && !string.IsNullOrEmpty(c))
                ? c
                : "[mirror-smoke] native poll-send edit-mode resume test";

            Debug.Log("[AgentCmd:MirrorSmoke] 觸發 SmokeTest（結果看 [DiscordMirror] log；send 需幾秒，等 tick 輪詢）");
            UCL_DiscordMirrorDaemon.SmokeTest(url, content);

            await UniTask.CompletedTask;
        }
    }
}
#endif
