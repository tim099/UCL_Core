// 區塊職責：依 rooms.json roster 建立對應 UCL_ChatTavernRoomAsset 實例（rich data 殼）
// 物理意義：rooms.json 是 lightweight roster（id / name / description / created_at），
//          UCL_ChatTavernRoomAsset 是 rich data view layer（banner / color / rules / tags）。
//          首次建立 rich data 之前需要先 seed 出對應 .json 殼 — 本 Cmd 完成這件事。
//          產出後可在 Editor 內用 UCL_SelectAssetPage / UCL_CommonEditPage 編輯，
//          編完用 Cmd_MigrateAssetToTemplate 把它們搬到 Templates~（成為跨專案範本）。
// 數值影響：呼叫 UCL_Asset.Save() 寫 .json 到 <module>/UCL_Assets/UCL_ChatTavernRoomAsset/<id>.json
//          已存在的 .json 預設 skip（force=false）；force=true 會覆寫
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Agent Command：依 rooms.json roster 建立對應 UCL_ChatTavernRoomAsset 實例殼。
    /// 跟 <see cref="Cmd_SeedTavernIdentityAssets"/> 平行 — identity / room 兩個資料軸都採 UCL_Asset 對齊。
    ///
    /// 參數：
    /// - <c>force</c>（選填，預設 <c>false</c>）：true = 覆寫已存在的 Asset；false = skip
    /// - <c>onlyId</c>（選填）：只 seed 指定 room id；空 = 全 roster
    /// </summary>
    public class Cmd_SeedTavernRoomAssets : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "SeedTavernRoomAssets";

        public override string ShortDescription =>
            "Seed UCL_ChatTavernRoomAsset .json shells from rooms.json roster (one per room).";

        public override string ArgsSchema =>
            "force=true|false 是否覆寫已存在的 Asset (default: false) | " +
            "onlyId=只 seed 指定 room id（空字串 = 全部 roster）";

        public override string ExampleArgs => "force=false";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_SeedTavernRoomAssets.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            bool force = string.Equals(GetArg(args, "force", "false"), "true", System.StringComparison.OrdinalIgnoreCase);
            string onlyId = GetArg(args, "onlyId", "");

            var roster = UCL_ChatTavernIO.LoadRooms();
            if (roster == null || roster.rooms == null || roster.rooms.Count == 0)
            {
                Debug.LogWarning("[Cmd:SeedTavernRoomAssets] rooms.json 為空 — 無 room 可 seed");
                return;
            }

            int created = 0, skipped = 0, failed = 0;
            foreach (var room in roster.rooms)
            {
                token.ThrowIfCancellationRequested();
                if (room == null || string.IsNullOrEmpty(room.id)) continue;
                if (!string.IsNullOrEmpty(onlyId) && room.id != onlyId) continue;

                try
                {
                    var asset = new UCL_ChatTavernRoomAsset(room.id);
                    if (asset.ContainsAsset(room.id) && !force)
                    {
                        skipped++;
                        Debug.Log($"[Cmd:SeedTavernRoomAssets] skip 已存在: {room.id}");
                        continue;
                    }

                    asset.Save();
                    created++;
                    Debug.Log($"[Cmd:SeedTavernRoomAssets] {(force ? "FORCE-WRITE" : "CREATE")} {room.id}");
                }
                catch (System.Exception ex)
                {
                    failed++;
                    Debug.LogError($"[Cmd:SeedTavernRoomAssets] {room.id} 失敗: {ex.Message}");
                }
            }

            UnityEditor.AssetDatabase.Refresh();
            Debug.Log(
                $"[Cmd:SeedTavernRoomAssets] 完成：created={created} / skipped={skipped} / failed={failed}\n" +
                $"  下一步：Editor 內用 UCL_SelectAssetPage 找 UCL_ChatTavernRoomAsset 編輯，\n" +
                $"           編完後跑 Cmd_MigrateAssetToTemplate assetType=UCL_ChatTavernRoomAsset id=* 搬到 Templates~"
            );
        }
    }
}
#endif
