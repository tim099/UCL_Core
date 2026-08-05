---
title: UCL_GitFlattenSyncPage — Git 攤平同步頁
last_updated: 2026-08-05
---

# UCL_GitFlattenSyncPage

把**含 submodule 的 repo** 攤平成純檔案，同步到**另一個 repo 的工作目錄**。

- submodule 內容落在它原本的路徑上
- `.gitmodules` 與 gitlink 條目**完全不存在**於輸出
- **src 只被讀**（不 fetch / 不 commit / 不動 index / 不動 ref）
- **dst 只寫檔案**（不 commit、不碰 dst 的 git）

> [!NOTE]
> 頁面只是外殼。實際工作全在 [`Tools~/git_flatten_sync.py`](../../../Tools~/git_flatten_sync.py) ——
> 同一套邏輯要能在沒有 Editor 的環境（CI / agent）跑，所以**事實來源是那支腳本**，
> 頁面不自己實作任何 git 操作，連 submodule 清單都是問腳本拿的
> （兩套探索遲早會不一致，而不一致的那天沒人會發現：勾選畫面看起來永遠正常）。

## 頁面操作

| 區塊 | 說明 |
|---|---|
| 來源 / 目標 | 任意兩個 repo（**不綁本專案**），可用 `…` 選資料夾 |
| Submodule 開關 | 搜尋式下拉（`UCL_GUILayout.PopupSearchCache`）+ 逐項勾選；顯示父記錄 SHA / 磁碟 HEAD / drift / 未 init。**取消勾選父 submodule 時，其下巢狀無論自己勾不勾都被屏蔽**（但巢狀自己的設定會保留，父恢復後回到原本選擇）。src 沒有 submodule 時**整區隱藏** —— `PopupSearchCache` 選項為 0 會 LogError |
| 攤平基準 | `recorded`（父記錄的 gitlink SHA）／`head`（submodule 磁碟 HEAD）。**刻意沒有「自動」** —— 見下方 fail closed |
| 清除 stale | 刪掉「上次同步寫過、這次來源已沒有」的檔。首次同步（無 manifest）不刪 |
| 試跑 | **完全唯讀**，不寫任何檔。印出將寫入 / 已相同 / 衝突 / stale 清單 |
| 同步 | 走 `UCL_OptionPage` 二次確認才執行 |

設定存 `EditorPrefs`（JSON）。**路徑是絕對路徑，換機器要重填** —— 已知且刻意（Tim 2026-08-05 裁決）。

## 會被拒絕執行的情況（fail closed，不是警示）

警示可以被忽略，拒絕不能。以下一律拒絕，`--force` 也不放行：

| 情況 | 為什麼 |
|---|---|
| `dst == src`、或兩者互相嵌套 | 會邊讀邊覆蓋自己 |
| dst 有 `Temp/UnityLockfile` | **Unity 正開著那個專案** —— 寫進去就是覆蓋人家正在編輯的本地內容 |
| submodule 未 init | 內容不在本機，攤不出來。**不會靜默跳過** —— 少了東西的 dst 看起來跟成功一模一樣 |
| drift 且未指定基準 | 父記錄 ≠ 磁碟 HEAD 時**沒有預設值**（見下） |
| `--mode head` 但該 SHA 不可回溯 | 未 push 的 commit 攤進 dst = 目標端有一份無法回溯來源的內容 |

另外 dst 上**被本地改過的檔**會先擋下並列出（exit 5），確認後才能用 `--force` 覆蓋。

### 為什麼 drift 沒有預設值

兩種選擇各有一種**外觀成功**的失效：

- 攤「父記錄」→ 靜默少掉尚未 bump 的內容
- 攤「磁碟 HEAD」→ 靜默多一份無法回溯的內容

兩者都不會報錯。所以基準必須是人的顯式手勢。
（2026-08-05 拍板：@gura「不幫使用者做靜默選擇」+ @Sirius「fail closed，head 再驗可達性」。）

## 「完全同步」是怎麼被證明的

1. **預期集合**由來源圖獨立產生（src 自身樹 + 各納入 submodule 的樹加前綴，濾掉 gitlink 與 `.gitmodules`）
2. 寫入後**逐檔獨立重算 blob SHA**（自己算 `sha1("blob <len>\0" + content)`，
   **不呼叫 `git hash-object`** —— 拿被測工具自己的雜湊驗它自己是循環論證）
3. 缺檔數與內容不符數都必須為 0，否則 exit 6
4. **驗證沒過就不寫 manifest** —— 失敗的狀態不可被記成「上次同步結果」，否則下次 prune 會照錯的清單刪

### 只同步 tracked 內容

`.gitignore` 掉的檔不會過去。要磁碟完整快照請用別的工具。

### manifest

`<dst>/.ucl_flatten/manifest.json`（可用 `--manifest` 移到 dst 之外）。
它是**本工具在 dst 唯一新增的非來源檔案**，且不列入驗證集合。

用途是 stale 追蹤：只刪「上次是我們寫的」檔案，絕不碰不是我們寫的東西。
**沒被 prune 掉的 stale 會留在 manifest 裡** —— 抹掉紀錄會讓那些檔變成永久孤兒
（少東西會被發現，多東西不會）。

## CLI

```bash
# 只列 submodule（給 UI 畫勾選清單；不需要 --dst，不受 fail closed 影響）
python <UCL_Core>/Tools~/git_flatten_sync.py --src <來源 repo> --list-submodules

# 試跑 / 同步
python <UCL_Core>/Tools~/git_flatten_sync.py \
    --src <來源 repo> --dst <目標 repo> \
    [--mode recorded|head] [--exclude a,b] [--prune] [--force] [--apply] [--format json]
```

`--list-submodules` **列出全部，含被排除的** —— UI 的清單若只含納入項，
取消勾選之後那一列就消失、使用者無法還原（頁面第一版就是這樣壞的）。
**清單是「有什麼」，勾選是「要不要」，兩件事分開。**

## Process 管理

頁面呼叫腳本走 `UCL_ProcessRegistryService`（tag `git_flatten_sync`）：
spawn 前 `KillAllByTag` → `Register` → 結束時 `Unregister`。

全量同步可能跑數分鐘，而 domain reload / recompile 會清掉 C# 的 `Process` 物件，
**但 OS 層的 python 不會跟著死** —— 沒有這道 guard，每次重編再按一次就多一顆孤兒，
累積成屍潮。檢視／處置走 `UCL_ProcessAdminPage`。
硬規則見 [`Coding_Standards.md`](../Agent/Coding_Standards.md) 的「外部 Process」。

exit code：`0` 成功／`2` 參數錯／`3` 防呆拒絕／`4` fail closed（未 init / drift / 不可回溯）／
`5` 有本地衝突而未帶 `--force`／`6` 驗證未通過。
