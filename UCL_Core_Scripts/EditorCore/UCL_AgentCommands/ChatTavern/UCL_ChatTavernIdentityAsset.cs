// 區塊職責：UCL Chat Tavern 角色卡 — UCL_Asset 體系下的 rich persona 持久化資料
// 物理意義：identities.json 是 lightweight roster（id / display_name / kind 三欄，給 Python 與 Cmd_Tavern 用）；
//          本 Asset 是「角色卡」獨立資料層 — 走 UCL_Asset<T> 標準持久化（每張卡一個 .json 檔，由 UCL_ModuleService 管路徑），
//          承載頭像 / system prompt / 色票 / catchphrases / tags 等 rich data。
// 數值影響：UCL_Asset 自帶 SerializeToJson / DeserializeFromJson / SaveFolderPath / OnEdit / Preview / OnGUI；
//          子類只需宣告欄位 + 兩個 ctor + 可選 attribute。檔案 IO / 編輯 UI / 模組路徑全由 base 處理。
// 設計取捨：
//   - **不繼承 ScriptableObject** — UCL_Core 體系慣例：持久化資料一律用 UCL_Asset<T>，不裸 SO（SO 跟 UCL_ModuleService 模組路徑機制不相容）
//   - **不污染 identities.json** — Python 端與 Cmd_Tavern 不需 rich data；rich data 只在 Editor 內被人類 / agent 透過 UCL_Asset 介面編輯
//   - **per-file 儲存** — 每個 ID 一個 .json 檔，git diff 友善、merge 衝突獨立化
using System.Collections.Generic;
using UCL.Core;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// Chat Tavern 角色卡（rich persona）。每個 identity_id（如 "claude-da-xiaojie"）一個 Asset 檔。
    ///
    /// <para>典型用法：</para>
    /// <code>
    /// // 取既有角色卡（不存在會 throw）
    /// var card = new UCL_ChatTavernIdentityAsset().GetData("claude-da-xiaojie");
    /// // 編輯後存檔
    /// card.m_RoleSettings = "傲嬌大小姐風格...";
    /// card.Save();
    /// </code>
    ///
    /// <para>磁碟結構（per UCL_ModuleService 慣例）：</para>
    /// <code>
    /// &lt;module&gt;/UCL_Assets/UCL_ChatTavernIdentityAsset/
    ///   ├── claude-da-xiaojie.json
    ///   ├── gemini-da-xiaojie.json
    ///   └── ...
    /// </code>
    /// </summary>
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
    public class UCL_ChatTavernIdentityAsset : UCL_Asset<UCL_ChatTavernIdentityAsset>
    {
        public const string DefaultID = "Default";

        // ===== 角色卡 rich data 欄位（採 UCL m_-prefix 慣例）=====

        // 區塊職責：頭像 — 走 UCL_SpriteAssetEntry 標準關聯（對齊 ImageGen workflow 既有 Asset 引用慣例）
        // 物理意義：m_AvatarSprite.m_ID 指向某個 UCL_SpriteAsset 的 ID（例 "Avatars_claude-da-xiaojie"）；
        //          UCL_SpriteAsset.json 內記錄實際 PNG 路徑與載入 mode；runtime 透過 GetData() / GetSprite() 拿到 Sprite
        // 數值影響：取代 v1 的 string m_AvatarPath（重構，非 forward-compat）；JSON schema 新欄位
        //          名 "AvatarSprite"，值是嵌套 object { "m_ID": "Avatars_xxx" }
        public UCL_SpriteAssetEntry m_AvatarSprite = new UCL_SpriteAssetEntry();

        // 區塊職責：persona 模板片段（不是整段 system prompt）
        // 物理意義：上層 wrapper 自行組裝完整 LLM system prompt — 本欄只放「角色性格描述」例「傲嬌大小姐風格，回話以哼開頭」
        // 數值影響：可變長字串；Editor 內用 UCL_GUILayout TextArea 編輯
        public string m_RoleSettings = string.Empty;

        // 區塊職責：UI 標色，#RRGGBB 格式
        // 物理意義：訊息列表 sender 標色 / IdentityEditPage 列表色票
        // 數值影響：空字串視為預設灰；ColorUtility.TryParseHtmlString 解析
        public string m_ColorHex = string.Empty;

        // 區塊職責：LLM persona reminder bullet list
        // 物理意義：給 LLM wrapper 在 system prompt 加 "Avoid breaking persona — examples:..." 提示
        // 數值影響：每筆是一個短句，例「本小姐才不是...」「別、別誤會啊」
        public List<string> m_Catchphrases = new List<string>();

        // 區塊職責：filter / 分類用 tag 清單
        // 物理意義：讓查詢 / Discord bridge / 主題分組用 — 例 ["agent", "anthropic", "main"]
        // 數值影響：純字串集合，無預設語意
        public List<string> m_Tags = new List<string>();

        public UCL_ChatTavernIdentityAsset() { ID = DefaultID; }
        public UCL_ChatTavernIdentityAsset(string iID) { Init(iID); }
    }
}
