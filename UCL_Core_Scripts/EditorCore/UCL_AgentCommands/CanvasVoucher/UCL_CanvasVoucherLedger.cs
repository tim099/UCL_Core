// 區塊職責：繪圖券 (Canvas voucher) 的 C# 端 canonical ledger — grant / balance / consume 單一 owner。
// 物理意義：Tim 2026-07-22 拍板 — 券發放流程收攏到 C# static class(對齊 UCL_TreasuryLedger)，根治
//          「C# spawn python canvas.py → canvas.py 的 cwd 相對 DEFAULT_CANVAS_ROOT 解析到錯的
//           AgentCommands(CardGame/AgentCommands stray) → 寫進讀不到的地方」那一整類跨 process 路徑 split bug。
//          C# 一律用 UCL_AgentCommandsPath.DataRoot 正規解析，與讀取端同源、零 cwd 依賴。python 端改透過
//          Cmd_CanvasVoucher 走 run_cmd 操作同一 owner(單寫者)，不再各自直寫檔造成 drift。
// 數值影響：讀寫 <DataRoot>/Canvas/vouchers/<persona>.json，schema 與 canvas.py 既有格式相容:
//          {persona, balance, history:[{ts,uuid,type,amount,source,ref}]}；原子 tmp+replace 寫入。
// 設計取捨：去 #if UNITY_EDITOR guard(純 file IO，無 Editor 依賴，對齊 UCL_TreasuryLedger 2026-05-13 決定)；
//          券綁 persona(Tim 拍板「券綁 persona / token 綁 bank」)。
using System;
using System.IO;
using UCL.Core.JsonLib;
using UCL.Core.EditorLib.AgentCommands.Voucher;   // 共用底層 UCL_VoucherLedgerCommon（時戳/uuid/原子讀改寫）
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.CanvasVoucher
{
    /// <summary>
    /// 繪圖券 canonical ledger — 唯一寫入 owner。發券/查餘額/用券皆走此，C# 端與 python(經 Cmd) 共用。
    /// </summary>
    public static class UCL_CanvasVoucherLedger
    {
        // 正規路徑：一律以 DataRoot 錨定(= C# 讀取端同源)，不吃任何 cwd 相對解析
        static string VouchersDir => Path.Combine(UCL_AgentCommandsPath.DataRoot, "Canvas", "vouchers");
        static string PathFor(string persona) => Path.Combine(VouchersDir, persona + ".json");

        /// <summary>讀某 persona 的繪圖券餘額（缺檔=0）。純讀不寫。</summary>
        public static int GetBalance(string persona)
        {
            if (string.IsNullOrEmpty(persona)) return 0;
            try
            {
                string p = PathFor(persona);
                if (!File.Exists(p)) return 0;
                var d = JsonData.ParseJson(File.ReadAllText(p));
                return d != null ? d.GetInt("balance", 0) : 0;
            }
            catch { return 0; }
        }

        /// <summary>發券：balance += amount，append grant history。回 (before, after)。amount 必 &gt;0。</summary>
        public static (int before, int after) Grant(string persona, int amount, string source, string refText)
        {
            if (string.IsNullOrEmpty(persona)) throw new ArgumentException("persona 不可為空");
            if (amount <= 0) throw new ArgumentException($"amount 需為正整數: {amount}");
            return Apply(persona, "grant", amount, +amount, source, refText);
        }

        /// <summary>用券：balance -= amount，append consume history。回 (before, after)。餘額不足 throw。</summary>
        public static (int before, int after) Consume(string persona, int amount, string source, string refText)
        {
            if (string.IsNullOrEmpty(persona)) throw new ArgumentException("persona 不可為空");
            if (amount <= 0) throw new ArgumentException($"amount 需為正整數: {amount}");
            int bal = GetBalance(persona);
            if (bal < amount) throw new InvalidOperationException($"繪圖券不足: persona={persona} 餘額={bal} < 欲用={amount}");
            return Apply(persona, "consume", amount, -amount, source, refText);
        }

        // 共用寫入路徑：讀(或 init 新檔) → balance += delta → append history entry → 原子寫回。
        // 機制（讀改寫 + 原子寫 + 時戳/uuid）走 UCL_VoucherLedgerCommon 共用；本函式只填繪圖券 schema。
        static (int before, int after) Apply(string persona, string type, int amount, int delta, string source, string refText)
        {
            string p = PathFor(persona);
            int before = 0, after = 0;
            UCL_VoucherLedgerCommon.MutateFile(p, () => JsonData.ParseJson("{}"), d =>
            {
                if (!d.Contains("persona")) d["persona"] = persona;
                before = d.Contains("balance") ? d.GetInt("balance", 0) : 0;
                after = before + delta;
                d["balance"] = after;
                if (!d.Contains("history") || !d["history"].IsArray) d["history"] = JsonData.ParseJson("[]");
                var e = JsonData.ParseJson("{}");
                e["ts"] = UCL_VoucherLedgerCommon.IsoNow();
                e["uuid"] = UCL_VoucherLedgerCommon.ShortUuid();
                e["type"] = type;
                e["amount"] = amount;
                e["source"] = string.IsNullOrEmpty(source) ? "" : source;
                e["ref"] = string.IsNullOrEmpty(refText) ? "" : refText;
                d["history"].Add(e);
            });
            Debug.Log($"[CanvasVoucher] {type} {amount} → {persona} (balance: {before} → {after})");
            return (before, after);
        }
    }
}
