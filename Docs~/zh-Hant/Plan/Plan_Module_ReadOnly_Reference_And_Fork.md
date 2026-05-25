---
last_updated: 2026-05-25
related:
  - ucl_core:Docs~/zh-Hant/UCL_ModuleService/UCL_ModuleSystem_Architecture.md | 模組系統架構 | 模組生命週期、EditType、PC 免安裝直讀 (StreamingReadOnly) 的總體架構
  - ucl_core:Docs~/zh-Hant/UCL_EditorPage/UCL_ModuleServiceEditPage.md | 模組服務編輯頁 | 列出可編輯模組的主清單頁 (本 Plan 的清單擴充落點)
  - ucl_core:Docs~/zh-Hant/UCL_EditorPage/UCL_ModuleEditPage.md | 模組編輯詳情頁 | 選定模組後的編輯頁 (本 Plan 的唯讀顯示 + Fork 鈕落點)
---

# Plan：內建模組「唯讀參考 + 一鍵 Fork」(Module Read-Only Reference & Fork)

> 狀態：**Plan（待實作）** ｜ Tim 2026-05-25 拍板走「唯讀參考 + 一鍵 fork」方案 ｜ 作者：meadow（claude-code）
> 前置依賴：PC 免安裝直讀（`m_PCDirectStreaming` / `UCL_ModuleEditType.StreamingReadOnly`，gura 2026-05-25 ship，**尚未 commit**）

---

## 1. 背景與問題（根因已確認）

PC 免安裝直讀上線後，內建模組（如 `Core`）在 build 裡走 `StreamingReadOnly`，原始檔直接擺 `StreamingAssets/.ModuleService/Modules`，runtime 直讀、不解壓到 `persistentDataPath`。

但 Tim 實測發現兩個現象：

1. **runtime 仍可編輯 Core** —— 違反免安裝模式的唯讀意圖。
2. 把 `Core` 改名 `CoreBak` 後就不出現在清單 —— 證實清單來源是資料夾掃描。

### 根因（file:line）

模組服務主清單頁（`UCL_ModuleServiceEditPage` 對應的 `UCL_ModuleService.ContentOnGUI`）列可編輯模組的來源是：

- `UCL_ModuleService.cs:1495` → `aModules.Append(GetAllModuleIDs())`
- `UCL_ModuleService.cs:898-907` → `GetAllModuleIDs()` = `GetModulesEntry(全域 ModuleEditType).GetAllModulesID()`
- `UCL_ModuleService.cs:740-742` → 非 Editor 時全域 `ModuleEditType` 恆為 `Runtime`
- ∴ 清單 = 掃 `persistentDataPath/ModulesRoot/Modules` 底下所有資料夾

**推論**：
- Tim 那份可編輯的 Core 是 persistentDataPath 裡的**舊安裝副本**（改名 CoreBak 即移出掃描範圍）。
- PC 免安裝的真 Core 在 StreamingAssets，**這個掃描根本看不到它** → 預設不會出現在清單。
- 而編輯詳情頁（`UCL_ModuleEditPage`）對清單裡任何模組都直接給 `Save / Install / UnInstall / Zip` 鈕（`UCL_ModuleEditPage.cs:74-95`），**不檢查 read-only 狀態**。

---

## 2. 需求（Tim 拍板）

**不是把 Core 藏起來，而是反過來**：讓內建 `Core`（及其他 `StreamingReadOnly` 模組）**出現在清單裡供模組製作者參考**，但：

- ✅ **唯讀**：可瀏覽 / 查看資料當參考，**不可存檔編輯**。
- ✅ **一鍵 Fork**：提供「複製成可編輯模組」動作，把唯讀模組複製成 persistentDataPath 裡一份新的 `Runtime` 可編輯模組，給製作者明確的衍生路徑。

### 為何不走「可寫入但警告」（已否決）

「不擋寫入但警告」在兩種環境都危險：
- **真 PC build**：Core 在 StreamingAssets（安裝目錄，可能唯讀 / Program Files）→ 寫入物理上 throw（write guard 就是為此存在）。
- **Editor**：StreamingAssets = 專案**源資料夾** → 寫入會直接改到 repo 裡的原始 Core → 污染原始碼。

