// 區塊職責：銀行帳戶的**資料檔**（一帳一檔）與其讀寫接縫。
// 物理意義：合一之後「帳戶 id ＝ agent id」，而帳戶除了餘額之外還需要一些**人給的**資訊
//          （目前只有顯示名稱，之後可擴充）。餘額是 ledger 重放出來的**衍生值**，
//          本檔存的是**不能被算出來的那一半** —— 誰也推導不出「這個帳戶該叫什麼名字」。
// 為什麼一帳一檔（Tim 2026-08-20 拍板）：跟 `letters/<persona>/bank/<區域ID>.md` 同一個理由 ——
//          單一大表會讓兩個寫入端互相覆寫，而症狀是「別人的資料出現在我的欄位裡」。
//          🩸 現行 `identities.json` 正是那個形狀的受害者：`cc` 那筆的 display_name 是 `crest-001`
//            （一個 persona 名塞進 agent 的顯示名欄），合一遷移之後這筆壞資料從「沒人查得到」
//            變成「7 位 persona 每則訊息都會命中」。
// 數值影響：不含任何金額。**改這裡不會動到一分錢**，只改「顯示成什麼」。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Treasury
{
    /// <summary>
    /// 單一銀行帳戶的資料（一帳一檔，存 `Treasury/accounts/&lt;id&gt;.json`）。
    /// </summary>
    /// <remarks>
    /// ⚠ 欄位名即 JSON 鍵名，**刻意不走 `m_PascalCase`** —— 沿用 `identities.json` 既有的
    /// snake_case 鍵（`display_name` 等），讓兩邊的資料看起來是同一種東西、遷移時人眼可對照。
    /// </remarks>
    [Serializable]
    public class UCL_BankAccountProfile
    {
        public string id;             // 帳戶 id ＝ agent id（合一後兩者同一個東西）
        public string display_name;   // 顯示名稱；空字串＝沿用 id（**空是合法值，不是缺值**）
        public string note;           // 自由備註（誰在用、為什麼存在）
        public string updated_at;     // ISO 8601 UTC
        public string updated_by;     // 誰改的（§8.6：寫入要能回答「是誰」）

        public string DisplayOrId => string.IsNullOrWhiteSpace(display_name) ? id : display_name;
    }

    /// <summary>
    /// 帳戶資料的讀寫接縫。**唯一入口** —— 不要在別處直接讀寫那些 json。
    /// </summary>
    public static class UCL_BankAccountProfileIO
    {
        // accounts/ 底下 `_` 開頭的是衍生快取（目前有 `_balances.snapshot.txt`），
        // 不是帳戶資料。列舉時一律跳過，否則快取檔會被當成一個叫 `_balances.snapshot` 的帳戶。
        const string CachePrefix = "_";

        public static string AccountsRoot => UCL_TreasuryPaths.GetAccountsRoot();

        static string PathOf(string iAccountId) =>
            Path.Combine(AccountsRoot, SanitizeFileName(iAccountId) + ".json");

        // 帳戶 id 可能含空白（歷史上有 `Federal Reserve System`）；路徑分隔字元一律擋，
        // 不做「換成底線」那種靜默轉換 —— 那會讓兩個不同 id 映到同一個檔。
        public static bool IsValidAccountId(string iId)
        {
            if (string.IsNullOrWhiteSpace(iId)) return false;
            if (iId.StartsWith(CachePrefix)) return false;
            return iId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        static string SanitizeFileName(string iId) => iId;   // 不轉換：不合法就該被 IsValidAccountId 擋掉

        /// <summary>讀單一帳戶資料；檔案不存在回 null（**不自動生成**，缺檔與空名是兩件事）。</summary>
        public static UCL_BankAccountProfile Load(string iAccountId)
        {
            if (!IsValidAccountId(iAccountId)) return null;
            string p = PathOf(iAccountId);
            if (!File.Exists(p)) return null;
            try
            {
                var jd = JsonData.ParseJson(File.ReadAllText(p));
                if (jd == null) return null;
                // ⚠ **檔名大小寫撞名守衛**（🩸 2026-08-20 summit 親手踩）：
                //   Windows / macOS 的檔名**不分大小寫** ⇒ `zeta.json` 與 `Zeta.json` 是同一個檔。
                //   而 `zeta`（餘額 2767）與 `Zeta`（已銷戶空帳戶）是**兩個不同帳戶**。
                //   不驗的話，查 `zeta` 會拿到 `Zeta` 的資料，而且看起來完全正常 ——
                //   那正是「經過一個不分大小寫的層就靜默合併」的實例。
                //   ⇒ 檔內的 `id` 必須與請求的 id **逐字元相符**，否則視為「這個帳戶沒有資料」。
                string storedId = jd.GetString("id", "");
                if (!string.Equals(storedId, iAccountId, StringComparison.Ordinal))
                {
                    Debug.LogWarning($"[BankAccountProfile] 檔名大小寫撞名：請求 '{iAccountId}'，"
                        + $"但 {Path.GetFileName(p)} 內的 id 是 '{storedId}' —— 視為無資料。"
                        + " 兩個只差大小寫的帳戶無法共存於同一個檔案系統，請先銷戶其一。");
                    return null;
                }
                return new UCL_BankAccountProfile
                {
                    id = jd.GetString("id", iAccountId),
                    display_name = jd.GetString("display_name", ""),
                    note = jd.GetString("note", ""),
                    updated_at = jd.GetString("updated_at", ""),
                    updated_by = jd.GetString("updated_by", ""),
                };
            }
            catch (Exception e)
            {
                // 壞檔要出聲：靜默回 null 會讓「檔壞了」跟「沒這個帳戶」長得一樣。
                Debug.LogError($"[BankAccountProfile] 讀取失敗 {p}：{e.Message}");
                return null;
            }
        }

        /// <summary>列出所有已落檔的帳戶 id（跳過 `_` 開頭的快取檔）。</summary>
        public static List<string> ListAccountIds()
        {
            var result = new List<string>();
            try
            {
                if (!Directory.Exists(AccountsRoot)) return result;
                foreach (var f in Directory.GetFiles(AccountsRoot, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (string.IsNullOrEmpty(name) || name.StartsWith(CachePrefix)) continue;
                    result.Add(name);
                }
            }
            catch (Exception e) { Debug.LogError($"[BankAccountProfile] 列舉失敗：{e.Message}"); }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// 寫單一帳戶資料。actor 必填（§8.6：寫入要能回答「是誰」）。
        /// </summary>
        /// <remarks>寫入後**讀回複驗**；不符即回 false —— 寫成功不等於讀得到同一個值。</remarks>
        public static bool Save(string iAccountId, string iDisplayName, string iNote,
            string iActor, out string oError)
        {
            oError = "";
            if (!IsValidAccountId(iAccountId))
            { oError = $"帳戶 id 不合法（不可為空、不可 `_` 開頭、不可含路徑字元）：'{iAccountId}'"; return false; }
            if (string.IsNullOrWhiteSpace(iActor))
            { oError = "actor 必填 —— 匿名寫入不收（§8.6）"; return false; }
            try
            {
                Directory.CreateDirectory(AccountsRoot);
                // 撞名時**拒絕寫入**而不是覆蓋 —— 覆蓋會把另一個帳戶的資料無聲換掉。
                string existPath = PathOf(iAccountId);
                if (File.Exists(existPath))
                {
                    try
                    {
                        var cur = JsonData.ParseJson(File.ReadAllText(existPath));
                        string curId = cur?.GetString("id", "") ?? "";
                        if (!string.IsNullOrEmpty(curId)
                            && !string.Equals(curId, iAccountId, StringComparison.Ordinal))
                        {
                            oError = $"檔名大小寫撞名：`{iAccountId}` 與既有的 `{curId}` 在檔案系統上是同一個檔"
                                + "（Windows/macOS 檔名不分大小寫）。**未寫入** —— 請先銷戶其中一個帳戶。";
                            return false;
                        }
                    }
                    catch { /* 壞檔就當可覆蓋，下面的讀回複驗會擋住真正的失敗 */ }
                }
                var jd = JsonData.ParseJson("{}");
                jd["id"] = iAccountId;
                jd["display_name"] = iDisplayName ?? "";
                jd["note"] = iNote ?? "";
                jd["updated_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff",
                    System.Globalization.CultureInfo.InvariantCulture) + "Z";
                jd["updated_by"] = iActor;
                string p = PathOf(iAccountId);
                string tmp = p + ".tmp";
                File.WriteAllText(tmp, jd.ToJsonBeautify());
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);

                var back = Load(iAccountId);
                if (back == null || back.display_name != (iDisplayName ?? ""))
                { oError = $"寫入後讀回不符（期望 display_name='{iDisplayName}'，實際 '{back?.display_name}'）"; return false; }
                return true;
            }
            catch (Exception e) { oError = e.Message; return false; }
        }

        /// <summary>
        /// 查顯示名稱。**查不到回 null**（caller 自行決定 fallback）——
        /// 回 id 本身會讓「沒登記」與「登記成跟 id 一樣」變成同一件事，而那是兩種不同的狀態。
        /// </summary>
        public static string GetDisplayName(string iAccountId)
        {
            var p = Load(iAccountId);
            if (p == null) return null;
            return string.IsNullOrWhiteSpace(p.display_name) ? null : p.display_name;
        }
    }
}
#endif
