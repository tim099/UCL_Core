// 區塊職責：自由時間的活動清單掃描（雙層 md 來源的唯一解析器）。
// 物理意義：活動的事實來源是 md frontmatter —— 共用層跟著 UCL_Core 走、專案層跟著 repo 走，
//          同 id 專案層覆蓋共用層。Cmd_FreeTime 擲骰與 UCL_FreeTimeAdminPage 管理**共用本掃描器**，
//          不各寫一份（兩份掃描器的漂移症狀是「頁面看到的清單跟實際擲出來的不一樣」，而它不會報錯）。
// 歷史：本檔一度還放過「末段提示門檻」設定（tail_warn_seconds）。
//      Tim 2026-08-14 拍板**把末段提示整個拔掉**，設定隨之移除 ——
//      沒有消費端的設定就是一個比事實大的名字，留著只會讓人以為那裡還有一道防護。
//      拔除理由與血證寫在 Cmd_FreeTime.StepNext 的註解裡（燈拆掉 vs 燈調暗）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UCL.Core.EditorLib.AgentCommands.Awakening;

namespace UCL.Core.EditorLib.AgentCommands.FreeTime
{
    /// <summary>
    /// 活動的**特殊邏輯標記**（Tim 2026-08-17 拍板；frontmatter 欄位 `kind`）。
    /// <para>
    /// 為什麼是 enum 而不是自由字串規則名：管理頁要能給下拉選單。字串欄位打錯
    /// （`live-strem`）會**安靜地什麼都不做**，而下拉選單根本打不出那個值。
    /// </para>
    /// <para>
    /// 為什麼存在 md frontmatter 而不是另一份設定檔：活動的事實來源只有 md 一處。
    /// 這條是本系統付過學費的 —— v1 的 `AgentCommands/FreeTime/activities.json`
    /// 正是因為「雙源同步漂移」被廢止，管理頁也明著寫了「不另存一份 override 設定」。
    /// </para>
    /// <para>
    /// 新增一種 kind ＝ 改這個 enum ＋ 在 <see cref="UCL_FreeTimeGating"/> 補對應邏輯。
    /// 兩邊都要動是刻意的：**沒有實作的標記不該存在**（名字比事實大的東西，
    /// 會讓人以為那裡有一道邏輯）。
    /// </para>
    /// </summary>
    public enum UCL_FreeTimeActivityKind
    {
        /// <summary>一般活動 —— 永遠可選、永遠在普通層，不走任何特殊邏輯。</summary>
        Default = 0,

        /// <summary>
        /// 觀看直播：**沒開播就隱藏**（不列入候選 —— 陪看一個不存在的節目是純粹的浪費）；
        /// 開播時進最優先層並附上本場節目名。
        /// </summary>
        StreamWatch = 1,

        /// <summary>
        /// 下棋：有未完成棋局、且**對手也在自由時間中**時進最優先層（對手在線才接得上手）。
        /// 沒有這個條件時仍是普通活動 —— 隨時可以開新局徵人，不隱藏。
        /// </summary>
        Chess = 2,

        /// <summary>
        /// 繪圖：**永久繪圖券存量超過門檻**時進最優先層並提示「請多多使用」（Tim 2026-08-18 拍板）。
        /// <para>
        /// 為什麼盯的是**永久券**而不是可花總額：限時券本來就會過期、本來就該花掉，
        /// 它多不代表「囤積」。會囤起來的是永久券 —— 而囤著的券對誰都沒有價值。
        /// </para>
        /// <para>**不隱藏**：券少的時候畫圖照樣做得成，只是不特別值得優先。</para>
        /// </summary>
        CanvasVoucherFull = 3,
    }

    /// <summary>
    /// 一筆自由時間活動（來源＝一個 md 檔的 frontmatter；正文是給人讀的說明，不進本型別）。
    /// </summary>
    public class UCL_FreeTimeActivity
    {
        public string id = "";
        public string name = "";
        public string how = "";
        public string path = "";        // md 絕對路徑（路徑不該被推導，該被傳遞）
        public int minMinutes;          // 建議所需分鐘；0＝未設定（不做時間感知排序）
        public bool enabled = true;
        public bool isProjectLayer;     // true＝專案層（同 id 會覆蓋共用層）

