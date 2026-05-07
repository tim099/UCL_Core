# UCL_Core Skills — 跨專案 Skill 來源

> source-of-truth；**不直接被 agent 讀**。每個專案要先用 `install_skills.py` 把這裡的 skill 拷到自家 `.claude/skills/` 才會被 Claude Code 載入。

---

## 為什麼不直接放 `.claude/skills/`

- UCL_Core 是 git submodule，路徑因專案而異（`Assets/UCL/UCL_Core` / `CardGame/Assets/UCL/UCL_Core` / 純 root...）
- Claude Code 只掃 `<project-root>/.claude/skills/`，不掃 submodule 內的 skills 目錄
- 所以這裡是 source，每個專案各自安裝一份本地副本

## 結構

```
Skills~/
├── README.md                # 本檔
├── _manifest.json           # 列出所有 skill 與 metadata，install 腳本用
└── <skill-name>/
    └── SKILL.md             # frontmatter + 內容
```

`Skills~` 結尾的 `~` 是讓 Unity Asset Database 跳過此目錄（避免被當成 game asset 匯入）。

## 內容慣例

- **SKILL.md 走 lazy-pointer 風格**：body 短，只放 TL;DR + 關鍵地雷 + 「先讀 `ucl_core:Docs~/zh-Hant/Workflows/<X>.md`」。完整知識留在 workflow 檔，單一事實源。
- frontmatter 必填 `name` / `description`；description 寫清楚觸發場景，給 agent 做 description-based dispatch。

## 安裝

從專案根目錄（或任何位置）跑：

```bash
python <UCL_Core>/Tools~/install_skills.py
```

腳本會：
1. 從自己位置往上找專案根（`.git` 或 `.claude/`）
2. 把這裡每個 skill 目錄拷到 `<root>/.claude/skills/<skill>/`
3. 寫 `.ucl_source` 標記 + UCL_Core commit hash
4. 寫 `.claude/skills/.ucl_installed` 全域標記

升級 UCL_Core submodule 後 → 重跑一次。

## 不同 Agent

第一波只支援 Claude Code（`.claude/skills/`）。其他 target（Cursor `.cursor/rules/` / Gemini `GEMINI.md` / `AGENTS.md`）規劃中，由 `install_skills.py --target` 開展。不支援 Skill 機制的 agent 透過 UCL_Core 自身 `CLAUDE.md` + workflow 檔仍可運作（只是失去 lazy load）。

## 主專案怎麼處理 .gitignore

建議把安裝出的副本 ignore：

```
.claude/skills/ucl-*/
.claude/skills/.ucl_installed
```

理由：source-of-truth 在 UCL_Core，每次 submodule bump 主專案不該跟著 commit 重複拷貝。專案專屬的 skill（如 EOV 的 `cardgame-docs-guide`）照常 commit。
