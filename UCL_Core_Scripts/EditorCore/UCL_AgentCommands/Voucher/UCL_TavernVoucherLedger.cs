// 區塊職責：酒館券 (Tavern voucher / 自由時間券) 的 C# 端 canonical ledger — grant / balance 單一 owner。
// 物理意義：對齊 UCL_CanvasVoucherLedger 的角色（券發放收攏到 C# static owner，正規路徑解析、零 cwd 依賴），
//          但 schema 是 agent_bonus_quota.json 的巢狀 agents.<bank>.personas.<persona>.total_remaining + history
//          （繪圖券是 per-persona 檔的 balance）。history entry 對齊 work_session.py fire_voucher_accrual 的欄位
//          （id / granted_at / granted_by / kind / amount / used / remaining / source / ref）以保審計一致。
// 數值影響：讀寫 <DataRoot>/ChatTavern/agent_bonus_quota.json；原子 tmp+replace（走 UCL_VoucherLedgerCommon）。
// 設計取捨：Tim 2026-07-24 拍板 — 原本「酒館券 canonical owner 無 grant CLI 故 BankAdminPage 不 C# 直寫」的
//          禁令，已被繪圖券改走 C# canonical ledger（UCL_CanvasVoucherLedger）的先例推翻：C# static owner 本身
//          就是有審計、正規路徑的 canonical 寫入者，非「繞 owner 直寫」。酒館券比照建立本 ledger 收口。
//          酒館券綁 (bank, persona)（token 綁 bank、券綁 persona，但酒館券分桶在 bank 下的 personas）。
using System;
using System.IO;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Voucher
{
    /// <summary>
    /// 酒館券 canonical ledger — 唯一寫入 owner。發券 / 查餘額走此，與 work_session.py 的 accrual 共用同一份 quota 檔。
    /// </summary>
    public static class UCL_TavernVoucherLedger
    {
        // 正規路徑：以 DataRoot 錨定（= 讀取端 / work_session.py 同源），不吃 cwd 相對解析
        static string QuotaPath => Path.Combine(UCL_AgentCommandsPath.DataRoot, "ChatTavern", "agent_bonus_quota.json");

        /// <summary>讀 (bank, persona) 的酒館券餘額（total_remaining；缺檔/缺節點 = 0）。純讀不寫。</summary>
        public static int GetBalance(string bank, string persona)
        {
            if (string.IsNullOrEmpty(bank) || string.IsNullOrEmpty(persona)) return 0;
            try
            {
                if (!File.Exists(QuotaPath)) return 0;
                var d = JsonData.ParseJson(File.ReadAllText(QuotaPath));
                if (d == null || !d.Contains("agents")) return 0;
                var agents = d["agents"];
                if (!agents.Contains(bank)) return 0;
                var bankNode = agents[bank];
                if (!bankNode.Contains("personas")) return 0;
                var personas = bankNode["personas"];
                if (!personas.Contains(persona)) return 0;
                return personas[persona].GetInt("total_remaining", 0);
            }
            catch { return 0; }
        }

        /// <summary>
        /// 發酒館券：agents.&lt;bank&gt;.personas.&lt;persona&gt;.total_remaining += amount，append history。
        /// 回 (before, after)。amount 必 &gt;0；缺任何層級節點自動 init（對齊 work_session fire_voucher_accrual）。
        /// </summary>
        public static (int before, int after) Grant(string bank, string persona, int amount, string source, string refText)
        {
            if (string.IsNullOrEmpty(bank)) throw new ArgumentException("bank 不可為空");
            if (string.IsNullOrEmpty(persona)) throw new ArgumentException("persona 不可為空");
            if (amount <= 0) throw new ArgumentException($"amount 需為正整數: {amount}");

            int before = 0, after = 0;
            UCL_VoucherLedgerCommon.MutateFile(QuotaPath, () => JsonData.ParseJson("{}"), d =>
            {
                // 逐層 ensure（缺就 init，對齊 work_session.py 的 setdefault 鏈）
                if (!d.Contains("agents") || !d["agents"].IsObject) d["agents"] = JsonData.ParseJson("{}");
                var agents = d["agents"];
                if (!agents.Contains(bank) || !agents[bank].IsObject)
                    agents[bank] = JsonData.ParseJson("{\"personas\":{}}");
                var bankNode = agents[bank];
                if (!bankNode.Contains("personas") || !bankNode["personas"].IsObject)
                    bankNode["personas"] = JsonData.ParseJson("{}");
                var personas = bankNode["personas"];
                if (!personas.Contains(persona) || !personas[persona].IsObject)
                    personas[persona] = JsonData.ParseJson("{\"total_remaining\":0,\"history\":[]}");
                var pb = personas[persona];

                before = pb.Contains("total_remaining") ? pb.GetInt("total_remaining", 0) : 0;
                after = before + amount;
                pb["total_remaining"] = after;

                if (!pb.Contains("history") || !pb["history"].IsArray) pb["history"] = JsonData.ParseJson("[]");
                var e = JsonData.ParseJson("{}");
                e["id"] = $"admin-{UCL_VoucherLedgerCommon.ShortUuid()}-voucher-{persona}";
                e["granted_at"] = UCL_VoucherLedgerCommon.IsoNow();
                e["granted_by"] = "system (BankAdminPage)";
                e["kind"] = "tavern_voucher";
                e["amount"] = amount;
                e["used"] = 0;
                e["remaining"] = amount;
                e["source"] = string.IsNullOrEmpty(source) ? "" : source;
                e["ref"] = string.IsNullOrEmpty(refText) ? "" : refText;
                pb["history"].Add(e);
            });
            Debug.Log($"[TavernVoucher] grant {amount} → {bank}.personas.{persona} (total_remaining: {before} → {after})");
            return (before, after);
        }
    }
}