        // 區塊職責：這件活動是否**必須在自由時間裡**才做得成（frontmatter `needs_session`，預設 true）。
        // 物理意義：多數活動是「自由時間才做的事」，而少數活動本身跟自由時間無關 ——
        //          下棋每一步都落盤、一局跨好幾次醒來（`min_minutes: 0` 就是那個意思），
        //          把它綁在場次上會讓「想走一步」變成「要先開一場自由時間」。
        //          （Tim 2026-09-11 拍板：下棋不綁定自由時間。）
        // 數值影響：false ⇒ `Cmd_FreeTimeActivity` 的 session 守衛放行，`iSession` 傳 null，
        //          **場次計數器（`activity` / `activities_done`）不寫**；
        //          ⭐ 而飢餓統計照記（`RecordPick` 只吃 persona，不吃 session）。
        // ⚠ 宣告式，⛔ 不在 Cmd 裡寫 `if (id == "chess")` —— 那會把規則裝在其中一條路上，
        //   而下一個「也不該綁場次」的活動不會有人想起來要改那一行。
        public bool needsSession = true;

        // 區塊職責：把「這件活動屬於哪一類」從活動本身分離出來（Tim 2026-08-18 拍板）。
        // 物理意義：原本一份 md 就是一「組」活動（`canvas-draw` ＝ 2D 畫布**或** 3D 雕刻、
        //          `gaming` ＝ TRPG**或** QA），於是子分支的選擇**完全沒有落盤** ——
        //          `session.activity` 只存得到組別 id，帳面上分不出做的是哪一個。
        //          更硬的一格：`tool` / `steps` 是掛在 md 上的，所以一組裡分支用不同工具時
        //          （canvas.py vs Cmd_Sculpture）**只有第一個分支接得到 op=step 代跑**，
        //          而第二個分支的缺席不會有任何地方喊。
        // ⇒ 現在一份 md ＝ 一件**具體活動**（id 對應一組設定、tool/steps 一對一），
        //   分類改由本欄承擔：骰面依 group 呈現具體項目，管理頁可改 group 而不必搬檔。
        // 數值影響：空字串＝未分組（骰面直接印活動名，不加前綴）。純顯示與組織用，
        //          **不參與可用性 / 優先層 / 時間感知三道排序** —— 那三道的判準在 kind 與 minMinutes。
        /// <summary>所屬分組（frontmatter `group`）。空＝未分組。骰面用它呈現具體項目的歸屬。</summary>
        public string group = "";

        // 區塊職責：讓活動的「一步」可以被 Cmd 代跑（Tim 2026-08-18）。
        // 物理意義：`how` 是給人讀的一整串自由文字（"chess.py lobby 找局 / start 開局徵人 / move 走子…"）——
        //          機器沒辦法從它取出「第一步該跑什麼」，所以活動層只能整串轉貼。
        //          下棋走一子、繪圖放一個像素**本來就是一次性的次秒級動作**，
        //          拆成「一步」之後 Cmd 就能代跑並在回傳檔接上下一步。
        // ⚠ **additive**：舊 md 沒填這兩欄 ⇒ `tool` 空 ⇒ 活動層回「此活動尚未支援代跑，
        //   用 op=pick 取得指令自己跑」。**沒填不是壞掉，是還沒接** —— 兩者要長得不一樣。
        // 數值影響：純資料；`steps` 是白名單，不在名單上的子命令一律拒跑
        //          （不做白名單＝把任意 argv 交給外部程式，那是 CLI 注入面）。
        /// <summary>代跑用的腳本檔名（frontmatter `tool`，例 `chess.py`）。空＝本活動不支援代跑。</summary>
        public string tool = "";
        /// <summary>允許代跑的子命令白名單（frontmatter `steps`，逗號分隔）。空＝即使有 tool 也不放行。</summary>
        public List<string> steps = new List<string>();

