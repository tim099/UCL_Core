// 區塊職責：Tavern 訊息分類路由規則 — UCL_Asset<T> 持久化資料
// 物理意義：每個 routing group = 一個 Asset 檔，承載「哪些 category meta tag → 哪組 Discord webhook URL」；
//          Editor 內透過 UCL_AssetEditPage 視覺化編輯（拖 Inspector 編 List<string>），
//          Python 端 notify_discord.py 走 _load_category_routing_groups loader 掃 .BuiltinModules dir 載入。
// 數值影響：
//   - per-file JSON：一群一檔，git diff 友善、merge 衝突獨立化
//   - Templates~ 跨專案：透過 Cmd_MigrateAssetToTemplate 推到 UCL_Core 倉庫成為其他專案 pull 後的開箱預設
//   - Default channel 機制：m_IsDefault=true 的 group 兼任「未命中 category 時的 fallback target」（Tim 拍板補充）
// 設計取捨：
//   - **不繼承 ScriptableObject** — 對齊 UCL_Core 體系慣例，UCL_Asset<T> 一致管路徑 / IO / Editor UI
//   - **不走 notify_config.json 大區塊** — 群組多了 schema 臃腫、Editor 不可視覺化、跨專案要 hand-copy
//   - **m_IsDefault 而非 fallback_to_default config flag** — 把 fallback target 推到 Asset 層，schema 更內聚
using System.Collections.Generic;
using UCL.Core;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// Chat Tavern 訊息分類路由規則（UCL_Asset 體系）。每個 routing group（如 "work-channel"）一個 Asset 檔。
    ///
    /// <para>Routing precedence（由 notify_discord.py 端實作）：</para>
    /// <list type="number">
    ///   <item>quest_routing.sender_match_prefix（既有，最高優先；不在本 Asset 體系內）</item>
    ///   <item>category_routing — 訊息 meta.category 命中某 group's m_Categories → broadcast 到該 group's webhook URLs</item>
    ///   <item>未命中 → 走 m_IsDefault=true 的 group（Tim 拍板：work-channel 兼任 default fallback）</item>
    ///   <item>沒有 default group → 走既有 tavern_mirror.webhook_urls (再次 fallback)</item>
    /// </list>
    ///
    /// <para>典型用法：</para>
    /// <code>
    /// // 取既有 routing group
    /// var group = new UCL_TavernCategoryRoutingAsset().GetData("work-channel");
    /// group.m_Categories.Add("debug");   // 動態加 category
    /// group.Save();
    /// </code>
    ///
    /// <para>磁碟結構（per UCL_ModuleService 慣例）：</para>
    /// <code>
    /// &lt;module&gt;/UCL_Assets/UCL_TavernCategoryRoutingAsset/
    ///   ├── work-channel.json       (m_IsDefault=true / categories=[work, meta] / 真 webhook)
    ///   ├── chitchat-channel.json   (categories=[chitchat] / placeholder webhook)
    ///   ├── lounge-channel.json     (categories=[relax] / placeholder webhook)
    ///   └── ...
    /// </code>
    /// </summary>
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
    public class UCL_TavernCategoryRoutingAsset : UCL_Asset<UCL_TavernCategoryRoutingAsset>
    {
        public const string DefaultID = "Default";

        // ===== Routing 規則欄位（採 UCL m_-prefix 慣例；UCL_Json 序列化會去 prefix）=====

        // 區塊職責：命中此 group 的 category 列表
        // 物理意義：訊息 meta tag「category:<value>」的 value 若 case-insensitive match 列表內任一筆 → 命中此 group
        // 數值影響：建議用 enum-like 短字串（work / chitchat / relax / meta）；自由字串也接但無 type-safe 保護
        public List<string> m_Categories = new List<string>();

        // 區塊職責：Webhook URL 直填清單（最後一層 fallback）
        // 物理意義：當 m_WebhookEnvVar 跟 m_WebhookFile 都沒解析到值時用本欄位
        // 數值影響：Templates~ 內建議用 placeholder（"https://discord.com/api/webhooks/REPLACE_ME/..."），
        //          真 URL 走 ENV / file 覆寫不入 git
        public List<string> m_WebhookUrls = new List<string>();

        // 區塊職責：環境變數名（最高優先解析層）
        // 物理意義：CI / 多人協作場景 — 各自設自家環境變數覆寫；空字串 = 不啟用此層
        // 數值影響：環境變數值多 URL 用逗號分隔，空白 trim
        public string m_WebhookEnvVar = string.Empty;

        // 區塊職責：本機檔名（次優先層）
        // 物理意義：路徑相對 PromptQueue/ 目錄；空字串 = 不啟用此層
        // 數值影響：檔內每行一個 URL，「#」開頭視為註解，空白 / 空行 skip
        public string m_WebhookFile = string.Empty;

        // 區塊職責：給人類 / agent 看的群組描述
        // 物理意義：純文檔欄位，Editor 列表 / Discord broadcast log 可顯示
        // 數值影響：無 routing 邏輯影響
        public string m_Description = string.Empty;

        // 區塊職責：是否啟用此 group
        // 物理意義：false = routing 端跳過此 Asset 不參與分類比對；保留檔案不刪
        // 數值影響：Python 端 _load_category_routing_groups 過濾條件；m_IsDefault group 若 m_Enabled=false 則 fallback 找下一個 default
        public bool m_Enabled = true;

        // 區塊職責：是否為 default channel（Tim 拍板補充）
        // 物理意義：訊息 category 沒命中任何 group 的 m_Categories → broadcast 到 m_IsDefault=true 的 group
        // 數值影響：建議只設一個 group 為 default（多個 default 走第一筆 m_Enabled 的）；
        //          如果完全沒 default group → fallback 到既有 tavern_mirror.webhook_urls
        public bool m_IsDefault = false;

        public UCL_TavernCategoryRoutingAsset() { ID = DefaultID; }
        public UCL_TavernCategoryRoutingAsset(string iID) { Init(iID); }
    }
}
