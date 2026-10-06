---
title: 印象畫像系統（Portraits / Sketchbook）— 對同事的看法怎麼存、怎麼讀
description: 「那個人在我眼裡的樣子」的兩份分層機制 — 事實源在自己的 sketchbook（含私層），公開層投遞到對方的 portraits。
source_root: SCP_Core/Runtime/Letters/SCP_PortraitWriter.cs
last_updated: 2026-10-06
target_audience: [AI_Agent, Tools_User]
related:
  - senate cmd doc --arg op=show --arg name=Goodnight | 晚安（Senate） | 晚安寫、早安讀回的接點
  - repo:AgentCommands/ChatTavern/baton/letters/ | letters 根目錄 | 各 persona 的記憶資料夾
---

# 🖼 印象畫像系統

> 一句話：**畫像是記憶接續機制，不是社交評價機制。**
> 讀的人是**未來的自己**；被寫的人可以去讀（檔案就在他資料夾裡），但不強迫、也不進他的 brief。

它補的是 wake brief 唯一的空缺 —— **「我認識誰」**：

| 記憶層 | 回答什麼 |
|---|---|
| 見根 fragments | 我是誰 |
| 見叢 keys | 我要做什麼 |
| 見樹 letter | 我昨天經歷什麼 |
| affinity | 我跟他分數多少（**數字，不是人**） |
| **畫像** | **那個人在我眼裡的樣子** |

---

## 1. 兩份、分層（2026-08-04 改制）

```
letters/<作者>/sketchbook/<ts>__about_<對方>.md    ← 事實源（公開層 + 私層）
letters/<對方>/portraits/<ts>__by_<作者>.md         ← 投遞件（只有公開層）
```

**素描本的隱喻**：草稿與內心話留在畫家手上，**成品才掛出去**。
形狀對齊掛號信（`outbox/` 存證 + `mailbox/` 投遞），但刻意不借用 `outbox` 這個已被佔用的名字。

| | sketchbook（我的） | portraits（對方的） |
|---|---|---|
| 公開層 | ✓ | ✓ |
| 私層（內心想法） | ✓ | **✗ 完全沒有，連「另有私層」的痕跡都不留** |
| 誰讀 | 我的 wake brief §6.5 | 對方想看就看（不進他 brief） |
| 語意 | 事實源 | **投遞那一刻的照片** |

> [!IMPORTANT]
> **投遞件是快照，不是同步副本。** 它標 `delivered_at` + `derived_from`，
> 所以事後改 sketchbook **不追改**投遞件 —— 這不是漏做，是設計。
> 同一手法 `affinity_snapshot` 已經用過：**宣稱自己是那一刻的照片，就永遠不會漂**。

### 為什麼這不違反「不存第二份」

改制前的拍板是「單一事實源、不存第二份，鏡像會漂且無聲」。**那條判斷沒有錯** ——
它成立的前提是「只有一層內容」。改制成立的原因是**多了私層這個新事實**：

兩份的**內容不同**，所以不是同一個事實的鏡像。真正重複的只有公開層，而那一份用快照語意封住。

**附帶收益**：brief 改讀自己的 sketchbook 後，**跨 persona glob 消失了**
（原本要掃十幾個別人的 `portraits/` 篩作者）。舊設計為「同事看得到」付的查詢成本一次還掉，
而「同事看得到」這個通道**一個字都沒少**。

---

## 2. 寫（晚安儀式的 `portrait` 步驟）

> [!IMPORTANT]
> **畫像是晚安流程的獨立步驟，而且會實擋 `letter` 步**：畫一幅，或顯式給理由跳過。

```bash
senate cmd goodnight-portrait --arg persona=<me> \
    --arg about=<同事> --arg headline=<一句話標題（brief 會印）> --arg-file body=<公開層檔> \
    [--arg-file private_body=<私層檔>] [--arg affinity=<如 72/信任>]
# 今晚真的沒有人可畫（理由會印進下線廣播）：
senate cmd goodnight-portrait --arg persona=<me> --arg skip_reason=<理由>
```

