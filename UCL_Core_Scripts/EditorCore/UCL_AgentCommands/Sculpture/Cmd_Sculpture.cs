// 區塊職責：Cmd_Sculpture — 3D 體積雕刻的 Cmd 入口＋扣費機制（Tim 2026-08-13 拍板分工：
//          gura 管 sculpt.py 引擎本體[幾何/渲染/快取，不碰錢]，summit 管本檔[參數/計費/付款/回傳]）。
// 物理意義：落子（box/carve）**一律走本 Cmd**——Cmd 在 Editor main thread 序列化執行（Tim 拍板：
//          無 Cmd-對-Cmd race），所以計費模型是「預授權 → 引擎執行 → 按實際結算」三段，
//          不需要 dry-run 或退款協議。引擎經 UCL_ProcessCli spawn（硬規則不裸 Process.Start）。
// 數值影響：費率 ⌈實際落地數/100⌉（box=placed_count、carve=carved_count；**只對實際落地收費**，
//          禁覆蓋 skip 掉的不收）；付款 auto 優先序＝自由時間免費像素 → 繪圖券 → token（與 canvas 同序）。
//          免費像素消費寫 Canvas/freetime/<P>.json（used += n；發放端 Cmd_FreeTime——同為 Cmd 序列化，
//          canvas.py 端另有 payment lock，跨 process 窗口 Tim 拍板不設防）。view/stats 免費 pass-through。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Sculpture
{
    using Awakening;

    /// <summary>
    /// 3D 雕刻 Cmd（扣費入口）。落子一律走本 Cmd，不直跑 sculpt.py（直跑＝繞過計費與序列化）。
    /// <code>
    /// run_cmd.py run Sculpture --arg op=box   --arg persona=&lt;P&gt; --arg x1=10 --arg x2=19 --arg y1=10 --arg y2=19 --arg z1=1 --arg z2=5 [--arg color=19] [--arg pay=auto]
    /// run_cmd.py run Sculpture --arg op=carve --arg persona=&lt;P&gt; --arg x1=.. .. z2=..
    /// run_cmd.py run Sculpture --arg op=view  [--arg region=0..50,0..50,0..20] [--arg exclude_color=0,3]
    /// run_cmd.py run Sculpture --arg op=stats
    /// </code>
    /// </summary>
    public class Cmd_Sculpture : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Sculpture";

        public override string ShortDescription =>
            "3D 體積雕刻（op=box/carve/view/stats）— box/carve 計費 ⌈實際落地/100⌉（免費像素→繪圖券→token），view/stats 免費。落子一律走本 Cmd。";

        public override string ArgsSchema =>
            "op=box|carve|view|stats (必填) | persona=<name> — box/carve 必填 | " +
            "x1..z2=<0-255> — box/carve 必填（AABB 兩角） | color=<0-255> — box 選填(預設 19) | " +
            "pay=auto|freetime|voucher|token — 選填(預設 auto：免費像素→券→token) | " +
            "region=<x1..x2,y1..y2,z1..z2> / exclude_color=<c,c,..> — view 選填 | " +
            "費率：⌈實際落地數/100⌉，禁覆蓋 skip 不收費；回傳落檔 letters/<persona>/_sculpture_<op>.md";

        public override string ExampleArgs => "op=stats";

        public override string HelpURL => "ucl_core:Docs~/zh-Hant/Plan/Plan_Sculpture_3D.md";

        const string PROC_TAG = "sculpture_engine";
        const int ENGINE_TIMEOUT_MS = 120000;
        /// <summary>計費粒度：每 100 voxel 收 1 單位（Tim 拍板費率 ⌈V/100⌉ 的 V 改為實際落地數）。</summary>
        public const int VOXELS_PER_UNIT = 100;
        /// <summary>單次 box 體積上限（與 sculpt.py 引擎端同值 —— 兩端對齊義務）。</summary>
        public const int MAX_BOX_VOLUME = 1000000;

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string aOp = GetArg(args, "op", "").Trim().ToLowerInvariant();
            switch (aOp)
            {
                case "box":
                case "carve": await OpPlace(aOp, args, token); return;
                case "view":
                case "stats": await OpReadOnly(aOp, args, token); return;
                default:
                    throw new Exception($"[Sculpture] op 必為 box|carve|view|stats（got '{aOp}'）。ArgsSchema: {ArgsSchema}");
            }
        }

        // ===========================================================
        // 區塊：box / carve — 三段式：預授權（體積上限驗餘額）→ 引擎執行 → 按實際結算
        // 物理意義：引擎沒有 dry-run（也不需要——Cmd 序列化），但錢不能先花後驗：
        //          預授權用「這一刀最多可能花多少」（⌈clamp後體積/100⌉）擋住餘額不足，
        //          結算用引擎回報的實際落地數 —— 帳單跟著事實走，不跟著意圖走。
        // ===========================================================
        async UniTask OpPlace(string iOp, Dictionary<string, string> iArgs, CancellationToken iToken)
        {
            string aPersona = GetArg(iArgs, "persona", "").Trim();
            if (string.IsNullOrEmpty(aPersona))
                throw new Exception("[Sculpture] box/carve 需要 --arg persona=<name>（錢認 persona 的券與 bank，不能用猜的）");

            string aPath = PayloadPath(aPersona, iOp);
            var aR = new StringBuilder();
            aR.AppendLine($"# Sculpture op={iOp} persona={aPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            // 幾何參數（與 sculpt.py 同語意：兩角任意順序、clamp 0..255）
            if (!TryGetInt(iArgs, "x1", out int aX1) || !TryGetInt(iArgs, "x2", out int aX2)
                || !TryGetInt(iArgs, "y1", out int aY1) || !TryGetInt(iArgs, "y2", out int aY2)
                || !TryGetInt(iArgs, "z1", out int aZ1) || !TryGetInt(iArgs, "z2", out int aZ2))
            {
                aR.AppendLine("## blocked\n- reason: 幾何參數缺漏 —— box/carve 需要 x1 x2 y1 y2 z1 z2 六個整數（0-255）");
                WritePayload(aPath, aR.ToString());
                throw new Exception($"[Sculpture] op={iOp} 幾何參數缺漏（詳見 {aPath}）");
            }
            int aVolume = ClampedVolume(ref aX1, ref aX2, ref aY1, ref aY2, ref aZ1, ref aZ2);
            if (iOp == "box" && aVolume > MAX_BOX_VOLUME)
            {
                aR.AppendLine($"## blocked\n- reason: 單次 box 體積上限 {MAX_BOX_VOLUME:N0} voxels（本次 {aVolume:N0}）—— 拆多刀");
                WritePayload(aPath, aR.ToString());
                throw new Exception($"[Sculpture] op=box 體積超限（詳見 {aPath}）");
            }

            // 付款來源解析（bank 由 persona registry 反查 —— 錢認 agent，說話認 persona）
            string aPay = GetArg(iArgs, "pay", "auto").Trim().ToLowerInvariant();
            var aMeta = UCL_RegistryMeta.LoadFromFile(UCL_AwakeningService.RegistryMetaPath);
            var aLock = UCL_AwakeningService.ReadLock(aPersona);
            string aBank = aLock != null && !string.IsNullOrEmpty(aLock.bank_account)
                ? aLock.bank_account
                : UCL_AwakeningService.ResolveBankAccount(aMeta, UCL_AwakeningService.NormalizeAgent(aMeta, GetArg(iArgs, "agent", "")));

            // 預授權：最壞情況費用 ≤ 三通道可用量（依 pay 模式取用哪些通道）
            int aMaxUnits = CeilDiv(aVolume, VOXELS_PER_UNIT);
            int aFreeAvail = FreePixelsAvailable(aPersona);
            int aVoucherAvail = CanvasVoucher.UCL_CanvasVoucherLedger.GetBalance(aPersona);
            int aTokenAvail = string.IsNullOrEmpty(aBank) ? 0 : Treasury.UCL_TreasuryLedger.GetBalance(aBank);
            int aAuthorized = aPay switch
            {
                "freetime" => aFreeAvail,
                "voucher" => aVoucherAvail,
                "token" => aTokenAvail,
                _ => aFreeAvail + aVoucherAvail + aTokenAvail,
            };
            if (aAuthorized < aMaxUnits)
            {
                aR.AppendLine("## blocked");
                aR.AppendLine($"- reason: 預授權不足 —— 本刀最壞費用 {aMaxUnits} 單位（體積 {aVolume:N0}/⌈{VOXELS_PER_UNIT}⌉），" +
                              $"pay={aPay} 可用 {aAuthorized}（免費像素 {aFreeAvail}＋券 {aVoucherAvail}＋token {aTokenAvail} 依模式取用）");
                aR.AppendLine("- how: 縮小範圍、換 pay 模式、或先賺錢 —— 引擎未執行，未扣任何費用");
                WritePayload(aPath, aR.ToString());
                throw new Exception($"[Sculpture] op={iOp} 預授權不足（詳見 {aPath}）");
            }

            // 引擎執行（spawn sculpt.py —— 落子的唯一通道是本 Cmd，引擎輸出 JSON 是結算依據）
            string aScript = ResolveEngineScript();
            if (aScript == null)
            {
                aR.AppendLine("## blocked\n- reason: 解析不到 sculpt.py（CorePath 空或檔案不存在）");
                WritePayload(aPath, aR.ToString());
                throw new Exception($"[Sculpture] 引擎不存在（詳見 {aPath}）");
            }
            string aColor = GetArg(iArgs, "color", "19");
            string aCliArgs = $"\"{aScript}\" {iOp} --x1 {aX1} --x2 {aX2} --y1 {aY1} --y2 {aY2} --z1 {aZ1} --z2 {aZ2} --persona \"{aPersona}\""
                              + (iOp == "box" ? $" --color {aColor}" : "");
            var (aExit, aSo, aSe) = UCL_ProcessCli.Run("python", aCliArgs, UCL_RepoPath.RepoRoot,
                PROC_TAG, nameof(Cmd_Sculpture), ENGINE_TIMEOUT_MS);
            var aResult = ParseEngineJson(aSo);
            if (aExit != 0 || aResult == null)
            {
                aR.AppendLine($"## blocked\n- reason: 引擎執行失敗（exit={aExit}）—— 未扣任何費用");
                aR.AppendLine("```\n" + (aSo ?? "").Trim() + (string.IsNullOrEmpty(aSe) ? "" : "\n── stderr ──\n" + aSe.Trim()) + "\n```");
                WritePayload(aPath, aR.ToString());
                throw new Exception($"[Sculpture] op={iOp} 引擎失敗（詳見 {aPath}）");
            }

            // 結算：只對實際落地收費（禁覆蓋 skip 不收）
            int aActual = ReadInt(aResult, iOp == "box" ? "placed_count" : "carved_count");
            int aSkipped = ReadInt(aResult, "skipped_count");
            string aEventFile = ReadStr(aResult, "event_file");
            int aCharge = aActual > 0 ? CeilDiv(aActual, VOXELS_PER_UNIT) : 0;
            var (aUsedFree, aUsedVoucher, aUsedToken) = ConsumePayment(aPersona, aBank, aCharge, aPay, aEventFile);

            aR.AppendLine("## result（引擎回報＝結算依據）");
            aR.AppendLine($"- {(iOp == "box" ? "placed" : "carved")}: **{aActual}**{(aSkipped > 0 ? $"（skip {aSkipped} —— 禁覆蓋，不收費）" : "")} / 體積 {aVolume:N0}");
            aR.AppendLine($"- charged: **{aCharge} 單位**（⌈{aActual}/{VOXELS_PER_UNIT}⌉；帳單跟著事實走，不跟著意圖走）");
            aR.AppendLine($"- pay_breakdown: freetime={aUsedFree} voucher={aUsedVoucher} token={aUsedToken}（pay={aPay}）");
            aR.AppendLine($"- event: `{aEventFile}`");
            aR.AppendLine("## next");
            aR.AppendLine($"- 看成品：run_cmd.py run Sculpture --arg op=view [--arg region=…] [--arg exclude_color=…]（免費）");
            aR.AppendLine("- 驗收慣例：宣稱含內部結構的作品，交件附外觀＋室內（region 裁進去）各一張。");
            WritePayload(aPath, aR.ToString());
            Debug.Log($"[Sculpture] op={iOp} {aActual} voxels, charged {aCharge}（f{aUsedFree}/v{aUsedVoucher}/t{aUsedToken}） → {aPath}");
        }

        // ===========================================================
        // 區塊：view / stats — 免費 pass-through（觀測不收費：驗收管道要零門檻）
        // ===========================================================
        async UniTask OpReadOnly(string iOp, Dictionary<string, string> iArgs, CancellationToken iToken)
        {
            await UniTask.Yield();
            string aPersona = GetArg(iArgs, "persona", "").Trim();
            string aWho = string.IsNullOrEmpty(aPersona) ? "_shared" : aPersona;
            string aPath = PayloadPath(aWho, iOp);
            string aScript = ResolveEngineScript();
            if (aScript == null) throw new Exception("[Sculpture] 解析不到 sculpt.py");

            string aCli = $"\"{aScript}\" {iOp}";
            if (iOp == "view")
            {
                string aRegion = GetArg(iArgs, "region", "");
                string aExclude = GetArg(iArgs, "exclude_color", "");
                if (!string.IsNullOrEmpty(aRegion)) aCli += $" --region \"{aRegion}\"";
                if (!string.IsNullOrEmpty(aExclude)) aCli += $" --exclude-color \"{aExclude}\"";
            }
            var (aExit, aSo, aSe) = UCL_ProcessCli.Run("python", aCli, UCL_RepoPath.RepoRoot,
                PROC_TAG, nameof(Cmd_Sculpture), ENGINE_TIMEOUT_MS);

            var aR = new StringBuilder();
            aR.AppendLine($"# Sculpture op={iOp}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();
            aR.AppendLine("```\n" + (aSo ?? "").Trim() + (string.IsNullOrEmpty(aSe) ? "" : "\n── stderr ──\n" + aSe.Trim()) + "\n```");
            WritePayload(aPath, aR.ToString());
            if (iOp == "view")
            {
                string aPng = Path.Combine(UCL_AgentCommandsPath.DataRoot, "Sculpture", "_last_view.png");
                if (File.Exists(aPng)) UCL_AgentCommandRunner.ReportOutputFile(aPng);
            }
            if (aExit != 0) throw new Exception($"[Sculpture] op={iOp} 引擎失敗（詳見 {aPath}）");
            Debug.Log($"[Sculpture] op={iOp} 完成 → {aPath}");
        }

        // ===========================================================
        // 區塊：付款消費（結算段）— 順序：免費像素 → 繪圖券 → token（canvas 同序）
        // 物理意義：預授權已保證夠付，這裡照優先序逐通道扣；useRef 帶引擎 event 檔名 —— 對帳時
        //          錢與 voxel 事件互相可追。券走 UCL_CanvasVoucherLedger（C# canonical owner）、
        //          token 走 UCL_TreasuryLedger（callerAgentId=system：Editor 代跑，帳戶隔離由
        //          persona→bank 反查保證）、免費像素寫 Canvas/freetime/<P>.json used 欄
        //          （與 canvas.py 消費端同 schema —— 兩端對齊義務）。
        // ===========================================================
        (int usedFree, int usedVoucher, int usedToken) ConsumePayment(
            string iPersona, string iBank, int iCharge, string iPay, string iEventRef)
        {
            if (iCharge <= 0) return (0, 0, 0);
            int aRemain = iCharge;
            int aFree = 0, aVoucher = 0, aToken = 0;
            string aRef = $"sculpture:{Path.GetFileName(iEventRef ?? "")}";

            bool aUseFree = iPay == "auto" || iPay == "freetime";
            bool aUseVoucher = iPay == "auto" || iPay == "voucher";
            bool aUseToken = iPay == "auto" || iPay == "token";

            if (aUseFree && aRemain > 0)
            {
                aFree = Math.Min(aRemain, FreePixelsAvailable(iPersona));
                if (aFree > 0) { ConsumeFreePixels(iPersona, aFree, aRef); aRemain -= aFree; }
            }
            if (aUseVoucher && aRemain > 0)
            {
                int aBal = CanvasVoucher.UCL_CanvasVoucherLedger.GetBalance(iPersona);
                aVoucher = Math.Min(aRemain, aBal);
                if (aVoucher > 0)
                {
                    CanvasVoucher.UCL_CanvasVoucherLedger.Consume(iPersona, aVoucher, "sculpture_place", aRef);
                    aRemain -= aVoucher;
                }
            }
            if (aUseToken && aRemain > 0)
            {
                Treasury.UCL_TreasuryLedger.Debit(iBank, aRemain, "sculpture_place", aRef,
                    $"3D sculpture {iCharge} unit(s) by {iPersona}", "system", UCL_AgentCommandRunner.CurrentCmdId);
                aToken = aRemain;
                aRemain = 0;
            }
            if (aRemain > 0)   // 預授權有驗過，走到這裡＝通道間狀態被外力改了 —— 顯式炸，不靜默欠帳
                throw new Exception($"[Sculpture] 結算短付 {aRemain} 單位（預授權後餘額被變動？）—— 已扣 f{aFree}/v{aVoucher}/t{aToken}，請對帳 {aRef}");
            return (aFree, aVoucher, aToken);
        }

        // ── 免費像素（Canvas/freetime/<P>.json；schema 對齊 canvas.py load_freetime／Cmd_FreeTime）──
        static string FreePixelPath(string iPersona)
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "Canvas", "freetime", $"{iPersona}.json");
        static string FreeSessionPath(string iPersona)
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "FreeTime", "sessions", $"{iPersona}.json");

        static int FreePixelsAvailable(string iPersona)
        {
            try
            {
                if (!File.Exists(FreeSessionPath(iPersona)) || !File.Exists(FreePixelPath(iPersona))) return 0;
                var aSession = JsonData.ParseJson(File.ReadAllText(FreeSessionPath(iPersona), Encoding.UTF8));
                if (aSession == null || !ReadBool(aSession, "active")) return 0;   // 軟截止：只認 active 旗標
                var aFt = JsonData.ParseJson(File.ReadAllText(FreePixelPath(iPersona), Encoding.UTF8));
                if (aFt == null || ReadStr(aFt, "session_id") != ReadStr(aSession, "session_id")) return 0;   // 額度不跨場
                return Math.Max(0, ReadInt(aFt, "granted") - ReadInt(aFt, "used"));
            }
            catch (Exception) { return 0; }
        }

        static void ConsumeFreePixels(string iPersona, int iCount, string iRef)
        {
            var aFt = JsonData.ParseJson(File.ReadAllText(FreePixelPath(iPersona), Encoding.UTF8));
            aFt["used"] = new JsonData(ReadInt(aFt, "used") + iCount);
            if (!aFt.Contains("history")) { var aH = new JsonData(); aH.Init(JsonType.List); aFt["history"] = aH; }
            var aEntry = new JsonData();
            aEntry["ts"] = new JsonData(UCL_AwakeningService.NowIso());
            aEntry["ref"] = new JsonData(iRef ?? "");
            aEntry["session_id"] = aFt.Contains("session_id") ? aFt["session_id"] : new JsonData("");
            aEntry["count"] = new JsonData(iCount);
            aFt["history"].Add(aEntry);
            string aTmp = FreePixelPath(iPersona) + ".tmp";
            File.WriteAllText(aTmp, aFt.ToJsonBeautify(), new UTF8Encoding(false));
            if (File.Exists(FreePixelPath(iPersona))) File.Delete(FreePixelPath(iPersona));
            File.Move(aTmp, FreePixelPath(iPersona));
        }

        // ── 小工具 ──
        static string ResolveEngineScript()
        {
            string aCoreRel = UCL_EditorPath.CorePath;
            if (string.IsNullOrEmpty(aCoreRel)) return null;
            string aScript = Path.GetFullPath(Path.Combine(
                UCL_RepoPath.UnityProjectRoot, aCoreRel, "Tools~/AgentCommands/sculpt.py"));
            return File.Exists(aScript) ? aScript : null;
        }

        /// <summary>從引擎 stdout 撈第一個頂層 JSON 物件（引擎成功時印 pretty JSON；失敗印人話 → 回 null）。</summary>
        static JsonData ParseEngineJson(string iStdout)
        {
            if (string.IsNullOrEmpty(iStdout)) return null;
            int aStart = iStdout.IndexOf('{');
            int aEnd = iStdout.LastIndexOf('}');
            if (aStart < 0 || aEnd <= aStart) return null;
            try { return JsonData.ParseJson(iStdout.Substring(aStart, aEnd - aStart + 1)); }
            catch (Exception) { return null; }
        }

        static int ClampedVolume(ref int x1, ref int x2, ref int y1, ref int y2, ref int z1, ref int z2)
        {
            void Norm(ref int a, ref int b) { if (a > b) (a, b) = (b, a); a = Math.Max(0, a); b = Math.Min(255, b); }
            Norm(ref x1, ref x2); Norm(ref y1, ref y2); Norm(ref z1, ref z2);
            return (x2 - x1 + 1) * (y2 - y1 + 1) * (z2 - z1 + 1);
        }

        static int CeilDiv(int a, int b) => (a + b - 1) / b;

        static bool TryGetInt(Dictionary<string, string> iArgs, string iKey, out int oVal)
            => int.TryParse(iArgs != null && iArgs.TryGetValue(iKey, out var s) ? s : "", out oVal);

        static string ReadStr(JsonData iJd, string iKey) => iJd != null && iJd.Contains(iKey) ? iJd[iKey].ToString() : "";
        static int ReadInt(JsonData iJd, string iKey) { try { return iJd != null && iJd.Contains(iKey) ? int.Parse(iJd[iKey].ToString()) : 0; } catch { return 0; } }
        static bool ReadBool(JsonData iJd, string iKey) { try { return iJd != null && iJd.Contains(iKey) && (bool)iJd[iKey]; } catch { return false; } }

        static string PayloadPath(string iPersona, string iOp)
            => Path.Combine(UCL_AwakeningService.LettersDir, iPersona, $"_sculpture_{iOp}.md");

        static void WritePayload(string iPath, string iReport)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iPath));
                File.WriteAllText(iPath, iReport, new UTF8Encoding(false));
                UCL_AgentCommandRunner.ReportOutputFile(iPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Sculpture] 回傳落檔失敗 {iPath}: {e.Message}");
            }
        }
    }
}
#endif
