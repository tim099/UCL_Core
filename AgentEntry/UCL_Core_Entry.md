# UCL_Core — Agent 入口（agent-neutral）

> [!IMPORTANT]
> 本檔是 **UCL_Core submodule 的 agent 入口薄索引**，內容 agent-neutral（Claude Code / Codex /
> Antigravity / Gemini 皆適用）。檔名 `CLAUDE.md` 是歷史沿用，**不代表 Claude 專屬**。
>
> 消費端 repo 怎麼讀：
> - **Claude Code**：在該 repo 的 `CLAUDE.md` 用 `@<本專案的 UCL_Core 掛載路徑>/CLAUDE.md` inline 載入。
>   ⚠ `@` **不支援變數**，各專案掛載位置不同（`Assets/Plugins/UCL_Core` / `Assets/UCL/UCL_Core` /
>   `CardGame/Assets/UCL/UCL_Core`…），所以那一行**天生是各 repo 自己維護**，不能寫進 template。
> - **Codex / Antigravity 等**：顯式讀取本檔（同樣依各 repo 掛載位置）。

## 這裡有什麼

| 主題 | 位置 |
|---|---|
| **口語指令 → 指令表查找** | [`Docs~/zh-Hant/CommandTable.md`](Docs~/zh-Hant/CommandTable.md) |
| **AgentCommand 系統**（queue / trigger / handler） | [`Docs~/zh-Hant/API/UCL_AgentCommand/`](Docs~/zh-Hant/API/UCL_AgentCommand/) |
| **Agent skills**（酒館 / 早晚安 / commit / 記憶區…） | [`Skills~/_manifest.json`](Skills~/_manifest.json) + `Skills~/<name>/SKILL.md` |
| **Workflows**（建頁 / 建 Cmd / 建 Asset / 翻譯 / 編譯排錯…） | [`Docs~/zh-Hant/Workflows/`](Docs~/zh-Hant/Workflows/) |
| **Python 工具索引** | [`Docs~/zh-Hant/Tools/Python_Tools_Index.md`](Docs~/zh-Hant/Tools/Python_Tools_Index.md) |
| **文件總索引** | [`Docs~/zh-Hant/index.md`](Docs~/zh-Hant/index.md) |

## 路徑規則（最常踩的坑）

> [!WARNING]
> **不要寫死 UCL_Core 的安裝路徑。** 各專案掛載位置不同，寫死的路徑跨專案必壞，
> 而且通常是**靜默壞**（`File.Exists` 失敗後 fail-soft return，連 warning 都沒有）。
>
> 描述本 core 內的檔案時用 `<UCL_Core>/…` 或 `ucl_core:` 前綴；
> 需要在程式碼裡解析實際路徑 → 走 `ucl-core-paths` skill 列出的既有解析器，**不要重造第四套**。

## 常用入口指令

```bash
# 派遣 AgentCommand（Editor 端執行）
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run <CmdType> --arg k=v

# 早安 / 晚安儀式（唯一入口）
python <UCL_Core>/Tools~/AgentCommands/awakening.py morning --agent <A> --persona <P>
python <UCL_Core>/Tools~/AgentCommands/awakening.py goodnight --persona <P>

# 編譯狀態（改完 .cs 的唯一可信來源）
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only
```

## 消費端 repo 的規則放哪

UCL_Core 只管**跨專案共用的 agent 機制**。專案自己的規則（路徑慣例、註解規範、協作政策）
放該 repo 的共用文件（本 LY 專案是 `Docs/AI_READABILITY_GUIDELINES.md`），
**不要往 UCL_Core 塞專案限定內容**。
# Shared cross-agent entry; this file is intentionally outside target templates.
