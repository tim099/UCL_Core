// 區塊職責：繪圖券的**批次**資料模型 —— 一次發券 ＝ 一個 batch，帶自己的到期時刻與剩餘量。
//
// 物理意義：券原本是一個純量 `balance`，於是「這幾張什麼時候過期」這件事**沒有地方可以存**。
//          Tim 2026-08-18 拍板期間限定券（免費像素要變成「綁自由時間場次、到期作廢」的券），
//          而到期是**每一批各自的**屬性 —— 同一個人可以同時持有永久券與兩批不同到期時刻的限時券。
//          ⇒ 餘額從「一個數字」變成「一組批次的剩餘量之和」，而**和誰求和取決於你在問什麼**
//            （永久？未過期的限時？可花總額？）—— 那三個問題的答案不同，見 UCL_CanvasVoucherLedger。
//
// 數值影響：`remain` 是這一批還沒被花掉的量（`amount` 是發放當時的量，保留供對帳）。
//          `expires_at` 空字串 ＝ **永久券**（不會過期）；非空 ＝ 限時券，過了就不能花。
//
// ⚠ 欄位名＝JSON 鍵名（`FieldNameUnityVer` 只脫 `m_`），而這份檔的讀取端**不只有 C#**
//   —— `canvas.py` 讀同一份檔算可花額度。改欄位名要同時改 python 端，
//   否則那邊拿到 0 而**不報錯**（「有券但算成沒券」跟「真的沒券」在輸出上一模一樣）。
// 2026-08-18 gura（Tim 拍板方案乙：balance 改推導、限時與永久讀取路徑分開）
using System;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.AgentCommands.CanvasVoucher
{
    /// <summary>
    /// 一批繪圖券（一次 grant ＝ 一批）。`expires_at` 空 ＝ 永久券。
    /// </summary>
    public class UCL_CanvasVoucherBatch : UnityJsonSerializable
    {
        /// <summary>批次 id（6-char hex，對齊 history 的 uuid 格式）。</summary>
        public string uuid = "";
        /// <summary>發放當時的數量（**不隨消費變動** —— 對帳時要知道原本發了多少）。</summary>
        public int amount = 0;
        /// <summary>還沒被花掉的量。`0` ＝ 這批花完了（寫入時會被清掉，history 仍留紀錄）。</summary>
        public int remain = 0;
        /// <summary>發放時刻 UTC ISO。</summary>
        public string granted_at = "";
        /// <summary>到期時刻 UTC ISO。**空字串 ＝ 永久券**（這是「永久」的唯一表示法，不要用別的值）。</summary>
        public string expires_at = "";
        /// <summary>發放來源（`admin_grant` / `freetime` / `book_tip` / `chess_win`…）。</summary>
        public string source = "";
        /// <summary>業務 ref（自由時間券填 session_id —— 那是「這批屬於哪一場」的憑據）。</summary>
        public string @ref = "";

        /// <summary>是否為永久券（`expires_at` 空）。</summary>
        public bool IsPermanent => string.IsNullOrEmpty(expires_at);

        // ===========================================================
        // 區塊職責：這批在 iNowUtc 時點還能不能花。
        // 物理意義：永久券恆可花。限時券比 `expires_at` —— **解析不出來時視為永久**（回 true）。
        // 數值影響：解析失敗選擇「可花」而不是「作廢」，因為壞掉的時戳讓人**損失已經持有的券**
        //          是不可逆的，而多讓他花一次只是帳面上多一筆可追的消費。
        //          ⇒ 壞資料的處置往「不奪走既有權益」那一邊倒。
        // ===========================================================
        public bool IsSpendableAt(DateTime iNowUtc)
        {
            if (remain <= 0) return false;
            if (IsPermanent) return true;
            if (!DateTime.TryParse(expires_at, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime aExp))
            {
                UnityEngine.Debug.LogWarning(
                    $"[CanvasVoucher] batch {uuid} 的 expires_at 解析不出來（'{expires_at}'）—— 視為永久券（不奪走既有權益）");
                return true;
            }
            return iNowUtc <= aExp.ToUniversalTime();
        }

        /// <summary>已過期且仍有剩餘（＝這一批將要／已經作廢的量）。</summary>
        public bool IsExpiredAt(DateTime iNowUtc) => remain > 0 && !IsPermanent && !IsSpendableAt(iNowUtc);
    }
}