        // 區塊職責：代跑時要不要替呼叫端補上身分旗標，以及補的是哪一個旗標。
        // 物理意義：`step_args` 是**原樣轉發**給工具的，而工具各自對身分的要求不同 ——
        //          同一支 canvas.py 裡 `view` / `pixel` / `stats` **連 --persona 這個選項都沒有**
        //          （硬塞會得到 `unrecognized arguments`），而 `place` / `note` / `claim` 少了它直接 exit 2。
        //          旗標名也不統一：canvas.py 是 `--persona`，library.py 是 `--reader`。
        // ⚠ 因此這件事**不能猜**（無條件注入會弄壞前三個 step，猜旗標名會弄壞 library）——
        //   由每份活動 md 自己宣告。**沒宣告＝不注入**（fail-closed，行為與改動前逐位元相同）。
        // 數值影響：純資料。空 personaFlag 或 step 不在 stepsNeedPersona ⇒ 完全不介入 argv。
        /// <summary>身分旗標名（frontmatter `persona_flag`，例 `--persona` / `--reader`）。空＝本活動不自動補身分。</summary>
        public string personaFlag = "";
        /// <summary>需要自動補身分的子命令（frontmatter `steps_need_persona`，逗號分隔）。空＝一個都不補。</summary>
        /// <remarks>
        /// 支援 <c>step=--flag</c> 覆寫**單一 step** 的旗標名 —— 因為同一支工具的旗標**不一定一致**：
        /// 🩸 2026-08-18 端到端實測：`library.py` 的 `resume` / `bookmark` 吃 <c>--reader</c>，
        /// 但 <c>shelf</c> 吃 <c>--persona</c>。用單一 personaFlag 描述整支工具會在 shelf 上失敗，
        /// 而那個失敗**只有真的跑過才會出現**（編譯過、宣告看起來也合理）。
        /// </remarks>
        public List<string> stepsNeedPersona = new List<string>();

        // 區塊職責：逐 step 改走 SCP cmd（in-process），其餘 step 照舊 spawn python。
        // 物理意義：一支活動只有一個 `tool:`，而那 35 支子命令是**一支一支**移植進 C# 的
        //          ⇒ 整支活動切過去會弄壞還沒移植的那幾個 step（`resume` / `shelf` / `volumes`…），
        //          而它們的失敗樣子是「那一步 exit 2、活動流程斷在中間」。
        //          ⇒ **路由的粒度是 step，不是活動。**
        // ⚠ **additive／fail-closed**：md 沒宣告 `cmd_steps` ⇒ 這一格完全不介入，
        //   行為與改動前**逐位元相同**（沿用身分注入那一格的形狀）。
        // 數值影響：純資料。
        /// <summary>
        /// 改走 SCP cmd 的 step 路由表（frontmatter <c>cmd_steps</c>，逗號分隔的
        /// <c>&lt;step&gt;=&lt;cmd&gt;:&lt;op&gt;</c>；省略 <c>:&lt;op&gt;</c> ⇒ op 用 step 名，
        /// 例 <c>log-chapter=book:log-chapter</c>）。空＝一步都不路由。
        /// </summary>
        public List<string> cmdSteps = new List<string>();

        /// <summary>
        /// 被路由的 step 的身分**參數名**（frontmatter <c>cmd_persona_arg</c>，例 <c>reader</c>）。
        /// ⚠ 這裡是參數名不是旗標 —— cmd 那側是 <c>--arg reader=&lt;persona&gt;</c>，不是 <c>--reader &lt;persona&gt;</c>。
        /// </summary>
        public string cmdPersonaArg = "";

        /// <summary>
        /// 被路由的 step 用哪個參數選子命令（frontmatter <c>cmd_step_arg</c>；空＝<c>op</c>）。
        /// 🩸 預設值敢給是因為**猜錯會大聲壞**：`senate cmd` 那側有 ArgSpec 預檢，
        /// 沒宣告的參數名會被擋下並印出合法清單（⛔ 不是靜默取預設值）。
        /// </summary>
        public string cmdStepArg = "";

