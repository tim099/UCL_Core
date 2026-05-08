// 區塊職責：依 identities.json roster 建立對應 UCL_ChatTavernIdentityAsset 實例（rich persona 殼）
// 物理意義：identities.json 是 lightweight roster（id / display_name / kind / created_at / last_seen_at），
//          UCL_ChatTavernIdentityAsset 是 rich persona view layer（avatar / role_settings / color / catchphrases / tags）。
//          首次建立 rich data 之前需要先 seed 出對應 .json 殼 — 本 Cmd 完成這件事。
//          產出後，Tim / 開發者可在 Editor 內用 UCL_SelectAssetPage / UCL_CommonEditPage 編輯，
//          編完再用 Cmd_MigrateAssetToTemplate 把它們搬到 Templates~（成為跨專案範本）。
// 數值影響：呼叫 UCL_Asset.Save() 寫 .json 到 <module>/UCL_Assets/UCL_ChatTavernIdentityAsset/<id>.json
//          已存在的 .json 預設 skip（force=false）；force=true 會覆寫
//          會根據 kind 預填 m_Tags 一筆（"agent" / "human" / "npc"）— 其他欄位保持空，等使用者編輯
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Agent Command：依 identities.json roster 建立對應 UCL_ChatTavernIdentityAsset 實例殼。
    ///
    /// 參數：
    /// - <c>force</c>（選填，預設 <c>false</c>）：true = 覆寫已存在的 Asset；false = skip
    /// - <c>onlyId</c>（選填）：只 seed 指定 id（適用測試 / 補單筆）；空 = 全 roster
    ///
    /// 預填欄位：
    /// - <c>ID</c>：對齊 roster id
    /// - <c>m_Tags</c>：根據 roster.kind 加一筆 tag（"agent" / "human" / "npc" / "system"）
    /// - 其他（avatar_path / role_settings / color_hex / catchphrases）保持空，待使用者編輯
    /// </summary>
    public class Cmd_SeedTavernIdentityAssets : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "SeedTavernIdentityAssets";

        public override string ShortDescription =>
            "Seed UCL_ChatTavernIdentityAsset .json shells from identities.json roster (one per identity).";

        public override string ArgsSchema =>
            "force=true|false 是否覆寫已存在的 Asset (default: false) | " +
            "onlyId=只 seed 指定 id（空字串 = 全部 roster）";

        public override string ExampleArgs => "force=false";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_SeedTavernIdentityAssets.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            bool force = string.Equals(GetArg(args, "force", "false"), "true", System.StringComparison.OrdinalIgnoreCase);
            string onlyId = GetArg(args, "onlyId", "");

            var roster = UCL_ChatTavernIO.LoadIdentities();
            if (roster == null || roster.identities == null || roster.identities.Count == 0)
            {
                Debug.LogWarning("[Cmd:SeedTavernIdentityAssets] identities.json 為空 — 無 identity 可 seed");
                return;
            }

            int created = 0, skipped = 0, failed = 0;
            foreach (var ident in roster.identities)
            {
                token.ThrowIfCancellationRequested();
                if (ident == null || string.IsNullOrEmpty(ident.id)) continue;
                if (!string.IsNullOrEmpty(onlyId) && ident.id != onlyId) continue;

                try
                {
                    var asset = new UCL_ChatTavernIdentityAsset(ident.id);   // ctor → Init(iID)，設 ID
                    if (asset.ContainsAsset(ident.id) && !force)
                    {
                        skipped++;
                        Debug.Log($"[Cmd:SeedTavernIdentityAssets] skip 已存在: {ident.id}");
                        continue;
                    }

                    // 預填一筆 tag 對應 kind（讓使用者一眼看到 agent / human / npc 分類）
                    string kindTag = string.IsNullOrEmpty(ident.kind) ? "agent" : ident.kind;
                    asset.m_Tags = new List<string> { kindTag };

                    asset.Save();
                    created++;
                    Debug.Log($"[Cmd:SeedTavernIdentityAssets] {(force ? "FORCE-WRITE" : "CREATE")} {ident.id} (kind={kindTag})");
                }
                catch (System.Exception ex)
                {
                    failed++;
                    Debug.LogError($"[Cmd:SeedTavernIdentityAssets] {ident.id} 失敗: {ex.Message}");
                }
            }

            UnityEditor.AssetDatabase.Refresh();
            Debug.Log(
                $"[Cmd:SeedTavernIdentityAssets] 完成：created={created} / skipped={skipped} / failed={failed}\n" +
                $"  下一步：Editor 內用 UCL_SelectAssetPage 找 UCL_ChatTavernIdentityAsset 編輯，\n" +
                $"           編完後跑 Cmd_MigrateAssetToTemplate 把它們搬到 Templates~ 當預設範本"
            );
        }
    }
}
#endif
