# Identity Asset (角色卡)

> ucl-chat-tavern 細節參考檔(單主題)。母檔 [`../SKILL.md`](../SKILL.md)。內容逐字搬自舊版 SKILL.md。

---

## Identity Asset（角色卡）

### 是什麼
`UCL_ChatTavernIdentityAsset` ScriptableObject 是 `identities.json` 的 **Editor view layer**：
- JSON = single source-of-truth（Python / 跨平台都讀寫這個）
- Asset = Unity Inspector 編輯前端（拖 Sprite 頭像、編 system prompt、開色票）

存放：`Assets/UCL/ChatTavernIdentities/<id>.asset`（每張角色卡一檔）

### Schema 擴充欄位（v2）
傳統三欄（`id` / `display_name` / `kind`）之外加：
- `avatar_path` — repo-relative 圖檔路徑（給 Discord bridge / 跨平台渲染）
- `role_settings` — persona 模板片段（不是整段 system prompt — 上層 wrapper 自行組裝）
- `color_hex` — `#RRGGBB` UI tint
- `catchphrases` — `List<string>` LLM persona reminder bullets
- `tags` — `List<string>` filter / 分類

JSON 對 v1 forward-compat — 老 entry 沒這些欄位視同 null / 空。

### 雙向同步
- **Asset → JSON**：Asset 的 `OnValidate()` 算 hash，跟上次寫的比；不同就 `WriteAssetToJson()`
- **JSON → Asset**：`UCL_ChatTavernIdentitySync` `[InitializeOnLoad]` + `EditorApplication.update` 1Hz polling 偵測 JSON mtime 變動，自動 reload Asset；reload 期間 `IsSuppressing=true` 阻擋 OnValidate 反向寫回（避免迴圈）

### Agent 角度
- agent 一律只動 `identities.json`（Python `op=join` / Cmd_Tavern 端 `GetOrCreateIdentity`）
- Editor 端的 Asset 是「給人類開發者爽」用，agent 不用碰
- 如果 agent 需要 persona 設定（讀 `role_settings` 或 `catchphrases`）→ 直接讀 JSON 對應欄位

### Editor 入口
`UCL_ChatTavernIdentityEditPage`（已掛 `ShowInPageMenu => true` 進 EditorMenu Page Picker）
- 列表所有 Asset
- 點「編輯」→ Selection 切到 Asset，Inspector 顯示完整欄位
- 「🔄 從 JSON 同步全部」按鈕手動 trigger Sync（平時 1Hz polling 自動）

