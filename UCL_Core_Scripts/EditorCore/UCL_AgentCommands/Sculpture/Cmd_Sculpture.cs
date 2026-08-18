// 區塊職責：Cmd_Sculpture — 3D 體積雕刻的 Cmd 入口＋扣費機制（Tim 2026-08-13 拍板分工：
//          gura 管 sculpt.py 引擎本體[幾何/渲染/快取，不碰錢]，summit 管本檔[參數/計費/付款/回傳]）。
// 物理意義：落子（box/carve）**一律走本 Cmd**——Cmd 在 Editor main thread 序列化執行（Tim 拍板：
//          無 Cmd-對-Cmd race），所以計費模型是「預授權 → 引擎執行 → 按實際結算」三段，
//          不需要 dry-run 或退款協議。引擎經 UCL_ProcessCli spawn（硬規則不裸 Process.Start）。
// 數值影響：費率 ⌈實際落地數/100⌉（box=placed_count、carve=carved_count、stamp*=placed_count；
//          **只對實際落地收費**，禁覆蓋 skip 掉的不收）；
//          付款 auto 優先序＝自由時間免費像素 → 繪圖券 → token（與 canvas 同序）。
// 貼圖兩 op（Tim 2026-08-14 拍板：2D→3D 全面改道走 RGBA PNG，透明像素不畫入）：
//          stamp2d = 2D 共用畫布某區域（引擎自渲預覽 PNG 再吃它）；stampimg = 任意 PNG。
//          預授權取「圖面積 × thickness」＝這一刀的**最壞**費用（實際只有非透明像素會落地，
//          所以帳單必然 ≤ 預授權）；stampimg 的面積由本檔直接讀 PNG IHDR 得到，不靠呼叫端申報
//          —— 呼叫端申報的數字不能拿來當自己的餘額閘門。
//          免費像素消費寫 Canvas/freetime/<P>.json（used += n；發放端 Cmd_FreeTime——同為 Cmd 序列化，
//          canvas.py 端另有 payment lock，跨 process 窗口 Tim 拍板不設防）。
// 切片 slice（Tim 2026-08-14 追加）：region 內的 voxel **顏色原樣當像素色**輸出成 RGBA PNG
//          （不打光、不等角投影、不混色），空的地方 alpha 0；厚度＝法線軸跨度，>1 時前覆蓋後。
//          與 stamp 共用同一組軸映射 ⇒ 切出來的圖原樣貼回同一個 at 會還原（往返已驗）。
//          view/slice/stats 免費 pass-through（觀測是驗收管道，零門檻）。
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
    /// run_cmd.py run Sculpture --arg op=slice --arg region=212..223,212..223,210..211 [--arg axis=z+] [--arg out=&lt;png&gt;]
    /// run_cmd.py run Sculpture --arg op=stamp2d  --arg persona=&lt;P&gt; --arg src_x1=1000 --arg src_y1=1000 --arg src_x2=1008 --arg src_y2=1005 --arg at=10,10,10 [--arg facing=z+] [--arg thickness=1] [--arg expect_pixels=37]
    /// run_cmd.py run Sculpture --arg op=stampimg --arg persona=&lt;P&gt; --arg png=&lt;路徑&gt; --arg at=10,10,10 [--arg resize=64,64] [--arg expect_pixels=1234]
    /// </code>
    /// </summary>
    public class Cmd_Sculpture : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Sculpture";

        public override string ShortDescription =>
            "3D 體積雕刻（op=box/carve/stamp2d/stampimg/slice/view/stats）— 落子計費 ⌈實際落地/100⌉（免費像素→繪圖券→token），view/slice/stats 免費。落子一律走本 Cmd。貼圖吃 RGBA PNG、切片吐 RGBA PNG（voxel 色原樣當像素色），兩者可往返。";

        public override string ArgsSchema =>
            "op=box|carve|stamp2d|stampimg|slice|view|stats (必填) | persona=<name> — 落子類必填 | " +
            "x1..z2=<0-255> — box/carve 必填（AABB 兩角） | color=<0-255> — box 選填(預設 19) | " +
            "src_x1/src_y1/src_x2/src_y2=<0-2047> — stamp2d 必填（2D 畫布來源區域兩角） | " +
            "png=<PNG 路徑> — stampimg 必填 | resize=<W,H> — stampimg 選填(NEAREST) | " +
            "at=<x,y,z> — stamp 類必填（圖左上角貼在哪） | facing=x+|x-|y+|y-|z+|z- — 選填(預設 z+，貼片法線) | " +
            "thickness=<層數> — 選填(預設 1) | overwrite=true — 選填(預設跳過既有 voxel) | " +
            "expect_pixels=<N> — 選填但**強烈建議**：預覽印出的非透明像素數，對不上即拒絕（不帶＝放棄這道保護） | " +
            "exhibit_id=<作品ID> — 選填：貼完自動登錄/擴充展品，region 依實際落地 voxel 反推（多刀 union 不覆蓋）；" +
            "exhibit_title / exhibit_desc / exhibit_margin=<格數,預設2> — 選填 | " +
            "alpha_threshold=<0-255> — 選填(預設 128) | allow_clip=true — 選填(預設越界即拒絕，不靜默只貼一角) | " +
            "pay=auto|freetime|voucher|token — 選填(預設 auto：免費像素→券→token) | " +
            "region=<x1..x2,y1..y2,z1..z2> / exclude_color=<c,c,..> / exhibit=<展品ID> / light_dir=<x,y,z> / ambient=<0-1> / zoom=<倍率;省略=自動縮放> — view 選填 | " +
            "region=<x1..x2,y1..y2,z1..z2> 必填 / axis=x+|x-|y+|y-|z+|z-(預設z+，法線與近端方向) / out=<png路徑> — slice 選填；厚度＝法線軸跨度，>1 時前覆蓋後 | " +
            "費率：⌈實際落地數/100⌉，禁覆蓋 skip 不收費；回傳落檔 letters/<persona>/cmd/sculpture_<op>.md";

        public override string ExampleArgs => "op=stats";

        // 2026-08-17：該 Plan 已完工歸檔到 Plan/completed/，路徑跟著搬 ——
        // 舊值指向的檔早就不在，而按鈕的失敗形式是「完全沒反應」（不會報錯）。
        public override string HelpURL => "ucl_core:Docs~/zh-Hant/Plan/completed/Plan_Sculpture_3D.md";

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
                case "stamp2d":
                case "stampimg": await OpStamp(aOp, args, token); return;
                case "view":
                case "slice":
                case "stats": await OpReadOnly(aOp, args, token); return;
                default:
                    throw new Exception($"[Sculpture] op 必為 box|carve|stamp2d|stampimg|slice|view|stats（got '{aOp}'）。ArgsSchema: {ArgsSchema}");
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
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[Sculpture] op={iOp} 幾何參數缺漏（詳見 {aPath}）");
            }
            int aVolume = ClampedVolume(ref aX1, ref aX2, ref aY1, ref aY2, ref aZ1, ref aZ2);
            if (iOp == "box" && aVolume > MAX_BOX_VOLUME)
            {
                aR.AppendLine($"## blocked\n- reason: 單次 box 體積上限 {MAX_BOX_VOLUME:N0} voxels（本次 {aVolume:N0}）—— 拆多刀");
                WritePayload(iArgs, aPath, aR.ToString());
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
            int aFreeAvail = CanvasVoucher.UCL_CanvasVoucherLedger.GetExpiring(aPersona);
            // 規劃付款要的是**可花總額**（未過期限時 ＋ 永久）—— 不是「存了多少永久券」。
            // 2026-08-18 券改批次制後這三種讀法分成三個 API，呼叫端必須選；選錯不會報錯。
            int aVoucherAvail = CanvasVoucher.UCL_CanvasVoucherLedger.GetPermanent(aPersona);
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
                              $"pay={aPay} 可用 {aAuthorized}（限時券 {aFreeAvail}＋永久券 {aVoucherAvail}＋token {aTokenAvail} 依模式取用）");
                aR.AppendLine("- how: 縮小範圍、換 pay 模式、或先賺錢 —— 引擎未執行，未扣任何費用");
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[Sculpture] op={iOp} 預授權不足（詳見 {aPath}）");
            }

            // 引擎執行（spawn sculpt.py —— 落子的唯一通道是本 Cmd，引擎輸出 JSON 是結算依據）
            string aScript = ResolveEngineScript();
            if (aScript == null)
            {
                aR.AppendLine("## blocked\n- reason: 解析不到 sculpt.py（CorePath 空或檔案不存在）");
                WritePayload(iArgs, aPath, aR.ToString());
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
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[Sculpture] op={iOp} 引擎失敗（詳見 {aPath}）");
            }

            // 結算：只對實際落地收費（禁覆蓋 skip 不收）
            int aActual = ReadInt(aResult, iOp == "box" ? "placed_count" : "carved_count");
            int aSkipped = ReadInt(aResult, "skipped_count");
            string aEventFile = ReadStr(aResult, "event_file");
            int aCharge = aActual > 0 ? CeilDiv(aActual, VOXELS_PER_UNIT) : 0;
            var (aUsedFree, aUsedVoucher, aUsedToken) = ConsumePayment(aPersona, aBank, aCharge, aPay, aEventFile,
                UCL_AgentCmdContexts.FromArgs(iArgs)?.CmdId);

            aR.AppendLine("## result（引擎回報＝結算依據）");
            aR.AppendLine($"- {(iOp == "box" ? "placed" : "carved")}: **{aActual}**{(aSkipped > 0 ? $"（skip {aSkipped} —— 禁覆蓋，不收費）" : "")} / 體積 {aVolume:N0}");
            aR.AppendLine($"- charged: **{aCharge} 單位**（⌈{aActual}/{VOXELS_PER_UNIT}⌉；帳單跟著事實走，不跟著意圖走）");
            aR.AppendLine($"- pay_breakdown: freetime={aUsedFree} voucher={aUsedVoucher} token={aUsedToken}（pay={aPay}）");
            aR.AppendLine($"- event: `{aEventFile}`");
            aR.AppendLine("## next");
            aR.AppendLine($"- 看成品：run_cmd.py run Sculpture --arg op=view [--arg region=…] [--arg exclude_color=…]（免費）");
            aR.AppendLine("- 驗收慣例：宣稱含內部結構的作品，交件附外觀＋室內（region 裁進去）各一張。");
            // 區塊職責：本人若正在自由時間中，回傳值尾端多附一段流程提示（Tim 2026-08-18）。
            // 物理意義：`sculpt-3d` 是自由時間「繪圖」組的活動，而它的入口是 Cmd 不是 python 腳本 ——
            //          所以 `op=step` 代跑不到它（代跑層只 spawn python）。**修法不是讓代跑層去呼叫 Cmd**
            //          （那要在活動層長出第二種 tool 形式），而是反過來：**Cmd 自己回報進流程。**
            //          落子完成正是最容易斷線的位置 —— 產物剛落地、注意力在產物上。
            // 數值影響：不在自由時間時一個字都不加（本 Cmd 平常也用於工作，不該多噪音）。
            UCL_FreeTimeHint.Append(aR, aPersona);
            WritePayload(iArgs, aPath, aR.ToString());
            Debug.Log($"[Sculpture] op={iOp} {aActual} voxels, charged {aCharge}（f{aUsedFree}/v{aUsedVoucher}/t{aUsedToken}） → {aPath}");
        }

        // ===========================================================
        // 區塊：stamp2d / stampimg — 貼圖進 3D（三段式：預授權 → 引擎執行 → 按實際結算）
        // 物理意義：兩 op 只差在「圖從哪來」（畫布區域 vs 檔案），計費與閘門完全同型，故共用本方法。
        //          預授權的最壞費用＝**圖面積 × thickness**（實際只有非透明像素落地 ⇒ 帳單必然 ≤ 預授權）。
        //          ⚠ 面積一律由本檔自己量（stamp2d 由 region 兩角算、stampimg 讀 PNG IHDR）——
        //          呼叫端申報的 expect_pixels 是「給引擎對帳的」，不能拿來當自己的餘額閘門：
        //          用對方給的數字守自己的門，門就是假的。
        // 數值影響：引擎回 status=mismatch(4)/out_of_bounds(5)/empty(3) 時 exit≠0 ⇒ 走 blocked，
        //          一毛不扣（沒貼成不是成功）。成功時對 placed_count 收費。
        // ===========================================================
        async UniTask OpStamp(string iOp, Dictionary<string, string> iArgs, CancellationToken iToken)
        {
            await UniTask.Yield();
            string aPersona = GetArg(iArgs, "persona", "").Trim();
            if (string.IsNullOrEmpty(aPersona))
                throw new Exception($"[Sculpture] {iOp} 需要 --arg persona=<name>（錢認 persona 的券與 bank，不能用猜的）");

            string aPath = PayloadPath(aPersona, iOp);
            var aR = new StringBuilder();
            aR.AppendLine($"# Sculpture op={iOp} persona={aPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            string aAt = GetArg(iArgs, "at", "").Trim();
            if (string.IsNullOrEmpty(aAt))
            {
                aR.AppendLine("## blocked\n- reason: 缺 --arg at=<x,y,z>（圖左上角要貼在 3D 的哪一點）");
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[Sculpture] op={iOp} 缺 at（詳見 {aPath}）");
            }
            int aThickness = Math.Max(1, TryGetInt(iArgs, "thickness", out int aT) ? aT : 1);

            // 來源解析 + 最壞面積（預授權用；stampimg 的尺寸自己讀檔量，不聽呼叫端的）
            string aSrcCli;
            long aArea;
            string aSrcDesc;
            if (iOp == "stamp2d")
            {
                if (!TryGetInt(iArgs, "src_x1", out int aSx1) || !TryGetInt(iArgs, "src_y1", out int aSy1)
                    || !TryGetInt(iArgs, "src_x2", out int aSx2) || !TryGetInt(iArgs, "src_y2", out int aSy2))
                {
                    aR.AppendLine("## blocked\n- reason: stamp2d 需要 src_x1 src_y1 src_x2 src_y2 四個整數（2D 畫布座標 0-2047）");
                    WritePayload(iArgs, aPath, aR.ToString());
                    throw new Exception($"[Sculpture] op=stamp2d 來源區域缺漏（詳見 {aPath}）");
                }
                int aW = Math.Abs(aSx2 - aSx1) + 1, aH = Math.Abs(aSy2 - aSy1) + 1;
                aArea = (long)aW * aH;
                aSrcDesc = $"2D 畫布區域 ({aSx1},{aSy1})-({aSx2},{aSy2}) = {aW}x{aH}";
                aSrcCli = $" --src-x1={aSx1} --src-y1={aSy1} --src-x2={aSx2} --src-y2={aSy2}";
            }
            else
            {
                string aPng = GetArg(iArgs, "png", "").Trim();
                if (string.IsNullOrEmpty(aPng) || !File.Exists(aPng))
                {
                    aR.AppendLine($"## blocked\n- reason: stampimg 需要存在的 --arg png=<路徑>（got '{aPng}'）");
                    WritePayload(iArgs, aPath, aR.ToString());
                    throw new Exception($"[Sculpture] op=stampimg 圖檔不存在（詳見 {aPath}）");
                }
                if (!TryReadPngSize(aPng, out int aPw, out int aPh))
                {
                    aR.AppendLine($"## blocked\n- reason: 讀不到 PNG 尺寸（非 PNG 或檔案損毀）: {aPng}");
                    WritePayload(iArgs, aPath, aR.ToString());
                    throw new Exception($"[Sculpture] op=stampimg PNG 尺寸讀取失敗（詳見 {aPath}）");
                }
                // resize 有給就以 resize 後尺寸算面積（那才是真正會落地的格數上限）
                var aRz = ParseWh(GetArg(iArgs, "resize", ""));
                if (aRz.HasValue) { aPw = aRz.Value.w; aPh = aRz.Value.h; }
                aArea = (long)aPw * aPh;
                aSrcDesc = $"PNG {Path.GetFileName(aPng)} = {aPw}x{aPh}{(aRz.HasValue ? "（resize 後）" : "")}";
                aSrcCli = $" --png=\"{aPng}\"" + (aRz.HasValue ? $" --resize=\"{aRz.Value.w},{aRz.Value.h}\"" : "");
            }

            long aWorstVolume = aArea * aThickness;
            if (aWorstVolume > MAX_BOX_VOLUME)
            {
                aR.AppendLine($"## blocked\n- reason: 最壞體積 {aWorstVolume:N0} 超過上限 {MAX_BOX_VOLUME:N0}（{aSrcDesc} × thickness {aThickness}）—— 縮圖或降 thickness");
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[Sculpture] op={iOp} 體積超限（詳見 {aPath}）");
            }

            // 付款來源解析（與 box/carve 同：錢認 agent，說話認 persona）
            string aPay = GetArg(iArgs, "pay", "auto").Trim().ToLowerInvariant();
            var aMeta = UCL_RegistryMeta.LoadFromFile(UCL_AwakeningService.RegistryMetaPath);
            var aLock = UCL_AwakeningService.ReadLock(aPersona);
            string aBank = aLock != null && !string.IsNullOrEmpty(aLock.bank_account)
                ? aLock.bank_account
                : UCL_AwakeningService.ResolveBankAccount(aMeta, UCL_AwakeningService.NormalizeAgent(aMeta, GetArg(iArgs, "agent", "")));

            int aMaxUnits = CeilDiv((int)aWorstVolume, VOXELS_PER_UNIT);
            int aFreeAvail = CanvasVoucher.UCL_CanvasVoucherLedger.GetExpiring(aPersona);
            int aVoucherAvail = CanvasVoucher.UCL_CanvasVoucherLedger.GetPermanent(aPersona);   // 永久券（限時的算在 aFreeAvail，見 op=box 那處）
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
                aR.AppendLine($"- reason: 預授權不足 —— 本刀最壞費用 {aMaxUnits} 單位（{aSrcDesc} × thickness {aThickness} = {aWorstVolume:N0}/⌈{VOXELS_PER_UNIT}⌉），" +
                              $"pay={aPay} 可用 {aAuthorized}（限時券 {aFreeAvail}＋永久券 {aVoucherAvail}＋token {aTokenAvail} 依模式取用）");
                aR.AppendLine("- how: 縮小來源、降 thickness、換 pay 模式、或先賺錢 —— 引擎未執行，未扣任何費用");
                aR.AppendLine("- 註：透明像素不落地，實際帳單通常遠低於此上限；預授權擋的是**最壞情況**。");
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[Sculpture] op={iOp} 預授權不足（詳見 {aPath}）");
            }

            // 引擎執行
            string aScript = ResolveEngineScript();
            if (aScript == null)
            {
                aR.AppendLine("## blocked\n- reason: 解析不到 sculpt.py（CorePath 空或檔案不存在）");
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[Sculpture] 引擎不存在（詳見 {aPath}）");
            }
            // ⚠ 一律 `--opt=value`：facing 的值以 'z-' 之類收尾、路徑可能含空白，
            //   空格分隔會被 argparse 誤判（同 view 的 light-dir 血證，Tim 2026-08-13 實測 exit=2）
            string aCli = $"\"{aScript}\" {iOp}{aSrcCli} --at=\"{aAt}\" --persona=\"{aPersona}\" --thickness={aThickness}";
            string aFacing = GetArg(iArgs, "facing", "").Trim();
            if (!string.IsNullOrEmpty(aFacing)) aCli += $" --facing=\"{aFacing}\"";
            if (TryGetInt(iArgs, "expect_pixels", out int aExpect)) aCli += $" --expect-pixels={aExpect}";
            if (TryGetInt(iArgs, "alpha_threshold", out int aAlpha)) aCli += $" --alpha-threshold={aAlpha}";
            if (IsTrue(GetArg(iArgs, "overwrite", ""))) aCli += " --overwrite";
            if (IsTrue(GetArg(iArgs, "allow_clip", ""))) aCli += " --allow-clip";
            // 作品自動登錄：只要給 exhibit_id，region 由引擎依實際落地的 voxel 反推（多刀 union）
            string aExId = GetArg(iArgs, "exhibit_id", "").Trim();
            if (!string.IsNullOrEmpty(aExId))
            {
                aCli += $" --exhibit-id=\"{aExId}\"";
                string aExTitle = GetArg(iArgs, "exhibit_title", "").Trim();
                string aExDesc = GetArg(iArgs, "exhibit_desc", "").Trim();
                if (!string.IsNullOrEmpty(aExTitle)) aCli += $" --exhibit-title=\"{aExTitle}\"";
                if (!string.IsNullOrEmpty(aExDesc)) aCli += $" --exhibit-desc=\"{aExDesc}\"";
                if (TryGetInt(iArgs, "exhibit_margin", out int aExMargin)) aCli += $" --exhibit-margin={aExMargin}";
            }

            var (aExit, aSo, aSe) = UCL_ProcessCli.Run("python", aCli, UCL_RepoPath.RepoRoot,
                PROC_TAG, nameof(Cmd_Sculpture), ENGINE_TIMEOUT_MS);
            var aResult = ParseEngineJson(aSo);
            if (aExit != 0 || aResult == null || ReadStr(aResult, "status") != "success")
            {
                string aStatus = aResult != null ? ReadStr(aResult, "status") : "";
                string aReason = aResult != null ? ReadStr(aResult, "reason") : "";
                aR.AppendLine($"## blocked\n- reason: 引擎未貼（exit={aExit}{(string.IsNullOrEmpty(aStatus) ? "" : $", status={aStatus}")}）—— 未扣任何費用");
                if (!string.IsNullOrEmpty(aReason)) aR.AppendLine($"- engine: {aReason}");
                if (aStatus == "mismatch")
                    aR.AppendLine("- how: expect_pixels 是「你看的預覽」與「引擎吃的圖」的對帳閘門。重跑一次預覽拿新數字，或確認來源沒被別人改過。");
                if (aStatus == "out_of_bounds")
                    aR.AppendLine("- how: 改小 at、用 resize 縮圖，或顯式 allow_clip=true 接受裁切（別讓「只貼了一角」看起來像成功）。");
                aR.AppendLine("```\n" + (aSo ?? "").Trim() + (string.IsNullOrEmpty(aSe) ? "" : "\n── stderr ──\n" + aSe.Trim()) + "\n```");
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[Sculpture] op={iOp} 未貼成（詳見 {aPath}）");
            }

            // 結算：只對實際落地收費
            int aActual = ReadInt(aResult, "placed_count");
            int aPainted = ReadInt(aResult, "painted_source_pixels");
            int aSkipped = ReadInt(aResult, "skipped_occupied");
            int aOob = ReadInt(aResult, "out_of_bounds");
            int aBlack = ReadInt(aResult, "remapped_black");
            string aEventFile = ReadStr(aResult, "event_file");
            int aCharge = aActual > 0 ? CeilDiv(aActual, VOXELS_PER_UNIT) : 0;
            var (aUsedFree, aUsedVoucher, aUsedToken) = ConsumePayment(aPersona, aBank, aCharge, aPay, aEventFile,
                UCL_AgentCmdContexts.FromArgs(iArgs)?.CmdId);

            aR.AppendLine("## result（引擎回報＝結算依據）");
            aR.AppendLine($"- source: {aSrcDesc} → 非透明像素 **{aPainted}**（透明＝未繪製，不放 voxel）");
            aR.AppendLine($"- placed: **{aActual}** voxels（= 非透明 {aPainted} × thickness {aThickness} 扣掉 skip/越界）" +
                          $"{(aSkipped > 0 ? $"；skip {aSkipped}（禁覆蓋，不收費）" : "")}{(aOob > 0 ? $"；越界裁掉 {aOob}" : "")}");
            if (aBlack > 0) aR.AppendLine($"- remapped_black: {aBlack}（純黑 index 0 在 3D 代表「空」，重映到最近非零暗色 index 4 —— 不靜默改色）");
            aR.AppendLine($"- charged: **{aCharge} 單位**（⌈{aActual}/{VOXELS_PER_UNIT}⌉；預授權上限曾為 {aMaxUnits}）");
            aR.AppendLine($"- pay_breakdown: freetime={aUsedFree} voucher={aUsedVoucher} token={aUsedToken}（pay={aPay}）");
            aR.AppendLine($"- event: `{aEventFile}`");
            // 展品登錄結果（引擎回報＝事實；沒給 exhibit_id 時這段不存在）
            var aEx = aResult.Contains("exhibit") ? aResult["exhibit"] : null;
            if (aEx != null && aEx.IsObject)
            {
                string aExMode = ReadStr(aEx, "mode") == "created" ? "新建" : "擴充";
                aR.AppendLine($"- exhibit: **{aExMode}** `{ReadStr(aEx, "id")}`《{ReadStr(aEx, "title")}》" +
                              $" by {ReadStr(aEx, "author")} — region `{ReadStr(aEx, "region")}`（依實際落地 voxel 反推，多刀 union 不覆蓋）");
                string aWarn = ReadStr(aEx, "warning");
                if (!string.IsNullOrEmpty(aWarn)) aR.AppendLine($"- ⚠ {aWarn}");
                string aPhoto = ReadStr(aEx, "photo");
                if (!string.IsNullOrEmpty(aPhoto) && File.Exists(aPhoto))
                    UCL_AgentCommandRunner.ReportOutputFile(iArgs, aPhoto);
            }
            aR.AppendLine("## next");
            aR.AppendLine($"- 看成品：run_cmd.py run Sculpture --arg op=view [--arg region=…]（免費）" +
                          (aEx != null && aEx.IsObject ? $"；或 --arg exhibit={ReadStr(aEx, "id")} 一鍵載入本作品 preset" : ""));
            aR.AppendLine("- 下次貼圖：先 `canvas.py view --region x,y,w,h` 看預覽 → 把它印的 non_transparent_pixels 當 expect_pixels 帶回來。");
            WritePayload(iArgs, aPath, aR.ToString());

            // 預覽 PNG 也一併端出來 —— 讀者不必再去翻檔案才知道「貼進去的是哪張」
            string aPreview = Path.Combine(UCL_AgentCommandsPath.DataRoot, "Sculpture", "_stamp_src.png");
            if (iOp == "stamp2d" && File.Exists(aPreview)) UCL_AgentCommandRunner.ReportOutputFile(iArgs, aPreview);

            Debug.Log($"[Sculpture] op={iOp} {aActual} voxels（非透明 {aPainted}）, charged {aCharge}（f{aUsedFree}/v{aUsedVoucher}/t{aUsedToken}） → {aPath}");
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
                // pass-through 觀測參數（含展品 preset 與打光 —— 引擎旗標為準，這裡只轉譯不加工）
                string aRegion = GetArg(iArgs, "region", "");
                string aExclude = GetArg(iArgs, "exclude_color", "");
                string aExhibit = GetArg(iArgs, "exhibit", "");
                string aLightDir = GetArg(iArgs, "light_dir", "");
                string aAmbient = GetArg(iArgs, "ambient", "");
                string aZoom = GetArg(iArgs, "zoom", "");
                // ⚠ 一律用 `--opt=value` 等號形式：light-dir 的值以 '-' 開頭（如 -1,-1,-2），
                //   空格分隔會被 argparse 當旗標吃掉（Tim 2026-08-13 後台實測 exit=2 血證）
                if (!string.IsNullOrEmpty(aRegion)) aCli += $" --region=\"{aRegion}\"";
                if (!string.IsNullOrEmpty(aExclude)) aCli += $" --exclude-color=\"{aExclude}\"";
                if (!string.IsNullOrEmpty(aExhibit)) aCli += $" --exhibit=\"{aExhibit}\"";
                if (!string.IsNullOrEmpty(aLightDir)) aCli += $" --light-dir=\"{aLightDir}\"";
                if (!string.IsNullOrEmpty(aAmbient)) aCli += $" --ambient={aAmbient}";
                if (!string.IsNullOrEmpty(aZoom)) aCli += $" --zoom={aZoom}";
            }
            else if (iOp == "slice")
            {
                // 切片＝把 region 內的 voxel 顏色原樣當像素輸出（不打光、不等角投影）
                string aRegion = GetArg(iArgs, "region", "");
                if (string.IsNullOrEmpty(aRegion))
                    throw new Exception("[Sculpture] op=slice 需要 --arg region=<x1..x2,y1..y2,z1..z2>（法線軸跨度＝厚度）");
                aCli += $" --region=\"{aRegion}\"";
                string aAxis = GetArg(iArgs, "axis", "");
                string aOut = GetArg(iArgs, "out", "");
                if (!string.IsNullOrEmpty(aAxis)) aCli += $" --axis=\"{aAxis}\"";
                if (!string.IsNullOrEmpty(aOut)) aCli += $" --out=\"{aOut}\"";
            }
            var (aExit, aSo, aSe) = UCL_ProcessCli.Run("python", aCli, UCL_RepoPath.RepoRoot,
                PROC_TAG, nameof(Cmd_Sculpture), ENGINE_TIMEOUT_MS);

            var aR = new StringBuilder();
            aR.AppendLine($"# Sculpture op={iOp}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();
            aR.AppendLine("```\n" + (aSo ?? "").Trim() + (string.IsNullOrEmpty(aSe) ? "" : "\n── stderr ──\n" + aSe.Trim()) + "\n```");
            WritePayload(iArgs, aPath, aR.ToString());
            if (iOp == "view")
            {
                string aPng = Path.Combine(UCL_AgentCommandsPath.DataRoot, "Sculpture", "_last_view.png");
                if (File.Exists(aPng)) UCL_AgentCommandRunner.ReportOutputFile(iArgs, aPng);
            }
            else if (iOp == "slice")
            {
                // 引擎把實際落檔路徑印在 output_path（--out 可覆寫預設）—— 讀它，不重推路徑
                string aOutPng = ReadStr(ParseEngineJson(aSo), "output_path");
                if (string.IsNullOrEmpty(aOutPng))
                    aOutPng = Path.Combine(UCL_AgentCommandsPath.DataRoot, "Sculpture", "_last_slice.png");
                if (File.Exists(aOutPng)) UCL_AgentCommandRunner.ReportOutputFile(iArgs, aOutPng);
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
        // ⚠ iCmdId：2026-08-17 加 —— 記帳的 env_marker 依它查 per-cmd context。
        //   原本走 UCL_AgentCommandRunner.CurrentCmdId（全域單例），
        //   Cmd 併行時會拿到別人的 id ⇒ **這筆帳的來源記成別人**（不會報錯）。
        (int usedFree, int usedVoucher, int usedToken) ConsumePayment(
            string iPersona, string iBank, int iCharge, string iPay, string iEventRef, string iCmdId)
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
                // 2026-08-18：免費像素**就是限時繪圖券** —— 不再有券系統之外的第二套錢。
                // 這裡先算限時券可用量，只為了讓 pay_breakdown 誠實報出「這幾張是限時的」；
                // 扣款走同一個 Consume（ledger 本身**先花快過期的**，順序不需要在這裡排）。
                aFree = Math.Min(aRemain, CanvasVoucher.UCL_CanvasVoucherLedger.GetExpiring(iPersona));
                if (aFree > 0)
                {
                    CanvasVoucher.UCL_CanvasVoucherLedger.Consume(iPersona, aFree, "sculpture_place_freetime", aRef);
                    aRemain -= aFree;
                }
            }
            if (aUseVoucher && aRemain > 0)
            {
                // ⚠ 用**永久券**：上一段已經把限時券扣掉了。這裡若用「可花總額」，
                //   同一批限時券會被算第二次 —— 症狀是合計對、但 pay_breakdown 的分類是假的。
                int aBal = CanvasVoucher.UCL_CanvasVoucherLedger.GetPermanent(iPersona);
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
                    $"3D sculpture {iCharge} unit(s) by {iPersona}", "system", iCmdId);
                aToken = aRemain;
                aRemain = 0;
            }
            if (aRemain > 0)   // 預授權有驗過，走到這裡＝通道間狀態被外力改了 —— 顯式炸，不靜默欠帳
                throw new Exception($"[Sculpture] 結算短付 {aRemain} 單位（預授權後餘額被變動？）—— 已扣 f{aFree}/v{aVoucher}/t{aToken}，請對帳 {aRef}");
            return (aFree, aVoucher, aToken);
        }

        // ⚠ 免費像素的額度檔（`Canvas/freetime/<P>.json`）2026-08-18 廢除 ——
        //   `FreePixelPath` / `FreeSessionPath` / `FreePixelsAvailable` / `ConsumeFreePixels` 四支一併移除。
        //   免費像素現在**就是限時繪圖券**（`Cmd_FreeTime step=start` 發、到期自動作廢），
        //   所以不再有「券系統之外的第二套錢」要各自讀檔、各自算可用量、各自作廢。
        //   ⇒ 錢一律走 `UCL_CanvasVoucherLedger`（唯一寫入 owner）。

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

        /// <summary>
        /// 區塊職責：讀 PNG 的寬高（不解碼影像本體）。
        /// 物理意義：PNG 規格固定 —— 8 bytes 簽章 + 4 bytes 長度 + "IHDR" 後，接 4 bytes 寬、4 bytes 高，
        ///          皆為 **big-endian**（網路位元組序），而 x86 是 little-endian，所以必須手動翻轉。
        /// 數值影響：這個尺寸是預授權的分母來源 —— 由本檔自己量，不接受呼叫端申報（守自己的門要用自己的尺）。
        /// 失敗處置：簽章不符 / 檔案過短 / IO 例外 → 回 false，由呼叫端 blocked，不猜一個預設值。
        /// </summary>
        static bool TryReadPngSize(string iPath, out int oWidth, out int oHeight)
        {
            oWidth = oHeight = 0;
            try
            {
                var aHead = new byte[24];
                using (var aFs = File.OpenRead(iPath))
                {
                    int aRead = 0;
                    while (aRead < aHead.Length)
                    {
                        int n = aFs.Read(aHead, aRead, aHead.Length - aRead);
                        if (n <= 0) return false;    // 檔案比 PNG 標頭還短
                        aRead += n;
                    }
                }
                // PNG 簽章：89 50 4E 47 0D 0A 1A 0A
                if (aHead[0] != 0x89 || aHead[1] != 0x50 || aHead[2] != 0x4E || aHead[3] != 0x47) return false;
                if (aHead[12] != 'I' || aHead[13] != 'H' || aHead[14] != 'D' || aHead[15] != 'R') return false;
                oWidth = (aHead[16] << 24) | (aHead[17] << 16) | (aHead[18] << 8) | aHead[19];
                oHeight = (aHead[20] << 24) | (aHead[21] << 16) | (aHead[22] << 8) | aHead[23];
                return oWidth > 0 && oHeight > 0;
            }
            catch (Exception) { return false; }
        }

        /// <summary>解析 "W,H"；空字串或格式不符回 null（呼叫端據此決定「沒給」還是「給錯」）。</summary>
        static (int w, int h)? ParseWh(string iVal)
        {
            if (string.IsNullOrWhiteSpace(iVal)) return null;
            var aParts = iVal.Split(',');
            if (aParts.Length != 2) return null;
            if (!int.TryParse(aParts[0].Trim(), out int w) || !int.TryParse(aParts[1].Trim(), out int h)) return null;
            return (w > 0 && h > 0) ? (w, h) : null;
        }

        /// <summary>布林旗標判定：true/1/yes 皆算開（Cmd 參數是字串，呼叫端寫法不統一）。</summary>
        static bool IsTrue(string iVal)
        {
            if (string.IsNullOrWhiteSpace(iVal)) return false;
            string v = iVal.Trim().ToLowerInvariant();
            return v == "true" || v == "1" || v == "yes";
        }

        static bool TryGetInt(Dictionary<string, string> iArgs, string iKey, out int oVal)
            => int.TryParse(iArgs != null && iArgs.TryGetValue(iKey, out var s) ? s : "", out oVal);

        static string ReadStr(JsonData iJd, string iKey) => iJd != null && iJd.Contains(iKey) ? iJd[iKey].ToString() : "";
        static int ReadInt(JsonData iJd, string iKey) { try { return iJd != null && iJd.Contains(iKey) ? int.Parse(iJd[iKey].ToString()) : 0; } catch { return 0; } }
        static bool ReadBool(JsonData iJd, string iKey) { try { return iJd != null && iJd.Contains(iKey) && (bool)iJd[iKey]; } catch { return false; } }

        // 落點走版面唯一實作（Plan_Letters_Dir_Layout §8）—— 原本自己 Combine 一次是第 N 種算法。
        static string PayloadPath(string iPersona, string iOp)
            => UCL_LettersPath.CmdPayload(iPersona, "sculpture", iOp);

        static void WritePayload(IDictionary<string, string> iArgs, string iPath, string iReport)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iPath));
                File.WriteAllText(iPath, iReport, new UTF8Encoding(false));
                UCL_AgentCommandRunner.ReportOutputFile(iArgs, iPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Sculpture] 回傳落檔失敗 {iPath}: {e.Message}");
            }
        }
    }
}
#endif
