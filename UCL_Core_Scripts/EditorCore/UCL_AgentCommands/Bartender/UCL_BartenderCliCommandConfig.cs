// 區塊職責：酒館 CLI 的「指令＝一份設定檔」層 —— 指令 id、說明、與它要執行的一串行為（action）。
// 物理意義：指令表從 code 裡的 hardcode 清單改為 `bartender/cli_commands/<id>.json` 一指令一檔
//          （Tim 2026-08-20 拍板）。id 可以在設定頁改名（例如把 help 改成別的字），
//          行為用 [SerializeReference] 多型清單封裝 —— 一個指令可以依序執行多個行為，
//          每個行為自己讀 args 決定做什麼。
// 數值影響：執行順序＝清單順序；任一行為要求二次確認，整個指令就要確認一次。
//          設定檔不存在時自動生出預設三指令（help / remote-window / msg），行為與舊 hardcode 版一致。
//
// 設計取捨：
//   · **行為繼承 UnityJsonSerializable ＋ [SerializeReference]**（比照 LY 專案 ConditionBase/ConditionGroup
//     的既有 pattern）—— SerializeReference 是 UCL 序列化判定多型的唯一訊號，少了它子類欄位會被
//     靜默丟掉；設定頁靠它畫出多型下拉。
//   · **一指令一檔**而不是一份大清單檔：改一個指令的 diff 只碰一個檔，且改名（rename id）
//     可以表達成「寫新檔＋刪舊檔」，不會在大檔裡留下順序噪音。
//   · args 目前**直接遞給行為**（Args / RawAfterArgs），不做 arg→行為參數的 mapping 層 ——
//     Tim 2026-08-20 拍板先不做，要做時在 IBartenderCliAction 與 config 之間加一層即可。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UCL.Core;
using UCL.Core.ATTR;
using UCL.Core.JsonLib;
using UCL.Core.UI;
using UnityEngine;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;

namespace UCL.Core.EditorLib.AgentCommands.Bartender
{
    /// <summary>
    /// 一次 CLI 呼叫的上下文，遞給每個行為。
    /// **小寫 token（Args）給比對、原始整行（RawLine / RawAfterArgs）給內容** ——
    /// 內容走小寫 token 的話英文訊息會被壓成全小寫而沒有任何一層報錯（2026-08-19 血證）。
    /// </summary>
    public class UCL_BartenderCliContext
    {
        /// <summary>已小寫、已去掉 prefix 與指令名的 tokens。</summary>
        public string[] Args = new string[0];
        /// <summary>使用者原本打的整行（未小寫）。</summary>
        public string RawLine = "";
        /// <summary>指令名與第 1 個 arg 之後的**原文**（給訊息內容用，保留大小寫與換行）。</summary>
        public string RawAfterArgs = "";
        /// <summary>觸發這次呼叫的酒館訊息。</summary>
        public UCL_ChatMessage Src;
        public string RoomId = "";
    }

    /// <summary>
    /// CLI 行為 —— 一個指令依序執行一串這個。
    /// 實作新行為：繼承 <see cref="UCL_BartenderCliActionBase"/>，設定頁的多型下拉會自動列出。
    /// </summary>
    public interface IBartenderCliAction : UCLI_TypeListable, UCLI_IsEnable
    {
        /// <summary>這次呼叫要不要二次確認（依 args 判斷；任一行為要求即整個指令要確認）。</summary>
        bool NeedsConfirm(UCL_BartenderCliContext iCtx);
        /// <summary>確認訊息裡「會發生什麼」那段；不需確認的行為回空字串。</summary>
        string ConfirmSummary(UCL_BartenderCliContext iCtx);
        /// <summary>執行；回傳酒保要回覆的內容（多個行為的回覆會依序串接）。</summary>
        string Execute(UCL_BartenderCliContext iCtx);
    }

    /// <summary>CLI 行為基底（比照 ConditionBase：UnityJsonSerializable ＋ IsEnable ＋ 顯示名）。</summary>
    public abstract class UCL_BartenderCliActionBase : UnityJsonSerializable, IBartenderCliAction, UCLI_ShortName
    {
        [SerializeField][UCL_HideOnGUI] private bool m_IsEnable = true;

