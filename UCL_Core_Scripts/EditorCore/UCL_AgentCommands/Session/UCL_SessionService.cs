
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 08/18 2026
// Session 統一管理層：路徑解析 / 讀寫 / 收工 / 「這個 persona 現在在哪種 session」。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;
// NowIso() 在 Awakening 命名空間 —— 收工時刻的格式全系統一份，不在這裡自己 ToString("o")
using UCL.Core.EditorLib.AgentCommands.Awakening;

namespace UCL.Core.EditorLib.AgentCommands
{
    // ===========================================================
    // 區塊職責：session 種類的登記處（kind ＝ 資料夾名 ＝ 顯示名）。
    // 物理意義：kind 字串同時是 `<DataRoot>/<kind>/sessions/<persona>.json` 的路徑段。
    //          登記制而不是掃資料夾：掃出來的東西無法判斷「那是我們認得的 session
    //          還是別人放的同名資料夾」，而**認錯一個資料夾就會回報一個不存在的 session**。
    // 數值影響：純常數 + 清單；新增種類就在 Kinds 加一筆。
    // ===========================================================
    public static class UCL_SessionKind
    {
        /// <summary>自由時間（Cmd_FreeTime 管理，本 service 的首位租客）。</summary>
        public const string FreeTime = "FreeTime";
        /// <summary>觀影（Cmd_StreamWatch）。⚠ 目前**尚未**改用本 service 寫入，見 Kinds 註解。</summary>
        public const string StreamWatch = "StreamWatch";

        // ⚠ **只列已經確認 schema 對得上的種類。**
        // FreeTime：已改由 UCL_FreeTimeSession 讀寫，欄位對齊（2026-08-18 round-trip 實測）。
        // StreamWatch：檔案裡確實有 active，但 session_id / end_ts / until_local 是否齊備
        //   **我沒有實測**（撰寫時磁碟上沒有任何進行中的觀影 session 可對帳）——
        //   所以它不在 Kinds 裡。列進去會讓查詢結果多一格「看起來查過了」的假讀數：
        //   欄位缺席時 typed model 只會拿到預設值，而 active=false 跟「沒這場」長得一樣。
        //   ⇒ 要納管請先實測一場，再把它加進來。
        public static readonly string[] Kinds = { FreeTime };
    }

    // ===========================================================
    // 區塊職責：所有 session 的單一操作入口 —— 路徑 / 讀 / 寫 / 收工 / 現況查詢。
    // 物理意義：在此之前「自由時間 session 檔在哪」被寫死在**三個地方**
    //          （Cmd_FreeTime、UCL_FreeTimeGating、Cmd_Sculpture），三份各自 Path.Combine。
    //          那種重複的失敗是靜默的：改了一處、另兩處指向舊位置，然後兩邊各自運作、都不報錯。
    // 數值影響：檔案格式與位置完全不變（路徑組法逐字相同），只是收成一處。
    // ===========================================================
    public static class UCL_SessionService
    {
        // ===========================================================
        // 區塊職責：session 檔的唯一路徑組法 —— **一人一檔位**。
        // 物理意義（TASK-0054 拍板⑤）：路徑從 `<DataRoot>/<kind>/sessions/<persona>.json`
        //          扁平化為 `<DataRoot>/sessions/<persona>.json`，kind 改存進 json 欄位。
        //          ⇒ 「同一個人同時兩種 session」變成**資料形狀層的不可能**，而不是靠守衛擋。
        //          （守衛仍在，它負責擋下並指路 —— 但那是操作層；這裡是形狀層。）
        // ⚠ **路徑不再吃 kind**：留一個不影響結果的參數就是死參數，而死參數會被下一個人
        //   讀成「這裡有做 kind 隔離」。⇒ 要 kind 的地方一律去讀檔案裡的欄位。
        // ⚠ 不做 migration（Tim 拍板）：舊 `<Kind>/sessions/` 的檔不搬不轉，
        //   切換後它們就不再被讀到。切之前要確認沒有進行中的場（active 全 false）。
        // ===========================================================
        /// <summary>`<DataRoot>/sessions/<persona>.json` —— 所有 session 檔的唯一路徑組法。</summary>
        public static string SessionPath(string iPersona)
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "sessions", $"{iPersona}.json");

