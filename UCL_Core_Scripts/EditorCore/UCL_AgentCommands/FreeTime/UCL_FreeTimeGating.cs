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

// 券的存量判定走 ledger（唯一 owner）—— 不自己讀券檔
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
                        // 有未完成棋局、對手也在自由時間、**而且輪到我走** → 最優先。
                        // ⭐ 「輪到我」那一格是 2026-09-11 Tim 加的判準。理由：不輪到我的時候
                        //   我能做的只有等，而把「等」頂到最優先會佔掉一個真的做得成的位置。
                        //   ⇒ 置頂的理由必須是「現在做得動」，不是「這件事存在」。
                        // **不隱藏、也不移除定語**：不輪到我時它仍留在骰面上、仍印出「等對方走」
                        //   —— 那是**資訊**（有一局在跑），不是推薦。nameSuffix 與 priority 是兩件事，
                        //   套用處（Cmd_FreeTime 的 name ＝ a.name ＋ nameSuffix）不看 priority。
                        // 📌 而「很久沒下過棋」**不在這裡處理** —— 那條是**通用**的飢餓置頂
                        //   （住 Cmd_FreeTime，判準不看活動是什麼；Chess 沒有 case 可言，同 Default）。
                        //   ⇒ 不輪到我又很久沒下 ⇒ 照樣會被那條頂起來。⛔ 不在本 case 重做一份，
                        //     那會變成兩份飢餓判準，而它們遲早各說各話且兩邊都不報錯。
                        if (TryFindWaitingChess(iPersona, out string aOpponent, out int aGameIdx, out bool aMyTurn))
                        {
                            // ⚠ `|=` 不是 `=` —— 下面還有第二個置頂理由，
                            //   用 `=` 會讓後來的理由被這一行的 false 抹掉。
                            aRes.priority |= aMyTurn;
                            // 用「對方」不用「他」—— 骰面不該替沒說明稱謂的人做假設。
                            aRes.nameSuffix += aMyTurn
                                ? $" ♟ 第 {aGameIdx} 局輪到你，@{aOpponent} 也在自由時間"
                                : $" ♟ 第 {aGameIdx} 局進行中，@{aOpponent} 也在自由時間（**等對方走，不急**）";
                        }

                        // ── 理由二：有人開了一局在等，而我配得上去（Tim 2026-09-11 拍板加）──
                        // ⭐ 這一格**不看對方在不在自由時間** —— 一局在等人跟開局的人此刻在不在無關。
                        //   ⛔ 它也不是理由一的替代品：理由一是「我那局該我走」，這裡是「有一局我還沒坐進去」。
                        //   兩者可以同時成立，那時骰面**兩句都印**（它們回答不同的問題）。
                        // 🩸 為什麼要有它：`start --vs-open` 躺了三個月（全酒館徵人廣播 5 筆、最後一筆 08-17）
                        //   —— 積木在、路通，而沒有任何一層告訴人「現在有一局在等」⇒ 它就不會被用。
                        //   **積木存在 ≠ 有人用它。**
                        if (TryFindJoinableChess(iPersona, out string aOpener, out int aJoinIdx,
                                                 out int aJoinMoves, out int aWaiting))
                        {
                            aRes.priority = true;
                            aRes.nameSuffix += $" 🪑 @{aOpener} 開了一局在等（第 {aJoinIdx} 局，已走 {aJoinMoves} 手"
                                               + (aWaiting > 1 ? $"；共 {aWaiting} 局在等" : "")
                                               + "）—— `match` 直接入座";
                        }
                        return aRes;
                    }

                case UCL_FreeTimeActivityKind.CanvasVoucherFull:
                    {
                        // 永久繪圖券囤太多 → 最優先，並把數字印在名字上（Tim 2026-08-18 拍板）。
                        //
                        // 為什麼盯**永久券**而不是可花總額：限時券本來就會過期、本來就該花掉，
                        // 它多不代表囤積。會囤起來的是永久券 —— 而囤著的券對誰都沒有價值。
                        //
                        // **不隱藏**：券少時畫圖照樣做得成，只是不特別值得優先。
                        int aPermanent = CanvasVoucher.UCL_CanvasVoucherLedger.GetPermanent(iPersona);
                        if (aPermanent > VOUCHER_HOARD_THRESHOLD)
                        {
                            aRes.priority = true;
                            aRes.nameSuffix += $" 🎟 永久券 {aPermanent} 張（> {VOUCHER_HOARD_THRESHOLD}）—— 請多多使用";
                        }
                        return aRes;
                    }

                default:
                    return aRes;
            }
        }

        // 區塊職責：永久券「囤太多」的門檻（Tim 2026-08-18 指定 100）。
        // 物理意義：超過就把繪圖活動推到最前面並印出張數 —— 提示的是**存量**，不是可花總額。
        // 數值影響：門檻本身不擋任何事（只影響排序與名字），所以調它的代價很低。
        const int VOUCHER_HOARD_THRESHOLD = 100;

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
                    if (aCfg != null && aCfg.Contains("enabled") && !aCfg.GetBool("enabled", false)) return false;
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

        // ===========================================================
        // 區塊：可加入的棋局判定 —— 「**別人**開了一局在等人，而我配得上去」
        // 物理意義：這一格跟上面那個判定問的是**相反的問題**：上面問「我那局輪到我了嗎」，
        //          這裡問「有沒有一局我還沒坐進去」。兩者都成立時骰面會同時講。
        // ⭐ 而它**刻意不看對手在不在自由時間**（上面那條看）——
        //   一局在等人，跟開局的人此刻是否在線無關：下棋每步落盤、跨好幾次醒來
        //   （`min_minutes: 0` 就是那個意思）。要求對方在線才顯示，等於把一局等了三天的棋藏起來。
        // 🩸 為什麼這一格值得存在（Tim 2026-09-11 拍板加）：`chess.py start --vs-open`
        //   躺了三個月 —— 全酒館「徵人」廣播只有 5 筆、最後一筆 2026-08-17。
        //   積木在、路通、而**沒有任何一層告訴人「現在有一局在等」** ⇒ 它就不會被用。
        //
        // ⛔⛔ **判準重複警告 —— 這裡與 `chess.py` 的 `pick_match_candidate` 是同一份判斷的兩份實作。**
        //   跨語言（C# 骰面／python 配對）沒辦法共用一份，所以這裡把失效樣子寫死在紙上：
        //   **漂掉的症狀是「骰面說有一局在等，而 `match` 去了卻開了新局」** ——
        //   兩邊都不會報錯，而讀的人會以為是配對壞了。
        //   ⇒ 改任一邊的過濾條件（status／OPEN 座／solo／排除自己在座）**必須同時改另一邊**。
        //   📌 對應位置：`<UCL_Core>/Tools~/AgentCommands/chess.py` → `pick_match_candidate()`
        //      （那邊也有一條指回本函式的註解）。
        // 數值影響：掃同一批 `<DataRoot>/Chess/games/*.json`；取 status=in_progress 且
        //          （有 OPEN 座 或 solo）且**我不在任何一座**；多局取「已走手數最少、同手數取小 index」
        //          —— 與 python 那側的排序鍵逐字相同（那也是可複驗的理由）。
        //          任何讀取失敗 → 回 false（少一個優先推薦，不炸擲骰）。
        // ===========================================================
        public static bool TryFindJoinableChess(string iPersona, out string oOpener, out int oGameIndex,
                                                out int oMoves, out int oWaitingCount)
        {
            oOpener = null; oGameIndex = 0; oMoves = 0; oWaitingCount = 0;
            if (string.IsNullOrEmpty(iPersona)) return false;
            try
            {
                string aDir = Path.Combine(UCL_AgentCommandsPath.DataRoot, "Chess", "games");
                if (!Directory.Exists(aDir)) return false;
                bool aFound = false;
                int aBestMoves = int.MaxValue, aBestIdx = int.MaxValue;
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
                    bool aHasOpen = string.IsNullOrEmpty(aWhite) || string.IsNullOrEmpty(aBlack);
                    bool aSolo = !string.IsNullOrEmpty(aWhite)
                                 && string.Equals(aWhite, aBlack, StringComparison.OrdinalIgnoreCase);
                    if (!aHasOpen && !aSolo) continue;
                    // ⛔ 我已經在座的局不算「可加入」—— `chess.py join` 本來就會擋，
                    //    而骰面把它算進來的話，人照著去跑 match 會拿到一個他配不上去的理由。
                    if (string.Equals(aWhite, iPersona, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(aBlack, iPersona, StringComparison.OrdinalIgnoreCase)) continue;

                    oWaitingCount++;
                    int aIdx = 0; int.TryParse(Str(aGame, "index"), out aIdx);
                    int aMoves = CountChessMoves(aGame);
                    if (aMoves < aBestMoves || (aMoves == aBestMoves && aIdx < aBestIdx))
                    {
                        aBestMoves = aMoves; aBestIdx = aIdx;
                        oOpener = string.IsNullOrEmpty(aWhite) ? aBlack : aWhite;
                        oGameIndex = aIdx; oMoves = aMoves;
                        aFound = true;
                    }
                }
                return aFound;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FreeTime] 可加入棋局判定失敗（骰面照常，只是少一個優先推薦）: {e.Message}");
            }
            return false;
        }

        // 真正的走子數 —— history 裡也記 join:/release: 這類事件，那些不是手數。
        // ⚠ 與 `chess.py` 的 `count_moves()` 同一份定義（見上面的判準重複警告）。
        static int CountChessMoves(JsonData iGame)
        {
            if (iGame == null || !iGame.Contains("history")) return 0;
            var aHist = iGame["history"];
            // 照 codebase 慣例先問 IsArray 再 Count（見 UCL_ReadingLibraryIO 的 aliases 走法）。
            if (aHist == null || !aHist.IsArray) return 0;
            int aN = 0;
            for (int i = 0; i < aHist.Count; i++)
            {
                string aUci = Str(aHist[i], "uci");
                if (aUci != null && aUci.Length >= 4 && !aUci.Contains(":")) aN++;
            }
            return aN;
        }

        public static bool IsInFreeTime(string iPersona)
        {
            try
            {
                // 判準委派 SCP_ActivitySession.IsRunningAt —— 與本函式原本逐條相同
                // （active、比 end_ts、缺 end_ts 時只能信 active）。收成一處的理由不是 DRY：
                // 這條判準散在 C# 兩處 + python 一處時，改一處另兩處照舊運作、都不報錯。
                // ⚠ 原實作用 `(bool)aS["active"]` 硬轉 —— 那要求 JSON 是**原生 bool**。
                //   UCL_Json 的欄位序列化會把 bool 寫成 "True"/"False" 字串，硬轉會丟例外
                //   （被下方 catch 吞成 false ⇒ 靜默判成「不在自由時間」）。
                //   typed model 讀取端雙接，這一格因此順帶變穩。
                var aSession = SCP.Core.Session.SCP_ActivitySessionStore.Load(
                    UCL_AgentCommandsPath.ScpDataRoot, iPersona, SCP.Core.Session.SCP_ActivitySessionKind.FreeTime);
                if (aSession == null) return false;
                return aSession.IsRunningAt(DateTime.Now, out _);
            }
            catch (Exception) { return false; }
        }

        // 🩸 2026-09-11：這一行原本是 `iJd != null && iJd.Contains(iKey) ? iJd[iKey].ToString() : ""`，
        //   而它漏了第三格：**鍵在、而值是 JSON null**（棋局的 OPEN 座就是 `"white": null`）。
        //   三格的失敗長得一樣（NullRef），而呼叫端 `TryFindWaitingChess` 把例外
        //   fail-soft 吞掉 ⇒ **Chess 優先層在任何一局有 OPEN 座時就整條靜默失效**，
        //   而骰面照常印、只是少一個推薦 —— 沒有任何人會發現。
        //   ⇒ 「不隱藏」變成了「沒有人看見」：fail-soft 有出聲（Debug.LogWarning），
        //     而那行警告躺在 Editor.log 裡沒有人讀。抓到它的是我加新判定時**同一行警告出現兩次**。
        // ⚠ 而 JSON null 不能回 `"null"` 字串 —— 那會讓空座位變成一個叫 "null" 的人。
        static string Str(JsonData iJd, string iKey)
        {
            if (iJd == null || !iJd.Contains(iKey)) return "";
            var aV = iJd[iKey];
            if (aV == null || aV.JsonType == JsonType.None) return "";
            return aV.ToString();
        }
    }
}
#endif