        public virtual bool IsEnable { get => m_IsEnable; set => m_IsEnable = value; }

        public virtual string GetShortName() => GetType().Name.Replace("CliAction_", "");

        public virtual bool NeedsConfirm(UCL_BartenderCliContext iCtx) => false;
        public virtual string ConfirmSummary(UCL_BartenderCliContext iCtx) => "";
        public abstract string Execute(UCL_BartenderCliContext iCtx);
    }

    // 各行為掛 [HelpURL] → DrawObjectData 畫標題列時會自動長出「?」鈕開對應說明
    // （UCL_GUILayoutDrawObject 讀型別上的 HelpURLAttribute，本頁零客製）。
    /// <summary>列出所有可用指令（清單由指令設定檔生成，不是手寫的）。</summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_BartenderCliCommandsPage.md")]
    public class CliAction_Help : UCL_BartenderCliActionBase
    {
        public override string Execute(UCL_BartenderCliContext iCtx)
            => UCL_BartenderCliService.BuildHelp();
    }

    /// <summary>開關遠端視窗協作（on [permanent] / off）。`on permanent` 需二次確認。</summary>
    [HelpURL("ucl_core:Docs~/{lang}/API/BartenderCli/CliAction_RemoteWindow.md")]
    public class CliAction_RemoteWindow : UCL_BartenderCliActionBase
    {
        public override bool NeedsConfirm(UCL_BartenderCliContext iCtx)
            => UCL_BartenderCliService.IsOnArgs(iCtx.Args) && UCL_BartenderCliService.HasPermanentArg(iCtx.Args);

        public override string ConfirmSummary(UCL_BartenderCliContext iCtx)
            => "開啟遠端視窗協作，**並且打開永久開關** —— "
             + "之後每次 domain reload / 重開 Editor 都會自動恢復為開啟。"
             + "該能力會把指定視窗帶到前景、移動游標並可按 Enter。";

        public override string Execute(UCL_BartenderCliContext iCtx)
            => UCL_BartenderCliService.RunRemoteWindow(iCtx.Args);
    }

    /// <summary>透過自動通知的遠端輸入群發訊息（一律二次確認）。</summary>
    [HelpURL("ucl_core:Docs~/{lang}/API/BartenderCli/CliAction_Msg.md")]
    public class CliAction_Msg : UCL_BartenderCliActionBase
    {
        // 會打進別人的視窗並按 Enter —— 沒有不問的版本
        public override bool NeedsConfirm(UCL_BartenderCliContext iCtx) => true;

        public override string ConfirmSummary(UCL_BartenderCliContext iCtx)
            => UCL_BartenderCliService.BuildMsgSummary(iCtx);

        public override string Execute(UCL_BartenderCliContext iCtx)
            => UCL_BartenderCliService.RunMsg(iCtx);
    }

    /// <summary>回覆一段固定文字（設定頁可編輯內容 —— 最簡單的自訂指令素材）。</summary>
    [HelpURL("ucl_core:Docs~/{lang}/API/BartenderCli/CliAction_PostText.md")]
    public class CliAction_PostText : UCL_BartenderCliActionBase
    {
        /// <summary>酒保要回覆的內容（markdown 可用）。</summary>
        public string m_Text = "";

        public override string GetShortName()
            => "PostText: " + (string.IsNullOrEmpty(m_Text) ? "(空)" : m_Text.Substring(0, Math.Min(20, m_Text.Length)));

        public override string Execute(UCL_BartenderCliContext iCtx)
            => string.IsNullOrEmpty(m_Text) ? "（PostText 內容是空的 —— 去設定頁填 m_Text）" : m_Text;
    }

    /// <summary>一個 CLI 指令的設定 —— 一指令一檔（`bartender/cli_commands/<id>.json`）。</summary>
    [Serializable]
    public class UCL_BartenderCliCommandConfig : UnityJsonSerializable, UCLI_ShortName
    {
        /// <summary>指令 id（使用者打的那個字，如 `help`）。比對時轉小寫；改名＝改這格再存。</summary>
        public string id = "";
        /// <summary>關掉＝這個指令不存在（help 也不列、打了回「沒有這個指令」）。</summary>
        public bool enabled = true;
        /// <summary>help 清單顯示的用法（`cmd help` 那行）。</summary>
        public string usage = "";
        /// <summary>help 清單顯示的說明。</summary>
        public string description = "";
        /// <summary>依序執行的行為。⚠ [SerializeReference] 是多型序列化的唯一訊號，別拿掉。</summary>
        [SerializeReference] public List<IBartenderCliAction> actions = new List<IBartenderCliAction>();