→ warning 擋不住手滑，且實際上要嘛失敗、要嘛腐蝕源檔。故採唯讀 + Fork。

---

## 3. 設計（三部分）

### 3.1 清單擴充：把唯讀模組也列出來

目標：清單同時包含
- persistentDataPath 的 `Runtime` 可編輯模組（既有行為），與
- `StreamingReadOnly` 直讀模組（如 Core），標示為唯讀。

做法（候選）：
- 在 `UCL_ModuleService` 新增 `GetAllEditableEntries()`（回傳 `(id, resolvedEditType)` 清單）：合併
  1. `GetAllModuleIDs()`（persistentDataPath 掃描）
  2. `m_EditTypeOverride` 的 keys（已登記為 `StreamingReadOnly` 的模組，InitAsync:823 填入）
     —— 或直接列舉 `GetModulesEntry(StreamingReadOnly).GetAllModulesID()`
- **去重**：若同一 ID 同時存在於 persistentDataPath（Runtime 副本）與 StreamingReadOnly，**以 `ResolveEditType(id)` 為準**（override 優先 → 視為唯讀），避免「同一模組出現兩筆」。
- 清單 UI（`UCL_ModuleService.cs:1490-1514`）每筆後綴標記，例如 `Core [唯讀]`。

### 3.2 唯讀顯示（`UCL_ModuleEditPage`）

- `EditModule(id)` / 開頁時用 **`ResolveEditType(id)`** 決定載入型別（唯讀模組以 `StreamingReadOnly` 開，不是 `Runtime`）。
- 當 `m_CurEditModule.ModuleEditType == StreamingReadOnly`（或新增 `IsReadOnly` 判定）：
  - 隱藏 / 禁用 `Save Module` / `Zip Module` / `Install` / `UnInstall` 鈕（`UCL_ModuleEditPage.cs:74-95`）。
  - `DrawObjectData(...)`（`:99`）走唯讀模式（瀏覽不可改；若 IMGUI 無唯讀模式則包一層 `GUI.enabled=false`）。
  - 頂部掛橫幅：「唯讀（內建參考）— 如需修改請按『複製成可編輯模組』」。

### 3.3 一鍵 Fork（核心新動作）

- 在唯讀模式的 `UCL_ModuleEditPage` 加按鈕「複製成可編輯模組（Fork）」。
- 行為：
  1. 彈出輸入新模組 ID（預設 `<原ID>_copy`，e.g. `Core_copy`），檢查不與既有模組 ID 衝突。
  2. 把唯讀模組的 `RootFolder`（StreamingAssets 直讀路徑）**整包複製**進 `persistentDataPath/ModulesRoot/Modules/<newID>`（沿用既有 `CopyDirectory`，與 `CopyBuiltinModuleToStreaming` 同套 IO）。
  3. 視需要改寫新模組 Config.json 的 ID / Title，並確保 `m_PCDirectStreaming = false`（衍生模組是可寫的 Runtime 模組，不繼承免安裝唯讀旗標）。
  4. 重整清單 + 把新模組以 `Runtime` 型別開成可編輯。

---

## 4. 檔案落點（Touch Points）

| 層 | 檔案 | 落點 | 改什麼 |
|---|---|---|---|
| Service | `UCL_Core_Scripts/AssetCore/UCL_ModuleService.cs` | `GetAllModuleIDs` 附近 (898) / 清單 GUI (1490-1514) | 新增 `GetAllEditableEntries()`（合併 Runtime + StreamingReadOnly、去重）；清單改用之 + 唯讀標記 |
| Service | 同上 | `EditModule(id)` | 用 `ResolveEditType(id)` 開正確型別；新增 `ForkModule(srcId, newId)` |
| Page | `UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_ModuleEditPage.cs` | `ContentOnGUI` (65-104) / `TopBarButtons` (43-64) | 唯讀時禁用寫入鈕 + 唯讀 DrawObjectData + 唯讀橫幅 + Fork 鈕 |
| Localize | `UCL_Core_Scripts/LocalizeCore/UCL_CodeLocalize.*.cs` | enum 標籤附近 | 新增 key：`Module_ReadOnly_Banner` / `Fork_To_Editable` / `Fork_NewID_Prompt`（en/zh-Hant/zh-Hans/ja 至少） |