        // 區塊職責：查某個 step 該補哪個旗標。
        // 物理意義：先找 step 專屬覆寫（`shelf=--persona`），沒有才回退活動層 personaFlag。
        // 數值影響：回空字串＝這個 step 不補（呼叫端據此完全不介入 argv）。
        public string PersonaFlagForStep(string iStep)
        {
            return LookupNeedPersona(iStep, personaFlag);
        }

        /// <summary>
        /// 被路由到 cmd 的 step 該補哪個**參數名**（回空＝不補）。
        /// ⚠ `steps_need_persona` 的覆寫寫法是旗標式（`shelf=--persona`），
        /// 而 cmd 那側要的是名字 ⇒ 這裡把前導的 `-` 剝掉。
        /// 剝掉是為了讓同一份宣告在兩條路上都成立；⛔ 而剝錯也不會靜默 ——
        /// 名字不對會被 ArgSpec 預檢當場擋下。
        /// </summary>
        public string PersonaArgForStep(string iStep)
        {
            return LookupNeedPersona(iStep, cmdPersonaArg).TrimStart('-');
        }

        // 區塊職責：`steps_need_persona` 的共用查表（兩條路各自帶自己的 fallback）。
        // ⚠ 抽成一支是因為**同一份宣告要餵兩個消費端** —— 抄第二份的話，
        //   哪天覆寫語法改了只會有一邊跟上，而兩邊都不會報錯。
        string LookupNeedPersona(string iStep, string iFallback)
        {
            if (stepsNeedPersona == null) return "";
            foreach (var aEntry in stepsNeedPersona)
            {
                int aEq = aEntry.IndexOf('=');
                string aName = aEq < 0 ? aEntry : aEntry.Substring(0, aEq);
                if (!string.Equals(aName.Trim(), iStep, System.StringComparison.OrdinalIgnoreCase)) continue;
                string aOverride = aEq < 0 ? "" : aEntry.Substring(aEq + 1).Trim();
                return aOverride.Length > 0 ? aOverride : iFallback;
            }
            return "";
        }

        /// <summary>
        /// 查某個 step 有沒有被路由到 SCP cmd。
        /// <para>回 <c>routed=false</c> 且 <c>error</c> 空 ＝ 這個 step 沒宣告路由（走原本的 python spawn）。</para>
        /// <para>⛔ **宣告了但寫壞不當成「沒路由」** —— 那會靜默走回舊路，
        /// 而「我以為它改走 C# 了」與「它還在跑 python」在畫面上一模一樣。
        /// 寫壞就回 <c>error</c>，由呼叫端擋下並把那一行原文印出來。</para>
        /// </summary>
        public (bool routed, string cmd, string op, string error) CmdRouteForStep(string iStep)
        {
            if (cmdSteps == null) return (false, "", "", "");
            foreach (var aEntry in cmdSteps)
            {
                int aEq = aEntry.IndexOf('=');
                string aName = (aEq < 0 ? aEntry : aEntry.Substring(0, aEq)).Trim();
                if (!string.Equals(aName, iStep, System.StringComparison.OrdinalIgnoreCase)) continue;

                string aTarget = aEq < 0 ? "" : aEntry.Substring(aEq + 1).Trim();
                if (aTarget.Length == 0)
                    return (false, "", "", $"`cmd_steps` 的 `{aEntry}` 沒寫目標（要 `<step>=<cmd>:<op>`）");

                int aColon = aTarget.IndexOf(':');
                string aCmd = (aColon < 0 ? aTarget : aTarget.Substring(0, aColon)).Trim();
                string aOp = aColon < 0 ? "" : aTarget.Substring(aColon + 1).Trim();
                if (aCmd.Length == 0)
                    return (false, "", "", $"`cmd_steps` 的 `{aEntry}` 沒寫 cmd 名");
                return (true, aCmd, aOp.Length > 0 ? aOp : aName, "");
            }
            return (false, "", "", "");
        }

        /// <summary>特殊邏輯標記（frontmatter `kind`；缺欄位＝Default）。</summary>
        public UCL_FreeTimeActivityKind kind = UCL_FreeTimeActivityKind.Default;

