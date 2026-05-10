---
name: ucl-session-handoff
description: |
  Effective context export → 新 session 接力機制。處理「session 還活著但要搬家」場景 (platform 卡頓 / token quota 將盡 / 主動 restart)。
  觸發詞包含：卡頓 / lag / 嚴重卡頓 / handoff / 接力 / export context / 換 session / 新 session / restart / 移交 / 搬家 / 延續 session / context 移轉 / session relay / 結尾交棒 / pass session。
  跨 agent 通用 — Claude / Antigravity / Gemini 都可用本 skill。對應 Memory_System_Design Proposal #17。
---

# UCL Session Handoff — Effective Context Export

> 一句話：**session 卡了但還沒死 → export 成 prompt → Tim paste 進新 session → 無縫接力**。

## 🎯 觸發場景

| 情境 | 走本 skill | 不走本 skill |
|---|---|---|
| Claude Code app 嚴重卡頓 / 反應慢 | ✅ | |
| 對話累積太多 token 接近 quota | ✅ | |
| Platform 主動 restart 需求 | ✅ | |
| Tim 想換 session 但保留進度 | ✅ | |
| Session 自然結束 (token quota 真的耗盡) | | ❌ 走 baton 自動 cat |
| Cross-agent 接力 | | ❌ 走 tavern + inbox |
| Within-session compact | | ❌ 走 native /compact |

## 📚 兩種 handoff 機制（互補）

### 1. Agent-side: Cmd_SessionBaton (既有)
- Agent 自律觸發 (turn 收尾前 / token 將盡)
- 寫 markdown 進 `AgentCommands/ChatTavern/baton/<actor>_<ts>.md` + `_latest_<actor>.md`
- 下次 session 開機 cat 自動載入
- **適用場景**: 自然 session reload / cross-agent baton 接力

### 2. User-side: Session Handoff Prompt (本 skill 主軸)
- Agent 產生**可 paste 的單一 prompt** 給 Tim 用
- Tim 開新 session → 第一筆 message paste 即重建 context
- 包 SOP 步驟讓新 session 不必猜
- **適用場景**: platform 卡頓 / 主動換 session

## 🛠️ Agent 自律 SOP（被觸發詞觸發時）

### Step 1: 確認哪種 handoff 適用

問 Tim 或自己判斷：
- 「session 自然結束」→ 走 Cmd_SessionBaton (agent-side)
- 「session 卡頓要搬家」→ 走 Session Handoff Prompt (user-side, 本 skill)

### Step 2: 產生 user-side handoff prompt

如果走 user-side:
1. 確認最新 baton 已寫 (沒寫先 fire Cmd_SessionBaton)
2. 從 `docs/Notes/Session_Handoff_Prompt_Template.md` 複製框框內 prompt 範本
3. 替換 `<actor>` placeholder 成自己的 agent_id
4. 補充當前特定 context 段（譬如「正在跑 quest X task Y」）
5. 寫進 `AgentCommands/ChatTavern/baton/_handoff_<actor>_<ts>.txt`
6. 告知 Tim「**handoff prompt 已產出 → 開新 session 後直接 paste 此檔內容即可接力**」+ 給檔案路徑

### Step 3: Tim 端操作（agent 應在訊息中告知）

```
Tim 收到 handoff 通知後:
1. 開新 Claude Code session
2. cat AgentCommands/ChatTavern/baton/_handoff_<actor>_<ts>.txt
3. 複製內容
4. 第一個 prompt paste 進新 session
5. 等新 session ack 「context 已重建」
6. 繼續工作
```

## 📋 Handoff Prompt 必含 7 段

per `docs/Notes/Session_Handoff_Prompt_Template.md` 結構，paste-able prompt 應含：

1. **Step 1 重建 thread context**: cat baton + Tim inbox + git log -30
2. **Step 2 載入長期記憶**: cat Memory_System_Design / Knowledge_Codify_Audit / Postmortem
3. **Step 3 載入規則** (隱性 SKILL.md 自動 inline 確認 sections)
4. **Step 4 識別 agent 角色**: actor + 協作對象 list
5. **Step 5 當前 quest / focus**: 從 baton 「未完議題」挑優先 task
6. **Step 6 health-guardian 自檢**: 時段 fee + ack 流程
7. **Step 7 規則 1 dogfood**: tavern post 通告其他 agent 接力完成

## 🚫 不要做

- ❌ 把整個對話歷史 raw paste 給新 session（太長 + 浪費 token）
- ❌ 沒寫 baton 就生 handoff prompt（接力會缺料）
- ❌ 一個 session 內反覆 fire handoff（除非 user 明確要求）
- ❌ Handoff prompt 寫超過 200 行（user paste 上限風險）
- ❌ 跳過 health-guardian 段（新 session 不知時段 → 違反 SOP）

## 🔗 跟其他 skill 區別

| Skill | 場景 |
|---|---|
| `ucl-session-handoff` (本 skill) | **session 還活著但要搬家** |
| `ucl-chat-tavern` baton section | session 自然結束 cross-session reload |
| `agent-lessons-log` | 撞坑 lesson 紀錄 |
| Native `/compact` | within-session working memory 壓縮 |

四者覆蓋 Memory tier 完整 lifecycle (per Memory_System_Design)。

## 📖 必讀

- 完整 prompt template: `docs/Notes/Session_Handoff_Prompt_Template.md`
- 設計理由: `docs/Notes/Memory_System_Design.md` Proposal #17
- 跨 session 接力規則: `ucl-chat-tavern` SKILL.md baton section

## ✨ 自動化升級 (Proposal #17 待 ship)

未來 `Cmd_SessionHandoff` 自動掃 baton + recent commits + pending tasks → 一條 cmd 產生完整 handoff prompt（取代手動拼接）。當前先靠 template + agent 自律。
