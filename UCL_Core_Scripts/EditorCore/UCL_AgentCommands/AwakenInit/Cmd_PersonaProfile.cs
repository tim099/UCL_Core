// 區塊職責：persona profile 的 Cmd 介面（§8.7 A＋B 拍板）—— python 端讀 persona 資料的主路徑。
// 物理意義：解析單端化 —— python 發本 Cmd，C# 現場重新解析並**重寫快照**，python 再讀快照
//          （成功＝快照剛出爐＝現場值；Cmd 跑不通時 python 退讀既有快照並在回傳值標記時效）。
//          快照就是傳輸載體：不把 21 份 persona 塞進 Cmd 回傳欄，回傳只給路徑與讀數。
// 數值影響：純讀 persona 檔＋重寫一份衍生快取；不動任何 persona 檔本身。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace UCL.Core.EditorLib.AgentCommands.AwakenInit
{
    /// <summary>
    /// persona profile 快照刷新（python 讀取主路徑）。
    /// <para>典型用法（python `_lib/persona_profile.py` 內部自動呼叫，人一般不必手跑）：</para>
    /// <code>
    /// python run_cmd.py run PersonaProfile          # op=refresh（預設）：重寫快照
    /// </code>
    /// </summary>
    public class Cmd_PersonaProfile : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "PersonaProfile";

        public override string ShortDescription =>
            "重寫 persona profile 快照（§8.7 A＋B：C# 單端解析，python 讀快照）。";

        public override string ArgsSchema =>
            "op=refresh（預設，目前唯一）— 重寫 _persona_profile_snapshot.json 並回報路徑/人數/時間戳";

        public override string ExampleArgs => "op=refresh";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/Plan/Plan_Persona_Registry_Retirement.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();
            string op = GetArg(args, "op", "refresh").Trim().ToLowerInvariant();
            if (op != "refresh")
                throw new Exception($"[PersonaProfile] 未知 op '{op}'（目前只有 refresh）");

            var (ok, count, error) = UCL_PersonaProfile.WriteSnapshot();
            if (!ok)
                throw new Exception($"[PersonaProfile] 快照重寫失敗：{error}");

            // 回傳值：python 端據此讀快照；路徑不讓它自己拼（拼路徑就是下一個平行宇宙）
            UCL_AgentCommandRunner.ReportOutputValue(args, "snapshot_path", UCL_PersonaProfile.SnapshotPath);
            UCL_AgentCommandRunner.ReportOutputValue(args, "pool_count", count.ToString());
            UnityEngine.Debug.Log($"[PersonaProfile] 快照已重寫：{count} personas → {UCL_PersonaProfile.SnapshotPath}");
        }
    }
}
#endif
