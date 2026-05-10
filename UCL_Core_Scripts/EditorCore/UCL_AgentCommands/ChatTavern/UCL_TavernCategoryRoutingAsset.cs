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

        // 區塊職責：此 group 是否視為「工作頻道」— 發訊息到此 group 自動 credit 1 token (Tim 拍板)
        // 物理意義：Op_Post 結尾 hook 會用本旗標判定要不要發「工作訊息薪資」(per Tim 規則：工作訊息視為上班，固定 1 token)
        // 數值影響：true → Op_Post auto-credit accountId=sender amount=1 source_kind=work_post；false → 不結算
        // 邊界：跟 m_IsDefault 是兩個獨立旗標 — 一個 group 可以是 default 但不是 work（譬如 lounge 設 default 不該領薪），
        //      也可以是 work 但不是 default（譬如未來 review-channel 也算工作）
        public bool m_IsWorkChannel = false;

        // 區塊職責：此 group 是否為「壟斷分類」— 命中此 category 的訊息 ONLY 到此 group，main / 其他 additive group 都跳過
        // 物理意義：解 T42 「買一送一」bug：戰鬥 log (category=battle) 應專屬 valor-channel，
        //          不該塞滿 #聊天酒館 main 頻道。設此旗標 = 該 group 對其 categories 排他擁有
        // 數值影響：Python notify_discord.py 端 routing layer 加判定；命中 exclusive group → main 跳過 +
        //          其他 non-exclusive group additive 也跳過（其他 exclusive group 仍各收一份）
        // 邊界：跟 m_IsDefault 互斥使用（default = fallback；exclusive = sole owner）；
        //       多 exclusive 同時命中 → 各送一份（exclusive 排他針對 main + non-exclusive，不互相排）
        // 安全：預設 false 保留現有行為（main + additive），forward-compat 不破壞既有 routing
        public bool m_Exclusive = false;

        public UCL_TavernCategoryRoutingAsset() { ID = DefaultID; }
        public UCL_TavernCategoryRoutingAsset(string iID) { Init(iID); }

        // ===========================================================
        // 區塊職責：靜態 helper — 對齊 Python 端 _match_msg_to_routing_groups precedence，
        //          給 C# 端（Cmd_Tavern.Op_Post）判定訊息 routing target group 用
        // 物理意義：載入所有 enabled UCL_TavernCategoryRoutingAsset → 命中 category 的 group，
        //          沒命中走 IsDefault group。回傳第一筆 matched group instance（or null 表都沒命中且沒 default）
        // 數值影響：fail swallow — Asset dir 不存在 / 任何例外 → 回 null（caller 自決 fallback）
        // ===========================================================

        /// <summary>
        /// 對訊息 category meta value 解析該 broadcast 的 routing group。
        /// </summary>
        /// <param name="category">訊息 meta.category 值（可空字串 = 不帶 category）</param>
        /// <returns>命中的 group instance；沒命中且沒 default group → null</returns>
        public static UCL_TavernCategoryRoutingAsset ResolveTargetGroup(string category)
        {
            try
            {
                // 走 GetAllIDs(true) — 強制重掃 module asset dir，避免 cache 漏看新加入的 group asset
                var allIDs = new UCL_TavernCategoryRoutingAsset().GetAllIDs(true);
                if (allIDs == null || allIDs.Count == 0) return null;

                string normalized = (category ?? "").Trim().ToLowerInvariant();

                UCL_TavernCategoryRoutingAsset defaultGroup = null;
                foreach (var id in allIDs)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    UCL_TavernCategoryRoutingAsset g = null;
                    try { g = new UCL_TavernCategoryRoutingAsset().GetData(id, false); /* iUseCache=false → 強制重讀 disk JSON */ } catch { continue; }
                    if (g == null || !g.m_Enabled) continue;

                    // Layer 1: category 命中 group's m_Categories
                    if (!string.IsNullOrEmpty(normalized) && g.m_Categories != null)
                    {
                        foreach (var c in g.m_Categories)
                        {
                            if (!string.IsNullOrEmpty(c) && string.Equals(c.Trim().ToLowerInvariant(), normalized))
                                return g;
                        }
                    }

                    // 順手蒐集 default group 候選（取第一筆 enabled IsDefault）
                    if (defaultGroup == null && g.m_IsDefault) defaultGroup = g;
                }

                // Layer 2: 都沒命中 → fallback default group
                return defaultGroup;
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[UCL_TavernCategoryRoutingAsset.ResolveTargetGroup] fail (category={category})：{ex.Message}");
                return null;
            }
        }
    }
}
