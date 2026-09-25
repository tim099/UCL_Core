// 區塊職責：`SCP_IChessGateway` 的 **Editor 端實作** ＋ 在 Editor 載入時把它裝上 `SCP_ChessGatewayHost`。
// 物理意義：TASK-0268 —— 棋局本體（`SCP.Core.Chess`）住在 SCP_Core；自由時間的下棋 step 經
//           `cmd_steps` 路由在 **Editor 內 in-process** 派遣 `SCP_Cmd_Chess`（`Cmd_FreeTimeActivity.RunCmdStep`）。
//           那條路上本體要的兩格宿主能力在這裡接：
//           · 廣播 ＝ registry 拿 `Cmd_Tavern`，走同一條 `Op_Post` pipeline（同 `Cmd_Library` share 那一格）
//           · 發券 ＝ `UCL_VoucherAuthority.Grant`（權威在 Senate Server，本層只是 client）
// 數值影響：無自有狀態。
//
// 🩸 判準：
//   ① **`Cmd_Tavern.ExecuteAsync` 是 async，而閘介面是同步的**（本體跑在 SCP_Cmd.Execute 裡）。
//      ⇒ 同步完成的（常態：寫入端委派 CLI 本來就阻塞主執行緒）照實回成功／失敗；
//        **還沒完成的就不假裝知道結果** —— 回 true 但 detail 明講「已派出、結果看 Editor.log」，
//        並掛一個會把例外印出來的收尾。⛔ 不在主執行緒上 `GetResult()` 等它（那會把 Editor 凍住）。
//   ② 身分只給 persona，⛔ 不帶 sender_id（理由見 `SenateChessGateway` 判準②）。
//   ③ lane 在這條路上**沒有受詞**：in-process 不經 queue ⇒ 不會互相排隊。收下但不用，⛔ 不假造一條分道。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
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
            => "⤷ 棋局由 Unity Editor in-process 跑（SCP_Cmd_Chess）／廣播走 Cmd_Tavern／券走 UCL_VoucherAuthority";

        public bool Broadcast(string iSenderPersona, string iLane, string iBody, string iMetaJson, out string oDetail)
        {
            oDetail = "";
            UCL_AgentCommandHandlerBase aTavern = UCL_AgentCommandRegistry.Get("Tavern");
            if (aTavern == null)
            {
                oDetail = "找不到 Tavern handler —— registry 未註冊？";
                return false;
            }
            var aArgs = new Dictionary<string, string>
            {
                ["op"] = "post",
                ["room"] = "tavern",
                ["body"] = iBody,
                ["meta"] = iMetaJson,
            };
            if (!string.IsNullOrEmpty(iSenderPersona)) aArgs["persona"] = iSenderPersona;

            UniTask aTask;
            try { aTask = aTavern.ExecuteAsync(aArgs, CancellationToken.None); }
            catch (Exception e)
            {
                oDetail = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
            switch (aTask.Status)
            {
                case UniTaskStatus.Succeeded:
                    oDetail = "Cmd_Tavern 同步完成";
                    return true;
                case UniTaskStatus.Pending:
                    // 判準①：不知道就說不知道。
                    oDetail = "已派出（Cmd_Tavern 非同步進行中）—— 成敗看 Editor.log 的 [Chess] 那一行";
                    LogWhenDone(aTask).Forget();
                    return true;
                default:
                    try { aTask.GetAwaiter().GetResult(); }
                    catch (Exception e) { oDetail = $"{e.GetType().Name}: {e.Message}"; }
                    if (oDetail.Length == 0) oDetail = "Cmd_Tavern 回報 " + aTask.Status;
                    return false;
            }
        }

        static async UniTaskVoid LogWhenDone(UniTask iTask)
        {
            try
            {
                await iTask;
                Debug.Log("[Chess] 廣播已由 Cmd_Tavern 完成（非同步那一趟）");
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