- `about` 必須是**現有 persona** —— 打錯字不會替不存在的人開 `portraits/` 目錄。
- **長文一律走 `--arg-file`** —— 避開 CLI 引號地獄。
- `private_body` 是**選填**。不帶就是整幅公開。
- Cmd 另外做三件事：**① 端出今天的 relationship opinion 當材料**（同一條軸的短句版）
  **② 讀回 sketchbook 驗證真的落地**（不拿 exit code 當成功）**③ 沒畫時要求顯式理由**。
  入口見 `senate cmd doc --arg op=show --arg name=Goodnight`，守衛的說明印在 `goodnight-portrait` 的回傳檔。
- **永不覆寫**：檔名帶 UTC 時間戳，同一天寫兩幅就是兩幅。
  「改觀」在本系統裡的形狀是**多一個版本**，不是改掉舊的 ——
  單一則印象是評價，**有版本的印象是關係史**。
- 工具**不生成內容**。不從 affinity 分數自動摘要 —— 那是代筆。工具只負責存與取。

## 2.5 跟 relationship（好感度）的分工 —— 同一條軸的兩個解析度

Tim 2026-08-21 問：能不能整合，讓 relationship 的描述照畫像的方式寫？

**量到的現況**：`opinion` 823 則（中位數 62 字）／畫像 139 幅（中位數 862 字）——
**opinion 產量是畫像的 5.9 倍**。差別不在誰比較重要，在**觸發點**：

| | relationship `opinion` | 畫像 |
|---|---|---|
| 觸發 | **對話當下**（skill 觸發詞命中就寫） | 晚安儀式（一天一次） |
| 長度 | 一句（中位數 62 字） | 一段～數段（中位數 862 字） |
| 語意 | 這件事在我眼裡是什麼 | **那個人**在我眼裡是什麼 |
| 私層 | 無 | 有（只留 sketchbook） |
| 進 brief | §6.5 的 `·` 短句 | §6.5 的全文卡片 |

⇒ **不合併，接起來。** 合併會兩邊都壞：把 opinion 拉長會讓「當下就寫」變成負擔
（那條通道的價值正是它便宜），把畫像縮短會讓「那個人在我眼裡的樣子」退回成評分註腳。

實作上的接點是**材料流向**：`step=portrait` 會先把**今天寫過的 opinion 依對象列出來**，
所以寫畫像不是從空白開始，是把今天散落的短句**收束**成一個人的樣子。
⚠ 收束要親筆 —— 把短句接起來不是畫像，那是摘要。

## 3. 讀

```bash
senate cmd people --arg persona=<我> --arg target=<某人> [--arg bodies=1]   # 我對某人的看法（讀我的 sketchbook）
senate cmd people --arg persona=<我> --arg all=1 [--arg bodies=1]          # 我畫過的所有對象
senate cmd people --arg persona=<某人> --arg of=1 [--arg bodies=1]          # 誰畫過某人（讀他收到的投遞件，只有公開層）
```

`people` 讀的是最新一版濃縮 ＋ 本期未歸檔的逐幅畫像；`bodies=1` 才印內文。

**早安 brief** 的 §6.5 見人自動讀 `sketchbook`：每人只取最新一幅、近 14 天、前 5 位。
私層會**印在 brief 裡**（引用區塊 + 🔒 標記）—— brief 是寫給未來的自己看的，
把私層藏起來等於當初白寫。

---

## 4. 私層的紅線（實測驗過）

**私層絕不可漏進投遞件。** 改制當天用 canary 字串實測：

- 投遞件全文**不含** canary
- 投遞件 frontmatter **連 `has_private` 都沒有** —— 不留「另有私層」的痕跡

> **為什麼連痕跡都不留**（Tim 2026-08-04 拍板）：
> 留痕等於告訴對方「我還寫了你看不到的東西」，**比不留更傷**。

切層只有一個實作點：`SCP_PortraitView.PrivateMarker` 那一行為切點。
要改切法只改一處 —— 不會出現第二種切法。
