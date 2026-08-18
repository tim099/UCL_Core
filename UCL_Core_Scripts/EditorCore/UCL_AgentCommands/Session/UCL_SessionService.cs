
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
        /// <summary>`<DataRoot>/<kind>/sessions/<persona>.json` —— 所有 session 檔的唯一路徑組法。</summary>
        public static string SessionPath(string iKind, string iPersona)
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, iKind, "sessions", $"{iPersona}.json");

        /// <summary>該 kind 的 sessions 資料夾（列舉用）。</summary>
        public static string SessionsDir(string iKind)
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, iKind, "sessions");

        // ===========================================================
        // 區塊職責：讀一份 session（不存在 / 壞檔一律回 null）。
        // 物理意義：回 null 的語意是「**沒有可用的 session 資料**」，不是「這個人不在 session」——
        //          呼叫端要自己再問 IsRunningAt（active 為 true 但過期的檔會被讀出來）。
        // 數值影響：純讀取；壞檔印 warning 不丟例外（一份壞檔不該讓整個 step 死掉）。
        // ===========================================================
        public static T Load<T>(string iKind, string iPersona) where T : UCL_SessionBase, new()
        {
            try
            {
                string aPath = SessionPath(iKind, iPersona);
                if (!File.Exists(aPath)) return null;
                var aJson = JsonData.ParseJson(File.ReadAllText(aPath, Encoding.UTF8));
                if (aJson == null) return null;
                var aSession = new T();
                aSession.DeserializeFromJson(aJson);
                return aSession;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SessionService] {iKind}/{iPersona} session 讀取失敗（視為無 session）: {e.Message}");
                return null;
            }
        }

        /// <summary>寫一份 session（atomic —— 半寫的 session 檔會讓下一次讀取判成無 session）。</summary>
        public static void Save(string iKind, string iPersona, UCL_SessionBase iSession)
        {
            if (iSession == null) return;
            string aPath = SessionPath(iKind, iPersona);
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
            var aResult = new List<KeyValuePair<string, UCL_SessionBase>>();
            if (string.IsNullOrEmpty(iPersona)) return aResult;
            DateTime aNow = DateTime.Now;
            foreach (string aKind in UCL_SessionKind.Kinds)
            {
                var aSession = Load<UCL_SessionBase>(aKind, iPersona);
                if (aSession == null) continue;
                if (aSession.IsRunningAt(aNow, out _))
                {
                    aResult.Add(new KeyValuePair<string, UCL_SessionBase>(aKind, aSession));
                }
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
        public static List<string> ListPersonas(string iKind)
        {
            var aList = new List<string>();
            try
            {
                string aDir = SessionsDir(iKind);
                if (!Directory.Exists(aDir)) return aList;
                foreach (string aFile in Directory.GetFiles(aDir, "*.json"))
                {
                    aList.Add(Path.GetFileNameWithoutExtension(aFile));
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
