// 區塊職責：自動 commit 的**分群規則**（單一真相源）——「這個檔屬於哪一群、哪些檔永遠不收」。
// 物理意義：規則原本只住在 `UCL_AutoCommitPage` 裡，那時它唯一的消費端就是那頁。
//          Tim 2026-08-20 要求 `/ucl-commit` 流程也能用自動 commit ⇒ 出現第二個消費端（`Cmd_AutoCommit`）。
//          ⇒ 規則搬到這裡**共用**，而不是在 Cmd 裡再寫一份。
//          🩸 理由不是「重複很醜」：這種規則的錯配等級是「檔進錯 commit」——
//            兩份規則漂掉的症狀是「同一個檔在頁面被分到 A 群、在 Cmd 被分到 B 群」，
//            而兩邊各自看起來都正常。本 repo 已經為「同一個數字兩處各存一份」付過好幾次帳。
// 數值影響：純資料與純函式，不碰 IO、不碰 GUI 狀態 ⇒ 抽離不改變任何行為。
//          **執行**（git add / commit）刻意留在各自的呼叫端：頁面要 async ＋ 進度條，
//          Cmd 要同步 ＋ 回傳值，硬要共用會生出一個誰都不好用的中間層。
// ⚠ **2026-08-21 部分撤銷** 2026-08-07 的「規則寫在程式碼、不開放 UI／參數編輯」拍板。
//   撤銷後的形狀是「可宣告、但掀不動地板」，不是「全面開放」：
//   · 本檔這兩組寫死的 GroupDef（agent / letters）**沒有變** —— AgentCommands 本層與
//     persona 信件庫的分群仍是專案慣例，不由任何檔案覆寫。
//   · 新增：其他 repo 可以在自己根目錄放 `.ucl_autocommit.json` 宣告自己的分群
//     （見 UCL_AutoCommitConfig；第一個消費者是 Chess）。設定檔**入版控、由該 repo 擁有、
//     改動在 diff 裡看得見** —— 這跟當年那句針對的「執行期參數」（不留痕跡）不是同一種東西。
//   · 地板由**判定順序**保證，不是由「呼叫端記得檢查」保證：Classify 的順序是
//     subptr → ephemeral → 分群 ⇒ 設定檔寫什麼前綴都碰不到 ephemeral 與那兩個特殊群。
//   · 設定檔只吃**前綴清單**，不吃 regex —— 比 code 更受限（理由見下方 GroupDef 的區塊註解：
//     「錯配是『檔進錯 commit』等級，規則要一眼能驗證」）。
#if UNITY_EDITOR
using System;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>自動 commit 的分群規則。`UCL_AutoCommitPage`（人按）與 `Cmd_AutoCommit`（agent 跑）共用。</summary>
    public static class UCL_AutoCommitRules
    {
        // 區塊職責：分群規則（順序即優先序，第一個命中的收走）
        // 物理意義：Match 吃「相對該 repo root 的正斜線路徑」。規則刻意用前綴不用 regex ——
        //          這裡的錯配是「檔進錯 commit」等級，規則要一眼能驗證。
        public class GroupDef
        {
            public string Key;
            public string Label;
            public Func<string, bool> Match;
            public string Message;       // commit 訊息主體（檔數統計由呼叫端補在後面）
            public bool DefaultOn;
        }

        /// <summary>掃描結果的兩個特殊群：巢狀 submodule pointer／未分類。</summary>
        public const string KEY_SUBPTR = "__subptr";
        public const string KEY_OTHER = "__other";

        // ── AgentCommands 本層 ──────────────────────────────────────────
        public static readonly GroupDef[] AgentGroupDefs =
        {
            new GroupDef
            {
                Key = "chat",
                Label = "酒館訊息（[chat] 獨立 commit — 硬規則）",
                Match = p => p.StartsWith("ChatTavern/rooms/"),
                Message = "[chat] sync tavern messages & inbox (auto)",
                DefaultOn = true,
            },
            new GroupDef
            {
                Key = "treasury",
                Label = "Treasury（帳本 / 帳戶）",
                Match = p => p.StartsWith("Treasury/"),
                Message = "chore(treasury): sync ledger & account state (auto)",
                DefaultOn = true,
            },
            new GroupDef
            {
                Key = "runtime",
                Label = "Agent runtime state（cursor / bartender / persona / canvas…）",
                Match = p => p.StartsWith("ChatTavern/") || p.StartsWith("AwakenInit/")
                             || p.StartsWith("Canvas/") || p.StartsWith("Inbox/"),
                Message = "chore(runtime): sync agent runtime state (auto)",
                DefaultOn = true,
            },
        };

        // ── persona 信件庫（letters/<persona>/，各自一個 repo）───────────
        // 區塊職責：persona 信件庫的分群規則
        // 物理意義：這裡的分界不是「檔案類型」，是**作者是誰** ——
        //          投遞件（別人寫的、系統寫的）與機械維護檔可以自動收；
        //          她自己寫的一律落到未分類（預設不勾），留給她自己的收尾 commit。
        // ⚠ `outbox/` 是掛號信的寄件存證（寄出時由工具生成、內容是投遞那一刻的快照），
        //   跟 `mailbox/` 同一個通道的兩端，所以同群 —— 它不是「她寫的信」，是通道的複本。
        public static readonly GroupDef[] PersonaGroupDefs =
        {
            new GroupDef
            {
                Key = "mailbox",
                Label = "信件通道（mailbox/ 系統信與掛號信投遞、outbox/ 寄件存證）",
                Match = p => p.StartsWith("mailbox/") || p.StartsWith("outbox/"),
                Message = "[mailbox] 收信件通道檔（系統信／投遞／存證）(auto)",
                DefaultOn = true,
            },
            new GroupDef
            {
                Key = "portraits",
                Label = "他人投遞的畫像（portraits/ — 作者是別人，我只是收件人）",
                Match = p => p.StartsWith("portraits/"),
                Message = "[portraits] 收他人投遞的畫像 (auto)",
                DefaultOn = true,
            },
            // ===========================================================
            // 區塊職責：`profile/` —— persona 身分欄的新家（退場案 §8.2 一欄一檔）。
            // 物理意義：這些檔是**機械產生**的：Phase 1 read-through lazy migration
            //          在消費端第一次讀到該 persona 時，把 legacy `personas/<p>.json` 的
            //          identity 欄逐欄抄成 `profile/<field>.md`（審計 actor=lazy-migration）。
            //          觸發者通常是**別人**的讀取，落地時該 persona 不在線
            //          ⇒ 沒有人會 commit 它們，正是自動 commit 存在的理由。
            // ⚠ 為什麼預設勾：**身分現在住在這裡**。沒進版控的 profile/ 等於
            //   「這個人是誰」只存在這一台機器上，而 legacy 那份是不會再更新的舊值
            //   ⇒ 一次磁碟意外就真的丟了。
            // ⚠ 內容也可能是人改的（`Cmd PersonaProfile op=set`，例如 Tim 設某人的 email）——
            //   但那是**設定**不是**作品**：不是信、不是碎片、不是素描本，
            //   沒有「替她簽名」的問題（紅線是有作者的產出，不是有意圖的設定）。
            // ===========================================================
            new GroupDef
            {
                Key = "profile",
                Label = "身分欄 profile/（退場案 Phase 1 遷移產物：一欄一檔）",
                Match = p => p.StartsWith("profile/"),
                Message = "[data] 收 profile/ 身分欄（Phase 1 lazy migration 產物）(auto)",
                DefaultOn = true,
            },
            // ===========================================================
            // 區塊職責：`bank/` —— persona 的銀行綁定（Tim 2026-08-20 拍板，一區一檔）。
            // 物理意義：`bank/<區域ID>.md` 的內容＝該 persona 在那個區域使用的帳號（＝agent id）。
            //          寫入走接縫 `UCL_PersonaProfile.WriteBankAccount`（actor/reason 必填＋審計）。
            // ⚠ 為什麼預設勾（同 profile/ 但更硬）：**錢的歸屬現在住在這裡**。
            //   沒進版控等於「這個人的薪水該進哪個帳號」只存在這一台機器上，
            //   而缺綁定的處置是落央行 ⇒ 一次磁碟意外的症狀不是報錯，是薪水靜默轉向。
            // ⚠ 這一群裡會出現**別的專案的檔**（letters 是同一個 repo 被多專案掛著）——
            //   那是正常的，照收。⛔ 絕不因為「不認識這個區域」而排除或刪除。
            // ===========================================================
            new GroupDef
            {
                Key = "bank",
                Label = "銀行綁定 bank/（區域 → 帳號；一區一檔）",
                Match = p => p.StartsWith("bank/"),
                Message = "[data] 收 bank/ 銀行綁定（區域 → 帳號）(auto)",
                DefaultOn = true,
            },
            new GroupDef
            {
                Key = "letters_mech",
                Label = "機械維護檔（_latest.md 指標 / cmd/.gitignore）",
                Match = p => p == "_latest.md" || p == "cmd/.gitignore",
                Message = "[data] 同步機械維護檔（指標／目錄 ignore）(auto)",
                DefaultOn = true,
            },
        };

        /// <summary>取該模式的規則表。`iPersonaLetters`＝letters 模式。</summary>
        public static GroupDef[] Defs(bool iPersonaLetters)
            => iPersonaLetters ? PersonaGroupDefs : AgentGroupDefs;

        // ephemeral —— 永遠不進候選（分類矩陣：*.log / wait 旗標 / 臨時渲染 / DebugLogs，
        // 見 ucl-commit skill 的檔案分類）。pending.trigger / *.tmp 是 Cmd queue 的瞬時檔。
        public static bool IsEphemeral(string path)
        {
            string name = path;
            int slash = path.LastIndexOf('/');
            if (slash >= 0) name = path.Substring(slash + 1);
            if (name.EndsWith(".log") || name.EndsWith(".tmp")) return true;
            if (name == "_last_op.md" || name == "_last_view.md"
                || name == "_active_waits.json" || name == "pending.trigger") return true;
            if (name.StartsWith("_wait_")) return true;
            if (path.StartsWith("DebugLogs/") || path.Contains("/DebugLogs/")) return true;
            return false;
        }

        /// <summary>
        /// 一個路徑該進哪一群。`iIsSubPointer`＝這個路徑是巢狀 submodule 的 pointer。
        /// 回 null ＝ ephemeral（不進候選）。
        /// </summary>
        public static string Classify(string iPath, GroupDef[] iDefs, bool iIsSubPointer)
        {
            if (iIsSubPointer) return KEY_SUBPTR;
            if (IsEphemeral(iPath)) return null;
            if (iDefs != null)
            {
                foreach (var def in iDefs)
                {
                    if (def.Match(iPath)) return def.Key;
                }
            }
            return KEY_OTHER;
        }
    }
}
#endif
