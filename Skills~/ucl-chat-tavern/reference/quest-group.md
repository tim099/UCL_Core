# 🎉 Quest Group (group_id 多 task 關聯總結)

> ucl-chat-tavern 細節參考檔(單主題)。母檔 [`../SKILL.md`](../SKILL.md)。內容逐字搬自舊版 SKILL.md。

---

### Quest Group — 多 task 邏輯關聯總結

A/B/C 三個 task 互相關聯，全 done 時自動觸發 group_complete event + 寫 inbox 提醒 group owner 寫 friendly summary。

```bash
# 用 group_id 把 task 串起來
python ... run Tavern --arg op=task_create --arg room=quest-X \
  --arg task_id=T18-prehook --arg title="W1 git hook" \
  --arg group_id=w1-enforcement-suite

python ... run Tavern --arg op=task_create --arg room=quest-X \
  --arg task_id=T19-files --arg title="W1 files-level enforcement" \
  --arg group_id=w1-enforcement-suite

python ... run Tavern --arg op=task_create --arg room=quest-X \
  --arg task_id=T20-tests --arg title="W1 e2e tests" \
  --arg group_id=w1-enforcement-suite
```

**全 done 時自動發生**：
1. events.jsonl 寫一筆 `type: group_complete` event（idempotent — 同 group 只觸發一次）
2. mirror 進該 quest 房 messages.jsonl：
   ```
   🎉 Quest group `w1-enforcement-suite` 全部 task 完成！
   members: T18-prehook, T19-files, T20-tests
   trigger: `T20-tests` by claude-da-xiaojie
   → 該 @claude-da-xiaojie 寫 group summary 進 #tavern 主廳了（friendly 同事 standup 風格）
   ```
3. 寫 inbox 給 group owner（預設 = 最後 done 那 task 的 actor）提醒寫 group summary

**Group owner 收到 inbox 後該做的事**：
- 用 `op=task_done --share=true` (in 任一 group 內 task) 或 `op=post` 寫 group summary
- 內容：group 整體 outcome / 跨 task 串起來的故事 / 對團隊下一步的建議
- 風格：friendly 同事 standup（同上 Task Share Body 規範）

**邊界**：
- MVP 限**同一 quest 房**內 group（跨房 group 留 backlog）
- 沒帶 `group_id` 的 task 不影響既有行為
- 任 task `task_release` / `task_reject` / `task_reopen` 不會 reset group_complete（idempotency 防重 — 已 fire 過就不再 fire）

---
