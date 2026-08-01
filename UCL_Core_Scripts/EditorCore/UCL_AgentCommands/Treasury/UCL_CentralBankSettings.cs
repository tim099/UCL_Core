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
//          「誰是真相源」再糊一次（而 rules.json 自己已經有分類擺錯的舊帳：
//          qa_bug_confirmed 等三項是 credit 卻掛在 spending_uses 底下）。
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

        /// <summary>央行顯示名 —— 廣播與後台用，ledger 仍以 account id 為準。</summary>
        public const string CentralBankDisplayName = "Pacific Standard Public Deposit Bank";

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
