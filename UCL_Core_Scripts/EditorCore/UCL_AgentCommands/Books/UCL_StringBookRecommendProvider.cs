// 區塊職責：UCL_StringProvider 子類 —— 從本專案圖書館藏書中隨機挑 N 本，回傳書名清單字串。
// 物理意義：藏書事實源是 `AgentCommands/Books/*/_donation.json`，讀取一律走既有的
//          UCL_BooksIO.LoadDonations()（唯一讀取點），本檔**不自己掃目錄、不自己 parse JSON** ——
//          自己重掃一份的話，哪天 BooksRoot 或欄位兜底規則改了，這裡會安靜地跟真相分岔。
// 數值影響：純唯讀。每次 GetString() **重新抽樣**，結果會變（見下方 remarks 的取捨說明）。
//
// ⚠ 為什麼放在 EditorCore 而不是 ProviderCore：
//    UCL_BooksIO 是 `#if UNITY_EDITOR` 的 Editor 端工具，本子類因此也只能在 Editor 存在。
//    放進 ProviderCore 會讓那個 runtime 層反過來依賴 Editor 層（層級倒置，且 build 會編不過）；
//    放在它依賴的東西旁邊，「這是 Editor-only」從路徑就看得出來。
//    ⇒ 後果：以此 provider 存下的資料在 **build 後的 runtime 還原不回來**（型別不存在）。
//      目前唯一消費端是酒保時間規則（Editor-only 工具），符合前提；
//      日後若有 runtime 消費端，要先把藏書讀取搬到 runtime 層才能沿用。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Books
{
    /// <summary>
    /// 從圖書館藏書中隨機挑 <see cref="m_Count"/> 本，回傳書名（每本一行）。
    /// </summary>
    /// <remarks>
    /// 物理意義：給「每日推薦書單」這類用途 —— 內容不是寫死的，而是每次求值當場抽。
    /// 數值影響：**每次 GetString() 都重新抽樣**，所以同一個 provider 連續呼叫兩次結果不同。
    ///          這是刻意的（推薦本來就該換），但代價要知道：
    ///          **編輯頁的預覽與實際廣播會抽到不同的書**。要對照的是「格式」不是「哪幾本」。
    /// 邊界：找不到圖書館目錄、或一本書都沒有 → 回傳 <see cref="string.Empty"/>（不是錯誤，
    ///      也不印 warning —— 沒有藏書是合法狀態，不該讓提醒訊息長出一段雜訊）。
    /// </remarks>
    [HelpURL("ucl_core:Docs~/{lang}/API/ProviderCore/UCL_StringBookRecommendProvider.md")]
    public class UCL_StringBookRecommendProvider : UCL_StringProvider
    {
        /// <summary>要推薦幾本。</summary>
        /// <remarks>
        /// 數值影響：實際取出的本數是 min(m_Count, 藏書數)；藏書不足時取全部，不補空行。
        ///          ≤ 0 視為 0 本 → 回傳空字串（讓「暫時不要推薦」有個表達方式，而不是報錯）。
        /// </remarks>
        [Header("推薦本數")]
        [SerializeField] private int m_Count = 10;

        /// <summary>每本之間的分隔字串。</summary>
        /// <remarks>
        /// 物理意義：預設換行 —— 消費端（如酒保提醒內文）本來就以行為單位，
        ///          一個 provider 展開成多行是預期用法。要排成一行可改成 "、"。
        /// </remarks>
        [Header("書名之間的分隔")]
        [SerializeField] private string m_Separator = "\n";

        public UCL_StringBookRecommendProvider() { }

        public UCL_StringBookRecommendProvider(int iCount)
        {
            m_Count = iCount;
        }

        public override string GetString()
        {
            if (m_Count <= 0) return string.Empty;

            // 走既有唯一讀取點；warnings 這裡刻意不收 —— 壞掉的單本書不該讓整段推薦消失，
            // 而壞檔的回報責任在 op=donations 那條路徑（它會列進 WARNING 區）。
            List<JsonData> aBooks = UCL_BooksIO.LoadDonations();
            if (aBooks == null || aBooks.Count == 0) return string.Empty;   // 沒有圖書館 / 沒有書

            // 取書名：title 缺就退回 book（資料夾名）—— 與 UCL_BooksIO.RenderDonations 同一套兜底，
            // 兩邊顯示的書名才不會一邊有一邊沒有。
            var aTitles = new List<string>(aBooks.Count);
            foreach (var aBook in aBooks)
            {
                if (aBook == null) continue;
                string aTitle = aBook.GetString(UCL_BooksIO.Key_Title, aBook.GetString(UCL_BooksIO.Key_Book, ""));
                if (!string.IsNullOrEmpty(aTitle)) aTitles.Add(aTitle);
            }
            if (aTitles.Count == 0) return string.Empty;

            // 部分 Fisher-Yates：只洗前 aTake 個位置就夠，不必洗完整份。
            int aTake = Mathf.Min(m_Count, aTitles.Count);
            for (int i = 0; i < aTake; i++)
            {
                int aSwap = Random.Range(i, aTitles.Count);
                (aTitles[i], aTitles[aSwap]) = (aTitles[aSwap], aTitles[i]);
            }

            var aSb = new StringBuilder();
            for (int i = 0; i < aTake; i++)
            {
                if (i > 0) aSb.Append(m_Separator ?? "\n");
                aSb.Append('《').Append(aTitles[i]).Append('》');
            }
            return aSb.ToString();
        }

        public override string ToString() => $"隨機推薦 {m_Count} 本藏書";
    }
}
#endif
