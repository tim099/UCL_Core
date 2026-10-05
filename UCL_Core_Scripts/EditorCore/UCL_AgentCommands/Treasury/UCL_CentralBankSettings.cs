// 區塊職責：Unity 這一側的**區域（貨幣）ID** 唯讀查詢 —— 讀取與合法性規則**轉呼叫 SCP_Core**，本檔不自己讀檔。
// ⭐ TASK-0330（2026-09-29）：央行設定的讀取收成一份 —— 貨幣 ID ＝ `SCP_BankRegion`、
//   央行帳號／掛號信費 ＝ `SCP_BankPolicy`（都在 `SCP_Core/Runtime/Bank/`，讀同一顆 `Bank/bank_settings.json`）。
//   Unity 這份的常數、夾值規則與讀檔整段移除（央行帳號／掛號信費在 Unity 已經沒有呼叫端）。
//   🩸 為什麼不留一份鏡像：兩份夾值規則漂掉時**兩邊都讀得出合法的數字**，而誰是權威在畫面上看不出來。
// ⭐ TASK-0278（2026-09-22）：保管費的門檻／費率／央行豁免同樣只在 `SCP_BankPolicy`，
//   扣繳整段在 Senate 端跑（`SCP_Demurrage`／Senate Server 的每日結算）。
//   ⇒ 底下那段經濟模型的敘述**仍然成立**（它講的是錢往哪流），只是實作換了宿主。
// 物理意義：保管費原本是寫死在程式裡的兩個 const（threshold=1000 / rate=5%），
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
//      回收 → 央行        ：跨日保管費（參數在 `SCP_BankPolicy`）
//      央行 → agent        ：請款核准撥款（Senate `bank op=approve`，央行不足即拒絕；Unity 端的 RequestStore 已於 TASK-0327 刪除）
//      蒸發               ：**央行活動**（尚未實作 —— Tim：「之後會有一些蒸發 credit 的地方」）
//    亦即：日常勞動報酬**刻意保持體外增發**，不受央行餘額影響 ——
//    讓「今天有沒有薪水」取決於公庫水位，會把可預測的報酬變成賭博。
//    通膨則由央行活動端的蒸發來收。**這是有意的半閉環，不是還沒做完的閉環。**
// 設計取捨：可調參數落 `Bank/bank_settings.json`，
//          不塞進 Treasury/rules.json —— 那份是經濟規則宣告，混進可調參數會讓
//          「誰是真相源」再糊一次（rules.json 自己就有過分類擺錯的舊帳 ——
//          三項 QA 獎金是 credit 卻掛在 spending_uses 底下；那三項已於 2026-08-04
//          隨 QA 獎金功能移除，但「宣告與實際用途會漂」這個風險本身沒消失）。
#if UNITY_EDITOR
using SCP.Core.Bank;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Treasury
{
    /// <summary>
    /// 區域（貨幣）ID 的唯讀查詢 —— 轉呼叫 <c>SCP_BankRegion</c>。
    /// <para>⛔ 央行帳號／掛號信費／保管費參數**不在這裡** —— 見 <c>SCP_BankPolicy</c>（TASK-0278／TASK-0330）。</para>
    /// </summary>
    public static class UCL_CentralBankSettings
    {
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
        //          預設 `SCP_BankRegion.DefaultRegion`（`Ducat`）；本專案（LY）＝ `Florin`
        //          （Tim 2026-08-20 命名：1252 年由佛羅倫斯共和國鑄造，杜卡特的一生宿敵與前輩）。

        /// <summary>本專案的區域（貨幣）ID。缺值／不合法一律回預設，**不猜**。</summary>
        public static string CurrencyId
        {
            get
            {
                string aId = SCP_BankRegion.Read(UCL_AgentCommandsPath.DataRoot, out string aWhy);
                // ⚠ 缺檔／缺那一格（未設定）不出聲，跟修前一樣；**狀態壞了**才出聲 ——
                //   靜默回預設會讓兩個專案都變成 Ducat，而那正是一區一檔要防的對撞。
                //   判準借 `SCP_BankRegion.Read` 說明字串裡的兩個詞（不合法／讀不了）；
                //   ⛔ 別在這裡另寫一份「什麼算壞」—— 那會是第二份規則。
                if (aWhy != null)
                {
                    if (aWhy.Contains("不合法"))
                        Debug.LogError($"[CentralBankSettings] {aWhy} —— 請直接修設定檔。");
                    else if (aWhy.Contains("讀不了"))
                        Debug.LogWarning($"[CentralBankSettings] {aWhy}");
                }
                return aId;
            }
        }

        /// <summary>合法性＝能安全當檔名 —— 與 <c>SCP_BankRegion.IsValid</c> 同一條規則（轉呼叫，不另寫一份）。</summary>
        /// <remarks>
        /// 它會被組進 `letters/&lt;persona&gt;/bank/&lt;id&gt;.md` ⇒ 含 `/` 或 `..` 就是寫到別的地方去，
        /// 而寫檔會自動建目錄 ⇒ 症狀是「憑空長出一個資料夾」而不是錯誤（2026-08-17 血證同族）。
        /// </remarks>
        public static bool IsValidCurrencyId(string iId) => SCP_BankRegion.IsValid(iId);
    }
}
#endif
