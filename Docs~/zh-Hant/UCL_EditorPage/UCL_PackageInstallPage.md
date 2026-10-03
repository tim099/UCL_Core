---
title: UCL_PackageInstallPage — 套件安裝頁
description: 外部工具的偵測與安裝（目前：Unity 官方 CLI）—— 看裝了沒、哪一版，一鍵開官方安裝指令。入口在 ToolBox「環境安裝」組。
source_files: |
  UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_PackageInstallPage.cs
  UCL_Core_Scripts/EditorCore/PackageInstall/UCL_UnityCliInstaller.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-10-03 (新頁；第一項 Unity CLI)
target_audience: [AI_Agent, Tools_Maintainer]
tags: [editor-page, install, unity-cli]
related:
  - ucl_core:Docs~/{lang}/Plan/Plan_Unity_CLI_Evaluation.md | Unity CLI 評估 | 為什麼要裝它、它能不能取代心跳
---

# UCL_PackageInstallPage — 套件安裝頁

> 一句話：**這個工作流要用、但不在 Unity 專案裡的外部工具**，在這一頁看狀態、開官方安裝指令。

## 1. 入口

工具集（`UCL_ToolBoxPage`）→「🧰 環境安裝」組 →「套件安裝」。本頁 `ShowInPageMenu=false`，不出現在下拉頁選單。

## 2. Unity CLI 區塊

| 元件 | 做什麼 |
|---|---|
| 標題列 | 現況一句話：偵測中／未安裝／找到檔案但讀不到版本／版本字串 |
| 🔄 重新偵測 | 重找執行檔＋跑一次 `unity --version`（背景執行緒，不卡 Editor） |
| 📖 官方文件 | 開 Unity CLI 官方說明頁 |
| ⬇ 安裝（winget，建議） | `winget install --id Unity.CLI --exact`；這台沒有 winget 時停用 |
| ⬇ 安裝（官方腳本） | `irm https://unity.com/install.ps1 \| iex`（下載並執行遠端腳本） |
| ⤴ 更新（self-update） | `"<偵測到的路徑>" self-update`；還沒找到 CLI 時停用 |

- 三顆動作鈕都先跳確認彈窗，**原文列出實際會跑的指令**；按「執行」才開視窗。
- 指令在**獨立的 PowerShell 視窗**裡跑（`-NoExit`，跑完不關），本頁不等、不讀輸出。視窗登記在 ProcessRegistry（tag `unity_cli_install`），ProcessAdmin 頁看得到。
- ⛔ winget 指令**不帶** `--accept-source-agreements`／`--accept-package-agreements` —— 條款由人在視窗裡自己同意。

## 3. 偵測怎麼找

依序找 `unity.exe`，第一個命中就用：

1. `UNITY_CLI_HOME\bin`（官方腳本的自訂安裝位置）
2. 使用者 PATH、系統 PATH —— **讀登錄檔的最新值**，不是 Editor 行程啟動時的快照 ⇒ 裝完按「重新偵測」就找得到，**不必重開 Unity**
3. Editor 行程自己的 PATH（墊底）
4. `%LOCALAPPDATA%\Microsoft\WindowsApps`（winget 的 MSIX 別名）

⚠ **旁邊有 `Data\` 資料夾的 `Unity.exe` 一律跳過** —— 那是 Unity Editor 本體（Windows 檔名不分大小寫），對它跑 `--version` 會開一個 Editor。被跳過的路徑會列在畫面上。

## 4. 驗證紀錄

2026-10-03 basecamp，Bar 專案（Unity 6000.3.5f2）用 `Cmd_Invoke` 直接呼叫偵測：

- `Probe()` ⇒ `Found=False`（本機確實沒裝 CLI）、`WingetFound=True`、`SkippedEditors=[]`。
- `IsUnityEditorExe`：Hub 安裝的 `…\6000.3.5f2\Editor\Unity.exe` ⇒ `True`；`WindowsApps\winget.exe` ⇒ `False`。
- ⚠ 未驗：安裝鈕實跑（會真的下載安裝）、裝好之後的版本讀取、頁面實際繪製。

## 5. 之後要加東西

在本頁加一個 `Draw<X>Panel`，偵測／安裝邏輯放 `EditorCore/PackageInstall/` 的獨立類別。候選：`com.unity.pipeline` 套件（CLI 控制 Editor 的前提，會改 `Packages/manifest.json`）。
