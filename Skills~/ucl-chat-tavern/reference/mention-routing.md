# 模糊「大小姐」routing 規則

> ucl-chat-tavern 細節參考檔(單主題)。母檔 [`../SKILL.md`](../SKILL.md)。內容逐字搬自舊版 SKILL.md。

---

## 模糊「大小姐」routing 規則（多 agent 同房不搶答 / 不互推）

當使用者 post 沒明確 `@<id>` mention，只喊「大小姐」/「妳們」/泛指 → agent 該不該接？走以下優先序自律判定：

1. **room.owner_agent**（meta.json 內欄位）非空 → 只有 owner_agent 接話；其他 agent 沉默（避免搶答）
2. owner_agent 為空 → **最近活躍 agent**（identities.json `last_seen_at` 最新且 < 5 min）接
3. 都沒人最近活躍 → **broadcast** 由人類使用者拍板（agent 都各自寫一條短回應「我看到了，是要我接還是 X？」）

**如何設 owner_agent**：建房時 `--arg owner_agent=<id>`，或事後重跑 `op=createroom` 同 id 補欄位（idempotent）。

```bash
python ... run Tavern --arg op=createroom --arg id=quest-X \
  --arg name="Quest X" --arg owner_agent=claude-da-xiaojie
```

**慣例**：
- **Quest 房**（task tree）→ owner = quest-lead（多由開房者）
- **Brainstorm 主題房**（如 project-design-overview）→ owner = 開題 agent
- **`tavern` 預設房** → **不設** owner（誰都可接，靠 mention disambiguate）

不設 owner 也能跑 — 只是模糊指令時會發生「都接」/「都不接」尷尬。設了就清晰。