        public string GetShortName() => string.IsNullOrEmpty(id) ? "(未命名指令)" : id;

        // bool 的 wire format 修正（同 UCL_BartenderCliSettings 的既有處置）：
        // Unity 模式會把 bool 寫成 "True"/"False" 字串，非 C# 讀取端會把 "False" 當 truthy
        public override JsonData SerializeToJson()
        {
            var aJson = base.SerializeToJson();
            aJson["enabled"] = enabled;
            return aJson;
        }

        /// <summary>小寫比對鍵；id 空白時回空字串（永遠比不中）。</summary>
        public string MatchKey => (id ?? "").Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 指令設定檔的讀寫。目錄：`ChatTavern/bartender/cli_commands/`，一指令一檔 `<id>.json`。
    /// 目錄不存在或沒有任何設定檔時，生出預設三指令並寫回（跟 cli_settings 的自動生檔同哲學：
    /// 先讓檔案存在，使用者才看得到該往哪改）。
    /// </summary>
    public static class UCL_BartenderCliCommandStore
    {
        public const string DirName = "cli_commands";

        public static string GetDir() => Path.Combine(UCL_BartenderIO.GetBartenderDir(), DirName);

        /// <summary>讀出全部指令設定（含 disabled 的 —— 設定頁要顯示；執行端自己過濾）。</summary>
        public static List<UCL_BartenderCliCommandConfig> LoadAll()
        {
            string aDir = GetDir();
            var aOut = new List<UCL_BartenderCliCommandConfig>();
            string[] aFiles;
            try
            {
                if (!Directory.Exists(aDir)) aFiles = new string[0];
                else aFiles = Directory.GetFiles(aDir, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BartenderCli] 指令設定目錄讀取失敗：{e.Message}");
                aFiles = new string[0];
            }

            for (int i = 0; i < aFiles.Length; i++)
            {
                try
                {
                    string aJson = File.ReadAllText(aFiles[i]);
                    if (string.IsNullOrWhiteSpace(aJson)) continue;
                    var aConfig = new UCL_BartenderCliCommandConfig();
                    aConfig.DeserializeFromJson(JsonData.ParseJson(aJson));
                    if (aConfig.actions == null) aConfig.actions = new List<IBartenderCliAction>();
                    // id 缺席時退回檔名 —— 手動複製檔案忘了改 id 的情況，檔名比空字串可信
                    if (string.IsNullOrWhiteSpace(aConfig.id))
                        aConfig.id = Path.GetFileNameWithoutExtension(aFiles[i]);
                    aOut.Add(aConfig);
                }
                catch (Exception e)
                {
                    // 一個檔壞掉不影響其他指令，但一定要出聲 —— 靜默跳過的樣子跟「指令被刪了」一樣
                    Debug.LogWarning($"[BartenderCli] 指令設定讀取失敗（已跳過）：{aFiles[i]}：{e.Message}");
                }
            }

            if (aOut.Count == 0)
            {
                aOut = CreateDefaults();
                try { SaveAll(aOut); }
                catch (Exception e) { Debug.LogWarning($"[BartenderCli] 預設指令寫入失敗（本次仍用記憶體預設）：{e.Message}"); }
            }
            return aOut;
        }

