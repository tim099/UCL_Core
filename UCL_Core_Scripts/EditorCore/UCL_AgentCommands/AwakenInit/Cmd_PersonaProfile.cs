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
            "§8.6 寫入接縫：單欄 patch（actor/reason 必填，缺了直接擋；附審計 jsonl＋快照刷新） | " +
            "op=unset persona=<name> field=<欄> actor=<誰> reason=<憑什麼> — 刪掉 profile/<欄>.md"
            + "（op=set 的逆操作，BUG-16：唯一能把欄位還原成非 profile 來源的正道；審計標 (unset)。"
            + "⚠ 語意是「移除新結構的覆蓋」：legacy 有 key 會退回 legacy 且下次存取自動再遷回 profile，"
            + "legacy 也沒有才真的回到 absent） | " +
            "op=get_bank persona=<name> [currency=<區域ID，預設本專案>] — 讀該 persona 在該區域的帳號"
            + "（回報 account / source / note；source != currency ＝跨區借用，不是本區宣告） | " +
            "op=set_bank persona=<name> account=<agent id> actor=<誰> reason=<憑什麼> [currency=<區域ID>] — "
            + "寫 letters/<persona>/bank/<區域ID>.md（一區一檔；附審計） | " +
            "op=migrate_bank actor=<誰> reason=<憑什麼> [currency=<區域ID>] [dry_run=0] [overwrite=1] — "
            + "由現況 persona.agent 導出全 pool 的綁定檔；**預設 dry_run（只印不寫）** | " +
            "op=rebind_region from=<舊區ID> to=<新區ID> actor= reason= [dry_run=0] — "
            + "把全 pool 的綁定從舊區搬到新區（**新區已有不同值＝衝突，整批不動**）；"
            + "後台改區域 ID 時會自動跑這一段，本 op 供預跑／跨專案遷移用 | " +
            "op=unbind persona=<name> actor= reason= [currency=<區域ID>] — 刪掉該 persona 在該區的綁定檔"
            + "（唯一能把綁定還原成「不存在」的手段；有審計） | " +
            "op=rename_agent from=<舊 agent id> to=<新 agent id> actor= reason= [currency=<區域ID>] "
            + "[dry_run=0] [allow_new=1] — agent id 改名：把綁定檔與 persona.agent **兩邊一起**改；"
            + "**預設 dry_run**；to 必須是 canonical 帳號且未銷戶（allow_new=1 只放寬前者，銷戶一律擋）";

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
                // BUG-16：set 的逆操作 —— 沒有 value（要刪的就是那個檔），其餘判準與 set 同。
                ["unset"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "persona", "field", "actor", "reason" },
                },
                // 區域銀行綁定（Tim 2026-08-20）。currency 省略＝用本專案的 CurrencyId ——
                // 顯式給是為了測試與跨區操作，不是給日常用的（日常不該記得自己在哪一區）。
                ["get_bank"] = new UCL_CmdOpSpec { Required = new[] { "persona" } },
                ["set_bank"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "persona", "account", "actor", "reason" },
                },
                // migrate 沒有 Required=persona —— 它掃全 pool。actor/reason 仍必填（§8.6 不因批次而放寬）。
                ["migrate_bank"] = new UCL_CmdOpSpec { Required = new[] { "actor", "reason" } },
                ["rebind_region"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "from", "to", "actor", "reason" },
                },
                ["unbind"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "persona", "actor", "reason" },
                },
                // rename_agent 同樣掃全 pool（沒有 Required=persona）——
                // 它的篩選鍵是 from（舊 agent id），不是某一個人。
                ["rename_agent"] = new UCL_CmdOpSpec
                {
                    Required = new[] { "from", "to", "actor", "reason" },
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
            // ===========================================================
            // 區塊職責：op=unset —— set 的逆操作（BUG-16）。
            // 物理意義：三態 profile/legacy/absent 是設計的一部分，而 set 只能前進不能後退，
            //          唯一復原手段變成手刪 profile/ 檔（繞過接縫與審計）。本 op 補上那條正道。
            // 數值影響：刪一個檔＋審計一行＋快照刷新；讀回複驗一律走 GetRaw(migrate:false) ——
            //          ⚠ GetString 會觸發 lazy migration，unset 完用它驗會**當場把檔生回來**。
            // ===========================================================
            if (op == "unset")
            {
                string persona = GetArg(args, "persona", "").Trim();
                string field = GetArg(args, "field", "").Trim();
                string actor = GetArg(args, "actor", "").Trim();
                string reason = GetArg(args, "reason", "").Trim();
                // 舊值用不遷移的讀法取（這裡讀一下不該留下任何寫入）
                var rawBefore = UCL_PersonaProfile.GetRaw(persona, false);
                string oldVal = rawBefore == null ? "" : rawBefore.GetString(field, "");
                if (!UCL_PersonaProfile.UnsetProfileField(persona, field, actor, reason,
                        out bool hadFile, out string unsetErr))
                    throw new Exception($"[PersonaProfile] unset 失敗：{unsetErr}");
                // 讀回複驗：來源必須不再是 profile（用不觸發遷移的總表讀）。
                var srcs = UCL_PersonaProfile.GetFieldSources(persona);
                string nowSrc = (srcs != null && srcs.TryGetValue(field, out var s0))
                    ? s0 : UCL_PersonaProfile.SRC_ABSENT;
                if (nowSrc == UCL_PersonaProfile.SRC_PROFILE)
                    throw new Exception($"[PersonaProfile] unset 後 {persona}.{field} 來源仍是 profile —— 未生效");
                var rawAfter = UCL_PersonaProfile.GetRaw(persona, false);
                string nowVal = rawAfter == null ? "" : rawAfter.GetString(field, "");
                UCL_AgentCommandRunner.ReportOutputValue(args, "had_file", hadFile ? "1" : "0");
                UCL_AgentCommandRunner.ReportOutputValue(args, "old_value", oldVal);
                UCL_AgentCommandRunner.ReportOutputValue(args, "now_value", nowVal);
                UCL_AgentCommandRunner.ReportOutputValue(args, "now_source", nowSrc);
                string aLegacyNote = nowSrc == UCL_PersonaProfile.SRC_LEGACY
                    ? "　⚠ legacy 仍有此欄：讀取端退回 legacy，且**下一次消費端存取會 lazy migration 抄回 profile/**（unset 對這種欄是暫時的）"
                    : "";
                UnityEngine.Debug.Log($"[PersonaProfile] unset {persona}.{field}："
                    + $"'{oldVal}'（had_file={(hadFile ? 1 : 0)}）→ '{nowVal}'（source={nowSrc}，actor={actor}）{aLegacyNote}");
                return;
            }
            // ===========================================================
            // 區塊職責：區域銀行綁定的三個 op（Tim 2026-08-20 拍板）。
            // 物理意義：綁定＝「這個 persona 在這個區域用哪個帳號（＝agent id）」，
            //          住在 letters/<persona>/bank/<區域ID>.md，一區一檔
            //          （理由見 UCL_LettersPath.BankDirName 區塊：letters 是同一個 repo 被多專案掛著）。
            // 數值影響：get_bank 純讀；set_bank 寫單檔＋審計；
            //          migrate_bank **預設 dry_run** —— 批次寫入的預設值必須是「不寫」，
            //          因為它的破壞面是全 pool，而打錯一個參數的成本不該是 21 個檔。
            // ===========================================================
            if (op == "get_bank" || op == "set_bank" || op == "migrate_bank"
                || op == "rebind_region" || op == "unbind" || op == "rename_agent")
            {
                string currency = GetArg(args, "currency", "").Trim();
                if (string.IsNullOrEmpty(currency))
                    currency = Treasury.UCL_CentralBankSettings.CurrencyId;
                if (!Treasury.UCL_CentralBankSettings.IsValidCurrencyId(currency))
                    throw new Exception($"[PersonaProfile] 區域 ID 不合法（要能當檔名）：'{currency}'");

                if (op == "get_bank")
                {
                    string persona = GetArg(args, "persona", "").Trim();
                    string acc = UCL_PersonaProfile.GetBankAccount(persona, currency,
                        out string src, out string note);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "currency", currency);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "account", acc);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "source", src);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "note", note);
                    UnityEngine.Debug.Log($"[PersonaProfile] get_bank {persona}@{currency} = "
                        + $"'{acc}'（source={src}{(string.IsNullOrEmpty(note) ? "" : "；" + note)}）");
                    return;
                }

                if (op == "set_bank")
                {
                    string persona = GetArg(args, "persona", "").Trim();
                    string account = GetArg(args, "account", "").Trim();
                    string actor = GetArg(args, "actor", "").Trim();
                    string reason = GetArg(args, "reason", "").Trim();
                    string before = UCL_PersonaProfile.GetBankAccount(persona, currency,
                        out string beforeSrc, out _);
                    if (!UCL_PersonaProfile.WriteBankAccount(persona, currency, account,
                            actor, reason, out string bankErr))
                        throw new Exception($"[PersonaProfile] set_bank 失敗：{bankErr}");
                    // 印 ✓ 不算數，讀回來才算。
                    string after = UCL_PersonaProfile.GetBankAccount(persona, currency,
                        out string afterSrc, out _);
                    if (after != account || afterSrc != currency)
                        throw new Exception($"[PersonaProfile] set_bank 寫入後讀回不符："
                            + $"期望 '{account}'@{currency}、實際 '{after}'@{afterSrc}");
                    UCL_AgentCommandRunner.ReportOutputValue(args, "currency", currency);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "old_account", before);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "old_source", beforeSrc);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "account", after);
                    UnityEngine.Debug.Log($"[PersonaProfile] set_bank {persona}@{currency}："
                        + $"'{before}'（{beforeSrc}）→ '{after}'（actor={actor}）");
                    return;
                }

                if (op == "rebind_region")
                {
                    // 區塊職責：換區重綁（後台改區域 ID 時自動跑的同一段邏輯）。
                    // 物理意義：本 op 只做「複製到新區」——**不刪舊區、不翻設定**。
                    //          那兩件事的擁有者是後台（它才知道使用者按了確認）；
                    //          CLI 這條留給「預跑」與「跨專案遷移」用。
                    // 數值影響：預設 dry_run；衝突（新區已有不同值）大於 0 時**拋例外**，
                    //          因為呼叫端最可能的下一步是繼續，而繼續會做出半套狀態。
                    string from = GetArg(args, "from", "").Trim();
                    string to = GetArg(args, "to", "").Trim();
                    string actor = GetArg(args, "actor", "").Trim();
                    string reason = GetArg(args, "reason", "").Trim();
                    bool dryRun = GetArg(args, "dry_run", "1").Trim() != "0";
                    if (!Treasury.UCL_CentralBankSettings.IsValidCurrencyId(from)
                        || !Treasury.UCL_CentralBankSettings.IsValidCurrencyId(to))
                        throw new Exception($"[PersonaProfile] from／to 必須是合法區域 ID（能當檔名）：'{from}' → '{to}'");

                    string rep2 = UCL_PersonaProfile.CopyBankRegionAll(from, to, actor, reason, dryRun,
                        out int copied, out int skipped, out int conflicts, out int failed);
                    UnityEngine.Debug.Log(rep2);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "from", from);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "to", to);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "dry_run", dryRun ? "1" : "0");
                    UCL_AgentCommandRunner.ReportOutputValue(args, "copied", copied.ToString());
                    UCL_AgentCommandRunner.ReportOutputValue(args, "skipped", skipped.ToString());
                    UCL_AgentCommandRunner.ReportOutputValue(args, "conflicts", conflicts.ToString());
                    UCL_AgentCommandRunner.ReportOutputValue(args, "failed", failed.ToString());
                    if (conflicts > 0 || failed > 0)
                        throw new Exception($"[PersonaProfile] rebind_region 有 {conflicts} 筆衝突、"
                            + $"{failed} 筆失敗 —— 詳見 Editor log（衝突＝新區已有不同綁定，本 op 不覆寫也不挑）");
                    return;
                }

                if (op == "unbind")
                {
                    string persona = GetArg(args, "persona", "").Trim();
                    string actor = GetArg(args, "actor", "").Trim();
                    string reason = GetArg(args, "reason", "").Trim();
                    string before = UCL_PersonaProfile.GetBankAccount(persona, currency,
                        out string beforeSrc, out _);
                    bool hadOwn = UCL_PersonaProfile.HasOwnBankBinding(persona, currency);
                    if (!UCL_PersonaProfile.DeleteBankBinding(persona, currency, actor, reason,
                            out string delErr))
                        throw new Exception($"[PersonaProfile] unbind 失敗：{delErr}");
                    // 讀回複驗：刪完之後本區不該再有自己的綁定（可能改成跨區借用，那是對的）。
                    if (UCL_PersonaProfile.HasOwnBankBinding(persona, currency))
                        throw new Exception($"[PersonaProfile] unbind 後 {persona}@{currency} 仍有本區綁定 —— 未生效");
                    string after = UCL_PersonaProfile.GetBankAccount(persona, currency,
                        out string afterSrc, out string afterNote);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "currency", currency);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "had_own", hadOwn ? "1" : "0");
                    UCL_AgentCommandRunner.ReportOutputValue(args, "old_account", before);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "now_account", after);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "now_source", afterSrc);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "now_note", afterNote);
                    UnityEngine.Debug.Log($"[PersonaProfile] unbind {persona}@{currency}："
                        + $"'{before}'（{beforeSrc}）→ 現在 '{after}'（{afterSrc}）actor={actor}");
                    return;
                }

                // ==========================================================
                // 區塊職責：agent id 改名 —— 綁定檔與 persona.agent **同時**改，一邊都不能落單。
                // 物理意義：agent id 與帳號 id 合一（Tim 2026-08-20 拍板）的執行手段。
                //          綁定檔（letters/<p>/bank/<區>.md）與 registry 的 persona.agent 是同一件事的兩份
                //          記載，實測 2026-08-20 為 21/21 一致 —— 只改一邊就是親手製造第一筆不一致，
                //          而不一致的兩份記載**各自都能運作、都不報錯**。
                // 數值影響：不碰 ledger、不動任何一分錢。改的只是「綁定值叫什麼」。
                //          ⚠ 錢的搬遷是 ledger transfer（account-rename），是另一件事、另一個入口。
                // 守衛（三道，順序即嚴格度）：
                //   ① to 已銷戶 ⇒ **無條件擋**。銷戶帳號明文禁止金流，改名指過去不會報錯，
                //      只會讓未來的解析命中一個合法但死掉的帳號 —— 那正是這一族坑的形狀。
                //   ② to 非 canonical ⇒ 擋，除非顯式 allow_new=1（新帳號要有人負責，不給預設放行）。
                //   ③ 兩份記載不一致的 persona ⇒ 算 failed 且**不寫**。既然它已經歪了，
                //      本 op 不猜哪邊才對 —— 猜錯會把歪的那份蓋成「看起來很整齊」。
                // ==========================================================
                if (op == "rename_agent")
                {
                    string from = GetArg(args, "from", "").Trim();
                    string to = GetArg(args, "to", "").Trim();
                    string actor = GetArg(args, "actor", "").Trim();
                    string reason = GetArg(args, "reason", "").Trim();
                    bool dryRun = GetArg(args, "dry_run", "1").Trim() != "0";
                    bool allowNew = GetArg(args, "allow_new", "0").Trim() == "1";

                    if (from == to)
                        throw new Exception($"[PersonaProfile] rename_agent：from 與 to 相同（'{from}'）—— 沒有要改的東西");

                    // 守衛①：銷戶帳號無條件擋（allow_new 也放寬不了）。
                    if (Treasury.UCL_TreasuryAccountResolver.IsClosed(to, out string closedReason))
                        throw new Exception($"[PersonaProfile] rename_agent 拒絕：to='{to}' 是**已銷戶帳號** —— {closedReason}"
                            + "。改名指向銷戶帳號不會報錯，只會讓解析靜默命中一個禁止金流的合法帳號。");

                    // 守衛②：非 canonical 要顯式放行。
                    bool canonical = Treasury.UCL_TreasuryAccountResolver.IsCanonicalAccount(to);
                    if (!canonical && !allowNew)
                        throw new Exception($"[PersonaProfile] rename_agent 拒絕：to='{to}' 不是 canonical 帳號"
                            + "（不在 agent_banks／system_accounts）—— 確定要建新帳號請顯式帶 allow_new=1");

                    // ⚠ 實作在 UCL_PersonaProfile.RenameAgent —— 本 Cmd 與後台遷移頁**共用同一支**。
                    //   兩個入口各寫一份的話，會出現「CLI 驗過了而 UI 走另一條路」的分裂。
                    UCL_PersonaProfile.RenameAgent(from, to, currency, actor, reason, dryRun,
                        out int hit, out int renamed, out int failed, out string report);
                    UnityEngine.Debug.Log($"[PersonaProfile] {report}");
                    Treasury.UCL_TreasuryAccountResolver.Invalidate();
                    UCL_AgentCommandRunner.ReportOutputValue(args, "from", from);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "to", to);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "currency", currency);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "dry_run", dryRun ? "1" : "0");
                    UCL_AgentCommandRunner.ReportOutputValue(args, "hit", hit.ToString());
                    UCL_AgentCommandRunner.ReportOutputValue(args, "renamed", renamed.ToString());
                    UCL_AgentCommandRunner.ReportOutputValue(args, "failed", failed.ToString());
                    if (failed > 0)
                        throw new Exception($"[PersonaProfile] rename_agent 有 {failed} 筆失敗 —— 詳見 Editor log");
                    return;
                }

                // op == "migrate_bank"
                {
                    string actor = GetArg(args, "actor", "").Trim();
                    string reason = GetArg(args, "reason", "").Trim();
                    // 預設 dry_run：批次寫入的預設值必須是「不寫」。
                    bool dryRun = GetArg(args, "dry_run", "1").Trim() != "0";
                    bool overwrite = GetArg(args, "overwrite", "0").Trim() == "1";

                    var pool = UCL_PersonaProfile.PoolNamesSorted();
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"[PersonaProfile] migrate_bank currency={currency} "
                        + $"dry_run={(dryRun ? 1 : 0)} overwrite={(overwrite ? 1 : 0)} pool={pool.Count}");
                    int written = 0, skippedExisting = 0, skippedNoAgent = 0, failed = 0;
                    foreach (var p in pool)
                    {
                        string agent = UCL_PersonaProfile.GetString(p, "agent", "").Trim();
                        string cur = UCL_PersonaProfile.GetBankAccount(p, currency,
                            out string curSrc, out _);
                        bool hasOwn = curSrc == currency;
                        if (string.IsNullOrEmpty(agent))
                        {
                            skippedNoAgent++;
                            sb.AppendLine($"  ⛔ {p}：persona.agent 為空 —— 跳過（沒有可導出的來源）");
                            continue;
                        }
                        if (hasOwn && !overwrite)
                        {
                            skippedExisting++;
                            sb.AppendLine($"  ○ {p}：本區已有綁定 '{cur}'"
                                + $"{(cur == agent ? "（與 agent 相同）" : $"（⚠ 與 agent '{agent}' 不同）")} —— 跳過（overwrite=1 才覆寫）");
                            continue;
                        }
                        if (dryRun)
                        {
                            sb.AppendLine($"  → {p}：會寫入 '{agent}'"
                                + $"{(string.IsNullOrEmpty(cur) ? "" : $"（目前 '{cur}'，source={curSrc}）")}");
                            continue;
                        }
                        if (!UCL_PersonaProfile.WriteBankAccount(p, currency, agent, actor, reason,
                                out string err))
                        {
                            failed++;
                            sb.AppendLine($"  ✗ {p}：寫入失敗 —— {err}");
                            continue;
                        }
                        string back = UCL_PersonaProfile.GetBankAccount(p, currency, out string backSrc, out _);
                        if (back != agent || backSrc != currency)
                        {
                            failed++;
                            sb.AppendLine($"  ✗ {p}：寫入後讀回不符（期望 '{agent}'@{currency}、實際 '{back}'@{backSrc}）");
                            continue;
                        }
                        written++;
                        sb.AppendLine($"  ✓ {p}：'{back}'");
                    }
                    sb.AppendLine($"  ⇒ 寫入 {written}／既有跳過 {skippedExisting}／無 agent 跳過 {skippedNoAgent}／失敗 {failed}");
                    UnityEngine.Debug.Log(sb.ToString());
                    UCL_AgentCommandRunner.ReportOutputValue(args, "currency", currency);
                    UCL_AgentCommandRunner.ReportOutputValue(args, "dry_run", dryRun ? "1" : "0");
                    UCL_AgentCommandRunner.ReportOutputValue(args, "pool", pool.Count.ToString());
                    UCL_AgentCommandRunner.ReportOutputValue(args, "written", written.ToString());
                    UCL_AgentCommandRunner.ReportOutputValue(args, "skipped_existing", skippedExisting.ToString());
                    UCL_AgentCommandRunner.ReportOutputValue(args, "skipped_no_agent", skippedNoAgent.ToString());
                    UCL_AgentCommandRunner.ReportOutputValue(args, "failed", failed.ToString());
                    // 失敗不吞：批次的部分失敗最容易被讀成全部成功。
                    if (failed > 0)
                        throw new Exception($"[PersonaProfile] migrate_bank 有 {failed} 筆失敗 —— 詳見 Editor log");
                    return;
                }
            }

            if (op != "refresh")
                throw new Exception($"[PersonaProfile] 未知 op '{op}'"
                    + "（refresh / set / unset / get_bank / set_bank / migrate_bank / rebind_region / unbind / rename_agent）");

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
