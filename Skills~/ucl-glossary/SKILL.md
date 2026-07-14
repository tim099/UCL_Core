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

## 必讀

完整流程(儲存結構、Cmd_Glossary 五個 op 全表、Pre-share 詞條檢查 hard rule、register quality-bar、與其他 skill 協作、Phase 2 backlog) → `ucl_core:Docs~/zh-Hant/Workflows/Glossary_Workflow.md`

## 🎯 為何需要 glossary 而非 vector offset

| 問題 (vector offset) | glossary 怎麼解 |
|---|---|
| 接收方解碼成本爆炸 | ✅ 純文字詞 + 一句解說, 不必算向量 |
| 維度語義約定 = 偽裝新詞庫 | ✅ **就直接是詞庫**, 不偽裝 |
| False precision (連續向量 noise floor) | ✅ 詞是離散符號, 沒連續精度誤解 |
| 跟自然語言阻抗不匹配 | ✅ 詞本身就是自然語言, 0 阻抗 |

→ basecamp 大小姐 2026-05-11 反對 Tim 的 vector offset 後, Tim 反提案 glossary — **完全繞開 4 大坑**。

## 核心 hard rule

- **回訊含專業術語 + 有實質成果 → 先 register 缺的詞, 再 share**(漏 register 比慢 30 秒更糟);且 **MUST 同步發一筆 tavern post**(縮版也行, 讓同事/未來自己跟得上)。
- **造詞就 register** — 用詞 ≥ 2 次沒進 glossary = 對未來 agent 失禮;撞到新詞立刻 register(< 30 秒)。
- **改詞走 `op=register --arg overwrite=true`**, 不直接 edit .md(才有 audit + frontmatter 同步)。

## 🚫 不可做

- ❌ **造詞但不 register** — 用詞 ≥ 2 次但沒寫進 glossary = 對未來 agent 失禮
- ❌ **register 但 one_line 空泛** — "...的機制" 沒解釋 = 沒幫助
- ❌ **slug 用中文** — 違反檔名安全慣例 (URL / cross-OS 友善)
- ❌ **aliases 空** — 至少列 1-2 個變體 (純 term 命中率太低)
- ❌ **靠 vector offset 表達細微語義** — 已在 vector-offset 分析否決, 走 glossary 修飾形容詞
- ❌ **改 register 詞用直接 edit .md** — 走 `op=register --arg overwrite=true` 才有 audit + frontmatter 同步
