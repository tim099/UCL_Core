// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 08/14 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
//
// 區塊職責：ArgsSpec 的**唯一 C# 執行者** —— 別名歸一與 Required 檢查只有這一份實作。
// 物理意義：ArgsSpec 以前是**沒有人執行的宣告** —— 全 repo 只有匯出器讀它，
//          dispatcher 不驗、base 不驗，於是打錯參數名一律靜默取預設值。
//          Python 端雖有一套，但它**只服務 Tavern**（summit 2026-08-14 實測：
//          非 Tavern 的 Cmd 缺參數時 client 完全不擋），而且它天生是另一個語言的另一份實作。
// 數值影響：唯讀 —— **不修改傳入的 args**。歸一後的檢視只在本類別內部存在，
//          handler 的 GetArg 仍是執行期唯一的取值規則（見下方「為什麼不 mutate」）。
//
// ⚠ 為什麼是「只驗不改」：handler 內普遍是 `GetArg(a,"x", GetArg(a,"y", ...))` 的巢狀別名解析。
//   若 dispatcher 也去改寫 args，同一組別名就有**兩份優先序**，而 UCL_CmdArgsSpec 自己的註解
//   早就警告過「順序錯不會報錯，會安靜地選錯值」。gura 2026-08-14 反對留兩份規則（她是對的），
//   summit 給了讓兩者不必打架的條件：**驗證用的解析必須與 handler 呼叫同一份實作**。
//   本類別就是那份實作 —— handler 端要收斂巢狀 GetArg 時，改成呼叫 ResolveAlias 即可，
//   在那之前「一份規則、兩個呼叫點」仍然成立（不是兩份規則）。
//
// ⚠ 為什麼沒有「推導」步驟（Tim 2026-08-14 提「能反推的欄位視為可不填」）：
//   實務上那件事已經由**不把可推導欄位放進 Required** 表達了 ——
//   例如 Tavern `post` 的 Required 是 ["room","body"]，`agent` / `persona` 都不在其中
//   （agent 由 persona 反推、persona 缺席即匿名發言）。
//   於是不需要在驗證前插一段推導：**可推導 ⇒ 不是 Required**，同一件事用宣告表達，不用機制表達。
//   若日後出現「必須可解析、但無法用 Required 表達」的欄位，那時再加，不預先造。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// ArgsSpec 的唯一 C# 執行者：別名歸一 + Required 檢查。
    /// </summary>
    public static class UCL_CmdArgsValidator
    {
        /// <summary>
        /// 依 spec 的別名表把「使用者實際帶的鍵」歸一成 canonical 名，回傳**新的**檢視（不改原 args）。
        ///
        /// <para>⚠ 別名的**宣告順序即優先序**（見 <see cref="UCL_CmdOpSpec.Aliases"/>）：
        /// canonical 名若已存在就不被別名覆蓋；多個別名指向同一 canonical 時，**先宣告的先得**。
        /// 這與 handler 端 <c>GetArg(a,"x", GetArg(a,"y", …))</c> 的巢狀順序語意相同（外層先取）。</para>
        /// </summary>
        public static Dictionary<string, string> ResolveAlias(
            IReadOnlyDictionary<string, string> iArgs, IReadOnlyDictionary<string, string> iAliases)
        {
            var aView = new Dictionary<string, string>();
            if (iArgs != null)
            {
                foreach (var kv in iArgs) aView[kv.Key] = kv.Value;
            }
            if (iAliases == null || iArgs == null) return aView;

            foreach (var kv in iAliases)
            {
                string aAlias = kv.Key, aCanonical = kv.Value;
                if (string.IsNullOrEmpty(aAlias) || string.IsNullOrEmpty(aCanonical)) continue;
                // canonical 已經有值（顯式帶了，或前一個別名先填了）→ 不覆蓋。這就是「先到先得」。
                if (aView.TryGetValue(aCanonical, out var aExisting) && !string.IsNullOrEmpty(aExisting)) continue;
                if (iArgs.TryGetValue(aAlias, out var aVal) && !string.IsNullOrEmpty(aVal))
                {
                    aView[aCanonical] = aVal;
                }
            }
            return aView;
        }

        // 區塊職責：對單一 handler 做 Required 檢查（含子 op）。
        // 物理意義：兩層 —— cmd 層的 Required 先驗，再依 `op` 參數取子 op 規格驗第二層。
        //          子 op 這一層**不是為 Tavern 特別寫的**：UCL_CmdArgsSpec.Ops 本來就是資料模型的一部分，
        //          Python 端也照它驗。用既有結構不是「從一個樣本抽象」，是照著已宣告的形狀執行。
        // 數值影響：純檢查。回 true = 通過（含「沒有 spec 可驗」的情況，見下方）。
        /// <summary>
        /// 驗證參數是否滿足 handler 宣告的 Required。通過回 true；否則 oError 帶人讀得懂的原因。
        /// </summary>
        /// <remarks>
        /// **沒有宣告 ArgsSpec（null）的 handler 一律視為通過。** 目前 39 個 Cmd 中有 37 個如此 ——
        /// 這一態的語意（「明示不驗」還是「還沒宣告」）尚未拍板（apex-one 2026-08-14 提出 Q1），
        /// 所以這裡刻意**不替它做決定**：現況是不驗，本類別維持現況，等拍板後再改這一行。
        /// 把「還沒決定」實作成「照現況」，比實作成「我猜的那個」安全。
        /// </remarks>
        public static bool Validate(UCL_AgentCommandHandlerBase iHandler,
            IReadOnlyDictionary<string, string> iArgs, out string oError)
        {
            oError = null;
            if (iHandler == null) return true;

            UCL_CmdArgsSpec aSpec = null;
            try
            {
                aSpec = iHandler.ArgsSpec;
            }
            catch (System.Exception e)
            {
                // handler 自訂 property 拋例外 —— 與匯出器同策略：視為未宣告，不讓它擋住指令執行。
                UnityEngine.Debug.LogWarning($"[CmdArgs] '{iHandler.CommandType}' 的 ArgsSpec 取值失敗，跳過驗證：{e.Message}");
                return true;
            }
            if (aSpec == null) return true;   // 未宣告 → 維持現況不驗（見 remarks）

            // ── 第一層：cmd 層 ──
            var aView = ResolveAlias(iArgs, aSpec.Aliases);
            var aMissing = MissingOf(aView, aSpec.Required);
            var aAbsent = AbsentOf(aView, aSpec.RequiredPresent);
            if (aMissing.Count > 0 || aAbsent.Count > 0)
            {
                oError = Describe(iHandler, null, aMissing, aAbsent, aView, aSpec.Aliases);
                return false;
            }

            // ── 第二層：子 op ──
            if (aSpec.Ops == null || aSpec.Ops.Count == 0) return true;
            if (!aView.TryGetValue("op", out var aOp) || string.IsNullOrEmpty(aOp)) return true;  // 沒帶 op → 交給 handler 自己報
            if (!aSpec.Ops.TryGetValue(aOp.ToLowerInvariant(), out var aOpSpec) || aOpSpec == null) return true;

            var aOpView = ResolveAlias(aView, aOpSpec.Aliases);
            var aOpMissing = MissingOf(aOpView, aOpSpec.Required);
            var aOpAbsent = AbsentOf(aOpView, aOpSpec.RequiredPresent);
            if (aOpMissing.Count > 0 || aOpAbsent.Count > 0)
            {
                oError = Describe(iHandler, aOp, aOpMissing, aOpAbsent, aOpView, aOpSpec.Aliases);
                return false;
            }
            return true;
        }

        // 區塊職責：三態診斷 —— 只在**有人已經在查這個 Cmd** 的時候才出現的一行提示。
        // 物理意義：2026-08-14 拍板 `null = 還沒宣告`。但「未宣告」有 37 個成員，
        //          如果做成一張清單掛在牆上，第三天起就沒人看 —— 那正是我們今天在殺的東西。
        //          summit 的戰績是硬證據：同一條原則，寫進憲法那天她違反四次；
        //          裝進必經路徑的 crop_review.py 首日攔下四次。**0:4 對 4:0。**
        //          所以提示不建清單，只長在路上：Cmd 失敗報告裡加一行 —— 那一刻讀報告的人
        //          正在查這個 Cmd，是唯一「提示有人會讀」的時刻，而且一次只會出現一個。
        // 數值影響：純文字，不影響任何判定。掛了 [UnvalidatedArgs] 的不提示（那是已署名的主張）。
        /// <summary>
        /// 回傳該 handler 的 ArgsSpec 三態說明；**沒有必要提示時回 null**（呼叫端不印）。
        /// </summary>
        public static string DescribeSpecState(UCL_AgentCommandHandlerBase iHandler)
        {
            if (iHandler == null) return null;
            try
            {
                if (iHandler.ArgsSpec != null) return null;   // Spec：已宣告，無話可說
            }
            catch (System.Exception) { return null; }

            var aAttr = iHandler.GetType()
                .GetCustomAttributes(typeof(UCL_UnvalidatedArgsAttribute), false)
                .FirstOrDefault() as UCL_UnvalidatedArgsAttribute;
            if (aAttr != null) return null;                  // EmptySpec：明示不驗且已署名，不吵

            // NoSpec：還沒有人表態過 —— 只在這個時刻講一次
            return $"本 Cmd **尚未宣告 ArgsSpec**，因此執行前不做參數檢查"
                 + "（打錯參數名會靜默取預設值）。若這次失敗與參數有關，"
                 + "在 handler 覆寫 `ArgsSpec` 宣告 Required 可讓它在執行前就被擋下；"
                 + "若本 Cmd 確實不需要驗證，掛 `[UCL_UnvalidatedArgs(\"理由\")]` 表態即可（掛上後本提示消失）。";
        }

        /// <summary>
        /// 缺哪些 <see cref="UCL_CmdArgsSpec.Required"/>（**空字串也算缺**）。
        /// ⚠ 這個判準對「空值是合法輸入」的參數是錯的 —— 那種參數要宣告在
        /// <see cref="UCL_CmdArgsSpec.RequiredPresent"/>，由 <see cref="AbsentOf"/> 驗（BUG-15）。
        /// </summary>
        static List<string> MissingOf(IReadOnlyDictionary<string, string> iView, string[] iRequired)
        {
            var aMissing = new List<string>();
            if (iRequired == null) return aMissing;
            foreach (string aKey in iRequired)
            {
                if (string.IsNullOrEmpty(aKey)) continue;
                if (!iView.TryGetValue(aKey, out var aVal) || string.IsNullOrEmpty(aVal)) aMissing.Add(aKey);
            }
            return aMissing;
        }

        /// <summary>
        /// 缺哪些 <see cref="UCL_CmdArgsSpec.RequiredPresent"/> —— 判準是**在場**（ContainsKey），
        /// 不看值。顯式傳空值（`--arg x=`）算在場，通過（BUG-15）。
        /// </summary>
        static List<string> AbsentOf(IReadOnlyDictionary<string, string> iView, string[] iRequiredPresent)
        {
            var aAbsent = new List<string>();
            if (iRequiredPresent == null) return aAbsent;
            foreach (string aKey in iRequiredPresent)
            {
                if (string.IsNullOrEmpty(aKey)) continue;
                if (!iView.ContainsKey(aKey)) aAbsent.Add(aKey);
            }
            return aAbsent;
        }

        // 錯誤訊息要能直接動手修：缺什麼、目前帶了什麼、可接受的別名有哪些、完整 ArgsSchema。
        // 只說「缺少參數」會讓人回頭翻文件，而翻文件這件事本身就是這道驗證想省掉的成本。
        // ⚠ 訊息不准講出 code 分不出來的事，也不准把兩種原因壓成一句（BUG-15 的附帶單）：
        //   舊版對「有給但是空值」也印「缺少必要參數：[value]」，而同一句的「你目前傳的」
        //   又把 value 列在裡面 —— **一句話說它缺、又說我傳了**。
        //   讀到那句的人第一件事一定是去檢查自己有沒有傳，而他傳了 ⇒ 訊息把他推去查錯的地方。
        //   ⇒ 三種原因各自成句，並在「空值」那種情況直接指出修法（改宣告 RequiredPresent）。
        static string Describe(UCL_AgentCommandHandlerBase iHandler, string iOp,
            List<string> iMissing, List<string> iAbsentPresent,
            IReadOnlyDictionary<string, string> iView,
            IReadOnlyDictionary<string, string> iAliases)
        {
            string aWhere = string.IsNullOrEmpty(iOp) ? iHandler.CommandType : $"{iHandler.CommandType} op={iOp}";
            var aSb = new System.Text.StringBuilder();

            var aNotGiven = iMissing.Where(k => !iView.ContainsKey(k)).ToList();
            var aGivenEmpty = iMissing.Where(k => iView.ContainsKey(k)).ToList();

            aSb.Append($"[{aWhere}] 參數檢查未過");
            if (aNotGiven.Count > 0)
                aSb.Append($"｜沒帶必要參數：[{string.Join(", ", aNotGiven)}]");
            if (aGivenEmpty.Count > 0)
                aSb.Append($"｜必要參數有帶但是空值（本 Cmd 的 Required 把空值視為缺）：[{string.Join(", ", aGivenEmpty)}]");
            if (iAbsentPresent != null && iAbsentPresent.Count > 0)
                aSb.Append($"｜必須顯式在場的參數沒帶（可以是空值）：[{string.Join(", ", iAbsentPresent)}]");
            aSb.Append($"（你目前傳的：[{string.Join(", ", iView.Keys.Where(k => !k.StartsWith("_")).OrderBy(k => k))}]）");
            if (aGivenEmpty.Count > 0)
                aSb.Append($"\n  ↳ 若這些參數的**空值是合法輸入**（例如清空一個欄位），"
                         + $"它們該宣告在 ArgsSpec 的 `RequiredPresent` 而不是 `Required`。");
            if (iAliases != null && iAliases.Count > 0)
            {
                aSb.Append($"\n  ↳ 可接受的 alias：{{{string.Join(", ", iAliases.Select(kv => $"{kv.Key}→{kv.Value}"))}}}");
            }
            if (!string.IsNullOrEmpty(iHandler.ArgsSchema))
            {
                aSb.Append($"\n  ↳ ArgsSchema：{iHandler.ArgsSchema}");
            }
            return aSb.ToString();
        }
    }
}
#endif
