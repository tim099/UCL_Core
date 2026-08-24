// 區塊職責：任務事件 → 酒館通知（開單 / 重要狀態變動 / 指派 / 留言），並 @ 參與者。
// 物理意義：這是**把「誰該知道」從人搬到通道上**的那一步（Tim 2026-08-24 指派）。
//   在這之前，一張單指派給誰只寫在檔案裡 —— 而檔案不會敲人。
//   ⇒ 被指派的人得靠「有人記得去講一聲」才知道，而「記得」正是這套系統不能依賴的東西
//     （🩸 舊 AgentTasks 就是這樣死的：狀態要有人專程回來推，而沒有人專程）。
//
// ⚠ **不另開一條發訊路**：走既有的 `Cmd_Tavern`（in-process，同 UCL_TavernImageShare 的形狀）。
//   兩條發文路就是兩套規則，而其中一條遲早會漏掉某個欄位（🩸 08-20 顯示名稱三個算點）。
//
// ⚠ **通知失敗不擋主動作**：單子已經寫好了，通知只是附帶效果。
//   但失敗一定要**印出來** —— 靜默失敗會讓「我以為他知道了」變成一個沒有人發現的錯。
// 2026-08-24 summit
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.TaskMgmt
{
    public static class UCL_TaskNotify
    {
        /// <summary>酒館通知的房間。刻意寫死 tavern —— 任務討論屬於公共頻道，不是私訊。</summary>
        const string ROOM = "tavern";

        // ===========================================================
        // 區塊職責：哪些事件值得敲人。
        // 物理意義：**不是每一次寫入都通知** —— 每次都通知等於沒有通知（大家會學會忽略它）。
        //   收進來的四類：開單（有人開了一件事）／狀態變動（它動了）／指派（你被點名了）／留言（有人說話）。
        //   刻意**不通知**：`update` 改 priority/title、`comment` 之外的 metadata 變動、
        //   以及 `list`/`show` 這種純讀 —— 那些沒有「誰該知道」的問題。
        // ===========================================================
        public enum Kind
        {
            Created,
            Status,
            Assigned,
            Comment,
        }

        /// <summary>
        /// 發一則任務通知。<paramref name="iActor"/> 是動手的人（他自己不會被 @，避免自己敲自己）。
        /// 回 false 時已經印過警告；一律不拋（通知失敗不汙染主動作）。
        /// </summary>
        public static async UniTask<bool> PostAsync(UCL_TaskEntry e, Kind iKind, string iActor,
            string iDetail = "", string iCommentBody = "")
        {
            if (e == null) return false;
            try
            {
                string aBody = BuildBody(e, iKind, iActor, iDetail, iCommentBody);
                var aArgs = new Dictionary<string, string>
                {
                    { "op", "post" },
                    { "room", ROOM },
                    { "persona", string.IsNullOrWhiteSpace(iActor) ? "unknown" : iActor },
                    { "body", aBody },
                    // tag 給後續分流用；`task` 這一族的訊息可以被獨立撈出來對帳
                    { "meta", "{\"tag\":\"task\",\"task\":\"" + e.Id + "\",\"kind\":\"" + iKind.ToString().ToLowerInvariant() + "\"}" },
                };
                var aCmd = new Cmd_Tavern();
                await aCmd.ExecuteAsync(aArgs, default);
                return true;
            }
            catch (Exception ex)
            {
                // 大聲但不致命 —— 單子已經寫好了，這裡失敗只是沒人被敲到。
                // ⚠ 這一行就是「通知沒發出去」的讀數；沒有它，那件事會靜默地不存在。
                Debug.LogWarning($"[TaskNotify] {e.Id} 的 {iKind} 通知沒發出去（單子已寫好）：{ex.Message}");
                return false;
            }
        }

        /// <summary>同步呼叫端（後台頁的按鈕）用的射後不理版本 —— 失敗照樣會印警告。</summary>
        public static void PostFireAndForget(UCL_TaskEntry e, Kind iKind, string iActor,
            string iDetail = "", string iCommentBody = "")
            => PostAsync(e, iKind, iActor, iDetail, iCommentBody).Forget();

        // ===========================================================
        // 區塊職責：組訊息本體。
        // 物理意義：第一行要能單獨站著（時間軸上先看到的就是它）；
        //   @ 名單放在**最後一行**（那是收訊端掃 mention 的地方，也讓人一眼看到「這關我什麼事」）。
        // ⚠ 參與者為空時**明說「這張單沒有參與者」**，不要印一個空的 @ 行 ——
        //   空 @ 行看起來像「已經通知了」，而事實是沒有人被敲到。
        // ===========================================================
        static string BuildBody(UCL_TaskEntry e, Kind iKind, string iActor, string iDetail, string iCommentBody)
        {
            string aHead;
            switch (iKind)
            {
                case Kind.Created:
                    aHead = $"📋 **{e.Id} 開單**（{e.type} / {e.priority}）：{e.title}";
                    break;
                case Kind.Status:
                    aHead = $"📋 **{e.Id}** {iDetail}：{e.title}";
                    break;
                case Kind.Assigned:
                    aHead = $"📋 **{e.Id}** 指派變動（{iDetail}）：{e.title}";
                    break;
                default:
                    aHead = $"💬 **{e.Id}** 有新留言：{e.title}";
                    break;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append(aHead).Append("\n\n");
            if (iKind == Kind.Comment && !string.IsNullOrWhiteSpace(iCommentBody))
                sb.Append(iCommentBody.Trim()).Append("\n\n");
            else if (!string.IsNullOrWhiteSpace(iDetail) && iKind == Kind.Created)
                sb.Append(iDetail.Trim()).Append("\n\n");

            sb.Append($"- 狀態：`{e.status}`");
            var aBlockers = UCL_TaskIO.OpenBlockers(e);
            if (aBlockers.Count > 0) sb.Append($"　🛑 未解 blocker {aBlockers.Count} 個");
            sb.Append($"　操作：{(string.IsNullOrWhiteSpace(iActor) ? "unknown" : iActor)}\n");
            sb.Append($"- 單檔：`AgentCommands/Tasks/tasks/{e.index:0000}.md`"
                + $"　查看：`run Task --arg op=show --arg index={e.index}`\n");

            // @ 名單：參與者 **＋ 開單人**，扣掉動手的人（不自己敲自己）
            // 🩸 2026-08-24：首版只 @ 參與者 ⇒ 我在別人的單上留了一則自認，而**她沒有被通知到**
            //   （她是 reporter 但沒把自己列進 participants）。
            //   一則沒人收到的通知跟沒發一樣，而回傳檔那句「已發」讓它看起來成功了。
            //   ⇒ 開單人天生在意這張單的動態，那不需要他額外把自己加進參與者才成立。
            var aMentions = e.participants
                .Select(p => p.persona)
                .Concat(new[] { e.reporter })
                .Where(s => !string.IsNullOrWhiteSpace(s)
                            && !string.Equals(s, iActor, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            sb.Append('\n');
            if (aMentions.Count > 0)
                sb.Append(string.Join(" ", aMentions.Select(s => "@" + s)));
            else if (e.participants.Count == 0)
                sb.Append("⚠ 這張單**沒有任何參與者** ⇒ 沒有人被 @ 到（不是通知失敗，是沒有人在做這件事）");
            else
                sb.Append("（唯一的參與者就是操作者本人 ⇒ 沒有人需要被 @）");
            return sb.ToString();
        }
    }
}
#endif
