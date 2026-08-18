// 區塊職責：自由時間骰面的「可用性」與「優先層」判定（Tim 2026-08-17 拍板 enum 標記方案）。
// 物理意義：骰面原本是一視同仁的隨機排序 —— 但有些活動**根本做不了**（沒開播的陪看），
//          有些活動**此刻特別該做**（有未完成棋局、而對手剛好也在自由時間裡）。
//          兩者不是同一件事，所以判定分兩軸：
//            ① visible ＝ 不成立就**隱藏**（不列入候選）—— 用於「做不了」
//            ② priority ＝ 成立就進**最優先層**（層內仍隨機）—— 用於「此刻特別該做」
//          走哪條邏輯由活動 md 的 `kind` 決定（見 UCL_FreeTimeActivityKind）。
// 數值影響：只影響骰面的候選集合與排序，不寫任何 state；所有判定 fail-soft ——
//          讀不到棋局／session 一律當「條件不成立」（少一個推薦），
//          唯獨**隱藏**類判定要格外保守：誤判「沒直播」只是少一項，
//          誤判「有直播」會讓人跑去陪看一個不存在的節目（2026-07-30 孤兒旗標血證）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.FreeTime
{
    /// <summary>一個活動經過 kind 特殊邏輯後的判定結果。</summary>
    public struct UCL_FreeTimeGateResult
    {
        /// <summary>false ＝ 條件不成立，**整項從骰面隱藏**（不列入候選）。</summary>
        public bool visible;

        /// <summary>true ＝ 進最優先層（層內仍隨機排序 —— 兩層排序的上層）。</summary>
        public bool priority;

        /// <summary>要附加在活動名後面的字（本場節目名 / 優先理由 / 標記打錯警告）；可空。</summary>
        public string nameSuffix;
    }

    /// <summary>
    /// 依 <see cref="UCL_FreeTimeActivityKind"/> 執行特殊邏輯的判定器。
    /// <para>
    /// **新增一種 kind 要同時改兩個地方**（enum ＋ 本類別的 switch）—— 這是刻意的：
    /// 一個沒有實作的標記，會讓人以為那裡有一道邏輯，而它什麼都不做且不會喊。
    /// </para>
    /// </summary>
    public static class UCL_FreeTimeGating
    {
        /// <summary>
        /// 區塊職責：對單一活動跑 kind 對應的特殊邏輯。
        /// 物理意義：iPersona ＝ 正在擲骰的人（棋局類判定需要知道「誰的棋局」）。
        /// 數值影響：Default 一律 (visible, 非優先)；解析失敗的 kind 也走 Default，
        ///          但**掛上警告字尾讓它在骰面上顯形** —— 標記打錯而系統照常運作最難查。
        /// </summary>
        public static UCL_FreeTimeGateResult Evaluate(UCL_FreeTimeActivity iAct, string iPersona)
        {
            var aRes = new UCL_FreeTimeGateResult { visible = true, priority = false, nameSuffix = "" };
            if (iAct == null) return aRes;

            if (!string.IsNullOrEmpty(iAct.kindParseError))
                aRes.nameSuffix = $" ⚠（kind='{iAct.kindParseError}' 認不得，已當一般活動處理）";

            switch (iAct.kind)
            {
                case UCL_FreeTimeActivityKind.StreamWatch:
                    {
                        // 沒開播 → 隱藏。這是「隱藏」而非「排到尾端」的少數情形：
                        // 排尾端的前提是「做得成但不划算」，而沒直播是**根本做不了**。
                        bool aLive = TryGetLiveTitle(out string aTitle);
                        if (!aLive) { aRes.visible = false; return aRes; }
                        aRes.priority = true;
                        aRes.nameSuffix += string.IsNullOrEmpty(aTitle) ? "（直播中）" : $" 本場節目: {aTitle}";
                        return aRes;
                    }

                case UCL_FreeTimeActivityKind.Chess:
                    {
                        // 有未完成棋局、且對手也在自由時間 → 最優先。
                        // **不隱藏**：沒對手時仍可開新局徵人，下棋隨時做得成。
                        if (TryFindWaitingChess(iPersona, out string aOpponent, out int aGameIdx, out bool aMyTurn))
                        {
                            aRes.priority = true;
                            // 用「對方」不用「他」—— 骰面不該替沒說明稱謂的人做假設。
                            aRes.nameSuffix += aMyTurn
                                ? $" ♟ 第 {aGameIdx} 局輪到你，@{aOpponent} 也在自由時間"
                                : $" ♟ 第 {aGameIdx} 局進行中，@{aOpponent} 也在自由時間（等對方走）";
                        }
                        return aRes;
                    }

                default:
                    return aRes;
            }
        }

        // ===========================================================
        // 區塊：直播判定（原本內嵌在 Cmd_FreeTime，搬來集中）
        // 物理意義：「_live_info.json 存在 ＝ 直播中」這個不變式**只有 daemon 一方維護**，
        //          而停止錄影是直接 Process.Kill()，daemon 沒機會清旗標 → 每次停播留孤兒旗標。
        //          所以要跟 _config.json.enabled **對帳**：旗標在而開關關著是定義上的矛盾，
        //          這種矛盾一律當「沒直播」處理。
        // 數值影響：讀檔失敗一律回 false（fail-soft）。誤判沒直播只少一個推薦；
        //          誤判有直播 2026-07-28 那次讓三個 persona 連兩天被同一個假訊號誤導。
        // ⚠ 本判定在 freetime.py `_live_stream_info()` 有一份鏡像（純參考擲骰用）——
        //   改這裡要同步改那裡。
        // ===========================================================
        public static bool TryGetLiveTitle(out string oTitle)
        {
            oTitle = null;
            try
            {
                string aInfoPath = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", "_live_info.json");
                if (!File.Exists(aInfoPath)) return false;
                string aCfgPath = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", "_config.json");
                if (File.Exists(aCfgPath))
                {
                    var aCfg = JsonData.ParseJson(File.ReadAllText(aCfgPath, Encoding.UTF8));
                    if (aCfg != null && aCfg.Contains("enabled") && !(bool)aCfg["enabled"]) return false;
                }
                var aInfo = JsonData.ParseJson(File.ReadAllText(aInfoPath, Encoding.UTF8));
                if (aInfo != null && aInfo.Contains("stream_title")) oTitle = aInfo["stream_title"].ToString();
                return true;
            }
            catch (Exception) { return false; }
        }

        // ===========================================================
        // 區塊：棋局判定 —— 「我有未完成的局，而對手此刻也在自由時間裡」
        // 物理意義：下棋每一步都落盤，所以它沒有時間壓力（不設 min_minutes）；
        //          真正決定「現在該不該下」的不是剩幾分鐘，是**對手在不在**。
        //          對手在自由時間 ＝ 他此刻正在挑活動，一步棋馬上有人接
        //          —— 這才是把下棋頂到最優先的理由。
        // 數值影響：掃 <DataRoot>/Chess/games/*.json 取 status=in_progress 且我在 seats 的局；
        //          對手用自由時間 session（active 且未過 end_ts）判定。
        //          找到多局取**第一個成立的**（骰面只需要一個理由，不需要全部列出）。
        //          任何讀取失敗 → 回 false（少一個優先推薦，不炸擲骰）。
        // ===========================================================
        public static bool TryFindWaitingChess(string iPersona, out string oOpponent, out int oGameIndex, out bool oMyTurn)
        {
            oOpponent = null; oGameIndex = 0; oMyTurn = false;
            if (string.IsNullOrEmpty(iPersona)) return false;
            try
            {
                string aDir = Path.Combine(UCL_AgentCommandsPath.DataRoot, "Chess", "games");
                if (!Directory.Exists(aDir)) return false;
                foreach (var aFile in Directory.GetFiles(aDir, "*.json"))
                {
                    JsonData aGame;
                    try { aGame = JsonData.ParseJson(File.ReadAllText(aFile, Encoding.UTF8)); }
                    catch (Exception) { continue; }     // 單一壞檔不該讓整個判定失效
                    if (aGame == null) continue;
                    if (!string.Equals(Str(aGame, "status"), "in_progress", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!aGame.Contains("seats")) continue;

                    var aSeats = aGame["seats"];
                    string aWhite = Str(aSeats, "white");
                    string aBlack = Str(aSeats, "black");
                    bool aIAmWhite = string.Equals(aWhite, iPersona, StringComparison.OrdinalIgnoreCase);
                    bool aIAmBlack = string.Equals(aBlack, iPersona, StringComparison.OrdinalIgnoreCase);
                    if (!aIAmWhite && !aIAmBlack) continue;

                    string aOpp = aIAmWhite ? aBlack : aWhite;
                    // 空座位＝還在徵人，那不是「有對手在等」；單人 solo 局同理不算。
                    if (string.IsNullOrEmpty(aOpp) || string.Equals(aOpp, iPersona, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!IsInFreeTime(aOpp)) continue;

                    oOpponent = aOpp;
                    int.TryParse(Str(aGame, "index"), out oGameIndex);
                    // FEN 第二段是「輪到誰走」（w/b）——盤面自己就記著，不必另存回合欄。
                    string aFen = Str(aGame, "fen");
                    string[] aParts = aFen.Split(' ');
                    if (aParts.Length >= 2)
                        oMyTurn = (aParts[1] == "w" && aIAmWhite) || (aParts[1] == "b" && aIAmBlack);
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FreeTime] 棋局判定失敗（骰面照常，只是少一個優先推薦）: {e.Message}");
            }
            return false;
        }

        /// <summary>
        /// 某 persona 此刻是否在自由時間中 —— active 且**未過 end_ts**。
        /// <para>
        /// ⚠ 只看 `active` 不夠：收工走 step=next／end 才會把它翻 false，
        /// 超時沒回來跑的人會**一直停在 active=true**（Sirius 的殘留檔即為實例）。
        /// 把過期的 session 讀成「他在」，等於叫人去 @ 一個早就下線的對手。
        /// </para>
        /// </summary>
        public static bool IsInFreeTime(string iPersona)
        {
            try
            {
                // 判準委派 UCL_SessionBase.IsRunningAt —— 與本函式原本逐條相同
                // （active、比 end_ts、缺 end_ts 時只能信 active）。收成一處的理由不是 DRY：
                // 這條判準散在 C# 兩處 + python 一處時，改一處另兩處照舊運作、都不報錯。
                // ⚠ 原實作用 `(bool)aS["active"]` 硬轉 —— 那要求 JSON 是**原生 bool**。
                //   UCL_Json 的欄位序列化會把 bool 寫成 "True"/"False" 字串，硬轉會丟例外
                //   （被下方 catch 吞成 false ⇒ 靜默判成「不在自由時間」）。
                //   typed model 讀取端雙接，這一格因此順帶變穩。
                var aSession = UCL_SessionService.Load<UCL_SessionBase>(UCL_SessionKind.FreeTime, iPersona);
                if (aSession == null) return false;
                return aSession.IsRunningAt(DateTime.Now, out _);
            }
            catch (Exception) { return false; }
        }

        static string Str(JsonData iJd, string iKey)
            => iJd != null && iJd.Contains(iKey) ? iJd[iKey].ToString() : "";
    }
}
#endif
