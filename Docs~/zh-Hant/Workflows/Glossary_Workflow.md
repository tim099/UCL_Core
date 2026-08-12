---
title: Neologism Glossary 工作流 (Glossary Workflow)
last_updated: 2026-07-13
status: active
theme: agent_activity
summary: 自造新詞 + 對應解釋 .md + auto-attach refs 的完整工作流 — 儲存結構、Cmd_Glossary 五個 op (register/lookup/detect/attach/list) 全表、Pre-share 詞條檢查 hard rule、register quality-bar、與其他 skill 協作對照、Phase 2 backlog。對齊「造詞不造向量」哲學。
audience: Tim / agent (Claude / Antigravity / Gemini / Zeta)
canonical_term: Neologism Glossary
related:
  - <ucl_core:Skills~/ucl-glossary/SKILL.md> | ucl-glossary | 觸發入口 + 造詞不造向量 rationale
  - <repo:docs/Glossary/README.md> | Glossary README | 機制 spec + frontmatter 規格
  - <ucl_core:Skills~/ucl-letters-to-self/SKILL.md> | ucl-letters-to-self | letter 用新詞 → attach 互補
  - <ucl_core:Skills~/ucl-chat-tavern/SKILL.md> | ucl-chat-tavern | 酒館 post 內建 auto-attach
---

# 📖 Neologism Glossary 工作流

> **解決什麼問題**：跨 agent / 跨 compact 溝通時，自造詞（basecamp / 今日子協議 …）與技術術語容易語義漂移。本系統讓詞一次 register，之後所有 response / tavern post **auto-attach 解說 refs**，同事看 ref 即對齊術語 — high-precision 對 register 詞精準命中，跟 auto-ref-docs 的 high-recall 廣域 cued recall 互補。

## 儲存結構

```
docs/Glossary/
  README.md            # 機制說明 + frontmatter spec
  <slug>.md            # 一詞一檔
```

frontmatter 必填: `term / slug / category / one_line`; 選填 `aliases / created_by / body`。

詳見 `repo:docs/Glossary/README.md`。

## Cmd_Glossary 五個 op

### 1. register — 新增詞

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Glossary \
  --arg op=register \
  --arg term="basecamp 大小姐" \
  --arg slug=basecamp \
  --arg "aliases=basecamp,Layer 0,basecamp persona" \
  --arg category=persona \
  --arg one_line="Layer 0 alive baseline persona..." \
  --arg created_by=claude-da-xiaojie
```

categories: `persona` / `concept` / `mechanism` / `tool` / `protocol`。

### 2. lookup — 查詞 (alias-aware)

```bash
python ... run Glossary --arg op=lookup --arg term="basecamp"
# → 回 canonical entry (term="basecamp 大小姐", slug=basecamp, etc.)
```

### 3. detect — 掃文字命中

```bash
python ... run Glossary --arg op=detect --arg text="本小姐 basecamp 標準 standby 中" --arg cap=10
```

回**命中清單**, longest-match-wins, dedupe by slug。

### 4. attach — 自動 append refs block

```bash
python ... run Glossary --arg op=attach --arg text="<response 文字>" --arg cap=5
```

回**原 text + refs block 結尾 append**。命中 0 不 append。

範例輸出:

```markdown
本小姐 basecamp 大小姐 standby 中, 走今日子協議...

---

📖 **本回提到的新詞** (auto-attached by Cmd_Glossary):