        /// <summary>單一 sessions 資料夾（列舉用）。</summary>
        public static string SessionsDir()
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "sessions");

        // ===========================================================
        // 區塊職責：讀一份 session（不存在 / 壞檔一律回 null）。
        // 物理意義：回 null 的語意是「**沒有可用的 session 資料**」，不是「這個人不在 session」——
        //          呼叫端要自己再問 IsRunningAt（active 為 true 但過期的檔會被讀出來）。
        // 數值影響：純讀取；壞檔印 warning 不丟例外（一份壞檔不該讓整個 step 死掉）。
        // ===========================================================
        // ⚠ 扁平化之後 `iKind` **仍然有實際作用，而且作用變了**：它不再選路徑，而是**過濾**。
        //   一人一檔位 ⇒ 讀到的可能是這個人**別種** session 的檔。
        //   對「他在不在這個 kind」這個問題，那份檔的答案是**不在** ⇒ 回 null。
        //   🩸 若不比對就回傳，FreeTime 的額度判定會拿到一份 StreamWatch 的檔，
        //     而它有 active/end_ts ⇒ **判定會成功，只是量錯了東西**（沒有任何一層會喊）。
        // ⚠ 空 kind（舊檔沒有這個欄位）一律視為不符 —— 舊檔不該被當成任何 kind 的現行 session。
        public static T Load<T>(string iKind, string iPersona) where T : UCL_SessionBase, new()
        {
            try
            {
                string aPath = SessionPath(iPersona);
                if (!File.Exists(aPath)) return null;
                var aJson = JsonData.ParseJson(File.ReadAllText(aPath, Encoding.UTF8));
                if (aJson == null) return null;
                var aSession = new T();
                aSession.DeserializeFromJson(aJson);
                if (!string.IsNullOrEmpty(iKind)
                    && !string.Equals(aSession.kind, iKind, StringComparison.Ordinal)) return null;
                return aSession;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SessionService] {iKind}/{iPersona} session 讀取失敗（視為無 session）: {e.Message}");
                return null;
            }
        }

        // ⚠ `iKind` 在寫入端的作用是**落進 json 欄位**（扁平化後那是 kind 的唯一存放處）。
        //   在這裡蓋寫而不是要求呼叫端自己填：kind 與檔案位置本來由同一個動作決定，
        //   拆成兩個責任就會長出「檔在、kind 空」的檔，而那種檔讀取端一律當成不符 ⇒ 靜默消失。
        /// <summary>寫一份 session（atomic —— 半寫的 session 檔會讓下一次讀取判成無 session）。</summary>
        public static void Save(string iKind, string iPersona, UCL_SessionBase iSession)
        {
            if (iSession == null) return;
            if (!string.IsNullOrEmpty(iKind)) iSession.kind = iKind;
            string aPath = SessionPath(iPersona);
            Directory.CreateDirectory(Path.GetDirectoryName(aPath));
            AtomicWrite(aPath, iSession.SerializeToJson().ToJsonBeautify());
        }

        // ===========================================================
        // 區塊職責：收工 —— 翻 active、記原因與時刻、落盤。
        // 物理意義：收工是**三個欄位一起**的動作。散在各 Cmd 各寫一次時，漏掉 ended_at
        //          不會有任何症狀（沒人讀它），直到有人要對帳「這場實際跑多久」才發現沒紀錄。
        // 數值影響：寫一次檔。iReason 為 null 時存空字串（不存 "null" 字面）。
        // ===========================================================
        public static void Close(string iKind, string iPersona, UCL_SessionBase ioSession, string iReason)
        {
            if (ioSession == null) return;
            ioSession.active = false;
            ioSession.end_reason = iReason ?? "";
            ioSession.ended_at = UCL_AwakeningService.NowIso();
            Save(iKind, iPersona, ioSession);
        }

        // ===========================================================
        // 區塊職責：查「這個 persona 此刻在哪些 session 中」。
        // 物理意義：回傳的是**進行中**（active 且未過 end_ts）的種類清單，不是「檔案存在」的清單。
        // 數值影響：讀 Kinds.Length 個檔。
        // ⚠ 空清單的語意是「在**已登記的種類**裡沒查到」，不是「這個人絕對沒在任何 session」——
        //   未登記的種類（見 UCL_SessionKind.Kinds 註解）根本沒被看過。
        //   ⇒ 呼叫端回報時**必須一併說掃了哪些 kind**（ScannedKinds()），
        //     否則「沒查到」會被讀成「不在」，而那兩件事差很多。
        // ===========================================================
        public static List<KeyValuePair<string, UCL_SessionBase>> FindRunning(string iPersona)
        {
            // ⚠ 扁平化後這裡**只讀一個檔**（一人一檔位），不再遍歷 Kinds ——
            //   回傳仍是清單，因為「一人一場」是資料形狀給的保證，
            //   而呼叫端的問題（「他現在在哪些 session」）本身沒有變成單值。
            //   ⚠ 但仍過濾 `Kinds`：讀到一個未登記的 kind ⇒ 不列（同下方 ScannedKinds 的語意）。
            var aResult = new List<KeyValuePair<string, UCL_SessionBase>>();
            if (string.IsNullOrEmpty(iPersona)) return aResult;
            DateTime aNow = DateTime.Now;
            var aSession = Load<UCL_SessionBase>(null, iPersona);   // null ＝ 不過濾，先看它是哪一種
            if (aSession == null) return aResult;
            if (string.IsNullOrEmpty(aSession.kind)) return aResult;              // 舊檔／缺欄位：不當成任何 kind
            if (Array.IndexOf(UCL_SessionKind.Kinds, aSession.kind) < 0) return aResult;  // 未登記的種類
            if (aSession.IsRunningAt(aNow, out _))
            {
                aResult.Add(new KeyValuePair<string, UCL_SessionBase>(aSession.kind, aSession));
            }
            return aResult;
        }

        /// <summary>讀某 persona 在某 kind 的 session（不論進行中與否）—— 給後台頁列表用。</summary>
        public static UCL_SessionBase Peek(string iKind, string iPersona)
            => Load<UCL_SessionBase>(iKind, iPersona);

        /// <summary>本 service 實際會去看的種類（回報時要附上 —— 「沒查到」不等於「不在」）。</summary>
        public static string[] ScannedKinds() => UCL_SessionKind.Kinds;

        // ===========================================================
        // 區塊職責：列出某 kind 現有的所有 session 檔（persona 名）。
        // 物理意義：檔名即 persona。給後台頁做總覽用 —— 包含已收工的（那是歷史，不是雜訊）。
        // 數值影響：純列舉；資料夾不存在回空清單。
        // ===========================================================
        // ⚠ 扁平化後檔名不再帶 kind 資訊 ⇒ 必須**開檔讀 kind 欄位**才知道那是不是這一種。
        //   代價是列舉從「列目錄」變成「列目錄＋逐檔讀」；不付這個代價的話，
        //   後台頁的 FreeTime 列表會列出正在觀影的人 —— 而那看起來完全正常。
        public static List<string> ListPersonas(string iKind)
        {
            var aList = new List<string>();
            try
            {
                string aDir = SessionsDir();
                if (!Directory.Exists(aDir)) return aList;
                foreach (string aFile in Directory.GetFiles(aDir, "*.json"))
                {
                    string aPersona = Path.GetFileNameWithoutExtension(aFile);
                    if (!string.IsNullOrEmpty(iKind) && Load<UCL_SessionBase>(iKind, aPersona) == null) continue;
                    aList.Add(aPersona);
                }
                aList.Sort(StringComparer.Ordinal);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SessionService] 列舉 {iKind} sessions 失敗: {e.Message}");
            }
            return aList;
        }

        // ===========================================================
        // 區塊職責：原子寫入（先寫 .tmp 再 Replace/Move）。
        // 物理意義：session 檔被半寫時，下一次讀取會 parse 失敗 → 判成「無 session」
        //          ⇒ 那個人可以疊開第二場，而第一場的額度還掛在原處。
        // 數值影響：多一次暫存檔 IO；換來「讀到的一定是完整的一份」。
        // ===========================================================
        static void AtomicWrite(string iPath, string iContent)
        {
            string aTmp = iPath + ".tmp";
            File.WriteAllText(aTmp, iContent, new UTF8Encoding(false));
            if (File.Exists(iPath)) File.Delete(iPath);
            File.Move(aTmp, iPath);
        }
    }
}
#endif
