---
title: UCL_KnowledgeBaseAdminPage — 知識庫後台管理頁
description: Agent 長期記憶 / 文檔向量檢索的後台管理入口。環境檢查、依賴安裝、模型預熱、索引重建、檢索測試全在 Editor 內一鍵操作；計算委派 knowledge_base.py，與 agent 走 Cmd_KnowledgeBase 同一支腳本。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_KnowledgeBaseAdminPage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-07-23
target_audience: [Tools_User, Gameplay_Programmer]
related:
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_KnowledgeBase.md | Cmd_KnowledgeBase 指令規格 | agent 端的 op 派遣式 Cmd 介面
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_DocSearchPage.md | 文檔關鍵字搜尋頁 | 關鍵字精確搜尋（知識庫則走語意向量檢索，互補）
---

# 🧠 UCL_KnowledgeBaseAdminPage — 知識庫後台管理頁

> 一句話：**Agent 知識庫（長期記憶 / 文檔語意檢索）的管理儀表板**。真正的向量計算全在 `knowledge_base.py`，本頁只是它之上的薄 UI；agent 透過 `Cmd_KnowledgeBase` 走的是**同一支腳本**。

---

## 1. 開啟方式

控制台（UCL_ControlPanelPage）→「🧠 知識庫管理」。頁面右上 `?` 會依當前語系跳轉本文件。

---

## 2. 架構定位（三層，職責不重疊）

| 層 | 角色 | 說明 |
|---|---|---|
| `knowledge_base.py` | **唯一真相來源** | 真正算向量、建索引、跑檢索。位於 `<UCL_Core>/Tools~/AgentCommands/`（跨專案共用），index 快取落在**主專案** `AgentCommands/_vectors/` |
| `Cmd_KnowledgeBase` | **管理層自動化入口** | agent 經 queue.json 呼叫；op 分派委派 python |
| `UCL_KnowledgeBaseAdminPage` | **薄 UI** | 人在 Editor 點按鈕；經 `UCL_KnowledgeBaseRunner` 非同步 spawn python（不凍結 Editor），與 agent 同一條 code path |

> **嵌入後端**：`FlagEmbedding` 跑真正的 `BAAI/bge-m3`（dense + sparse + colbert 三合一；skeleton 先用 dense）。model id 可經 `--model` / 環境變數 `KB_EMBED_MODEL` 覆蓋（介面與模型解耦）。
>
> **熱路徑（每次檢索）刻意留純 Python**：agent 直接呼 `knowledge_base.py search`，不繞 Unity Cmd queue round-trip（保低延遲、不綁 Editor 存活）。本頁的檢索測試僅供 Editor 內驗證。

---

## 3. 面板功能

### 1) 環境與索引狀態
顯示 Python 版本、嵌入模型、後端依賴是否安裝、向量庫目錄、各 target 索引（chunk 數 / 是否已有向量 / 建立時間）。右上「🔄 重新整理狀態」重讀。

### 2) 依賴安裝與權重
- **📦 安裝 bge-m3 依賴（FlagEmbedding + torch）** — 走 `op=install`，跨專案 / 機器可重現（不需手動 pip）。torch 較大，可能數分鐘。
- **⬇️ 下載並預熱 bge-m3 權重（~1.2GB）** — 走 `op=prefetch`，首次下載 + 預熱一次推論。

### 3) 知識庫索引重建
- **📚 重建 Docs 索引** — 掃描專案 `Docs/**/*.md` → 切塊 → 建向量（`op=reindex --target docs`）。
- **🧠 重建 Lessons 索引** — 掃描 agent 經驗庫（`op=reindex --target lessons`）。
- 依賴未就緒時建 manifest-only（有 chunk 無向量，狀態 `pending_deps`），裝好後重跑才有向量。

### 4) 檢索測試
以**下拉選單**選 target（`KnowledgeBaseTarget` enum，只列合法值、填不了錯的）+ query，執行語意檢索回 top-k（`op=search`）。旁邊有「重建 {target} 索引」快捷鈕。供 Editor 內驗證用；高頻檢索建議 agent 直接呼 python。

---

## Target 是什麼、可以填哪些

`target` = 「要索引 / 檢索**哪一個語料庫**」。**目前僅兩個合法值**，填其他值（例如 `letters`）會直接回「未知 target」並列出合法清單：

| target | 語料庫 | 掃描來源 |
|---|---|---|
| `docs` | 專案文檔 | `<repo>/Docs/**/*.md` |
| `lessons` | Agent 經驗庫 | `AgentCommands/Lessons/*.jsonl` + `*.md` |

- 「重建索引」與「檢索測試」都吃這個值；狀態面板會列出目前可用的 target。
- 頁面上 target 是**下拉選單**（`KnowledgeBaseTarget` enum），選不到非法值；agent 走 CLI/Cmd 傳字串則由 python 端驗證、非法回「未知 target」。
- **不是自由欄位**：要新增 target 需開發者同步改兩處 — `knowledge_base.py` 的 `TARGET_DEFS`/`resolve_target_sources()` **與** C# 的 `KnowledgeBaseTarget` enum。

---

## 4. 典型首次啟用流程

1. 「📦 安裝 bge-m3 依賴」→ 等 pip 完成
2. 「⬇️ 預熱 bge-m3 權重」→ 等 ~1.2GB 下載
3. 「📚 重建 Docs 索引」→ 這次會有真向量（非 manifest-only）
4. 「🔍 執行檢索」→ 看語意命中

---

## 5. 設計備註

- **不凍結 Editor**：重活（install / prefetch / reindex）全在背景執行緒跑 python，完成才回主執行緒刷 UI。
- **路徑跨專案安全**：腳本位置經 `UCL_EditorPath.CorePath` 動態解析，不硬編 install path。
- **UTF-8**：C# 端設 `PYTHONIOENCODING=utf-8` + 腳本自身 `sys.stdout.reconfigure`，避免 Windows cp950 亂碼 / crash。
