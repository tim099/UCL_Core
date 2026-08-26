// UCL Chat Tavern — Markdown 渲染（prototype v1）
// 把訊息列表渲染為人類友善的 markdown，給 agent 當下一回合 prompt 的 context。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    public static class UCL_ChatTavernRender
    {
        /// <summary>
        /// 把訊息陣列渲染成 markdown。
        /// </summary>
        /// <param name="title">區段標題（例如 "🍺 cs-cleanup — 最新 100 筆"）</param>
        /// <param name="messages">要顯示的訊息（依 seq 升冪）</param>
        /// <param name="highlightSeq">標星號的 seq（例如剛 post 的那筆）；null 為不標</param>
        /// <param name="header">置頂額外資訊（例如「你是 &lt;agent-name&gt;」，由呼叫方填入 agent 自己的稱呼）；null 略過</param>
        public static string RenderMessages(string title, List<UCL_ChatMessage> messages, int? highlightSeq = null, string header = null)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(title).Append("\n\n");
            if (!string.IsNullOrEmpty(header))
            {
                sb.Append(header).Append("\n\n");
            }
            if (messages == null || messages.Count == 0)
            {
                sb.Append("_(尚無訊息)_\n");
                return sb.ToString();
            }
            foreach (var m in messages)
            {
                bool hl = highlightSeq.HasValue && m.seq == highlightSeq.Value;
                sb.Append(hl ? "**" : "");
                sb.Append("[seq ").Append(m.seq).Append("] ");
                sb.Append(ShortTime(m.ts)).Append(" ");
                if (!string.IsNullOrEmpty(m.kind) && m.kind != "chat")
                {
                    sb.Append("(").Append(m.kind).Append(") ");
                }
                sb.Append(m.DisplayName).Append(": ");
                sb.Append(m.body ?? "");
                if (m.reply_to.HasValue) sb.Append(" _(↩ ").Append(m.reply_to.Value).Append(")_");
                sb.Append(hl ? "**" : "");
                if (m.meta != null && m.meta.Count > 0)
                {
                    sb.Append("\n  - meta:");
                    foreach (var kv in m.meta) sb.Append(" `").Append(kv.Key).Append("=").Append(kv.Value).Append("`");
                }
                if (m.refs != null && m.refs.Count > 0)
                {
                    sb.Append("\n  - refs:");
                    foreach (var r in m.refs)
                    {
                        sb.Append(" [").Append(string.IsNullOrEmpty(r.label) ? r.path : r.label).Append("](").Append(r.path);
                        if (!string.IsNullOrEmpty(r.anchor)) sb.Append("#").Append(r.anchor);
                        sb.Append(")");
                    }
                }
                sb.Append("\n");
            }
            return sb.ToString();
        }

        /// <summary>把 ISO 8601 ts 轉成 HH:mm:ss（顯示用，省空間）。</summary>
        static string ShortTime(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "??:??:??";
            // 預期格式 "yyyy-MM-ddTHH:mm:ssZ"
            int t = iso.IndexOf('T');
            if (t < 0 || t + 9 > iso.Length) return iso;
            return iso.Substring(t + 1, 8);
        }

        /// <summary>渲染 + 寫到 _last_view.md。</summary>
        public static string WriteLastView(string roomId, string roomName, List<UCL_ChatMessage> messages, int? highlightSeq, string header = null)
        {
            string title = $"🍺 {roomName} — 最新 {messages?.Count ?? 0} 筆";
            string md = RenderMessages(title, messages, highlightSeq, header);
            UCL_ChatTavernIO.EnsureRoomDir(roomId);
            File.WriteAllText(UCL_ChatTavernIO.GetLastViewPath(roomId), md, new System.Text.UTF8Encoding(false));
            return md;
        }

        /// <summary>把任意 markdown 字串寫到 _last_op.md（給 agent 抓 Cmd 結果）。</summary>
        public static void WriteLastOp(string md)
        {
            UCL_ChatTavernIO.EnsureTavernDir();
            // 區塊職責：cmd_id stamp 注入（T-LastOp-CmdId 2026-06-12）
            // 物理意義：_last_op.md 是多 session 共用檔 — 多個 Claude chat 並發對同一 Editor 發 cmd 時，
            //          A 的 cmd_wait 可能讀到 B 在同窗口寫的 fail marker（mtime 在 A submit 之後）而誤報失敗
            //          （實證：2026-06-12 21:27 kiara post 成功被 gura chat 的 T07 fail marker 污染誤報 exit 2）。
            //          stamp 當前執行 cmd 的 queue Id 進檔，Python 端 check_cmd_result_file 比對相符才認帳。
            // 數值影響：stamp 插在第一行之後 — 第一行是 Python 端 fail/success marker 判定行不可動，
            //          且 Python 只讀前 4KB，stamp 必須靠檔頭。Runner 沒設 CurrentCmdId（IMGUI 手動操作
            //          等非 queue 路徑）→ 不 stamp，輸出與舊版完全一致。
            string cmdId = UCL_AgentCommandRunner.CurrentCmdId;
            if (!string.IsNullOrEmpty(cmdId))
            {
                string stamp = "<!-- cmd_id: " + cmdId + " -->";
                int nl = md.IndexOf('\n');
                md = nl >= 0
                    ? md.Substring(0, nl + 1) + stamp + "\n" + md.Substring(nl + 1)
                    : md + "\n" + stamp + "\n";
            }
            // ⚠ 全域 `_last_op.md` **保留、內容不變** —— 它不是只給人讀的：run_cmd 的
            //   fail-detection（check_cmd_result_file）與 tavern_cmd.py 等仍讀這一份，
            //   第一行 marker ＋ cmd_id 章是那條通道的 wire format，stub 化＝拆掉活的偵測。
            //   （TASK-0059：全域槽的「互相覆蓋」由 cmd_id 章擋在讀取端；閱讀通道遷 per-persona，見下）
            File.WriteAllText(UCL_ChatTavernIO.GetLastOpPath(), md, new System.Text.UTF8Encoding(false));

            // ===========================================================
            // 區塊職責：per-persona 鏡寫（TASK-0059，對齊 0026/0044 搬法）。
            // 物理意義：全域槽 last-write-wins —— 兩人先後跑同 op，慢的那份蓋掉快的那份，
            //   而讀自己那次結果的人拿到別人的視圖（0026 ① 的原始病）。
            //   persona 從 cmd context 拿（AgentId＝lane persona）；回傳檔名的 cmd 段取自
            //   cmd_id 尾段（`20260826-…-tavern` → `tavern_last_op.md`）—— 本函式收 16 支
            //   Cmd 的輸出，不逐支傳型別，從 id 取是唯一不用改每個呼叫端的路。
            // ⚠ context 拿不到（IMGUI 手動操作等非 queue 路徑）⇒ 只寫全域，行為與舊版全等。
            // 數值影響：AddOutput 讓 run_cmd 印「📄 回傳檔：<per-persona 路徑>」指向本次這個人。
            // ===========================================================
            try
            {
                var aCtx = UCL_AgentCmdContexts.Get(cmdId);
                if (aCtx != null && !string.IsNullOrEmpty(aCtx.AgentId))
                {
                    int aCut = cmdId.LastIndexOf('-');
                    string aSlug = aCut >= 0 && aCut < cmdId.Length - 1 ? cmdId.Substring(aCut + 1) : "cmd";
                    string aPayload = UCL_LettersPath.CmdPayload(aCtx.AgentId, aSlug, "last_op");
                    Directory.CreateDirectory(Path.GetDirectoryName(aPayload));
                    File.WriteAllText(aPayload, md, new System.Text.UTF8Encoding(false));
                    aCtx.AddOutput(aPayload);
                }
            }
            catch (System.Exception e)
            {
                // 鏡寫失敗不影響主通道（全域檔已落）—— 但要出聲，安靜的鏡寫失敗長得像「沒有這個功能」
                UnityEngine.Debug.LogWarning($"[ChatTavernRender] per-persona 鏡寫失敗（全域 _last_op.md 已寫）：{e.Message}");
            }
        }
    }
}
#endif
