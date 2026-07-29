// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/04 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
// Abstract base class for Agent Command handlers.
// 子類自動被 UCL_AgentCommandRegistry 反射掃描並註冊。
// 2026-05-13 (Zeta): 去掉 #if UNITY_EDITOR guard — 本檔零 Editor API, 純 abstract + helper.
//                    保留 EditorCore/ 路徑慣例不動 (asmdef 單一, guard 是唯一隔離手段, 但本檔不需隔離).
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Agent Command Handler 抽象基底。
    ///
    /// 新增一條指令的標準寫法：寫一個繼承此類別的 class，覆寫 <see cref="CommandType"/> 與
    /// <see cref="ExecuteAsync"/>。Registry 會在 static ctor 內透過反射自動掃到並註冊。
    /// </summary>
    public abstract class UCL_AgentCommandHandlerBase
    {
        /// <summary>指令類型名稱（對應 queue.json 的 Type 欄位，大小寫不敏感）。</summary>
        public abstract string CommandType { get; }

        /// <summary>一行描述（用於 Page UI 列表，給人類看）。</summary>
        public virtual string ShortDescription => "";

        /// <summary>支援的 Args 格式說明（純文字 / Markdown，給 Page 顯示）。</summary>
        public virtual string ArgsSchema => "";

        // 區塊職責：機器可讀的參數規格 —— 供 reflection 匯出成 commands_schema.json 給 Python client 預檢用。
        // 物理意義：與上方 ArgsSchema 職責分明 —— ArgsSchema 寫「這參數是什麼意思」（人讀），
        //          本屬性寫「哪些必填、哪些是別名」（機器讀）。以前後者只存在於 Python 端手抄表，
        //          抄漏就會發生「C# 有實作但 client 擋死」（血證 2026-07-29 create_trpg_room）。
        // 數值影響：預設 null = 不提供結構化規格；匯出時該 Cmd 只出名稱，client 不對它做參數預檢。
        //          **不覆寫是完全合法的選擇**，不是遺漏 —— 意思是「我沒有要 client 幫忙擋參數」。
        /// <summary>
        /// 機器可讀的 Args 規格（給 <c>Cmd_ExportCmdSchema</c> 反射匯出）。預設 null = 不提供。
        /// 詳見 <see cref="UCL_CmdArgsSpec"/> 與
        /// <c>Docs~/zh-Hant/Plan/Plan_AgentCmd_Schema_Reflection_Export.md</c>。
        /// </summary>
        public virtual UCL_CmdArgsSpec ArgsSpec => null;

        /// <summary>
        /// 範例 Args（給 Page「Fill Example」按鈕用，格式為 <c>k1=v1;k2=v2</c>）。
        /// 子類覆寫此屬性以提供「一鍵填入即可執行」的預設參數，方便人類在 Editor 內快速試跑。
        /// 空字串表示沒有範例（按鈕會被禁用）。
        /// </summary>
        public virtual string ExampleArgs => "";

        /// <summary>HelpURL — Page 上「查看說明」按鈕跳轉的目標。可使用 ucl_core: / eov_docs: prefix。</summary>
        public virtual string HelpURL => "";

        /// <summary>
        /// Per-cmd timeout 上限 (seconds)。default 1200 (20 min)，Tim 2026-05-13 拍板。
        ///
        /// <para>子類 override 場景：</para>
        /// <list type="bullet">
        /// <item>Cmd_Tavern op=post --wait-reply 馬拉松對話：override → 1800 (30 min) 含 buffer</item>
        /// <item>長 build / 大 batch import：override → 3600 (1 hr)</item>
        /// <item>快速查詢類 cmd：可下調 → 60 (1 min) 早期失敗</item>
        /// </list>
        ///
        /// <para>單次 call override：caller 帶 args <c>_timeout_sec=N</c> 即時覆寫該筆 cmd timeout。</para>
        ///
        /// <para>Timeout fire 時：Runner 對 handler 發 CancellationToken cancel + 標
        /// <c>LastRunError=timeout</c>。注意：handler 若沒 honor token (e.g. sync File IO)，仍會在 background
        /// 跑到自己結束 — Runner 不被卡死, 但 cmd 真實 cancel 失敗 (Cancel ≠ Timeout caveat, Zeta 2026-05-13 戳)。</para>
        /// </summary>
        public virtual int TimeoutSeconds => 1200;

        /// <summary>實際執行邏輯（async）。</summary>
        public abstract UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token);

        // ===== 共用 helper（給子類使用） =====

        // 區塊職責：從 args dict 取出指定 key 的字串值，缺 key 或 value 為空字串時回 defaultVal。
        // 物理意義：UCL Runner 會把 queue.json 的 Args 餵成 Dictionary<string,string>；子類解析 args 時最常用的取值動作。
        //          採「嚴格版」語意 — value="" 也視為「未提供」，避免「使用者誤填空字串」陷阱。
        //          若子類需要區分「key 不存在」vs「value 是空字串」（例如允許空字串作為合法輸入），
        //          請改用 args.TryGetValue 自行處理，不要呼叫此 helper。
        // 數值影響：純讀取，不修改 args；對所有子類安全。
        /// <summary>
        /// Args dict 取值 helper：缺 key / null args / value 為空字串時回傳 <paramref name="defaultVal"/>。
        /// </summary>
        /// <param name="args">queue.json 傳進來的參數 dict（可為 null）</param>
        /// <param name="key">參數名稱（大小寫敏感，與 ArgsSchema 對齊）</param>
        /// <param name="defaultVal">缺值時回傳的預設值（可為 null）</param>
        /// <returns>有效字串值；否則 <paramref name="defaultVal"/></returns>
        protected static string GetArg(Dictionary<string, string> args, string key, string defaultVal)
        {
            if (args == null) return defaultVal;
            return args.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : defaultVal;
        }
    }
}
