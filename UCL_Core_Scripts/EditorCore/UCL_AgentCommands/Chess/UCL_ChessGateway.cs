// 區塊職責：`SCP_IChessGateway` 的 **Editor 端實作** ＋ 在 Editor 載入時把它裝上 `SCP_ChessGatewayHost`。
// 物理意義：TASK-0268 —— 棋局本體（`SCP.Core.Chess`）住在 SCP_Core；自由時間的下棋 step 經
//           `cmd_steps` 路由在 **Editor 內 in-process** 派遣 `SCP_Cmd_Chess`（`Cmd_FreeTimeActivity.RunCmdStep`）。
//           那條路上本體要的兩格宿主能力在這裡接：
//           · 廣播 ＝ Senate `tavern-post`／`tavern-post-system`（TASK-0366，經 `UCL_TavernSenatePost`）
//           · 發券 ＝ `UCL_VoucherAuthority.Grant`（權威在 Senate Server，本層只是 client）
// 數值影響：無自有狀態。
//
// 🩸 判準：
//   ① **發文要秒級（spawn senate），而閘介面是同步的**（本體跑在 SCP_Cmd.Execute 裡）。
//      ⇒ **不假裝知道結果** —— 回 true 但 detail 明講「已派出、結果看 Editor.log」，
//        並掛一個會把成敗印出來的收尾。⛔ 不在主執行緒上等它（那會把 Editor 凍住）。
//   ② 身分只給 persona，⛔ 不帶 sender_id（理由見 `SenateChessGateway` 判準②）。
//   ③ lane 在這條路上**沒有受詞**：in-process 不經 queue ⇒ 不會互相排隊。收下但不用，⛔ 不假造一條分道。
#if UNITY_EDITOR
using System;
using Cysharp.Threading.Tasks;
using SCP.Core.Chess;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Chess
{
    [InitializeOnLoad]
    public sealed class UCL_ChessGateway : SCP_IChessGateway
    {
        static UCL_ChessGateway()
        {
            // ⚠ 工廠吃資料根當參數，⛔ 不自己解析（同 SCP_CanvasGatewayHost 的判準）。
            //   Editor 這側只有一個資料根 ⇒ 參數收下不用；而這一格若有一天要分專案，改的是這一行。
            SCP_ChessGatewayHost.Factory = _ => new UCL_ChessGateway();
        }

        public string HostQualifier
            => "⤷ 棋局由 Unity Editor in-process 跑（SCP_Cmd_Chess）／廣播走 Senate tavern-post（背景）／券走 UCL_VoucherAuthority";

        public bool Broadcast(string iSenderPersona, string iLane, string iBody, string iMetaJson, out string oDetail)
        {
            // TASK-0366：廣播走 Senate（有 persona ⇒ `tavern-post`；沒有 ⇒ `tavern-post-system`，身分 tavern-keeper，不計酬）。
            // 判準①：spawn 要秒級，⛔ 不在主執行緒上等 ⇒ 派到背景、回「已派出」，成敗由收尾印進 Editor.log。
            bool aSystem = string.IsNullOrEmpty(iSenderPersona);
            BroadcastInBackground(aSystem, iSenderPersona, iBody, iMetaJson).Forget();
            oDetail = "已派出（Senate " + (aSystem ? "tavern-post-system" : "tavern-post") + "，背景進行）—— 成敗看 Editor.log 的 [Chess] 那一行";
            return true;
        }

        static async UniTaskVoid BroadcastInBackground(bool iSystem, string iPersona, string iBody, string iMetaJson)
        {
            try
            {
                ChatTavern.UCL_TavernSenatePost.Result r = iSystem
                    ? await ChatTavern.UCL_TavernSenatePost.PostSystemAsync("tavern-keeper", null, "tavern", iBody, iMetaJson)
                    : await ChatTavern.UCL_TavernSenatePost.PostAsync(iPersona, "tavern", iBody, iMetaJson);
                if (r.Posted) Debug.Log($"[Chess] 廣播{r.Describe()}");
                else Debug.LogWarning($"[Chess] ⚠ 廣播{r.Describe()}（棋步已存檔，酒館少一則盤面）");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Chess] ⚠ 廣播沒落地（棋步已存檔，酒館少一則盤面）：{e.GetType().Name}: {e.Message}");
            }
        }

        public int? GrantCanvasVoucher(string iPersona, int iAmount, string iSource, string iRef, out string oDetail)
        {
            oDetail = "";
            try
            {
                (int _, int aAfter) = Voucher.UCL_VoucherAuthority.Grant(iPersona, "canvas", iAmount, iSource, iRef);
                return aAfter;
            }
            catch (Exception e)
            {
                oDetail = $"{e.GetType().Name}: {e.Message}";
                return null;
            }
        }
    }
}
#endif
