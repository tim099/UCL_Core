---
title: UCL_PackageInstallPage — 套件安裝頁
description: 外部工具的偵測與安裝（目前：Unity 官方 CLI、Unity Pipeline 套件）—— 看裝了沒、哪一版，一鍵開官方安裝指令。入口在 ToolBox「環境安裝」組。
source_files: |
  UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_PackageInstallPage.cs
  UCL_Core_Scripts/EditorCore/PackageInstall/UCL_UnityCliInstaller.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-10-03 (新頁；Unity CLI＋Pipeline 套件)
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
- 同日 Tim 用 winget 裝好 CLI 之後：`Probe()` ⇒ `Found=True`、`WindowsApps\unity.exe`、`1.0.0-beta.12` ⇒ 「裝好之後的版本讀取」補驗。
- Pipeline：`InstalledVersion()` ⇒ `0.8.0-exp.1`；頁面會組出的 `command editor_status --project-path …` 在 shell 原樣跑 ⇒ exit 0、`ready`；
  已安裝時再跑 `pipeline install` ⇒ manifest／lock 的 md5 前後一致（不會亂改檔）。⚠ 頁面按鈕本身與繪製仍未實點。

## 5. Unity Pipeline 套件區塊（`com.unity.pipeline`）

CLI 連進正在跑的 Editor 的前提（Editor 內開 7800 埠）；沒裝時 `unity status` 回 `STATUS_NO_INSTANCES`。

| 元件 | 做什麼 |
|---|---|
| 標題列 | 已解析到的版本（`PackageInfo.FindForPackageName`），沒裝顯示「未安裝」 |
| 🔄 重新讀取 | 重讀版本（首幀讀一次，⛔ 不每幀讀） |
| ⬇ 安裝 | 沒裝時出現：`unity pipeline install --project-path <專案根> --non-interactive` |
| ⤴ 升級 | 已裝時出現：`unity pipeline upgrade …`（有新版才升） |
| 🔌 測試連線 | `unity command editor_status`；不改檔 ⇒ 不跳確認 |

- 三個動作都**透過上方的 Unity CLI** 執行 ⇒ CLI 沒找到時停用。
- 安裝／升級**會改 `Packages/manifest.json` 與 `packages-lock.json`（進版控）**，確認彈窗會講；在背景跑（非互動），輸出原文顯示在本頁，成功後呼叫 `PackageManager.Client.Resolve()`（Editor 失焦時不一定會自己讀新 manifest）。
- 版本由 CLI 決定 —— 不用 `Client.Add` 寫死名稱或版本（套件是實驗版，2026-10-03 為 `0.8.0-exp.1`）。
- 為什麼連線測試用 `editor_status` 不用 `unity status`：前者經過 Editor 主執行緒（主執行緒凍 10 秒時它卡 10.39 秒），後者凍住時照回 `ready`。讀數見 `Plan_Unity_CLI_Evaluation.md` §5.1。
- ⚠ 連線測試必須在背景執行緒等：`editor_status` 要 Editor 主執行緒回答，在主執行緒上同步等它是自己等自己。

## 6. 之後要加東西

在本頁加一個 `Draw<X>Panel`，偵測／安裝邏輯放 `EditorCore/PackageInstall/` 的獨立類別。