        /// <summary>
        /// `kind` 欄位的原始字串 —— **只在解析失敗時非空**。
        /// 存它是為了讓「打錯的標記」在骰面上顯形，而不是靜靜地退回 Default：
        /// 靜默退回的症狀是「我明明標了直播，它卻還是照常出現」，而沒有任何地方會喊。
        /// </summary>
        public string kindParseError = "";
    }

    /// <summary>
    /// 自由時間設定與活動清單的讀寫。對齊 UCL_BartenderIO 慣例：路徑集中、讀不到回預設、寫入原子替換。
    /// </summary>
    public static class UCL_FreeTimeIO
    {
        public static string GetFreeTimeDir()
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "FreeTime");

        // ── 活動清單（雙層：共用層跟著 UCL_Core 走、專案層跟著 repo 走）──
        // ⚠ 不寫死 UCL_Core 安裝路徑 —— 由 UCL_EditorPath.CorePath 現算（各專案掛載位置不同）。
        public static string GetSharedActivityDir()
        {
            string aCoreRel = UCL_EditorPath.CorePath;
            return string.IsNullOrEmpty(aCoreRel) ? null
                : Path.GetFullPath(Path.Combine(UCL_RepoPath.UnityProjectRoot, aCoreRel,
                    "Docs~/zh-Hant/FreeTime/Activities"));
        }

        public static string GetProjectActivityDir()
            => Path.GetFullPath(Path.Combine(UCL_RepoPath.RepoRoot, "docs/FreeTime/Activities"));

        /// <summary>
        /// 區塊職責：掃描兩層活動 md，回傳合併後的清單（**含停用項**）。
        /// 物理意義：同 id 專案層覆蓋共用層（含 enabled:false 停用覆蓋 —— kotoko QA 血證：
        ///          過濾必須發生在 merge 之後，否則專案層的「停用」會被共用層的「啟用」蓋回去）。
        /// 數值影響：**回傳含停用項**，由呼叫端決定要不要濾 —— 擲骰要濾掉，管理頁要看得到。
        ///          這是本掃描器唯一的一份實作：Cmd 擲骰與管理頁共用，不各寫一份
        ///          （兩份掃描器的漂移症狀是「頁面看到的清單跟實際擲出來的不一樣」，而它不會報錯）。
        /// </summary>
        public static List<UCL_FreeTimeActivity> ScanActivities()
        {
            var aMerged = new Dictionary<string, UCL_FreeTimeActivity>();
            ScanActivityDir(GetSharedActivityDir(), false, aMerged);
            ScanActivityDir(GetProjectActivityDir(), true, aMerged);   // 同 id 專案層覆蓋
            var aList = new List<UCL_FreeTimeActivity>(aMerged.Values);
            aList.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
            return aList;
        }