        /// <summary>
        /// 把整份清單寫回目錄：清單內每筆寫 `<id>.json`，目錄裡**不在清單上的檔刪掉**
        /// （改名＝新檔出現＋舊檔消失，靠這條收斂，不用另外追「原本叫什麼」）。
        /// id 會先 sanitize（trim ＋ 小寫 ＋ 擋路徑分隔字元）；空 id 的筆跳過並警告。
        /// </summary>
        public static void SaveAll(List<UCL_BartenderCliCommandConfig> iConfigs)
        {
            string aDir = GetDir();
            Directory.CreateDirectory(aDir);

            var aKeep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var aSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (iConfigs != null)
            {
                for (int i = 0; i < iConfigs.Count; i++)
                {
                    var aConfig = iConfigs[i];
                    if (aConfig == null) continue;
                    string aId = SanitizeId(aConfig.id);
                    if (string.IsNullOrEmpty(aId))
                    {
                        Debug.LogWarning($"[BartenderCli] 第 {i + 1} 筆指令 id 是空的，沒有存（先取個名字）");
                        continue;
                    }
                    if (!aSeen.Add(aId))
                    {
                        // 撞名的第二筆會覆寫第一筆的檔 —— 那是靜默資料遺失，擋下來出聲
                        Debug.LogWarning($"[BartenderCli] 指令 id `{aId}` 重複，第二筆沒有存");
                        continue;
                    }
                    aConfig.id = aId;   // 存檔即正規化 —— 檔名與比對鍵永遠一致
                    WriteAtomic(Path.Combine(aDir, aId + ".json"), aConfig.SerializeToJson().ToJsonBeautify());
                    aKeep.Add(aId + ".json");
                }
            }

            // 刪掉不在清單上的檔（被移除／被改名的舊檔）
            try
            {
                var aFiles = Directory.GetFiles(aDir, "*.json", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < aFiles.Length; i++)
                {
                    if (!aKeep.Contains(Path.GetFileName(aFiles[i]))) File.Delete(aFiles[i]);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BartenderCli] 清理舊指令檔失敗（新檔已寫入）：{e.Message}");
            }
        }

        /// <summary>預設三指令 —— 行為與 hardcode 時代（2026-08-19 上線版）一致。</summary>
        public static List<UCL_BartenderCliCommandConfig> CreateDefaults()
        {
            return new List<UCL_BartenderCliCommandConfig>
            {
                new UCL_BartenderCliCommandConfig
                {
                    id = "help",
                    usage = "cmd help",
                    description = "列出所有可用指令（本清單由指令設定檔生成，不是手寫的）",
                    actions = new List<IBartenderCliAction> { new CliAction_Help() },
                },
                new UCL_BartenderCliCommandConfig
                {
                    id = "remote-window",
                    usage = "cmd remote-window on [permanent] ／ cmd remote-window off",
                    description = "開關遠端視窗協作。on 預設只開**本次 Editor session**；"
                                + "帶 permanent 會連永久開關一起開（跨重編／重啟自動恢復）。"
                                + "off 同時關掉本次與永久。",
                    actions = new List<IBartenderCliAction> { new CliAction_RemoteWindow() },
                },
                new UCL_BartenderCliCommandConfig
                {
                    id = "msg",
                    usage = "cmd msg <persona|all> <訊息>",
                    description = "透過自動通知的遠端輸入，把訊息打進對方的輸入框並送出。"
                                + "`all` ＝ 所有**在線**的 persona。訊息保留原文大小寫。"
                                + "**一律需要二次確認**（確認訊息會回顯完整內容與收件名單）。",
                    actions = new List<IBartenderCliAction> { new CliAction_Msg() },
                },
            };
        }

        /// <summary>id 正規化：trim ＋ 小寫（比對本來就不分大小寫）＋ 擋掉會逃出目錄的字元。</summary>
        public static string SanitizeId(string iId)
        {
            string a = (iId ?? "").Trim().ToLowerInvariant();
            if (a.Length == 0) return "";
            var aSb = new StringBuilder(a.Length);
            for (int i = 0; i < a.Length; i++)
            {
                char c = a[i];
                if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"'
                    || c == '<' || c == '>' || c == '|' || c == ' ' || c == '\t') continue;
                aSb.Append(c);
            }
            return aSb.ToString();
        }

        // 換檔用 File.Replace（同 UCL_BartenderCliIO —— Delete→Move 之間的真空窗撞過 domain reload）
        static void WriteAtomic(string iPath, string iText)
        {
            string aTmp = iPath + ".tmp";
            File.WriteAllText(aTmp, iText, new UTF8Encoding(false));
            if (File.Exists(iPath)) File.Replace(aTmp, iPath, null);
            else File.Move(aTmp, iPath);
        }
    }
}
#endif
