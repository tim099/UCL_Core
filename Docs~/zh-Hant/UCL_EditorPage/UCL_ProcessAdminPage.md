---
title: Process 管理頁 (UCL_ProcessAdminPage)
description: C# 端 child process 註冊中心的 UI — 列出所有經 UCL_ProcessRegistryService 註冊的外部 process，即時身分驗證 (Alive/Dead/PidReused/Unknown)，防誤殺 kill 與殘留記錄清理。
tags: [editor-page, process, daemon, registry]
aliases: [Process 管理, process registry, 程序管理]
target_audience: [AI_Agent, Tools_User]
last_updated: 2026-07-27
---

# 🧩 Process 管理頁 (UCL_ProcessAdminPage)

> 一句話：**C# 開的每顆外部 process 都要在 `UCL_ProcessRegistryService` 登記**，本頁是檢視與處置台 — 解「多顆 daemon 併跑互踩」「recompile 後 Process 物件蒸發變孤兒」「光憑 PID 誤殺別人」三族問題（Tim 2026-07-27 拍板，起因：疑似多 daemon 造成短時間重複 OCR）。

## 核心機制

### 身分三重驗證（防誤殺的關鍵）

PID 會被 OS 回收再發 — 記錄裡的 PID 活著 ≠ 是當初那顆。驗證 = **PID + process name + start time (UTC, 容差 2s)** 三者都吻合才算 `Alive`：

| 狀態 | 意義 | 允許操作 |
|---|---|---|
| 🟢 `Alive` | 三重比對全過，確定是本尊 | Kill（二段確認） |
| ⚫ `Dead` | PID 不存在或已退出 | 移除記錄 |
| 🟠 `PidReused` | PID 活著但 name / start_time 不吻合 — **已易主，絕不可 kill** | 移除記錄 |
| 🟡 `Unknown` | 拿不到對方資訊（權限等）— 保守不動手 | 移除記錄 |

`KillRegistered` 在 kill 前還會做最後一次 start_time 複驗（Validate 到 Kill 之間的 race 窗）。

### 每 process 單檔持久化

記錄落主專案 `AgentCommands/_process_registry/<tag>_<pid>.json`（atomic 換檔寫入）— domain reload / recompile 後 C# 的 `Process` 物件蒸發，但檔案記錄還在，仍可接管處置。單檔設計避免併發互蓋、壞檔不連坐。

記錄欄位：`pid` / `process_name` / `start_time_utc`（身分關鍵）/ `tag`（這顆在做什麼）/ `description` / `command_line` / `registered_by` / `registered_at_utc`。

## Spawn 端接入方式

```csharp
// spawn 前 singleton guard: kill 之前註冊的所有同 tag process (防同類多開併寫)
// Alive 驗證通過才 kill; Dead/PidReused 只清記錄 (PID 易主的現任持有者絕不碰); Unknown 保守跳過
UCL_ProcessRegistryService.KillAllByTag("my_daemon");

// spawn 後註冊 — allowMultiple 預設 false = singleton 模式:
// 註冊時自動 kill 既存同 tag process (確保同功能同時只有一顆); 允許多開就傳 true
var proc = new Process { StartInfo = psi };
proc.Start();
UCL_ProcessRegistryService.Register(proc, "my_daemon", "說明這顆在做什麼", nameof(MyCaller));
// UCL_ProcessRegistryService.Register(proc, "worker_pool", "可多開的 worker", nameof(MyCaller), allowMultiple: true);

// 正常收掉後註銷
UCL_ProcessRegistryService.Unregister(pid, "my_daemon");
```

已接入：`UCL_ScreenStreamDaemon`（tag=`screenstream_daemon`）。其他 spawn 點（Bartender / Tavern / KnowledgeBase / MediaAdmin runner…）可逐步接入。

## Python 端對偶 Service（process_registry.py）

Python 端啟動的常駐 process 走 `<UCL_Core>/Tools~/AgentCommands/process_registry.py` — **與 C# 共用同一目錄、同一 json schema、同一身分驗證語意**（統一管理處，Tim 2026-07-27 拍板）：

```python
from process_registry import register_self, kill_all_by_tag, unregister

# 常駐腳本啟動時自我註冊 (skip_if_exists: C# spawn 端已代註冊時不重寫)
register_self("my_watch_session", description="...", registered_by="my_script.py",
              allow_multiple=False)   # False = singleton, 註冊時先收掉既存同 tag
```

CLI：`python process_registry.py list | cleanup | kill-tag <tag>`

跨端對齊要點：
- `process_name` 存**無副檔名 basename**（`python` 而非 `python.exe`）— 對齊 C# `Process.ProcessName`，兩端互相 Validate 不誤判
- start_time 容差同為 2.0s；`kill_all_by_tag` 絕不殺 `os.getpid()` 自己（雙保險）
- 依賴 `psutil`（缺時全部 fail-soft 成 `unknown`，保守不動手）

已接入：`screenstream_daemon.py` 啟動時自我註冊（`skip_if_exists=True` — C# 已代註冊時保留 C# 出處；CLI 手動啟動時補能見度）。

### STT / OCR 歸屬說明

STT（`SttCacheWorker`）與 OCR（`OcrWorkerPool`）是 **screenstream_daemon process 內的 threads，不是獨立 process** — 沒有自己的 PID，隨 daemon 記錄一併受管；其停滯防治歸 daemon 內的 T-STT-Watchdog。

## 頁面操作

- **🔄 立即重新整理**（另有每 2s 自動 refresh）
- **🧹 清理失效記錄** — 批次移除 Dead / PidReused 殘檔
- **📂 開啟資料夾** — 直接看記錄檔
- 每列：狀態 / tag / PID / start time / registered_by / cmdline；`Alive` 給二段確認 Kill，其餘只給移除記錄

## 關聯

- Service 本體：`UCL_Core_Scripts/EditorCore/UCL_ProcessRegistry/UCL_ProcessRegistryService.cs`
- [UCL_ScreenStreamPage（螢幕直播錄影）](UCL_ScreenStreamPage.md) — 第一個接入的 daemon spawn 端