        static void ScanActivityDir(string iDir, bool iIsProject, Dictionary<string, UCL_FreeTimeActivity> ioMerged)
        {
            if (string.IsNullOrEmpty(iDir) || !Directory.Exists(iDir)) return;
            foreach (var aMd in Directory.GetFiles(iDir, "*.md"))
            {
                if (Path.GetFileName(aMd).StartsWith("_")) continue;   // _README.md 等說明檔不算活動
                try
                {
                    string aId = Nz(UCL_AwakeningService.ReadFrontmatterField(aMd, "id"), Path.GetFileNameWithoutExtension(aMd));
                    int.TryParse(UCL_AwakeningService.ReadFrontmatterField(aMd, "min_minutes") ?? "", out int aMinMinutes);
                    var aKind = ParseKind(UCL_AwakeningService.ReadFrontmatterField(aMd, "kind"), out string aKindErr);
                    ioMerged[aId] = new UCL_FreeTimeActivity
                    {
                        kind = aKind,
                        kindParseError = aKindErr,
                        id = aId,
                        name = Nz(UCL_AwakeningService.ReadFrontmatterField(aMd, "name"), Path.GetFileNameWithoutExtension(aMd)),
                        how = UCL_AwakeningService.ReadFrontmatterField(aMd, "how") ?? "",
                        group = (UCL_AwakeningService.ReadFrontmatterField(aMd, "group") ?? "").Trim(),
                        path = aMd,
                        minMinutes = aMinMinutes,
                        enabled = !string.Equals(Nz(UCL_AwakeningService.ReadFrontmatterField(aMd, "enabled"), "true"),
                                                 "false", StringComparison.OrdinalIgnoreCase),
                        isProjectLayer = iIsProject,
                        needsSession = !string.Equals(Nz(UCL_AwakeningService.ReadFrontmatterField(aMd, "needs_session"), "true"),
                                                      "false", StringComparison.OrdinalIgnoreCase),
                        tool = (UCL_AwakeningService.ReadFrontmatterField(aMd, "tool") ?? "").Trim(),
                        steps = ParseSteps(UCL_AwakeningService.ReadFrontmatterField(aMd, "steps")),
                        personaFlag = (UCL_AwakeningService.ReadFrontmatterField(aMd, "persona_flag") ?? "").Trim(),
                        stepsNeedPersona = ParseSteps(UCL_AwakeningService.ReadFrontmatterField(aMd, "steps_need_persona")),
                        cmdSteps = ParseSteps(UCL_AwakeningService.ReadFrontmatterField(aMd, "cmd_steps")),
                        cmdPersonaArg = (UCL_AwakeningService.ReadFrontmatterField(aMd, "cmd_persona_arg") ?? "").Trim(),
                        cmdStepArg = (UCL_AwakeningService.ReadFrontmatterField(aMd, "cmd_step_arg") ?? "").Trim(),
                    };
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[FreeTime] 活動 md 讀取失敗，跳過：{aMd}（{e.Message}）");
                }
            }
        }

        /// <summary>
        /// 區塊職責：`kind` 欄位字串 → enum。
        /// 物理意義：空值／缺欄位＝Default（絕大多數活動不需要特殊邏輯，不該逼每份 md 都寫）。
        /// 數值影響：**認不得的值不是靜默 Default**，會回填 oParseError 讓它在管理頁與骰面上顯形 ——
        ///          標記打錯而系統照常運作，是「以為有防護其實沒有」那一類最難查的壞法。
        /// </summary>
        public static UCL_FreeTimeActivityKind ParseKind(string iRaw, out string oParseError)
        {
            oParseError = "";
            string aVal = (iRaw ?? "").Trim();
            if (aVal.Length == 0) return UCL_FreeTimeActivityKind.Default;
            if (Enum.TryParse(aVal, true, out UCL_FreeTimeActivityKind aKind)
                && Enum.IsDefined(typeof(UCL_FreeTimeActivityKind), aKind))
                return aKind;
            oParseError = aVal;
            Debug.LogWarning($"[FreeTime] 認不得的 kind='{aVal}' —— 視為 Default，並在骰面標記。"
                + $" 可用值：{string.Join(" / ", Enum.GetNames(typeof(UCL_FreeTimeActivityKind)))}");
            return UCL_FreeTimeActivityKind.Default;
        }

        // 區塊職責：`steps: move, board, lobby` → 白名單清單。
        // 物理意義：白名單存在的理由不是整潔，是**不把任意 argv 交給外部程式** ——
        //          活動層代跑時 step 名直接進 argv，沒有白名單就是一條 CLI 注入面。
        // 數值影響：空／缺欄位回空清單 ⇒ 呼叫端一律拒跑（fail-closed，不是 fail-open）。
        static List<string> ParseSteps(string iRaw)
        {
            var aList = new List<string>();
            if (string.IsNullOrWhiteSpace(iRaw)) return aList;
            foreach (var aPart in iRaw.Split(','))
            {
                string aTrim = aPart.Trim();
                if (aTrim.Length > 0) aList.Add(aTrim);
            }
            return aList;
        }

        static string Nz(string iVal, string iFallback) => string.IsNullOrEmpty(iVal) ? iFallback : iVal;

    }
}
#endif
