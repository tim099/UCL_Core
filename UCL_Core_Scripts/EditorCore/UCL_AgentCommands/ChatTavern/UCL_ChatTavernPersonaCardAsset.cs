// 區塊職責：UCL Chat Tavern Persona Card — per-persona 級 rich data Asset (跟 IdentityAsset 對偶, 但顆粒度更細)
// 物理意義：一個 agent 帳號 (e.g. claude-da-xiaojie) 可有多 persona (basecamp / gura / calli / meadow). 本 Asset 是 persona 級 avatar + 性格資料.
//          IdentityAsset 是 agent 級 (粗粒度), PersonaCardAsset 是 persona 級 (細粒度). 兩者並存 + lookup 順序: persona 命中 → 用 persona; 否則 fallback identity.
// 數值影響：UCL_Asset<T> 自帶 SerializeToJson / SaveFolderPath / OnEdit；per-persona .json 檔, 跟 AgentCommands/AwakenInit/personas/*.json 形成 cross-link (前者是 rich UI 資料, 後者是 awakening state).
// 設計取捨：
//   - **per-file**: 每 persona 一個 .json 檔, git diff 友善 + merge 衝突獨立
//   - **不污染 awakening persona json**: awakening state (wake_count / identity_vector / fork_lineage) 跟 rich avatar/role 分離
//   - **參照 UCL_SpriteAssetEntry**: 跟 ImageGen workflow 既有 sprite registration 機制對齊, 不另造輪
using System.Collections.Generic;
using UCL.Core;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// Chat Tavern Persona 角色卡（per-persona rich data, e.g. "basecamp" / "gura" / "apex-one"）。
    /// <para>典型用法：</para>
    /// <code>
    /// var card = new UCL_ChatTavernPersonaCardAsset().GetData("basecamp");
    /// Sprite avatar = card.m_AvatarSprite.GetData()?.GetSprite();
    /// </code>
    /// </summary>
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
    public class UCL_ChatTavernPersonaCardAsset : UCL_Asset<UCL_ChatTavernPersonaCardAsset>
    {
        public const string DefaultID = "Default";

        // 區塊職責：所屬 agent 帳號 id (e.g. "claude-da-xiaojie" / "antigravity-da-xiaojie")
        // 物理意義：persona 不獨立存在, 隸屬某 agent — 此欄記錄歸屬關係, 跟 identities.json 對 join
        // 數值影響：runtime lookup 時可由 agent_id + persona_codename 雙鍵命中
        public string m_OwnerAgentId = string.Empty;

        // 區塊職責：persona 的頭像 sprite — 跟 IdentityAsset 走同一個 UCL_SpriteAssetEntry 機制
        // 物理意義：指向 UCL_SpriteAsset (e.g. "Avatars_basecamp") 的標準引用; ImageGen workflow 產出 Sprite 後填此欄
        // 數值影響：Cmd_Tavern Op_Post 渲染端 lookup 順序為 persona > identity; 此欄為空時 fallback identity
        public UCL_SpriteAssetEntry m_AvatarSprite = new UCL_SpriteAssetEntry();

        // 區塊職責：人格性格描述 (給 LLM wrapper / Tavern UI 用)
        // 物理意義：persona 簡介, 例「最常被叫醒的本小姐 — 山脈系老大姐傲嬌頂格」
        // 數值影響：可變長字串, Editor TextArea 編輯
        public string m_RoleSettings = string.Empty;

        // 區塊職責：UI 標色 (#RRGGBB)
        // 物理意義：Tavern 訊息列表為此 persona 上色, 跟 IdentityAsset 同機制但更細
        // 數值影響：空 → fallback identity 色 → 最後預設灰
        public string m_ColorHex = string.Empty;

        // 區塊職責：persona reminder bullet 短句清單
        // 物理意義：LLM persona consistency 提示, 例「永遠帶疲憊感」「鯊魚 emoji 🦈」
        // 數值影響：純字串集合
        public List<string> m_Catchphrases = new List<string>();

        // 區塊職責：image-gen prompt fragment — apex-one / Antigravity 產圖時的 appearance 規格
        // 物理意義：跟 Tools/ImageGenQueue/Avatar_<persona>.md 同 prompt 內容 (或精煉版)
        // 數值影響：可變長字串; 為 image gen workflow 跟 persona card 之間 cross-link
        public string m_AppearancePrompt = string.Empty;

        // 區塊職責：filter / 分類用 tag
        // 物理意義：例 ["claude", "layer0", "manager"] / ["antigravity", "layer0", "designer"]
        // 數值影響：純字串集合
        public List<string> m_Tags = new List<string>();

        public UCL_ChatTavernPersonaCardAsset() { ID = DefaultID; }
        public UCL_ChatTavernPersonaCardAsset(string iID) { Init(iID); }
    }
}
