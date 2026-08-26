
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 08/18 2026
// 所有「一場有起訖時間的 session」的共同資料模型（FreeTime / StreamWatch / …）。
#if UNITY_EDITOR
using System;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.AgentCommands
{
    // ===========================================================
    // 區塊職責：session 共通欄位 —— 誰的、哪一場、什麼時候開、什麼時候該收、收了沒。
    // 物理意義：各種 session（自由時間 / 觀影 / …）過去各自用 JsonData 逐鍵手搭，
    //          於是「這個人在不在 session 中」這個判斷在每個 Cmd 裡各寫一次，
    //          而每一次都要記得「只看 active 不夠、還要比 end_ts」。
    //          抽成 base class 之後那條判準只有一份（IsRunningAt）。
    // 數值影響：純資料 + 判斷，不碰 IO（IO 走 UCL_SessionService）。
    // ===========================================================
    /// <summary>
    /// 一場 session 的共通資料（`&lt;kind&gt;/sessions/&lt;persona&gt;.json` 的公同部分）。
    /// 各 session 類型繼承本類並加自己的欄位（例：自由時間加 <c>rounds</c>）。
    /// </summary>
    /// <remarks>
    /// ⚠⚠ **欄位名刻意不走 `m_PascalCase` 慣例** —— 這裡的欄位名**就是 JSON 的鍵名**
    /// （<see cref="UnityJsonSerializable"/> 走 `FieldNameUnityVer`，只脫 `m_` 前綴，其餘原樣輸出）。
    ///
    /// 讀取端現況（2026-08-26 起）：**只剩 C#**。曾經的 python 讀取端
    /// （freetime.py 判「在不在自由時間」、canvas.py 判免費像素）已依 Tim 拍板退役 ——
    /// python 不直讀 session，一律問 Cmd（`SessionStatus` 的機讀 values）。
    ///
    /// ⚠ 但改欄位名仍然不是免費的：磁碟上有既有 session 檔（鍵名即相容面），
    /// 改名＝舊檔讀回預設值，而 `active=false` 跟「沒這場」長得一樣。
    /// 要動 schema 走儲存統一那類的單（TASK-0054），不要順手改。
    /// </remarks>
    public class UCL_SessionBase : UnityJsonSerializable
    {
        /// <summary>這場屬於誰（＝檔名，冗餘存一份供人直讀 json 時對帳）。</summary>
        public string persona = "";
        /// <summary>場次 id。額度／統計類資料以此綁定場次。</summary>
        public string session_id = "";
        /// <summary>開場 UTC ISO。</summary>
        public string start_ts = "";
        /// <summary>預定收工 UTC ISO（`yyyy-MM-ddTHH:mm:ss.fffZ`）。python 端判在不在 session 會讀它。</summary>
        public string end_ts = "";
        /// <summary>預定收工的本地時刻字串（`yyyy-MM-dd HH:mm`）—— 給人讀的，不參與判定。</summary>
        public string until_local = "";
        /// <summary>是否仍在進行。⚠ 只看它不夠 —— 超時沒回來收工的人會一直停在 true（用 <see cref="IsRunningAt"/>）。</summary>
        public bool active = false;
        /// <summary>收工原因（未收工時為空字串）。</summary>
        public string end_reason = "";
        /// <summary>實際收工 UTC ISO（未收工時為空字串）。</summary>
        public string ended_at = "";

        // ===========================================================
        // 區塊職責：bool 欄位強制寫成**原生 JSON bool**，不是 "True"/"False" 字串。
        // 物理意義：UCL_Json 的舊慣例把 bool 存成字串（UCL_JsonLib 序列化端 `aValue.ToString()`），
        //          載入端雙接所以 C# 這邊看不出差別 —— **但 python 端看得出**：
        //          `json.loads` 讀到 `"active":"False"` 得到字串 `"False"`，而它在 Python 裡是 **truthy**。
        // 🩸 2026-08-18 實測（round-trip 既有 Sirius.json 才發現）：改用 typed model 之後
        //          `"active":false` 變成 `"active":"False"`。後果不是解析失敗（那會喊），
        //          是當時的 python 讀取端 `if not s.get("active")` 通過 ⇒ **提前收工的人**
        //          （end_ts 還在未來）會被判成「還在自由時間」，而且完全不報錯。
        //          （2026-08-26 python 讀取端已退役；本 override 留著 —— 原生 bool 是正確的
        //          wire format，且磁碟上既有檔已是這個形狀，拆掉反而製造第二種形。）
        // 數值影響：序列化後 active 為原生 true/false，與 typed model 之前的手搭格式逐鍵相同。
        //          ⇒ 既有檔不需遷移，python 兩端不受影響。
        // ⛔ 別把這個 override 當樣板套用：它存在的理由是**這份檔有非 C# 讀取端**。
        //   純 C# 內部使用的資料沿用 UCL 舊慣例即可（載入端雙接，不會出事）。
        // ===========================================================
        public override JsonData SerializeToJson()
        {
            var aData = base.SerializeToJson();
            aData["active"] = new JsonData(active);
            return aData;
        }

        // ===========================================================
        // 區塊職責：把「這場還算不算進行中」收成唯一一個判斷點。
        // 物理意義：`active` 只在有人真的跑收工步驟時才被翻成 false ——
        //          超時就消失的人會把 true 留在檔案裡。**光看 active 會把早就下線的人算成在線。**
        //          （同一條判準 python 端 `_is_in_free_time` 也寫著，兩邊講的是同一件事。）
        // 數值影響：純判斷不寫檔。end_ts 解析不出來時**回 true** —— 沒有截止欄位只能信 active；
        //          寧可誤判「還在」也不要把一場真的在跑的 session 當不存在（後者會讓人疊開第二場）。
        // ===========================================================
        /// <summary>此刻是否仍在進行（active 且未過 end_ts）。iNowLocal 傳本地時間。</summary>
        public bool IsRunningAt(DateTime iNowLocal, out DateTime? oEndLocal)
        {
            oEndLocal = ParseIsoToLocal(end_ts);
            if (!active) return false;
            if (!oEndLocal.HasValue) return true;
            return iNowLocal <= oEndLocal.Value;
        }

        /// <summary>此刻是否仍在進行（不需要收工時刻時的簡寫）。</summary>
        public bool IsRunningNow() => IsRunningAt(DateTime.Now, out _);

        /// <summary>把 UTC ISO 字串轉本地時間；解析不出來回 null（不丟例外 —— 壞欄位不該讓整個 step 死掉）。</summary>
        public static DateTime? ParseIsoToLocal(string iIso)
        {
            if (string.IsNullOrEmpty(iIso)) return null;
            return DateTime.TryParse(iIso, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime aDt)
                ? aDt.ToLocalTime() : (DateTime?)null;
        }
    }
}
#endif
