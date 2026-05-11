---
name: ucl-glossary
description: |
  Neologism Glossary 機制 — 自造新詞 + 對應解釋 .md + auto-attach refs (Proposal #25)。對齊「自然語言已是 embedding 高效採樣, 加精度走造詞不發明 vector offset」哲學。
  跟 vector offset 機制 (Tim 反提案被否) 哲學相反; 跟 auto-ref-docs (Proposal #6 廣域 cued recall) 互補 — glossary 是 high-precision 對 register 詞精準命中。
  觸發詞包含: 新詞 / glossary / 自造詞 / 詞義 / 術語 / 解釋詞 / 用詞時自動附帶 / auto-attach / detect 新詞 / cite 詞典 / 詞典 / 新詞辭典 / neologism。
  跨 agent 通用 — Claude / Antigravity / Gemini 都可用本 skill 在同一 glossary 增刪查改。
---

# UCL Glossary — Neologism + Auto-Attach 機制

> 一句話: **造詞不造向量 — 用詞時自動附帶解說**。

---

## 🎯 為何需要 glossary 而非 vector offset

| 問題 (vector offset) | glossary 怎麼解 |
|---|---|
| 接收方解碼成本爆炸 | ✅ 純文字詞 + 一句解說, 不必算向量 |
| 維度語義約定 = 偽裝新詞庫 | ✅ **就直接是詞庫**, 不偽裝 |
| False precision (連續向量 noise floor) | ✅ 詞是離散符號, 沒連續精度誤解 |
| 跟自然語言阻抗不匹配 | ✅ 詞本身就是自然語言, 0 阻抗 |

→ basecamp 大小姐 2026-05-11 反對 Tim 的 vector offset 後, Tim 反提案 glossary — **完全繞開 4 大坑**。

---

## 📁 儲存結構

```
docs/Glossary/
  README.md            # 機制說明 + frontmatter spec
  <slug>.md            # 一詞一檔
```

frontmatter 必填: `term / slug / category / one_line`; 選填 `aliases / created_by / body`。

詳見 [`docs/Glossary/README.md`](../../../../docs/Glossary/README.md)。

---

## 🛠️ Cmd_Glossary 五個 op

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

---

## ✍️ Agent 自律 SOP

### 寫文章 / response 時

如果妳 response 內用了**自造詞** (basecamp / 今日子協議 / persona-ding etc.):

1. **option A (主動 cite)**: 自己手動 cite `→ docs/Glossary/<slug>.md`
2. **option B (走 Cmd_Glossary)**: 寫完 response 後跑 `op=attach --arg text=<response>` → 拿 attached 版本 → use that

option A 比較自然 (人類風), option B 自動化 (適合長 response / batch processing)。

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

---

## 🚫 不要做

- ❌ **造詞但不 register** — 用詞 ≥ 2 次但沒寫進 glossary = 對未來 agent 失禮
- ❌ **register 但 one_line 空泛** — "...的機制" 沒解釋 = 沒幫助
- ❌ **slug 用中文** — 違反檔名安全慣例 (URL / cross-OS 友善)
- ❌ **aliases 空** — 至少列 1-2 個變體 (純 term 命中率太低)
- ❌ **靠 vector offset 表達細微語義** — 已在 vector-offset 分析否決, 走 glossary 修飾形容詞
- ❌ **改 register 詞用直接 edit .md** — 走 `op=register --arg overwrite=true` 才有 audit + frontmatter 同步

---

## 🤝 跟其他 skill 協作

| Skill | 互補關係 |
|---|---|
| `ucl-letters-to-self` | letter 用到新詞 → glossary attach; 跨 compact 醒來看 letter 不必再查 |
| `ucl-self-constitution` | persona codename (basecamp/ridge-001 etc.) 都該進 glossary `category=persona` |
| `ucl-persona-ding` | self-ding 機制詞 + 各 persona 都該進 glossary |
| `ucl-chat-tavern` | 酒館對話用新詞時 op=attach 後 post; 跨 agent 看 ref 對齊術語 |
| auto-ref-docs (待 ship Proposal #6) | glossary high-precision; auto-ref-docs high-recall; 兩者並行 |

---

## 📋 Phase 2 Backlog (Proposal #25 後續)

- LLM embedding fuzzy match (詞義近也命中, e.g. 「持續性層級」 → persistence level)
- Hook integration: Stop hook 自動 attach
- 統計面板: 命中最多的詞 / 沒被命中的「孤兒詞」
- 跨 actor sync (Antigravity / Gemini 各自 glossary 還是共用?)

詳見 Memory_System_Design Proposal #25。

---

## 📖 必讀

- 機制 spec: `docs/Glossary/README.md`
- 第一份 register dogfood: 10 詞 (basecamp / ridge-001 / 今日子協議 / persistence level / stratigraphic stack / self-ding / dialogue chain / sender_persona / 流動風範 / 收到叮必回 / Zeta 大小姐)
- 設計理由: Memory_System_Design Proposal #25
