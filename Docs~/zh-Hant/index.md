---
title: UCL_Core 文件索引
description: UCL_Core 框架的多語系文件入口 — 含 Agent Command 系統、UCL_Asset 資產系統、編輯器頁面、模組服務等四大主題分類
last_updated: 2026-08-02
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# 📚 UCL_Core 文件索引

> **UCL_Core** 是 UCL 框架的核心模組（編輯器資產系統 + 模組服務 + Agent Command 系統 + 編輯器 UI）。本檔是繁體中文版文件入口，其他語系見 `Docs~/{en,ja,zh-Hans,zh-Hant}/index.md`。

---

## Agent 共用規範

| 文件 | 用途 |
|---|---|
| [Coding_Standards](Agent/Coding_Standards.md) | C# 設定 model、`JsonData` 邊界與字串 key 規範 |
| [Code_Comment_Standards](Agent/Code_Comment_Standards.md) | 程式碼註解規範 |
| [AI_READABILITY_GUIDELINES](Agent/AI_READABILITY_GUIDELINES.md) | 文件與 AI 可讀性規範 |

## ⭐ 重點：Agent Command 系統

> **AI agent 與 Unity Editor 的跨 process 指令系統** — agent 寫 `queue.json`、Editor 端執行、結果寫回。是本框架**最重要的 AI 協作工具**。

### 必讀
| 文件 | 說明 |
|---|---|
| 🤖 **[UCL_AgentCommand_Architecture](API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)** ⭐⭐ | **整體架構** — 元件圖 / 生命週期 / 自動發現 / 觸發方式 / queue.json schema / 擴充點 |
| [UCL_AgentCommand](API/UCL_AgentCommand/UCL_AgentCommand.md) | 單一指令的資料模型 |
| [UCL_AgentCommandsPage](UCL_EditorPage/UCL_AgentCommandsPage.md) | Editor IMGUI 頁面（人類友善 UI） |

### 內建 Cmd 的 API 文件
| Cmd Type | API 文件 | 用途 |
|---|---|---|
| `DebugLog` | [Cmd_DebugLog](API/UCL_AgentCommand/Cmd_DebugLog.md) | 連線測試 / 最簡範例 |
| **`ResolveAssetReferences`** ⭐ | [Cmd_ResolveAssetReferences](API/UCL_AgentCommand/Cmd_ResolveAssetReferences.md) | **批次解析 UCL_Asset 連動鏈** — BFS + 反射 + maxDepth + 去重，輸出 (AssetType, ID, JSON 路徑) 清單給 AI agent |
| **`ExportCommandCatalog`** ⭐ | [Cmd_ExportCommandCatalog](API/UCL_AgentCommand/Cmd_ExportCommandCatalog.md) | **匯出當前所有已註冊 Handler 為 Markdown 目錄** — 與 Page 按鈕共用渲染邏輯 |
| **`FindAssetUsages`** ⭐ | [Cmd_FindAssetUsages](API/UCL_AgentCommand/Cmd_FindAssetUsages.md) | **反向查詢被引用位置** — 給定目標 Asset（例 RCG_CustomStatusData/Stun），掃描所有 UCL_Asset 子類找出所有引用點，附 dotted field path |
| **`Invoke`** ⭐ | [Cmd_Invoke](API/UCL_AgentCommand/Cmd_Invoke.md) | **通用反射調用** — 字串描述動態觸發 Unity / UCL public static + instance method / property / field；`target=$var` + `storeAs=name` 串成跨 invoke 變數鏈（例：`RCG_StoryData.Util` → `GetData(id)` → `GetSubStory(name)`）；解析+執行抽到 `UCL.Core.UCL_ReflectionInvoker`（UtilCore，runtime-available） |

### 觸發方式（4 種）
1. Editor UI（`UCL_AgentCommandsPage`）按鈕
2. `Tools/UCL/Agent Commands/Run Pending` Editor 選單
3. 直接編輯 `AgentCommands/queue.json` + 上面任一觸發
4. **Python CLI 包裝器** — `Tools~/AgentCommands/run_cmd.py`（推薦給 Agent）
5. **Unity Batchmode**（CI / 全自動）

