// 區塊職責：逐 Skill 啟用設定（UCL_AgentSkillManagerPage 的 per-skill 開關狀態持久層）
// 物理意義：一個 asset = 一個 skill 的啟用狀態；asset 的 ID 即 skill 名(如 ucl-commit)、m_Enabled 為開關。
//          沿用 UCL_ConfigAsset「一檔一 ID」哲學 — 多 skill 用多檔，避免單一大檔 git merge 衝突，
//          每筆 skill 獨立 commit / diff。預設值走 Templates~ / .BuiltinModules（Plan_SkillManager_PerSkill_Toggle）。
// 數值影響：純資料層。Page toggle on/off 改 m_Enabled + Save() 落地；缺檔的 skill → 取 Template 預設。
using UnityEngine;

namespace UCL.Core
{
    /// <summary>
    /// 逐 skill 啟用設定 asset（UCL_Asset 版）。
    /// **一個檔案 = 一個 skill**，asset 的 <see cref="UCL_Asset{T}.ID"/> 就是 skill 名、<see cref="m_Enabled"/> 是開關。
    ///
    /// <para>磁碟結構：</para>
    /// <code>
    /// &lt;module&gt;/UCL_Assets/UCL_SkillConfigAsset/
    ///   ├── ucl-commit.json        { "m_Enabled": true }
    ///   ├── ucl-watch-video.json   { "m_Enabled": false, "m_Note": "低頻 niche, 預設關" }
    ///   └── ...
    /// </code>
    ///
    /// <para>設計：跟 install_skills.py 的 per-skill install/uninstall 對接 —
    /// toggle on → 裝該 skill、toggle off → 解除安裝；預設值來自 Templates~（淘汰候選預設 false）。</para>
    /// </summary>
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Config)]
    public class UCL_SkillConfigAsset : UCL_Asset<UCL_SkillConfigAsset>
    {
        public const string DefaultID = "Default";

        // 區塊職責：該 skill 是否啟用（裝/不裝）
        // 物理意義：true = 該 skill 應被安裝到 .claude/skills/；false = 不裝（淘汰候選 default-OFF）
        // 數值影響：Page 讀此值畫 toggle；toggle 改值 + Save() 後由 install_skills.py 落地
        public bool m_Enabled = true;

        // 區塊職責：備註（為何開/關，如「淘汰候選」「低頻」）
        public string m_Note = string.Empty;

        public UCL_SkillConfigAsset() { ID = DefaultID; }
        public UCL_SkillConfigAsset(string iID) { Init(iID); }
    }

    [System.Serializable]
    public class UCL_SkillConfigAssetEntry : UCL_AssetEntryDefault<UCL_SkillConfigAsset>
    {
        public UCL_SkillConfigAssetEntry() { ID = UCL_SkillConfigAsset.DefaultID; }
        public UCL_SkillConfigAssetEntry(string iID) { ID = iID; }
    }
}