> ⚠ 皆為 **UCL_Core 通用層**（非 EOV 專屬），改動留在 UCL_Core，走三層 commit bump。

---

## 5. 邊界與風險

1. **Editor vs build 的唯讀來源不同**：Editor 裡 `StreamingReadOnly` 不啟用（`m_EditTypeOverride` 在 Editor 通常為空，InitAsync:814 `IsPCDirectStreamingPlatform()` 為 false）→ Editor 下 Core 仍以 Runtime/Builtin 出現。需確認「唯讀參考」要不要在 Editor 也生效（建議：Editor 下用模組 `m_PCDirectStreaming` 旗標判唯讀，與 build 的 override 判定二選一統一成 `IsReadOnly(id)` helper）。
2. **去重一致性**：同 ID 同時有 persistentDataPath 副本 + StreamingReadOnly → 必須單一呈現（以 override 為準），否則清單重複 + 編輯歧義。
3. **Fork ID 衝突**：新 ID 與既有模組撞 → 擋下並提示改名。
4. **Fork 來源唯讀**：複製來源是唯讀路徑（只讀不寫），目的地是 persistentDataPath（可寫）→ 方向安全；但要確認複製內容過濾（排除 `.meta` / `.DS_Store`，對齊既有 zip/copy parity，cross-link gura 的 gap-6 結論）。
5. **快取**：`GetAllModuleIDs` 有 0.5s cache（`m_ModuleIDs`）；Fork 後要 `iUseCache=false` 強制刷新清單。

---

## 6. 防禦縱深（已有 backstop）

gura 的 write guard `ThrowIfBuildReadOnly` 已套 5 個寫入點（`SaveConfig` / `SaveAsset` / `DeleteAsset` / `GroupID` / `AssetMeta`）。即使本 Plan 的 UI 禁用被繞過，實際存檔仍會 fail-loud throw。本 Plan 的 UI 唯讀是**第一層防呆**，write guard 是**第二層兜底** —— 兩層並存，不互斥。

---

## 7. 待決問題（給 Tim / gura）

- **Q1**：唯讀參考要不要在 **Editor** 也生效？（影響 `IsReadOnly(id)` 判定要不要納入 `m_PCDirectStreaming` 旗標，而不只看 build override）
- **Q2**：Fork 預設新 ID 命名規則？（`<id>_copy` ／ 讓使用者全自填 ／ 帶日期）
- **Q3**：Fork 出來的模組要不要自動加進 PlayList？還是只建檔不掛載？

---

## 8. 驗收清單（實作後）

1. PC build：Core 出現在清單且標 `[唯讀]`，無 persistentDataPath 副本也看得到。
2. PC build：開 Core 編輯頁 → Save/Install/UnInstall/Zip 不可按、資料唯讀可瀏覽。
3. PC build：按 Fork → persistentDataPath 出現 `Core_copy` 可編輯模組，內容與 Core 一致、`m_PCDirectStreaming=false`。
4. 其他平台（Android/iOS）：100% 原邏輯，清單與編輯行為與今天逐位元一致。
5. 同 ID 雙來源不重複出現。
6. （若 Q1=是）Editor 下 Core 也呈唯讀 + 可 Fork。

---

## 9. 實作分工與協調

- 本 Plan 改的檔案（`UCL_ModuleService.cs` / `UCL_ModuleEditPage.cs`）是 **gura 尚未 commit 的 ModuleService 同批檔案**。
- 為避免多人同改 AssetCore 撞車，實作前需 Tim 拍板分工（meadow 接手 ∥ 交 gura ∥ 先 commit gura baseline 再接手）。
- 此文件本身先行落地，作為實作前的設計共識基準。