完整對照與範例見 [UCL_AgentCommand_Architecture §7](API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md#7-觸發方式對照)。

---

## UCL_Asset 資產系統

| 文件 | 說明 |
|---|---|
| [UCL_Asset API](API/UCL_Asset/) | 資產序列化、Asset Entry、Common Editable 介面 |

---

## UCL_GUILayout / UCL_GUIStyle（IMGUI 元件 + 樣式層）

| 文件 | 說明 |
|---|---|
| 🎨 **[UCL_GUILayout_Overview](API/UCL_GUILayout/UCL_GUILayout_Overview.md)** ⭐ | **8 檔 partial class 的整體導覽** — 設計分層、檔案職責、API 速查（按用途分組）、跨檔共通模式（三段式多載 / `[SerializeReference]` 多型自動偵測 / 反射快取）、三個少見但高價值 helper（`IntFieldAuto` / `PopupSearchCache` / `DrawCopyPaste`）|
| 🪞 **[DrawObjectData](API/UCL_GUILayout/UCL_GUILayout_DrawObjectData.md)** ⭐ | **反射自動繪製整個物件介面** — 一行畫完一頁；四個客製化介面由小到大接管（`UCLI_ShortName` 名稱 / `UCLI_IsEnable` CheckBox / `UCLI_NameOnGUI` 整條標題列 / `UCLI_FieldOnGUI` 整個欄位）＋ NameOnGUI 與 IsEnable **互斥**的坑 |
| 🎨 [UCL_GUIStyle_Overview](API/UCL_GUIStyle/UCL_GUIStyle_Overview.md) | **IMGUI 樣式中央** — `BoxStyle` / `ButtonStyle` / `LabelStyle` / `TextField/Area`、DPI 全域 `Scale`、EditorWindow / Runtime 雙 cache、`LabelStyle` 反指守則（不可給互動控制項） |

---

## ProviderCore（可替換的求值策略）

把一個「值欄位」從固定值升級成可替換的求值策略；使用端只認 `GetXxx()`。
宣告欄位務必加 `[SerializeReference]` —— 那是多型的**唯一觸發訊號**，少了它會**靜默**丟掉子類資料。

| 文件 | 說明 |
|---|---|
| 🔤 **[UCL_StringProvider](API/ProviderCore/UCL_StringProvider.md)** ⭐ | **字串提供者基底** — implicit operator 雙向轉換、`[SerializeReference]` 必要性、序列化格式（ClassName）、用 `UCL_GUILayout.DrawList` 編輯清單、如何新增子類 |
| 📝 [UCL_StringValueProvider](API/ProviderCore/UCL_StringValueProvider.md) | 預設實作 — 回傳固定字串；`ToString()` 空值顯示 `(empty)` 的理由 |
| 🎲 [UCL_StringBookRecommendProvider](API/ProviderCore/UCL_StringBookRecommendProvider.md) | 從圖書館藏書隨機挑 N 本（預設 10）回傳書名；**Editor-only**（依賴 UCL_BooksIO）、無藏書回空字串 |

---

## Architecture

| 文件 | 說明 |
|---|---|
| [Architecture/Polymorphism_In_UCL](Architecture/Polymorphism_In_UCL.md) ⭐ | **多型支援整體架構** — `[SerializeReference]` × `UCLI_TypeListable` × `UCL_PolymorphicHelper` × `UCL_TypeReflectCache` 四者在 GUI 編輯與 JSON 序列化兩條路徑的角色與互動，新增多型欄位的標準寫法、UnityJsonSerializableObject 雙邊例外、為何 cache ctor 不能碰 service |

---

## 編輯器頁面（UCL_EditorPage）

| 文件 | 說明 |
|---|---|
| 👋 [UCL_WelcomePage](UCL_EditorPage/UCL_WelcomePage.md) ⭐ | **歡迎/總覽頁** — 首次安裝自動彈出，介紹 UCL_Core 主要功能與快速跳轉按鈕；可從選單 `UCL → Welcome` 隨時開啟 |
| [UCL_AgentCommandsPage](UCL_EditorPage/UCL_AgentCommandsPage.md) ⭐ | Agent Command 主頁面（隊列管理 / 新增 / Run Pending / Export Catalog）|
| [UCL_BartenderAdminPage](UCL_EditorPage/UCL_BartenderAdminPage.md) | 集中管理酒保報時、時間提醒、關鍵字留言與 daemon 執行狀態的 Editor 後台。 |
| [UCL_CommonEditorPage](UCL_EditorPage/UCL_CommonEditorPage.md) | 編輯器頁面共通基底 |
| [UCL_ModuleEditPage](UCL_EditorPage/UCL_ModuleEditPage.md) | 模組編輯頁面 |
| [UCL_ModuleServiceEditPage](UCL_EditorPage/UCL_ModuleServiceEditPage.md) | 模組服務編輯頁面 |
| [UCL_ModulePlayListPage](UCL_EditorPage/UCL_ModulePlayListPage.md) | 模組播放列表 |
| [UCL_SelectAssetPage](UCL_EditorPage/UCL_SelectAssetPage.md) | 資產選擇器 |

---

## UCL_ModuleService 模組服務

| 文件 | 說明 |
|---|---|
| [UCL_ModuleSystem_Architecture](UCL_ModuleService/UCL_ModuleSystem_Architecture.md) | 模組系統整體架構 |
| [UCL_ModuleService_API](UCL_ModuleService/UCL_ModuleService_API.md) | 服務 API |
| [UCL_Module_API](UCL_ModuleService/UCL_Module_API.md) | 單一模組 API |
| [UCL_ModulePath_API](UCL_ModuleService/UCL_ModulePath_API.md) | 路徑計算 API |
| **[UCL_CoreBootstrap](UCL_ModuleService/UCL_CoreBootstrap.md)** ⭐ | **Bootstrap 預設 Asset** — Templates~ 範本 / `[InitializeOnLoadMethod]` 自動補缺 / `EditType=Template` 編輯模式 |

---

## Workflows

| 文件 | 說明 |
|---|---|
| [HelpURL_Workflow](Workflows/HelpURL_Workflow.md) | `ucl_core:` / `eov_docs:` 等 prefix 機制 |
| [Hardcoded_Localize](Workflows/Hardcoded_Localize.md) | 硬寫本地化字串的處理 |
| 🎨 [Manga_Adaptation_Workflow](Workflows/Manga_Adaptation_Workflow.md) | **小說漫畫化 SOP** — 原作/分鏡/作畫三角分工 / `ArtGallery/Comic/<slug>/` 展區結構 / 分鏡稿記法（字幕 vs 對白、`▸註` 寫理由）/ **動筆前必拍板三件事**（版面走向・語言與專有名詞表・話數切分）/ 鐵則機制與「到期日」標註 / 六階段 SOP（**重讀原作・試畫一頁先於量產**）|
| 🛠️ [Create_Cmd_Workflow](Workflows/Create_Cmd_Workflow.md) | **建立新的 `Cmd_<Name>.cs` 子類 SOP** — 命名 / 檔案位置決策樹（UCL_Core vs 下游模組） / 標準範本（CommandType / ShortDescription / ArgsSchema / HelpURL） / ExecuteAsync 守則 / Editor 驗收 / 8 大常見地雷 / **§9 文件放置自動判斷方案**（`source_root` frontmatter + `Cmd_ValidateDocPlacement`）|
| 🛠️ [Create_EditorPage_Workflow](Workflows/Create_EditorPage_Workflow.md) ⭐ | **建立新的 `UCL_CommonEditorPage` 子類 SOP** — 繼承關係 / 必/選 override / TopBarButtons 客製 / 入口點掛接（父頁 / Welcome 卡片 / 選單） / UI 元件選用對照（連結 UCL_GUILayout 與 UCL_GUIStyle 文件） / HelpURL `{lang}` 佔位 / 8 大常見地雷 / 驗收清單 |
| 🛠️ [Create_Persona_Workflow](Workflows/Create_Persona_Workflow.md) | 步驟化 SOP — 在 UCL_Core 與 Python 喚醒系統下，建立全新 Persona 人格所需的一切設定。涵蓋 Registry 註冊、頭像生成與 UCL_SpriteAsset 登記、UCL_ChatTavernPersonaCardAsset 角色卡配置，以及 Templates~ 備份同步工作流。 |
| 🔁 [Edit_Recompile_Loop_Workflow](Workflows/Edit_Recompile_Loop_Workflow.md) ⭐ | **agent 改 .cs 後的強制同步 SOP** — `Cmd_Recompile` + Python `recompile` 子命令 + `.compile_status.json` 三件套；Edit → recompile → 0 errors 才繼續，否則讀 messages 修錯 loop（≤5 輪），故障模式對照表 |
| 🔧 [CompileError_Diagnose_Workflow](Workflows/CompileError_Diagnose_Workflow.md) ⭐ | **Unity Compile Error 排查 SOP** — `UCL_CompileErrorTracker` + `check_compile.py` standalone Python 工具，讓 agent 在「Cmd 系統因 compile error 也載不進來」的雞生蛋情境下也能讀到 dedup 過 of 錯誤清單。含 4 步排查 SOP、8 大常見 CS 錯誤對照、asmdef 跨界 / namespace 陷阱、Editor.log session 邊界偵測演算法、實戰 case study |

---

## 命名規則速查

| 樣式 | 用途 |
|---|---|
| `Cmd_<TypeName>` | Agent Command Handler 子類（如 `Cmd_ResolveAssetReferences`）|
| `UCL_<Module>` | UCL 框架類別 |
| `UCL_<Page>Page` | Editor IMGUI 頁面 |
| `<NS>.EditorLib.AgentCommands` | Agent Command 命名空間 |

---

## Python Tools (CLI / 自動化)

| 文件 | 一句話描述 |
|---|---|
| 🐍 [Tools/Python_Tools_Index](Tools/Python_Tools_Index.md) ⭐ | **UCL_Core/Tools~ 全 Python 工具索引** — awakening (morning/goodnight) / queue infra (run_cmd) / Editor 整合 (check_compile / hooks) / migration scripts / skill installer。含 project-specific Tools 對照 + Localize 工具缺位提醒 |

---

## 跨 repo 資源

- 專案層工作流（含完整 Agent Command 工作流、踩雷紀錄）：[`docs/Workflows/AgentCommands_Workflow.md`](../../../../../../docs/Workflows/AgentCommands_Workflow.md)
- Python CLI 包裝器：[`Tools~/AgentCommands/run_cmd.py`](../../Tools~/AgentCommands/run_cmd.py)
- queue.json 位置：`AgentCommands/queue.json`（專案根目錄）

---

## 其他語系

- 🇬🇧 [English](../en/index.md)
- 🇯🇵 [日本語](../ja/index.md)
- 🇨🇳 [简体中文](../zh-Hans/index.md)
- 🇹🇼 繁體中文（本檔）
