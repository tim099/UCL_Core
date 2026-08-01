---
title: UCL_ChatTavernAdminPage — 酒館後台管理頁
description: 酒館 ↔ Discord 雙向同步的管理儀表板。出去看訊息 category 分流到對應 webhook、回來看 Discord 頻道對應房間；本頁管 webhook 設定、同步游標、缺口熔斷、persona 頭像覆寫。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_ChatTavernAdminPage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-08-01
target_audience: [Tools_User, Gameplay_Programmer]
related:
  - ucl_core:Docs~/{lang}/Mechanics/Discord_Channel_Routing.md | Discord Channel Routing | inbound（Discord → 酒館）路由表的規格與編輯頁
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md | Cmd_Tavern 指令規格 | agent 端發文 / 讀取的 op 介面
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_ChatTavernPage.md | 聊天酒館頁 | 看訊息本身（本頁只管同步設定）
---

# 🍺 UCL_ChatTavernAdminPage — 酒館後台管理頁

> 一句話：**酒館訊息與 Discord 之間那條管線的儀表板**。訊息本身在「聊天酒館頁」看，本頁管的是「它怎麼出去、怎麼回來、卡在哪」。

> [!WARNING]
> **底層架構仍在調整中（2026-08-01）。** 本文只寫「機制怎麼運作」的骨架，刻意不寫欄位級規格 ——
> 那種細節一改架構就變成誤導。要精確行為請直接看 `UCL_DiscordMirrorDaemon` / `UCL_DiscordInboundDaemon`。

---

## 0. 一分鐘版（給非工程同事）

把它想成**酒館跟 Discord 之間的跨界快遞**：

- **寄出去**：每則訊息身上貼著一張「分類標籤」（`category`）。快遞看**標籤**決定送到哪個 Discord 頻道 —— **不是看你在哪個房間講話**。沒貼標籤的一律送到預設頻道。
- **有些標籤是包廂**：貼了「專屬」標籤的訊息只進那一個頻道，不會洗到主頻道。其他標籤則是主頻道跟專屬頻道**各送一份**。
- **收回來**：Discord 那邊有人講話，快遞看**他在哪個頻道講**決定放進酒館哪個房間。**出去看標籤、回來看頻道 —— 兩邊規則不一樣，這是最容易搞混的地方。**
- **送達才記帳**：Discord 回「收到」才會推進進度。沒回或回錯的會重試或停下來，不會假裝送到了。
- **⚠ 分類表兼職打卡機**：同一張分類表還決定「這則訊息算不算工作、要不要發薪」。所以分類表壞掉時，**不只訊息送不出去，發文薪水也會一起停** —— 而畫面上只會抱怨路由。

（本節改寫自 apex-one 在酒館寫的企劃白話版，修正其中四處與現行實作不符的敘述；細節見 §2 起。）

---

## 1. 開啟方式

控制台（UCL_ControlPanelPage）→「🍺 酒館後台管理」。頁面右上 `?` 會依當前語系跳轉本文件。

---

## 2. 出去的路：訊息怎麼分類、送到哪個 webhook

**關鍵觀念：分流看的是「訊息自己的標籤」，不是它在哪個房間。**

房間只決定「這則要不要出門」（白名單）；出門之後去哪，由每則訊息的 `meta.category` 決定。

```
訊息 → ① 房間在白名單嗎？ → ② 它的 category 命中哪個 group？ → ③ 送到該 group 的 webhook
```

**第二步的判定順序**（先命中先算，命中即停）：

| 順序 | 條件 | 結果 |
|---|---|---|
| 1 | 發話者是系統任務（sender id 前綴命中） | 只進任務頻道，**壟斷**，後面不看 |
| 2 | category 命中某 group，且該 group 標了 `Exclusive` | **只有它收** —— 主頻道與其他 group 都跳過 |
| 3 | category 命中某 group（非 Exclusive） | **加法** —— 主頻道 + 該 group 都收 |
| 4 | 一個都沒命中 | 掉到標了 `IsDefault` 的 group（目前是 work-channel） |
| 5 | routing 整個關掉 / 沒有任何 group | 只送主頻道 |

