// 區塊職責：央行（Pacific Standard Public Deposit Bank）與跨日存款保管費參數的**唯一真相源**。
// 物理意義：保管費原本是 UCL_BartenderDaemon 裡兩個 const（threshold=1000 / rate=5%），
//          改參數要改 code、要重編、Tim 動不了。Tim 2026-08-01 要求後台可調 → 落 JSON。
//          更根本的一件事同時發生：**保管費不再蒸發，改存央行**。
//          原本 Debit 之後 token 直接消失（純 sink）；現在對央行帳戶補一筆等額 Credit
//          → 從「燒掉」變成「集中到公庫，之後由活動再分配」。
// 數值影響：⚠ 這是**經濟模型層級的改變，不只是參數搬家**。
//          保管費是全系統最大的排水管道（截至改版當日 189 筆 / 35,932 token，
//          佔全部出帳的 97%，是 agent 主動消費總額 1,029 的 35 倍）。
//          它改成蓄水池之後，**這個經濟體暫時沒有任何 sink**。
//          通膨控制從「燒掉」換成「集中再放出」——
//          若哪天央行放款速度大於收費速度，通膨會回來，屆時消費菜單可能要重新扛 sink 職責。
//          （gura 2026-08-01 主張菜單偏 circulation 的理由是「已經有 sink 了」——
//            那個前提在本次改版後失效，結論仍成立但理由要換成「央行接手了那個職責」。
//            記在這裡是為了不讓未來的人拿舊理由當「永遠不需要 sink」的依據。）
//
// 📐 貨幣供給的完整圖（Tim 2026-08-01 拍板，**刻意只閉一半**）：
//      增發（憑空 credit）：commit 打款 +5 / 發文計酬 +1 / QA 獎勵 …（auto hook，維持不變）
//      回收 → 央行        ：跨日保管費（本檔）
//      央行 → agent        ：請款核准撥款（UCL_TreasuryRequestStore.Approve，央行不足即拒絕）
//      蒸發               ：**央行活動**（尚未實作 —— Tim：「之後會有一些蒸發 credit 的地方」）
//    亦即：日常勞動報酬**刻意保持體外增發**，不受央行餘額影響 ——
//    讓「今天有沒有薪水」取決於公庫水位，會把可預測的報酬變成賭博。
//    通膨則由央行活動端的蒸發來收。**這是有意的半閉環，不是還沒做完的閉環。**
// 設計取捨：跟 UCL_ChatTavernSettings 同形狀（JSON + 原子寫 + 讀失敗回預設），
//          不塞進 Treasury/rules.json —— 那份是經濟規則宣告，混進可調參數會讓
//          「誰是真相源」再糊一次（rules.json 自己就有過分類擺錯的舊帳 ——
//          三項 QA 獎金是 credit 卻掛在 spending_uses 底下；那三項已於 2026-08-04
//          隨 QA 獎金功能移除，但「宣告與實際用途會漂」這個風險本身沒消失）。
#if UNITY_EDITOR
using System.IO;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Treasury
{
    /// <summary>
    /// 央行帳號與保管費參數。UCL_BartenderDaemon 跨日結算時取用；
    /// UCL_BankAdminPage「🏦 央行 / 保管費」面板可調；Python 端讀同一份 JSON。
    /// </summary>
    public static class UCL_CentralBankSettings
    {
        /// <summary>央行帳號 id（Tim 2026-08-01 命名：Pacific Standard Public Deposit Bank）。</summary>
        public const string DefaultCentralBankAccount = "pacific-standard-public-deposit-bank";

        /// <summary>預設央行的顯示名 —— 只在「央行仍是預設帳戶、且它沒有帳戶資料」時才用得到。</summary>
        public const string DefaultCentralBankDisplayName = "Pacific Standard Public Deposit Bank";

        // 區塊職責：央行的顯示名。
        // 物理意義：真相源是**帳戶資料**（`Treasury/accounts/<id>.json` 的 display_name，
        //          與酒館／Discord 的署名同一個來源）。
        // 🩸 為什麼不能是常數：央行帳戶可被設定（見 SetCentralBankAccount）——
        //   常數會在換了央行之後繼續顯示舊名字，而那是一個**看起來完全正常的錯誤**
        //   （判準⑤：別造一個名字比事實大的東西）。
        public static string CentralBankDisplayName
        {
            get
            {
                string acc = CentralBankAccount;
                string dn = UCL_BankAccountProfileIO.GetDisplayName(acc);
                if (!string.IsNullOrEmpty(dn)) return dn;
                return acc == DefaultCentralBankAccount ? DefaultCentralBankDisplayName : acc;
            }
        }

        // 預設值＝改版前的既有行為，本次只把硬編搬成可調參數，不偷改數字。
        public const int DefaultThreshold = 1000;
        /// <summary>費率以**千分比整數**存（50 = 5.0%）。</summary>
        /// <remarks>
        /// 為什麼不存 double：本 repo 的 JsonData 只有無參數的 `GetDouble()`，
        /// 帶 key + 預設值的多載**只在 GetInt / GetString 上驗證過**
        /// （UCL_ChatTavernSettings 用的就是那兩個）。為了一個小數點賭一個沒人用過的多載，
        /// 換來的是編譯期才發現、或更糟——執行期靜默拿到 0 的費率。
        /// 千分比整數同時給到 0.1% 的調整粒度，UI 仍以 % 顯示。
        /// </remarks>
        public const int DefaultFeePermille = 50;      // 5.0%

        /// <summary>費率下限 0 —— 等於停收（合法的關閉手段，不必改 code）。</summary>
        public const int MinFeePermille = 0;
        /// <summary>費率上限 500‰ = 50%。再高一晚就砍半，那不是保管費是沒收。</summary>
        public const int MaxFeePermille = 500;
        /// <summary>門檻下限 0 —— 等於全額計費。</summary>
        public const int MinThreshold = 0;

        public const string SettingsFileName = "bank_settings.json";

        static string SettingsPath =>
            Path.Combine(UCL_AgentCommandsPath.DataRoot, "Treasury", SettingsFileName);

        /// <summary>設定檔所在目錄 —— 給後台「開啟設定檔位置」用（路徑只有一個擁有者，不讓頁面自己拼）。</summary>
        public static string SettingsDir => Path.GetDirectoryName(SettingsPath);

        /// <summary>央行帳號 id。</summary>
        public static string CentralBankAccount
        {
            get
            {
                var jd = Load();
                if (jd != null && jd.Contains("central_bank_account"))
                {
                    string v = jd.GetString("central_bank_account", DefaultCentralBankAccount);
                    if (!string.IsNullOrEmpty(v)) return v.Trim();
                }
                return DefaultCentralBankAccount;
            }
        }

        // 區塊職責：改設央行帳戶（Tim 2026-08-20：要能選，不要寫死一個帳號）。
        // 物理意義：**這個值決定錢從哪裡撥出來** —— 後台打款、請款核准、跨日保管費的去處全看它。
        // 數值影響：改完之後所有撥款來源立刻換帳戶；不搬任何一分錢（舊央行的餘額原地不動）。
        // 為什麼寫成帶 out err 的方法而不是 setter：它會**拒絕**不合法的值，
        //   而 property setter 沒有地方講「為什麼沒寫進去」—— 靜默不寫比寫錯更難查。
        public static bool SetCentralBankAccount(string iAccount, out string oError)
        {
            oError = null;
            string acc = (iAccount ?? "").Trim();
            if (string.IsNullOrEmpty(acc)) { oError = "央行帳戶不可為空"; return false; }
            if (!UCL_BankAccountProfileIO.IsValidAccountId(acc))
            { oError = $"`{acc}` 含不能當檔名的字元（帳戶 id 要能當一帳一檔的檔名）"; return false; }
            if (acc == CentralBankAccount) { oError = $"`{acc}` 已經是央行，未變更"; return false; }
            SetString("central_bank_account", acc);
            // 印 ✓ 不算數，讀回來才算。
            string back = CentralBankAccount;
            if (back != acc) { oError = $"寫入後讀回不符：期望 `{acc}`、實際 `{back}`"; return false; }
            // 央行依定義是 canonical 帳戶 ⇒ 換人之後解析器要重新認識它，否則新央行會被當孤兒。
            UCL_TreasuryAccountResolver.Invalidate();
            return true;
        }

        /// <summary>超過這個餘額的部分才收保管費。</summary>
        public static int OvernightThreshold
        {
            get
            {
                var jd = Load();
                int v = (jd != null && jd.Contains("overnight_threshold"))
                    ? jd.GetInt("overnight_threshold", DefaultThreshold) : DefaultThreshold;
                return v < MinThreshold ? MinThreshold : v;
            }
            set => SetInt("overnight_threshold", value < MinThreshold ? MinThreshold : value);
        }

        /// <summary>超額部分的費率，千分比整數（50 = 5.0%）。</summary>
        public static int OvernightFeePermille
        {
            get
            {
                var jd = Load();
                int v = (jd != null && jd.Contains("overnight_fee_permille"))
                    ? jd.GetInt("overnight_fee_permille", DefaultFeePermille) : DefaultFeePermille;
                return ClampPermille(v);
            }
            set => SetInt("overnight_fee_permille", ClampPermille(value));
        }

        /// <summary>費率的小數形式（供計算用）：50‰ → 0.05。</summary>
        public static double OvernightFeeRate => OvernightFeePermille / 1000.0;

        /// <summary>費率的顯示字串（供 UI / 廣播用）：50‰ → "5"、25‰ → "2.5"。</summary>
        public static string FeeRateDisplay
        {
            get
            {
                int p = OvernightFeePermille;
                return (p % 10 == 0) ? (p / 10).ToString() : (p / 10.0).ToString("0.#");
            }
        }

        /// <summary>
        /// 央行自己免收保管費（Tim 2026-08-01 拍板：豁免，並在稽核廣播列出增額）。
        /// </summary>
        /// <remarks>
        /// 不豁免的話 debit 與 credit 會落在同一個帳號 —— 帳面淨額為零卻多兩筆 ledger，
        /// 是一筆沒有物理意義的帳。豁免必須**在廣播裡明講**：
        /// 靜默的豁免下次就沒有人記得為什麼那個帳號不在扣費名單上。
        /// </remarks>
        public static bool ExemptCentralBank
        {
            get
            {
                // 存 0/1 而非 bool —— 理由同費率：只用驗證過的 GetInt 多載（見 DefaultFeePermille 註解）
                var jd = Load();
                int v = (jd != null && jd.Contains("exempt_central_bank"))
                    ? jd.GetInt("exempt_central_bank", 1) : 1;
                return v != 0;
            }
            set => SetInt("exempt_central_bank", value ? 1 : 0);
        }

        /// <summary>付費掛號信件每封費用（Tim 2026-08-01；預設 5 token，後台可調）。</summary>
        /// <remarks>
        /// 0 = 免費寄信（合法設定，不是壞值）。
        /// ⚠ 這筆錢**蒸發，不進央行**（Tim 2026-08-01 明確指定：「掛號信費用(蒸發)」，
        ///   「蒸發代表 token 消失，不進入央行」）—— 也就是純 debit 無對應 credit。
        /// 這是保管費改制之後這個經濟體的**第一個真 sink**，職責跟央行相反：
        ///   央行 = 集中再分配（circulation）；掛號信費 = 真的減少貨幣總量（burn）。
        /// 兩者刻意分開，別看到「都是收費」就把它也導進央行 —— 那會讓系統再次沒有 sink。
        /// </remarks>
        public const int DefaultRegisteredMailFee = 5;

        public static int RegisteredMailFee
        {
            get
            {
                var jd = Load();
                int v = (jd != null && jd.Contains("registered_mail_fee"))
                    ? jd.GetInt("registered_mail_fee", DefaultRegisteredMailFee) : DefaultRegisteredMailFee;
                return v < 0 ? 0 : v;
            }
            set => SetInt("registered_mail_fee", value < 0 ? 0 : value);
        }

        // ===========================================================
        // ⚠ 帳號解析模式開關（`account_resolve_unified`）已於 2026-08-20 移除（Tim 拍板）。
        //   兩個專案都跑完合一遷移 ⇒ 合一是**唯一**模式，解析一律 persona → agent（一跳）。
        //   留著一條可切回去的舊鏈，會讓「已合一」與「還在過渡」在讀數上長得一模一樣。
        //   資料檔裡殘留的 `account_resolve_unified` 鍵不再被任何人讀取（無害，下次改設定自然掉）。
        //   舊行為的實作與血證見 git：`UCL_TreasuryAccountResolver` 的 ⓪ 段。

        // 區塊職責：本專案的**區域（貨幣）ID** —— 即 `letters/<persona>/bank/<CurrencyId>.md` 的檔名。
        // 物理意義：Tim 2026-08-20 拍板 —— 銀行（酒館系統）**每個專案有自己的 ID**（可理解為貨幣名），
        //          而 persona 在各區域使用的**帳號**存在它自己的 letters 底下、**一區一檔**。
        //          ⚠ 「agent id」這個舊詞同批退場：**帳號就是 agent id**，
        //            `cc` / `zeta` / `a` 那套獨立命名不再是任何人的 canonical
        //            （改名走 ledger transfer，見 Plan_Identity_Account_Unification §4.2 D）。
        //          🩸 為什麼「一區一檔」是硬需求而不是風格：persona 的 letters 是**同一個 git repo
        //            被多個專案掛著**（2026-08-20 實測 LY 與 D:/Unity/Bar 的 letters/kiara
        //            root commit 與 HEAD 完全相同）⇒ 存「單一值」的檔會被兩個專案**互相覆寫**，
        //            而症狀是「另一個專案的帳號」—— 一個完全合法的字串，沒有任何一層會出聲。
        // 數值影響：**本值是檔名。** 改它等於把全體 persona 的綁定檔重新定鍵 ⇒
        //          後台改動走二段確認，且必須同批改名 letters 底下的檔，否則全員一次落央行。
        //          預設 `Ducat`；本專案（LY）＝ `Florin`
        //          （Tim 2026-08-20 命名：1252 年由佛羅倫斯共和國鑄造，杜卡特的一生宿敵與前輩）。
        // ===========================================================
        public const string DefaultCurrencyId = "Ducat";

        /// <summary>本專案的區域（貨幣）ID。缺值／不合法一律回預設，**不猜**。</summary>
        public static string CurrencyId
        {
            get
            {
                var jd = Load();
                if (jd != null && jd.Contains("currency_id"))
                {
                    string v = jd.GetString("currency_id", DefaultCurrencyId);
                    if (IsValidCurrencyId(v)) return v.Trim();
                    // 落盤值壞掉要出聲：靜默回預設會讓兩個專案都變成 Ducat，
                    // 而那正是一區一檔要防的對撞（且症狀是「另一個專案的帳號」）。
                    Debug.LogError($"[CentralBankSettings] currency_id 落盤值不合法（'{v}'），本次改用預設 " +
                                   $"'{DefaultCurrencyId}' —— 請到 UCL_BankAdminPage 修正。");
                }
                return DefaultCurrencyId;
            }
            set
            {
                if (!IsValidCurrencyId(value))
                {
                    Debug.LogError($"[CentralBankSettings] 區域（貨幣）ID 不合法，**未寫入**：'{value}'");
                    return;
                }
                SetString("currency_id", value.Trim());
            }
        }

        /// <summary>合法性＝能安全當檔名。空白／`.`／`..`／路徑分隔／檔名非法字元一律拒。</summary>
        /// <remarks>
        /// 它會被組進 `letters/&lt;persona&gt;/bank/&lt;id&gt;.md` ⇒ 含 `/` 或 `..` 就是寫到別的地方去，
        /// 而寫檔會自動建目錄 ⇒ 症狀是「憑空長出一個資料夾」而不是錯誤（2026-08-17 血證同族）。
        /// </remarks>
        public static bool IsValidCurrencyId(string iId)
        {
            if (string.IsNullOrWhiteSpace(iId)) return false;
            string v = iId.Trim();
            if (v == "." || v == "..") return false;
            if (v.IndexOf('/') >= 0 || v.IndexOf('\\') >= 0) return false;
            foreach (char c in Path.GetInvalidFileNameChars())
                if (v.IndexOf(c) >= 0) return false;
            return true;
        }

        public static int ClampPermille(int v)
            => v < MinFeePermille ? MinFeePermille : (v > MaxFeePermille ? MaxFeePermille : v);

        static JsonData Load()
        {
            try
            {
                if (File.Exists(SettingsPath)) return JsonData.ParseJson(File.ReadAllText(SettingsPath));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[CentralBankSettings] 讀取失敗（改用預設）: {e.Message}");
            }
            return JsonData.ParseJson("{}");
        }

        static void SetInt(string key, int value) => Write(jd => jd[key] = new JsonData(value));

        static void SetString(string key, string value) => Write(jd => jd[key] = new JsonData(value));

        static void Write(System.Action<JsonData> mutate)
        {
            var jd = Load();
            mutate(jd);
            try
            {
                string dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // 原子寫：tmp + replace，避免 Python 端剛好讀到寫一半的檔
                string tmp = SettingsPath + ".tmp";
                File.WriteAllText(tmp, jd.ToJsonBeautify());
                if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
                File.Move(tmp, SettingsPath);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[CentralBankSettings] 寫入失敗: {e.Message}");
            }
        }
    }
}
#endif
