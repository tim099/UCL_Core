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
            "op=refresh（預設）— 重寫 _persona_profile_snapshot.json 並回報路徑/人數 | " +
            "op=set persona=<name> field=<欄> value=<值：純量欄字面收／結構欄(identity_vector,vector_history,fork_lineage)必須是合法 JSON 陣列，parse 或形狀失敗即擋；長 JSON 走 --arg-file value=> actor=<誰寫的> reason=<憑什麼> — " +
            "§8.6 寫入接縫：單欄 patch（actor/reason 必填，缺了直接擋；附審計 jsonl＋快照刷新）";

        public override string ExampleArgs => "op=set;persona=Template;field=email;value=t@example.com;actor=summit;reason=驗收";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/Plan/Plan_Persona_Registry_Retirement.md";

        // BUG-14（kiara）：沒宣告 ArgsSpec 時 `value` 打錯名（val=）⇒ 靜默取空字串 ⇒ 欄位被清空，
        // 而寫入成功、審計落行、快照跟上 —— 查帳時是一筆 actor/reason 都很正當的清空紀錄。
        // 預檢擋在 CLI 層；執行層另有顯式檢查（預檢可被停用，守衛要長在必經路上）。
        public override UCL_CmdArgsSpec ArgsSpec => new UCL_CmdArgsSpec
        {
            Ops = new Dictionary<string, UCL_CmdOpSpec>
            {
                ["refresh"] = new UCL_CmdOpSpec(),
                // BUG-15：`value` 從 Required 移到 RequiredPresent —— 清空欄位（value=）是合法操作，
                // 而 Required 的判準是「有值」，會把它擋掉（且擋在 handler 之前，讓下面那句
                // ContainsKey 守衛變成永遠跑不到的死碼）。判準要的是「在場」，不是「有值」。
                ["set"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "persona", "field", "actor", "reason" },
                    RequiredPresent = new[] { "value" },
                },
            }
        };

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();
            string op = GetArg(args, "op", "refresh").Trim().ToLowerInvariant();
            if (op == "set")
            {
                string persona = GetArg(args, "persona", "").Trim();
                string field = GetArg(args, "field", "").Trim();
                // BUG-14：value 必須**顯式在場** —— GetArg 的預設值分不出「沒給」跟「給了空字串」，
                // 而「沒給」多半是參數名打錯；清空欄位要顯式給 value=（空值）才算意圖。
                // BUG-15 之後這句與 ArgsSpec 的 RequiredPresent **判準一致**（都是 ContainsKey）；
                // 留著它是因為預檢可被停用／未宣告時仍要有守衛 —— 守衛要長在必經路上，
                // 不是長在可被跳過的那一層。（在此之前它被 Required 遮住，是死碼。）
                if (!args.ContainsKey("value"))
                    throw new Exception("[PersonaProfile] set 缺 value —— 參數名打錯？清空欄位請顯式給 value=（空值）");
                string value = GetArg(args, "value", "");
                string actor = GetArg(args, "actor", "").Trim();
                string reason = GetArg(args, "reason", "").Trim();
                string oldVal = UCL_PersonaProfile.GetString(persona, field, "");
                if (!UCL_PersonaProfile.SetField(persona, field, value, actor, reason, out string setErr))
                    throw new Exception($"[PersonaProfile] set 失敗：{setErr}");
                UCL_AgentCommandRunner.ReportOutputValue(args, "old_value", oldVal);
                UCL_AgentCommandRunner.ReportOutputValue(args, "new_value", value);
                UnityEngine.Debug.Log($"[PersonaProfile] set {persona}.{field}：'{oldVal}' → '{value}'（actor={actor}）");
                return;
            }
            if (op != "refresh")
                throw new Exception($"[PersonaProfile] 未知 op '{op}'（refresh / set）");

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