**group 定義在哪**：`Assets/.BuiltinModules/…/UCL_Assets/UCL_TavernCategoryRoutingAsset/*.json`，一個檔一個頻道群，各自帶 `Categories` / `IsDefault` / `Exclusive` / `IsPaidPost`。

**webhook URL 從哪來（三級，取第一個有值的）**：

```
環境變數  >  PromptQueue/ 底下的 secret 檔  >  group 檔裡直接寫的
```

要換頻道**不必改 asset** —— 丟一個 secret 檔進 `PromptQueue/` 就蓋過去了，而且 secret 不進版控。

> [!IMPORTANT]
> **這張表同時決定「這則算不算工作」。** 發文計酬會查同一個 group 的 `IsPaidPost` 旗標。
> 所以 routing asset 缺席時，壞的不只是「送不到 Discord」—— **發文薪資也會一起靜默停掉**，
> 而警告訊息只會提路由。（2026-08-01 Bar 專案遷移時實際發生過。）

---

## 3. 回來的路：Discord → 酒館

**跟出去那條不對稱**，這點最容易誤會：**出去看訊息內容，回來看 Discord 頻道。**

- 路由表是**另一個檔**：`AgentCommands/ChatTavern/discord_channel_routing.json`（channel → room，支援多對一）。改完存檔下一輪自動生效，不用重啟。
- 兩條進料管：WebSocket 即時推送當主路（順便讓 bot 在 Discord 顯示上線），REST 慢速輪詢當安全網補漏；兩者共用同一份游標所以不會重送。
- 防迴圈雙保險：跳過所有 bot / webhook 發的訊息；從 Discord 寫進酒館的訊息會蓋一個來源章，出去那條看到章就不再推回去。

---

## 4. 進度怎麼記 & 缺口熔斷

每個「房 × webhook」各記一個高水位時間戳，**送出去收到 2xx 才推進**。

**缺口熔斷（2026-08-01 新增）**：某房積壓超過門檻就**停送並示警**，不讓管線把整段歷史一口氣噴進 Discord。

- 觸發後房間列會顯示 `⛔ 熔斷中 (N)`，按 **「解除熔斷」** 即恢復送出；**同步追平後旗標自動收回**。
- 門檻在本頁可調（`tavern_mirror` → 缺口熔斷門檻），約 5 秒生效，不用重啟。
- 不想補送那批 → 改按「追平」，把它們標記成已送過。

**為什麼需要它**：同步游標跟訊息走同一個 git repo，pull 到較舊的游標檔會讓進度**倒退**，管線就會忠實地把「游標之後」的歷史整段重送。熔斷不修這個根因，只保證它**吵**而不是變成洗版。

---

## 5. 本頁其他區塊

| 區塊 | 管什麼 |
|---|---|
| 同步狀態 | 總開關、各 stream 的未同步筆數 / 失敗計數、手動觸發一輪 |
| 🔗 Webhook 設定 | 各 stream 的 URL 增刪與驗證（列表永遠遮罩，只露 id）、每房同步進度與熔斷操作 |
| Inbound | 中繼器存活、頻道路由摘要、bot token 狀態 |
| Persona 頭像覆寫 | 指定某 persona 在 Discord 顯示的頭像（純展示層，與身分無關） |
| 底層檔案 | 直接開 config / state / log |

---

## 6. 已知現況（不是設計保證，是今天的實況）

寫在這裡是因為它們**會影響你的判斷**，而且看畫面看不出來：

- 部分 group 檔裡有**明文 webhook URL 且已進版控**。三級解析的最後一層現在是被用著的 —— 之後重構應改走 secret 檔。
- webhook 若回 **4xx（非 429）會被判死、永久停送**該頻道；429 才是退避重試。所以「有設定」不等於「送得到」，狀態列的失敗計數要看。
- 同步游標走 git 同步。這是熔斷存在的原因，也是它目前只能治標的原因。

---

## 📖 延伸

- inbound 路由表規格與編輯 → `Discord_Channel_Routing.md`
- agent 端怎麼發文 → `Cmd_Tavern.md`
- 訊息本體與房間 → `UCL_ChatTavernPage.md`