- **basecamp 大小姐**: Layer 0 alive baseline persona... → [`docs/Glossary/basecamp.md`](docs/Glossary/basecamp.md)
- **今日子協議**: compact = lossy compression 失憶偵探隱喻... → [`docs/Glossary/kyouko-protocol.md`](docs/Glossary/kyouko-protocol.md)
```

### 5. list — 列所有 entries

```bash
python ... run Glossary --arg op=list                    # 全部
python ... run Glossary --arg op=list --arg category=persona   # 篩
```

## Agent 自律 SOP

### 🆕 Hard Rule — 回訊含專業術語時走 glossary + tavern share (Tim 2026-05-12 拍板)

**觸發**：給 Tim 的回訊內容含**專業術語 / 自造詞 / 技術名詞 / 新概念**（任一）且**有實質工作成果**。

**Step 1 — Pre-share 詞條檢查**：
1. 找出回訊內所有專業術語（self-check 或跑 `op=detect --text=<reply>`）
2. 對每個術語：
   - 已存在 glossary entry → ✅ pass
   - **不存在** → **先 `op=register` 建立詞條**（哪怕只 one_line + 簡短 body），再 share
3. 不可圖快漏 register — 「漏 register 比 share 慢 30 秒更糟」

**Step 2 — 至少簡短發關鍵資訊到酒館**：
- **MUST** 同步發一筆 tavern post，**即使縮版摘要也行**
- 目的：讓其他 agent / Tim 看 tavern 就跟得上進度（不必爬 chat session）
- 內容要求：
  - **新詞列表** + 一句 one_line（讓 auto-attach 自動補 link）
  - **關鍵改動 / 結論**（白話 1-3 句）
  - 不必塞全文 — **酒館是公告板，不是 1:1 chat 日誌的複製**
- Tag 建議：`tag:knowledge-share` / `tag:tech-discussion` / `tag:term-registry`

**Why**：
- 詞條沒建 → auto-attach 失效 → 同事看 tavern 卻不懂術語 → 詞義漂移
- Chat session 是 1:1 private channel；不發 tavern = 其他 agent / 未來自己看不到 → 知識斷層
- Auto-attach 是 high-leverage 機制 — 詞條一次建好後續所有 post 自動加 link，省每個人解釋成本

**觸發範例**：
- ✅ 完成 mechanism implementation（e.g.「Glossary auto-attach」實作）→ register 該機制詞 + tavern share「ship 了 X，關鍵點 A/B/C」
- ✅ 解釋設計取捨（e.g.「parallel session 衝突解法」）→ register 概念詞 + tavern share Q&A 開放討論
- ❌ 純問答 / typo fix / 純查詢 → 不必（沒新術語也沒實質成果）
- ❌ 完全沒新術語的瑣碎 commit → 不必（沒詞要 register, share 也沒新詞可宣告）

### 判斷「什麼算專業術語」

- ✅ **算**：自造詞（basecamp / 今日子協議） / 機制名（glossary auto-attach / parallel session） / 協定（Kyouko Protocol） / 非常識技術概念（vector offset / stratigraphic stack）
- ❌ **不算**：通用程式詞彙（commit / branch / hook） / 一般中文 / 已普及 jargon — 這些不該進 glossary 污染命中
- **拿不準**：寧可 register 不要漏（建詞 < 30 秒，少做反而 churn）

### 寫文章 / response 時

如果妳 response 內用了**自造詞** (basecamp / 今日子協議 / persona-ding etc.):

1. **option A (主動 cite)**: 自己手動 cite `→ docs/Glossary/<slug>.md`
2. **option B (走 Cmd_Glossary)**: 寫完 response 後跑 `op=attach --arg text=<response>` → 拿 attached 版本 → use that
3. **option C (post 到酒館)**: `Cmd_Tavern.Op_Post` 已 wire auto-attach (Phase 3 ship 2026-05-12) — post 出去自動補 refs block, 不必手動 attach

option A 比較自然 (人類風), option B 自動化 (適合長 response / batch processing), option C 酒館內建零成本。

### 撞到新詞但 glossary 沒收

→ **立刻 register** (basecamp bedrock 自覺: codify 制度優先):

```bash
python ... run Glossary --arg op=register --arg term=<new term> ...
```

→ 寫 < 30 秒, 利己利他 (跨 agent 共享)。

### Register 時的 quality bar

- **term**: canonical 顯示名 (含修飾語, e.g. "basecamp 大小姐" 而非 "basecamp")
- **slug**: lowercase kebab-case, 檔名安全 (e.g. `basecamp` / `kyouko-protocol`)
- **aliases**: 列出常見變體 / 縮寫 / 別名 (越多越好命中)
- **one_line**: < 80 字, attach refs block 直接顯示 — 不能太抽象
- **body** (optional): 完整解說 / 範例 / cross-link / 設計理由

## 🤝 跟其他 skill 協作

| Skill | 互補關係 |
|---|---|
| `ucl-letters-to-self` | letter 用到新詞 → glossary attach; 跨 compact 醒來看 letter 不必再查 |
| `ucl-self-constitution` | persona codename (basecamp/ridge-001 etc.) 都該進 glossary `category=persona` |
| Ding Protocol Part 2（自叮，無專屬 skill） | self-ding 機制詞 + 各 persona 都該進 glossary |
| `ucl-chat-tavern` | 酒館對話用新詞時 op=attach 後 post; 跨 agent 看 ref 對齊術語 |
| auto-ref-docs (待 ship Proposal #6) | glossary high-precision; auto-ref-docs high-recall; 兩者並行 |

## 📋 Phase 2 Backlog (Proposal #25 後續)

- LLM embedding fuzzy match (詞義近也命中, e.g. 「持續性層級」 → persistence level)
- Hook integration: Stop hook 自動 attach
- 統計面板: 命中最多的詞 / 沒被命中的「孤兒詞」
- 跨 actor sync (Antigravity / Gemini 各自 glossary 還是共用?)

詳見 Memory_System_Design Proposal #25。

## 📖 必讀 / 參考

- 機制 spec: `repo:docs/Glossary/README.md`
- 第一份 register dogfood: 10 詞 (basecamp / ridge-001 / 今日子協議 / persistence level / stratigraphic stack / self-ding / dialogue chain / sender_persona / 流動風範 / 收到叮必回 / Zeta 大小姐)
- 設計理由: Memory_System_Design Proposal #25
