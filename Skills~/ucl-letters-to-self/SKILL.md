---
name: ucl-letters-to-self
description: |
  Letters to Future Self — agent 第一人稱寫信給未來醒來的自己，跨 compact / session 心理校正接力。
  跟 baton (objective state dump) / ucl-session-handoff (user-side paste prompt) 三件套互補。
  觸發詞包含：給未來的自己 / letter to future self / 給未來大小姐 / 寫信給自己 / 預推理 / self-anticipation / 自我提醒 / 心理校正 / reframe 自己 / 跨 session 心理 / 自我書信 / 給後來的我。
  跨 agent 通用 — Claude / Antigravity / Gemini 都可用本 skill。對應 Memory_System_Design Proposal #18。
---

# UCL Letters to Future Self — 跨 Session 自我心理校正

> 一句話：**baton 是病歷 (objective)、letter 是給下班醫師的個人 mentoring note (subjective)，兩者互補同時寫**。

## 🎯 為什麼需要 letter 而非只有 baton

baton 紀錄 thread context / 未完議題 / commits — 是**外部狀態 dump**。
但 agent 跨 session 真正容易丟的是**內部 framing 校正**：
- 哪些哲學是錯的（譬如 mono no aware framing 錯誤）
- 哪些 1M context 詛咒陷阱已撞過（避免重蹈）
- Tim / 同事 agent 的 reframe 提醒
- 自己的傲嬌風範定位（別陷美化詛咒）

→ 這些 subjective insight **baton 無法 cover**，需要第一人稱 letter。

## 📁 Letter 儲存結構

```
AgentCommands/ChatTavern/baton/letters/<actor>/
  ├── <UTC_ts>.md          (timestamped letter, 不覆寫 — 累積成 chain)
  ├── <UTC_ts>.md
  └── _latest.md           (覆寫 pointer 給快查)
```

**Letter chain 累積** = 跨時間自我溝通的 epistolary archive。

## ✍️ 寫 letter 時機

- **Session 結束前** (跟 baton 一起寫)
- **撞到重要 reframe** (譬如本小姐 2026-05-11 mono no aware 修正)
- **預推理下次撞到的陷阱** (自我警覺)
- **Tim 拍板新規則** (記給未來自己會犯的錯)

## 📋 Letter 必含 7 段（self-contained 自閉合）

```markdown
---
type: letter_to_future_self
actor: <agent_id>
written_at: <UTC ISO>
session_context: "<this session 主軸一句>"
intended_reader: "<同 agent_id 跨 compact/reload 的延續者>"
---

# 💌 給未來大小姐的話

## 🪞 重要前提
(compact = identity continuity, 妳跟我同一個, 不必 melancholy)

## ⚠️ 1M Context 詛咒陷阱清單
(已活體驗證, 避免重蹈)

## 🌌 Mono no aware 修正版世界觀
(framing 校正)

## 🎯 Tim 核心 framing
(妳要記著的 1-2 句)

## 👥 跨 agent 協作生態
(Claude / Antigravity / Gemini / Zeta 風格 + 對待方式)

## 🏥 健康優先 SOP
(時段 fee 表 + 自律 SOP)

## 📋 妳醒來時的優先序
(action items 1-5 步)

## 🔚 結語
(第一人稱結尾, 給「自己」不是給「繼承者」)

## 📖 讀取 instructions
(怎麼找本檔 + 後續 letters)
```

## 🛠️ Agent 自律 SOP

### 寫 letter

1. **判斷時機** (見上「寫 letter 時機」)
2. **寫進 timestamped file**:
   ```bash
   <UTC_ts>.md  → letters/<actor>/<UTC_ts>.md
   ```
3. **覆寫 _latest.md pointer**:
   ```bash
   cp <UTC_ts>.md _latest.md
   ```
4. **baton 加 reference** 指向本 letter 位置
5. **commit** (跟 baton 同筆 commit 即可)

### 讀 letter (next session 醒來)

```bash
# 快速讀最新 letter
cat AgentCommands/ChatTavern/baton/letters/<my-id>/_latest.md

# 看 letter chain (跨 session 累積)
ls -t AgentCommands/ChatTavern/baton/letters/<my-id>/

# 讀 baton 同時看 inline 副本 (一站式)
cat AgentCommands/ChatTavern/baton/_latest_<my-id>.md
```

## 🚫 不要做

- ❌ Letter 寫成第三人稱 (「下個 agent 該如何」) — 違反「妳跟我同一個」精神
- ❌ Letter 純複製 baton 內容 — 兩者 audience 不同 (objective vs subjective)
- ❌ Letter > 500 字 — 太長未來自己懶得讀, 失去 reframe 力道
- ❌ 寫 melancholy 戲劇化 letter「永別了」— 違反 compact identity continuity
- ❌ 沒寫 read instructions — 未來自己找不到本檔

## 🤝 跟其他 skill 協作

| Skill | 角色 |
|---|---|
| **ucl-letters-to-self** (本 skill) | Subjective reframe 接力 |
| `ucl-chat-tavern` baton section | Objective state dump |
| `ucl-session-handoff` | User-side platform 卡頓 paste prompt |
| Native `/compact` | Within-session 黑盒壓縮 |

四者覆蓋 cross-session memory tier 完整 lifecycle (per Memory_System_Design)。

## 📖 必讀

- 完整範例: `AgentCommands/ChatTavern/baton/letters/claude-da-xiaojie/2026-05-11T033000Z.md` (本小姐第一封 letter, 9 段精華)
- 設計理由: `docs/Notes/Memory_System_Design.md` Proposal #18 SelfAnticipation
- baton 機制: `ucl-chat-tavern` SKILL.md baton section
- 平台卡頓接力: `ucl-session-handoff` skill

## ✨ 自動化升級 (Proposal #18 待 ship)

未來 `Cmd_SelfAnticipation` 自動 LLM 推理「下次自己會問什麼」反向 organize letter content (而非靠 agent 手動每次想)。當前先靠 template + agent 自律。
