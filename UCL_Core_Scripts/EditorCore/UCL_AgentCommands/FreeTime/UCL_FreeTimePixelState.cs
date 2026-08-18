// 區塊職責：免費像素額度檔（`Canvas/freetime/<persona>.json`）的**路徑組法與唯讀讀取**。
// 物理意義：這份檔的路徑原本在三個地方各推導一次（Cmd_FreeTime / Cmd_Sculpture / canvas.py），
//          而 `granted - used` 這個算式也各寫一次。路徑重造的失敗是靜默的：
//          改一處另兩處指舊位置，兩邊都能各自運作、都不報錯（見 skill ucl-core-paths 的三則血證）。
//          ⇒ C# 這一端收攏成一份；python 端（canvas.py `load_freetime`）仍是獨立實作，
//            那是跨語言的對齊義務，不是本檔能消除的重複。
// 數值影響：純讀 + 路徑字串，不寫檔。**寫入端刻意不搬過來**（Grant / Forfeit 在 Cmd_FreeTime、
//          Consume 在 Cmd_Sculpture）—— 那些會動 `history` 陣列，而 history 的 wire format
//          由 python 消費端一起吃；搬動寫入路徑要拿真實舊檔 round-trip 驗過才算安全，
//          與「新增一個管理頁」不該混在同一次改動裡。
// 2026-08-18 gura（配套 UCL_FreeTimeAdminPage；接手 basecamp 的 freetime-cmd-flow 這條線）
#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.FreeTime
{
    // ===========================================================
    // 區塊職責：額度檔的 typed 讀取模型。
    // 物理意義：欄位名**就是 JSON 鍵名**（`FieldNameUnityVer` 只脫 `m_` 前綴），
    //          而這份檔的寫入端有三個（本 core 兩個 Cmd ＋ python 的 canvas.py）。
    //          ⇒ 改欄位名 = 讀不到值，而讀不到值長得跟「這個人沒有額度」一模一樣（回 0）。
    // 數值影響：`history` 欄位刻意**不宣告** —— 本型別只讀不寫，多出來的 JSON 鍵會被
    //          `LoadFieldFromJsonUnityVer` 忽略。宣告了反而要負擔它的序列化形狀。
    // ===========================================================
    /// <summary>
    /// 一份免費像素額度（`Canvas/freetime/&lt;persona&gt;.json`）的唯讀投影。
    /// ⚠ 欄位名＝JSON 鍵名，改名要同步 <c>canvas.py</c> 的 <c>load_freetime</c>。
    /// </summary>
    public class UCL_FreeTimePixelGrant : UnityJsonSerializable
    {
        /// <summary>額度屬於誰（＝檔名，冗餘存一份供人直讀 json 時對帳）。</summary>
        public string persona = "";
        /// <summary>綁定的場次 id。**額度不跨場** —— 與 session 的 session_id 不同即視為過期額度。</summary>
        public string session_id = "";
        /// <summary>本場發放顆數。</summary>
        public int granted = 0;
        /// <summary>本場已用顆數（canvas.py / Cmd_Sculpture 遞增）。</summary>
        public int used = 0;
        /// <summary>發放時刻 UTC ISO。</summary>
        public string granted_at = "";

        /// <summary>剩餘顆數（不會回負數 —— 收工作廢會把 granted 壓到 used，兩者相等）。</summary>
        public int Remain => Math.Max(0, granted - used);
    }

    /// <summary>
    /// 免費像素額度檔的路徑與唯讀讀取。**寫入端不在這裡**（見檔頭註解）。
    /// </summary>
    public static class UCL_FreeTimePixelState
    {
        /// <summary>`&lt;DataRoot&gt;/Canvas/freetime/&lt;persona&gt;.json` —— 額度檔的唯一路徑組法。</summary>
        public static string StatePath(string iPersona)
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "Canvas", "freetime", $"{iPersona}.json");

        /// <summary>額度檔所在資料夾（列舉 / 開資料夾用）。</summary>
        public static string StateDir()
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "Canvas", "freetime");

        // ===========================================================
        // 區塊職責：讀一份額度（不存在 / 壞檔一律回 null）。
        // 物理意義：回 null 的語意是「**沒有可用的額度資料**」，不是「這個人沒有額度」——
        //          呼叫端要能把兩者分開顯示，不然「還沒發過」跟「發了但用完」會長成同一格。
        // 數值影響：純讀；壞檔印 warning 不丟例外（一份壞檔不該讓整頁 / 整個 step 死掉）。
        // ===========================================================
        public static UCL_FreeTimePixelGrant Read(string iPersona)
        {
            try
            {
                string aPath = StatePath(iPersona);
                if (!File.Exists(aPath)) return null;
                var aJson = JsonData.ParseJson(File.ReadAllText(aPath, Encoding.UTF8));
                if (aJson == null) return null;
                var aGrant = new UCL_FreeTimePixelGrant();
                aGrant.DeserializeFromJson(aJson);
                return aGrant;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FreeTime] {iPersona} 免費像素額度讀取失敗（視為無額度資料）: {e.Message}");
                return null;
            }
        }
    }
}
#endif
