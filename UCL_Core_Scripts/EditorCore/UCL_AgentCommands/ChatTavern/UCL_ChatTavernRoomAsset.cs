// 區塊職責：UCL Chat Tavern 房間（rich data）— UCL_Asset 體系下的房間設定持久化
// 物理意義：rooms.json 是 lightweight roster（id / name / description / created_at，給 Python 與 Cmd_Tavern 用）；
//          本 Asset 是「房間設定卡」獨立資料層 — 走 UCL_Asset<T> 標準持久化（每房一個 .json 檔，由 UCL_ModuleService 管路徑），
//          承載橫幅圖 / 房間主題色 / 房規 / tags 等 rich data。
// 設計取捨：跟 UCL_ChatTavernIdentityAsset 走完全平行的設計 — 雙層資料 (lightweight roster + rich Asset)；
//          identities + rooms 兩條軸都採 UCL_Asset 才一致。
using System.Collections.Generic;
using UCL.Core;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// Chat Tavern 房間設定卡（rich data）。每個 room_id（如 "demo"）一個 Asset 檔。
    ///
    /// <para>典型用法：</para>
    /// <code>
    /// var card = new UCL_ChatTavernRoomAsset().GetData("demo");
    /// card.m_RoomRules = "本房間禁止 spam";
    /// card.Save();
    /// </code>
    ///
    /// <para>磁碟結構：</para>
    /// <code>
    /// &lt;module&gt;/UCL_Assets/UCL_ChatTavernRoomAsset/
    ///   ├── demo.json
    ///   ├── design.json
    ///   └── ...
    /// </code>
    /// </summary>
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
    public class UCL_ChatTavernRoomAsset : UCL_Asset<UCL_ChatTavernRoomAsset>
    {
        public const string DefaultID = "Default";

        // ===== 房間 rich data 欄位 =====

        // 區塊職責：房間橫幅圖（Top banner / hero image）— 對齊 UCL_SpriteAssetEntry 標準 Asset 引用
        // 物理意義：給酒館頁未來顯示房間 banner / Discord bridge 用
        // 數值影響：m_BannerSprite.m_ID 指向 UCL_SpriteAsset entry id（例 "Rooms_demo"）
        public UCL_SpriteAssetEntry m_BannerSprite = new UCL_SpriteAssetEntry();

        // 區塊職責：房間主題色（#RRGGBB）
        // 物理意義：UI 標題列 / 訊息列表 accent；空字串視為預設
        // 數值影響：ColorUtility.TryParseHtmlString 解析；UI 端 fallback 灰
        public string m_ColorHex = string.Empty;

        // 區塊職責：房間規則（多行文字）
        // 物理意義：給人類 / agent 看「本房間特殊規則」例「禁止 spam」「只討論技術」「玩家請以匿名身分發言」
        // 數值影響：純顯示用；不被 Cmd_Tavern 邏輯讀取
        public string m_RoomRules = string.Empty;

        // 區塊職責：filter / 分類用 tag 清單
        // 物理意義：例 ["design", "agent-only"] / ["public", "casual"]；給日後查詢 / 分組用
        // 數值影響：純字串集合，無預設語意
        public List<string> m_Tags = new List<string>();

        public UCL_ChatTavernRoomAsset() { ID = DefaultID; }
        public UCL_ChatTavernRoomAsset(string iID) { Init(iID); }
    }
}
