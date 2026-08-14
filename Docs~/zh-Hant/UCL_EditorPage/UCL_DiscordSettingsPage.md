---
title: UCL_DiscordSettingsPage — Discord 設定
description: 集中管理 Discord inbound 白名單、名稱／@提及別名、個人簡介，以及從 Guild 匯入成員候選的 Editor 頁面。
source_root: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_DiscordSettingsPage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-08-14
target_audience: [Tools_User, Developer]
related:
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_ChatTavernAdminPage.md | 酒館後台 | webhook、鏡像與 daemon 狀態
  - ucl_core:Docs~/{lang}/Mechanics/Discord_Channel_Routing.md | Discord Channel Routing | inbound 路由與白名單資料語意
---

# 💬 UCL_DiscordSettingsPage — Discord 設定

控制台 →「💬 Discord 設定」→「開啟 Discord 設定頁」。

## 白名單與身分資料

頁面以 **Discord user ID 一人一列**，合併顯示舊 outbound @ 對照與新 inbound 資料。它是顯示與操作層的整合，**不遷移、不取代既有資料**：

- `tavern_mirror.discord_user_mentions` 仍是 outbound 真實 ping 的原有權威來源。例如既存 `David → 191938341137022976` 保持不變，`@David` 繼續正常運作。
- `tavern_inbound.user_whitelist` 只擴充 inbound 門禁、個人簡介和新別名，舊專案沒有該欄位時仍維持原本不過濾的行為。

同 ID 的名稱與別名會在同一列列出；可直接新增 `Dump → 191938341137022976`，不必先把 David 放進 inbound 白名單。若需要 inbound profile，再明確按「加入白名單」。

人員操作採搜尋式下拉選單：先選一位 Discord 成員，再在同一個詳情面板編輯 @ 別名、白名單狀態與個人簡介；避免長名單同時展開而誤操作。

每筆 `tavern_inbound.user_whitelist.users` 以 Discord user ID 為主鍵，並可設定：

| 欄位 | 用途 |
|---|---|
| 顯示名稱 | 酒館中該使用者的名稱；也可作為 `@名稱` 的 Discord ping 對照 |
| @ 提及別名 | 同一 ID 的額外稱呼。例：`David, Dump` 都轉成同一個 Discord user ID |
| 個人簡介 | 職位、溝通脈絡等；會隨 inbound 訊息寫入 `meta.discord_user_profile`，供 agent 回覆前參考 |

啟用白名單時，未列帳號的真人訊息不會進酒館；空白清單代表全部拒絕。
白名單的「啟用」布林設定使用 `UCL_GUILayout.CheckBox`；`UCL_GUILayout.Toggle` 僅保留給各區塊的摺疊狀態，避免把兩種 UI 語意混用。

## Guild 成員候選匯入

匯入會呼叫 Discord 的 **List Guild Members** API，取得 Guild 成員的 ID 與可用名稱，僅放在本頁本次 session 的候選清單。候選**不會自動變成白名單**；每列需明確按「加入白名單」。

必要條件：

- bot token 已由 Secret Manager 安裝；
- Discord Developer Portal → Bot → Privileged Gateway Intents 已開啟 **Server Members Intent (`GUILD_MEMBERS`)**；
- 填入 Guild ID（若頻道路由已有 `guild_id`，頁面會先帶入第一筆）。

一般文字頻道沒有「目前所有觀看者」API；此功能取得的是整個 Guild 的成員，並非頻道即時閱覽名單。

## 其他 Discord 設定

本頁集中人員／身分設定；webhook、mirror 狀態與 daemon 控制仍由酒館後台管理，channel → room 對照則在頻道路由頁編輯。兩者都有快捷入口，資料來源各自維持單一真相。
