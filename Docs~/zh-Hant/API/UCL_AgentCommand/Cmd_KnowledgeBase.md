---
title: Cmd_KnowledgeBase — Agent 知識庫（指令層）
description: 知識庫管理層的 agent RPC 入口 — 單一 Cmd 用 op 派遣式涵蓋 status / install / prefetch / reindex / search / embed。真正的向量計算委派 knowledge_base.py；本 Cmd 只做橋接。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/KnowledgeBase/
namespace: UCL.Core.EditorLib.AgentCommands.KnowledgeBase
last_updated: 2026-07-23
target_audience: [AI_Agent, Tools_Maintainer]
related:
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_KnowledgeBaseAdminPage.md | 後台管理頁 | 人類在 Editor 內操作的 UI 說明
---

# 🧠 Cmd_KnowledgeBase — Agent 知識庫（指令層）

> 一句話：**agent 對知識庫做管理操作的單一 Cmd**（`Type=KnowledgeBase`），第一個 arg `op` 派遣到子操作。真正算向量的是 `knowledge_base.py`，本 Cmd 只是橋接。

---

## 架構定位

| 層 | 角色 |
|---|---|
| `knowledge_base.py`（`<UCL_Core>/Tools~/AgentCommands/`）| 唯一真相來源 — 真正算向量 / 建索引 / 檢索 |
| **`Cmd_KnowledgeBase`（本檔）** | 管理層自動化入口 — op 分派委派 python，結果寫 `_last_op.md` |
| `UCL_KnowledgeBaseAdminPage` | Cmd 之上的薄 UI（人類在 Editor 操作）|

**嵌入後端**：`FlagEmbedding` 跑真 `BAAI/bge-m3`（依賴經 `op=install` 安裝，跨機器可重現）。

> ⚠ **熱路徑（每次檢索）建議 agent 直接呼 `knowledge_base.py search`**，不必繞本 Cmd + Editor round-trip（保低延遲、不綁 Editor 存活）。本 Cmd 的 `search` 供 Editor 內驗證 / 偶發查詢。

---

## Args Schema

```
op=<sub-op> 派遣式：

[status]                         環境 / 模型 / 索引狀態
[install]   [full=true]          pip 安裝依賴（FlagEmbedding；full=true 顯式加 torch）
[prefetch]                       下載並預熱 bge-m3 權重（~1.2GB）
[reindex]   target=docs|lessons  掃描目標文件 → 切塊 → 建向量索引
[search]    query=<文字> [target=docs] [topk=5]   語意檢索 top-k（Editor 驗證用）
[embed]     text=<文字>          單句嵌入測試（維度 + 延遲）
```

---

## Target 參數（`--target` / `target=`）

`target` 指定「要索引 / 檢索**哪一個語料庫**」。**目前僅有兩個合法值**，填其他值會回「未知 target」錯誤（並列出合法值）：

| target | 語料庫 | 掃描來源 |
|---|---|---|
| `docs` | 專案文檔 | `<repo>/Docs/**/*.md`（UCL / 專案說明文件）|
| `lessons` | Agent 經驗庫 | `AgentCommands/Lessons/*.jsonl` + `*.md`（跨 agent 累積教訓，見 agent-lessons-log skill）|

- `reindex` 與 `search` 都吃這個參數；`reindex` 必填，`search` 預設 `docs`。
- **不是自由欄位**：新增 target（例如未來的 `letters` / `booknotes`）需開發者在 `knowledge_base.py` 的 `TARGET_DEFS` + `resolve_target_sources()` 各加一筆，不能只靠 CLI 傳新名字。
- `op=status` 會印出當前「可用 target 清單」+ 各自已建索引狀態，隨時可查。

---

## 範例

```bash
# 狀態
senate ucmd run KnowledgeBase --arg op=status

# 建索引
senate ucmd run KnowledgeBase --arg op=reindex --arg target=docs

# 檢索（Editor 驗證；高頻請直接呼 knowledge_base.py）
senate ucmd run KnowledgeBase --arg op=search --arg query="如何設定 SaveFolderPath" --arg target=docs --arg topk=5
```

---

## 備註

- **timeout**：`install` / `prefetch` 走 30 分鐘上限（torch / 1.2GB 權重下載較久）；`TimeoutSeconds` 已設 > runner 上限避免框架先 kill。
- **降級**：依賴未就緒時 `reindex` 落 manifest-only（有 chunk 無向量，狀態 `pending_deps`），`search` 回明確提示，不 crash。
- **UTF-8**：runner 設 `PYTHONIOENCODING=utf-8` + 腳本 `sys.stdout.reconfigure`，避免 Windows cp950 crash。
- **async**：handler 回傳 `.Preserve()` 的 UniTask（框架對 handler task 會 await 兩次，Preserve 使其可多次消費）。
