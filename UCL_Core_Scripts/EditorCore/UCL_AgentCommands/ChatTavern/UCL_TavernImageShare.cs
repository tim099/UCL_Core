// 區塊職責：把「一張本地圖＋一句話」發進酒館 —— 繪圖成果（2D 畫布／3D 雕刻）自動分享的共用出口。
// 物理意義：走 in-process `Cmd_Tavern.ExecuteAsync`（同 ChatTavernPage 的 DoSend 手勢），
//          自動繼承完整發文管線：presence 更新、@mention inbox、Discord mirror、頭像解析、渲染。
//          圖用 refs 帶（repo 相對路徑）—— mirror daemon 看到本地圖片 refs 會改走 multipart
//          把檔案實體上傳到 Discord（見 UCL_DiscordMirrorDaemon 的附件分支）。
// 數值影響：失敗只回 false ＋原因，不拋例外 —— 分享是繪圖動作的附屬品，
//          分享失敗不該讓已經付了錢、落了子的主動作看起來失敗。
// Anti-pattern: 別在呼叫端自己 AppendMessage —— 那會繞過 mention/mirror/presence，
//               形成「頁面訊息有鏡像、自動分享沒有」的靜默分岔。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    public static class UCL_TavernImageShare
    {
        /// <summary>refs 只認得住 repo 裡的檔 —— repo 外的路徑做不出相對路徑，收訊端也讀不到。</summary>
        const long MAX_IMAGE_BYTES = 24L * 1024 * 1024;   // 對齊 Discord inbound 的附件上限（免費版 25MB 留餘裕）

        /// <summary>
        /// 發一則帶圖訊息進酒館。iAbsImagePath 必須在 repo 內；iTag 進 meta.tag（分流用）。
        /// 回 false 時 oDetail 帶原因；一律不拋（分享失敗不汙染主動作）。
        /// </summary>
        public static async UniTask<bool> PostAsync(string iRoom, string iPersona, string iBody,
            string iAbsImagePath, string iTag, UCL_StringResult oDetail = null)
        {
            string aDetail;
            try
            {
                if (string.IsNullOrWhiteSpace(iPersona)) { aDetail = "persona 空白"; Fail(oDetail, aDetail); return false; }
                if (string.IsNullOrEmpty(iAbsImagePath) || !File.Exists(iAbsImagePath))
                { aDetail = $"圖檔不存在：{iAbsImagePath}"; Fail(oDetail, aDetail); return false; }

                long aSize = new FileInfo(iAbsImagePath).Length;
                if (aSize > MAX_IMAGE_BYTES)
                { aDetail = $"圖檔過大（{aSize / 1024 / 1024}MB > 24MB），不發"; Fail(oDetail, aDetail); return false; }

                string aRepoRoot = UCL_RepoPath.RepoRoot.Replace('\\', '/').TrimEnd('/');
                string aAbs = Path.GetFullPath(iAbsImagePath).Replace('\\', '/');
                if (!aAbs.StartsWith(aRepoRoot, StringComparison.OrdinalIgnoreCase))
                { aDetail = $"圖檔不在 repo 內（refs 是 repo 相對路徑）：{aAbs}"; Fail(oDetail, aDetail); return false; }
                string aRel = aAbs.Substring(aRepoRoot.Length).TrimStart('/');

                var aArgs = new Dictionary<string, string>
                {
                    { "op", "post" },
                    { "room", string.IsNullOrEmpty(iRoom) ? "tavern" : iRoom },
                    { "persona", iPersona },
                    { "body", iBody ?? "" },
                    { "refs", aRel },
                    // tag 給後續流程分流；glossary 附掛照常（成果分享是對話，不是指令）
                    { "meta", "{\"tag\":\"" + (string.IsNullOrEmpty(iTag) ? "image-share" : iTag) + "\"}" },
                };
                var aCmd = new Cmd_Tavern();
                await aCmd.ExecuteAsync(aArgs, default);
                if (oDetail != null) oDetail.Value = $"已發（refs={aRel}）";
                return true;
            }
            catch (Exception e)
            {
                Fail(oDetail, $"發文失敗：{e.Message}");
                UnityEngine.Debug.LogWarning($"[TavernImageShare] {e.Message}");
                return false;
            }
        }

        static void Fail(UCL_StringResult oDetail, string iMsg)
        {
            if (oDetail != null) oDetail.Value = iMsg;
        }
    }

    /// <summary>async 方法不能帶 out 參數 —— 用這個殼把「原因」帶回去。</summary>
    public class UCL_StringResult
    {
        public string Value = "";
    }
}
#endif
