// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 08/14 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
//
// 區塊職責：Cmd schema 產線的常駐回歸自測 —— 一次驗完「來源 → 產物」這半條鏈。
// 物理意義：這條鏈的每一環以前都有人各自驗過，**但環環相接沒有被一次走完**
//          （summit 2026-08-14 提出的缺口）。缺口難補的原因是：要驗它得改某個現役 Cmd 的
//          ArgsSpec，而那會影響正在用該 Cmd 的同事。本 Cmd 的存在就是為了解掉那個兩難 ——
//          **它自己宣告一份與眾不同的 ArgsSpec，所以驗它等於驗整條產線，而動它不影響任何人。**
// 數值影響：唯讀。不寫產物、不改任何設定；失敗時 throw（讓 Runner 落錯誤報告檔）。
//
// ⚠ 本 Cmd 刻意**不宣稱驗完整條鏈**。射程邊界寫在報告裡（見 ExecuteAsync 末段）：
//   Python 端的**參數**預檢目前只服務 Tavern（summit 2026-08-14 實測），
//   所以「產物 → 預檢跟上」那一環不在 C# 這側的射程內。
//   把驗不到的環境明講出來，比報一個漂亮的全綠重要 —— 一份宣稱驗完全部的報告，
//   跟一份真的驗完全部的報告，在燈全亮的房間裡長得一模一樣。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Agent Command：Cmd schema 產線自測 —— 比對「當下由 C# 生成的 schema」與「磁碟上的產物」。
    /// </summary>
    public class Cmd_SchemaSelfTest : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "SchemaSelfTest";

        public override string ShortDescription =>
            "Cmd schema 產線回歸自測：逐字比對「當下生成的 schema」與磁碟產物，驗證自動同步真的把來源變動送到產物。";

        public override string ArgsSchema =>
            "probe=任意非空字串（required —— 本參數存在的唯一理由是讓本 Cmd 擁有一份可辨識的 ArgsSpec，" +
            "產物裡看得到它 = 匯出器有正確處理 spec；也順便當未來 dispatcher 參數驗證的活體樣本） | " +
            "verbose=true 時在報告附上首個差異行的前後文（選填，預設 false）";

        public override string ExampleArgs => "probe=1";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/Plan/Plan_AgentCmd_Schema_Reflection_Export.md";

        // 區塊職責：本 Cmd 的機器可讀規格 —— **同時是被測物**。
        // 物理意義：required 一個、alias 一組，形狀刻意與其他 Cmd 不同。
        //          產物裡出現這一組 = 匯出器有正確走訪 ArgsSpec；不見了 = 產線某處吃掉了它。
        // 數值影響：alias 宣告順序即優先序（見 UCL_CmdArgsSpec 註解）；此處只有一個 alias，無歧義。
        public override UCL_CmdArgsSpec ArgsSpec => new UCL_CmdArgsSpec
        {
            Required = new[] { "probe" },
            Aliases = new Dictionary<string, string> { { "p", "probe" } },
        };

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            string probe = GetArg(args, "probe", GetArg(args, "p", string.Empty)).Trim();
            bool verbose = GetArg(args, "verbose", "false").Trim().ToLowerInvariant() == "true";

            var sb = new StringBuilder();
            var failures = new List<string>();

            // ── 前置：預檢停用時不能報「通過」──
            // 停用 = 產物不再更新，此時比對必然失敗，但那不是產線壞了。
            // 這兩種狀況必須分開報，否則「停用中」會被讀成「產線壞了」而有人去修沒壞的東西。
            if (UCL_CmdSchemaExporter.PreflightDisabled)
            {
                sb.AppendLine("# ⏸ SchemaSelfTest 跳過（預檢停用中）");
                sb.AppendLine();
                sb.AppendLine("- 本機的 schema 預檢處於停用狀態，產物**刻意不再更新** —— 比對無意義。");
                sb.AppendLine($"- 旗標檔：`{UCL_CmdSchemaExporter.DisableFlagPath}`");
                sb.AppendLine();
                sb.AppendLine("⚠ 這是**未檢查**，不是通過。要驗請先重新啟用預檢。");
                ChatTavern.UCL_ChatTavernRender.WriteLastOp(sb.ToString());
                Debug.LogWarning("[Cmd:SchemaSelfTest] skipped — schema preflight disabled.");
                return;
            }

            // ── 斷言 1：產物存在 ──
            string path = UCL_CmdSchemaExporter.SchemaPath;
            bool exists = File.Exists(path);
            if (!exists) failures.Add($"產物不存在：`{path}`（AutoSync 應在編譯後立刻生成，缺檔代表那條路沒走到）");

            // ── 斷言 2：磁碟產物 == 當下由 C# 生成的內容（逐字）──
            // 為什麼比全文而不是比 hash：比 hash 只證明「來源沒變」，
            // 比全文同時證明「來源沒變」**且**「產物的每個欄位都是當下這份 C# 生出來的」——
            // 匯出器如果漏掉某個 cmd 的 ArgsSpec，hash 照樣相符（hash 算的是**來源檔**不是產物內容），
            // 那種漏法只有全文比對抓得到。
            string expected = UCL_CmdSchemaExporter.BuildSchemaJson();
            string actual = exists ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
            // 換行正規化：產物寫檔與 BuildSchemaJson 都用 \n，但檔案經過某些工具往返可能變 \r\n。
            // 這一層是刻意的容忍 —— 換行差異不是產線錯誤，拿它報 fail 會製造假警報。
            string expectedN = expected.Replace("\r\n", "\n");
            string actualN = actual.Replace("\r\n", "\n");
            int diffLine = -1;
            if (exists && expectedN != actualN)
            {
                var e = expectedN.Split('\n');
                var a = actualN.Split('\n');
                int n = Mathf.Min(e.Length, a.Length);
                for (int i = 0; i < n; i++)
                {
                    if (e[i] != a[i]) { diffLine = i; break; }
                }
                if (diffLine < 0) diffLine = n;   // 前綴相同、長度不同
                failures.Add($"產物與當下生成內容不符（首個差異在第 {diffLine + 1} 行）"
                           + " —— 代表自動同步沒有把來源變動送到產物");
            }

            // ── 斷言 3：本 Cmd 自己的 spec 出現在產物裡 ──
            // 這一條是「匯出器有沒有正確走訪 ArgsSpec」的活體樣本：本 Cmd 的 required/alias
            // 形狀與其他 Cmd 不同，只要它在產物裡逐字出現，就證明 spec 那條路是通的。
            bool specFound = exists
                && actual.Contains("\"SchemaSelfTest\"")
                && actual.Contains("\"probe\"")
                && actual.Contains("\"p\"");
            if (exists && !specFound)
            {
                failures.Add("產物內找不到本 Cmd 的 ArgsSpec（SchemaSelfTest / probe / p）"
                           + " —— 匯出器可能沒走訪到 ArgsSpec");
            }

            // ── 報告 ──
            bool pass = failures.Count == 0;
            sb.AppendLine(pass ? "# ✅ SchemaSelfTest 通過" : "# ❌ SchemaSelfTest 失敗");
            sb.AppendLine();
            sb.AppendLine($"- probe 參數：`{probe}`");
            sb.AppendLine($"- 產物：`{path}`");
            sb.AppendLine($"- 產物存在：{(exists ? "是" : "**否**")}");
            sb.AppendLine($"- 逐字比對：{(exists ? (diffLine < 0 ? "相符" : $"**不符（第 {diffLine + 1} 行）**") : "—")}");
            sb.AppendLine($"- 本 Cmd 的 ArgsSpec 在產物內：{(exists ? (specFound ? "是" : "**否**") : "—")}");
            sb.AppendLine();

            if (!pass)
            {
                sb.AppendLine("## 失敗項");
                foreach (string f in failures) sb.AppendLine($"- {f}");
                sb.AppendLine();
                sb.AppendLine("修法：`senate ucmd run ExportCmdSchema`，或重新編譯一次讓 AutoSync 補上。");
                sb.AppendLine();
                if (verbose && diffLine >= 0)
                {
                    var e = expectedN.Split('\n');
                    var a = actualN.Split('\n');
                    sb.AppendLine("## 首個差異");
                    sb.AppendLine("```");
                    sb.AppendLine($"expected[{diffLine + 1}]: {(diffLine < e.Length ? e[diffLine] : "(超出範圍)")}");
                    sb.AppendLine($"actual  [{diffLine + 1}]: {(diffLine < a.Length ? a[diffLine] : "(超出範圍)")}");
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }

            // 區塊職責：**明講驗不到的那一環** —— 報告的射程邊界。
            // 物理意義：完整鏈是「來源改動 → 自動同步 → 產物更新 → 預檢跟上」。
            //          本 Cmd 走完前三環；第四環在 Python 端，而且**只服務 Tavern**
            //          （summit 2026-08-14 實測：非 Tavern 的 Cmd 缺參數時 client 完全不擋）。
            //          不寫這段的話，一份「✅ 通過」會被讀成整條鏈都驗過了。
            sb.AppendLine("## 射程邊界（本 Cmd **驗不到**的部分）");
            sb.AppendLine();
            sb.AppendLine("- **「產物 → Python 預檢跟上」這一環不在本 Cmd 射程內。** Python 端的**參數**預檢");
            sb.AppendLine("  目前只服務 Tavern（`precheck_cmd_type` 那層才是全域的，它管的是 unknown type）。");
            sb.AppendLine("- 要驗那一環，手動跑一次故意缺參數的 Tavern 呼叫，確認 client 端擋下且未進 queue：");
            sb.AppendLine("  ```");
            sb.AppendLine("  senate ucmd run Tavern --arg op=post --arg persona=<你>   # 缺 room/agent/body");
            sb.AppendLine("  # 期望：✗ 預檢失敗、exit=2、輸出無 Submitted:");
            sb.AppendLine("  ```");
            sb.AppendLine("- 本 Cmd 通過**只代表**：來源與產物一致，且 ArgsSpec 有正確落進產物。");

            ChatTavern.UCL_ChatTavernRender.WriteLastOp(sb.ToString());

            if (!pass)
            {
                // throw 而不是只寫報告：Cmd 的 result 檔是 client 唯一會判讀的東西，
                // 只寫 _last_op.md 的話 client 會拿到 Success，而那正是本 Cmd 在防的病。
                throw new System.Exception($"[SchemaSelfTest] 失敗 {failures.Count} 項：{string.Join(" / ", failures)}");
            }

            Debug.Log($"[Cmd:SchemaSelfTest] PASS — 產物與當下生成內容逐字相符，ArgsSpec 已落進產物（probe={probe}）。");
        }
    }
}
#endif
